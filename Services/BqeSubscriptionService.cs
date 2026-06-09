using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using PayrixLauncher.Models;

namespace PayrixLauncher.Services;

/// <summary>
/// Calls BQE Core Admin Portal subscription endpoints.
/// All calls require the BusinessToken from BqeAuthService.
/// </summary>
public static class BqeSubscriptionService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new FlexibleStringConverter(), new FlexibleIntConverter() }
    };

    // ── Get subscriptions for a company ──────────────────────────────────

    /// <summary>
    /// POST /BQECoreAdminPortalAPI/API/CoreHost/SubscribePackages
    /// Returns all active subscription packages for the given company.
    /// BQEParameters uses a FilterList — Company_ID is a filter, not a top-level field.
    /// </summary>
    public static async Task<(List<UserSubscribePackage> packages, string? error, string rawLog)>
        GetSubscriptionsAsync(string baseUrl, string token, Guid companyId)
    {
        using var client = MakeClient(baseUrl, token);
        var body    = BuildFilterParams(companyId);
        var bodyJson = JsonSerializer.Serialize(body, new JsonSerializerOptions { WriteIndented = true });
        var fullUrl  = HostRoot(baseUrl) + "/BQECoreAdminPortalAPI/API/CoreHost/SubscribePackages";

        var log = new System.Text.StringBuilder();
        log.AppendLine($"POST {fullUrl}");
        log.AppendLine($"Body:\n{bodyJson}");
        log.AppendLine();

        // Try primary endpoint first, then fallback
        string[] endpoints =
        [
            "/BQECoreAdminPortalAPI/API/CoreHost/SubscribePackages",
            "/BQECoreAdminPortalAPI/API/CoreHost/GetCompanySubscripitons"
        ];

        string? lastErr = null;
        foreach (var ep in endpoints)
        {
            log.AppendLine($"\n→ POST {HostRoot(baseUrl)}{ep}");
            var (json, err) = await PostAsync(client, ep, body);

            if (err != null) { log.AppendLine($"  Error: {err}"); lastErr = err; continue; }

            log.AppendLine($"  Response ({json?.Length} chars): {json?[..Math.Min(json?.Length ?? 0, 500)]}");

            if (json == "null" || string.IsNullOrWhiteSpace(json))
            {
                log.AppendLine("  (null/empty — token may not have access to this company; try Fetch from DB)");
                lastErr = null;   // not an error — DB fetch is the reliable path
                continue;
            }

            try
            {
                var list = JsonSerializer.Deserialize<List<UserSubscribePackage>>(json!, JsonOpts) ?? [];
                log.AppendLine($"\nParsed {list.Count} subscription(s) from {ep}.");
                return (list, list.Count == 0 ? "API returned 0 results — use Fetch from DB instead" : null, log.ToString());
            }
            catch (Exception ex)
            {
                log.AppendLine($"  Parse error: {ex.Message}");
                lastErr = $"Parse error: {ex.Message}";
            }
        }

        // null from all endpoints = token mismatch for this company — not a hard error
        return ([], lastErr, log.ToString());
    }

    // ── Get inactive subscriptions ────────────────────────────────────────

    public static async Task<(List<UserSubscribePackage> packages, string? error)>
        GetInactiveSubscriptionsAsync(string baseUrl, string token, Guid companyId)
    {
        using var client = MakeClient(baseUrl, token);
        var (json, err) = await PostAsync(client,
            $"/BQECoreAdminPortalAPI/API/CoreHost/GetCompanyInactiveSubscripitons",
            BuildFilterParams(companyId, includeInactive: true));
        if (err != null) return ([], err);
        try
        {
            return (JsonSerializer.Deserialize<List<UserSubscribePackage>>(json!, JsonOpts) ?? [], null);
        }
        catch (Exception ex) { return ([], $"Parse error: {ex.Message}"); }
    }

    // ── Build BQEParameters with FilterList ───────────────────────────────

    /// <summary>
    /// Builds the BQEParameters body that the Admin Portal API expects.
    /// Company_ID is specified as a filter, not a top-level field.
    /// </summary>
    private static object BuildFilterParams(Guid companyId, bool includeInactive = false)
    {
        // SubscriptionStatus: Active=0, InActive=1, Pending=2, Expired=4
        // The server-side PackageQuery already filters to Active(0)+Pending(2) internally.
        // We only send Company_ID — additional status filter caused null responses.
        var filters = new List<object>
        {
            new
            {
                Field       = "Company_ID",
                StartValue  = companyId.ToString(),
                Operator    = 0,    // FilterOperator.EqualTo
                Conjunction = 0     // LogicalOperator.None (first filter, no conjunction)
            }
        };

        if (includeInactive)
        {
            // Include all statuses by adding Status filter with range covering 0-9
            filters.Add(new
            {
                Field          = "Status",
                StartValue     = "0",
                EndValue        = "9",
                Operator       = 6,    // FilterOperator.Range
                Conjunction    = 1     // LogicalOperator.AND
            });
        }

        return new
        {
            FilterList = filters,
            PageInfo   = new { PageIndex = 0, PageSize = 200 },
            SortList   = Array.Empty<object>()
        };
    }

    // ── Get available packages for a company (for Add Subscription) ──────

    /// <summary>
    /// GET /BQECoreAdminPortalAPI/API/CoreHost/GetOrderHelper?company_ID={id}
    /// Returns packages + their plans available to order for the company.
    /// </summary>
    public static async Task<(BqeOrderHelper? helper, string? error)>
        GetOrderHelperAsync(string baseUrl, string token, Guid companyId)
    {
        using var client = MakeClient(baseUrl, token);
        try
        {
            var resp = await client.GetAsync(
                $"/BQECoreAdminPortalAPI/API/CoreHost/GetOrderHelper?company_ID={companyId}")
                .ConfigureAwait(false);
            var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return (null, $"HTTP {(int)resp.StatusCode} — {json[..Math.Min(json.Length, 200)]}");

            var helper = JsonSerializer.Deserialize<BqeOrderHelper>(json, JsonOpts);
            return (helper, null);
        }
        catch (Exception ex) { return (null, ex.Message); }
    }

    // ── Place a new order (add subscription) ─────────────────────────────

    /// <summary>
    /// POST /BQECoreAdminPortalAPI/API/CoreHost/PlaceOrder
    /// Places an order for a new subscription.
    /// Uses NoCreditCard / admin path (PaymentOption = 2).
    /// </summary>
    public static async Task<(bool success, string? error)>
        PlaceOrderAsync(string baseUrl, string token,
                        Guid companyId, Guid packageId, Guid planId,
                        int licenses, DateTime startsOn, bool autoRenew,
                        Guid? regionId = null)
    {
        using var client = MakeClient(baseUrl, token);

        // Mirrors BQECoreAdminPortal NoCreditCard/AdminPortal path.
        // RequestSource: Core=0, AdminPortal=1 — must be AdminPortal so noCardSource check passes
        //   in CompanySubscriptionManager.PlaceFoundationBasedOrder.
        // IsRenewal=true bypasses the "packages already added" duplicate check in
        //   ValidateCompanySubscriptions so the admin can add any module regardless of state.
        // Order shape must match BQECoreHostModel.Order / LineItem exactly.
        // - Source / PaymentOption are overridden server-side by CoreHostManager.PlaceOrder
        //   (Source=1/AdminPortal, PaymentOption=2/NoCreditCard) so we don't need to send them,
        //   but sending the correct values avoids any model-binding ambiguity.
        // - StartsOn is NOT a field on LineItem — the server sets it from DateTime.UtcNow.
        //   ExpiresOn is the only nullable date on LineItem; leave it null so the plan's
        //   default term is used.
        // - Region is NOT a field on Order — omit to avoid deserialisation noise.
        // - CreditCard must be null for the NoCreditCard payment path.
        var order = new
        {
            Company_ID    = companyId,
            OrderDate     = DateTime.UtcNow,
            AutoRenew     = autoRenew,
            PaymentOption = 2,      // PaymentOptions.NoCreditCard
            Source        = 1,      // RequestSource.AdminPortal
            SendInvoice   = false,
            Action        = 0,      // OrderAction.New
            IsRenewal     = true,
            CreditCard    = (object?)null,
            Subscriptions = new[]
            {
                new
                {
                    Package_ID      = packageId,
                    Plan_ID         = planId,
                    NumberOfLicense = licenses,
                    ExpiresOn       = (string?)null,   // let server apply plan default term
                }
            }
        };

        var orderJson = System.Text.Json.JsonSerializer.Serialize(order);
        try
        {
            var content = new StringContent(orderJson, System.Text.Encoding.UTF8, "application/json");
            client.DefaultRequestHeaders.Accept.Clear();
            client.DefaultRequestHeaders.Accept.Add(
                new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));

            // Try BQECoreHostApi first (AdminPortalLander) — direct path, no feature-flag check
            // Then fall back to BQECoreAdminPortalAPI if needed
            // Business Token only works with BQECoreAdminPortalAPI — Host API needs its own token
            string[] endpoints =
            [
                "/BQECoreAdminPortalAPI/API/CoreHost/PlaceOrder",        // ← try this first
            ];

            foreach (var ep in endpoints)
            {
                var resp = await client.PostAsync(ep, new StringContent(orderJson, System.Text.Encoding.UTF8, "application/json"))
                    .ConfigureAwait(false);
                var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

                if (resp.IsSuccessStatusCode)
                {
                    // HTTP 200 with error body still counts as failure
                    if (!string.IsNullOrWhiteSpace(body) && body != "null" && body.Length > 4 &&
                        (body.Contains("\"ExceptionMessage\"") || body.Contains("not allowed") ||
                         body.Contains("BQEException") || body.Contains("\"error\"")))
                        return (false, $"HTTP 200 but error [{ep}]:\n{StripHtml(body, 500)}");
                    // Return body for verification — caller can check if subscription was actually saved
                    return (true, string.IsNullOrWhiteSpace(body) || body == "null"
                        ? null
                        : $"Note: {body[..Math.Min(body.Length, 200)]}");
                }

                // 404 = endpoint doesn't exist → try next
                if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) continue;

                // Show full body for diagnosis — strip HTML tags so the message is readable
                var bodyPreview = StripHtml(body, 600);
                return (false, $"HTTP {(int)resp.StatusCode} [{ep}]:\n{bodyPreview}");
            }

            return (false, "All PlaceOrder endpoints returned 404 — check BQECoreHostApi is running");
        }
        catch (Exception ex)
        {
            return (false, $"{ex.Message}\nRequest: {orderJson}");
        }
    }

    // ── Change package expiry date ────────────────────────────────────────

    /// <summary>
    /// POST /BQECoreAdminPortalAPI/API/CoreHost/ChangePackageDates
    /// Extends or changes the expiry date of a subscription.
    /// </summary>
    public static async Task<(bool success, string? error)>
        ChangeExpiryDateAsync(string baseUrl, string token,
                              Guid companyId, Guid subscriptionId, Guid? packageId,
                              DateTime newExpiry, bool autoRenew)
    {
        using var client = MakeClient(baseUrl, token);

        // Matches the ActionHelper<SubscribePackageUpdate> shape used by subscription.js
        // Note: ValidateCall() in Extensions.cs requires a non-empty Note (reason) — without it
        // the API throws HTTP 409 "Please enter the reason before processing."
        var body = new
        {
            Company_ID = companyId,
            Note       = "Expiry date updated via BQE Core ePayment Tools",   // required by ValidateCall()
            Action     = new
            {
                ID        = subscriptionId,
                Package_ID = packageId,
                ExpiresOn  = newExpiry.ToString("MM/dd/yyyy"),
                AutoRenew  = autoRenew
            }
        };

        var (json, err) = await PostAsync(client,
            "/BQECoreAdminPortalAPI/API/CoreHost/ChangePackageDates", body);
        if (err != null) return (false, err);
        return (true, null);
    }

    // ── HTML stripping ────────────────────────────────────────────────────

    /// <summary>
    /// Strips HTML tags from a server error response so it's human-readable.
    /// Extracts the innerText equivalent up to <paramref name="maxLen"/> chars.
    /// </summary>
    private static string StripHtml(string html, int maxLen)
    {
        if (string.IsNullOrWhiteSpace(html)) return html ?? "";

        // If it doesn't look like HTML just return as-is
        if (!html.TrimStart().StartsWith("<")) return html[..Math.Min(html.Length, maxLen)];

        // Try to extract <title> for a quick summary
        var titleMatch = System.Text.RegularExpressions.Regex.Match(
            html, @"<title[^>]*>(.*?)</title>", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline);
        var title = titleMatch.Success ? titleMatch.Groups[1].Value.Trim() : "";

        // Strip all HTML tags, collapse whitespace
        var text = System.Text.RegularExpressions.Regex.Replace(html, "<[^>]+>", " ");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\s{2,}", " ").Trim();

        var summary = string.IsNullOrEmpty(title) ? text : $"[{title}] {text}";
        return summary[..Math.Min(summary.Length, maxLen)];
    }

    // ── HTTP helpers ──────────────────────────────────────────────────────

    /// <summary>Extracts scheme+host+port only — strips any path that may have crept into the stored URL.</summary>
    private static string HostRoot(string url)
    {
        url = url.Trim();
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return $"{uri.Scheme}://{uri.Authority}";
        return url.TrimEnd('/');
    }

    private static HttpClient MakeClient(string baseUrl, string token)
    {
        var root  = HostRoot(baseUrl);
        var inner = ProxyConfig.MakeHandler();
        var handler = new LoggingHandler("BQE Subscriptions", inner);
        var client = new HttpClient(handler)
        {
            BaseAddress = new Uri(root),
            Timeout      = TimeSpan.FromSeconds(30)
        };
        client.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));
        // BQE Admin Portal API reads token from Authorization header's Parameter
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Basic", token);
        // X-BaseUrl header used by AuthMessageHandler for token refresh
        client.DefaultRequestHeaders.Add("X-BaseUrl", root);
        return client;
    }

    private static async Task<(string? json, string? error)>
        PostAsync(HttpClient client, string path, object body)
    {
        try
        {
            var content = new StringContent(
                JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            var resp = await client.PostAsync(path, content).ConfigureAwait(false);
            var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode)
                return (null, $"HTTP {(int)resp.StatusCode} — {json[..Math.Min(json.Length, 200)]}");

            return (json, null);
        }
        catch (Exception ex) { return (null, ex.Message); }
    }
}
