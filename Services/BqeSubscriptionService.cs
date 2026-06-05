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
        Converters = { new FlexibleIntConverter() }
    };

    // ── Get subscriptions for a company ──────────────────────────────────

    /// <summary>
    /// POST /BQECoreAdminPortalAPI/API/CoreHost/SubscribePackages
    /// Returns all active subscription packages for the given company.
    /// </summary>
    public static async Task<(List<UserSubscribePackage> packages, string? error)>
        GetSubscriptionsAsync(string baseUrl, string token, Guid companyId)
    {
        using var client = MakeClient(baseUrl, token);
        var body = new BqeParameters { CompanyId = companyId };

        var (json, err) = await PostAsync(client,
            "/BQECoreAdminPortalAPI/API/CoreHost/SubscribePackages", body);
        if (err != null) return ([], err);

        try
        {
            var list = JsonSerializer.Deserialize<List<UserSubscribePackage>>(json!, JsonOpts)
                       ?? [];
            return (list, null);
        }
        catch (Exception ex)
        {
            return ([], $"Parse error: {ex.Message}");
        }
    }

    // ── Get inactive subscriptions ────────────────────────────────────────

    /// <summary>
    /// POST /BQECoreAdminPortalAPI/API/CoreHost/GetCompanyInactiveSubscripitons
    /// </summary>
    public static async Task<(List<UserSubscribePackage> packages, string? error)>
        GetInactiveSubscriptionsAsync(string baseUrl, string token, Guid companyId)
    {
        using var client = MakeClient(baseUrl, token);
        var body = new BqeParameters { CompanyId = companyId };

        var (json, err) = await PostAsync(client,
            "/BQECoreAdminPortalAPI/API/CoreHost/GetCompanyInactiveSubscripitons", body);
        if (err != null) return ([], err);

        try
        {
            var list = JsonSerializer.Deserialize<List<UserSubscribePackage>>(json!, JsonOpts)
                       ?? [];
            return (list, null);
        }
        catch (Exception ex)
        {
            return ([], $"Parse error: {ex.Message}");
        }
    }

    // ── Change package expiry date ────────────────────────────────────────

    /// <summary>
    /// POST /BQECoreAdminPortalAPI/API/CoreHost/ChangePackageDates
    /// Extends or changes the expiry date of a subscription.
    /// </summary>
    public static async Task<(bool success, string? error)>
        ChangeExpiryDateAsync(string baseUrl, string token,
                              Guid companyId, Guid subscriptionId,
                              DateTime newExpiry, bool autoRenew)
    {
        using var client = MakeClient(baseUrl, token);

        // Wraps the inner object in ActionHelper<SubscribePackageUpdate> shape
        var body = new
        {
            Entity = new ChangePackageDateRequest
            {
                SubscriptionId = subscriptionId,
                CompanyId      = companyId,
                ExpiresOn      = newExpiry.ToString("yyyy-MM-ddT00:00:00"),
                AutoRenew      = autoRenew
            }
        };

        var (_, err) = await PostAsync(client,
            "/BQECoreAdminPortalAPI/API/CoreHost/ChangePackageDates", body);
        if (err != null) return (false, err);
        return (true, null);
    }

    // ── HTTP helpers ──────────────────────────────────────────────────────

    private static HttpClient MakeClient(string baseUrl, string token)
    {
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback =
                HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        };
        var client = new HttpClient(handler)
        {
            BaseAddress = new Uri(baseUrl.TrimEnd('/')),
            Timeout      = TimeSpan.FromSeconds(30)
        };
        client.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token);
        // BQE Core also accepts the token as a custom header
        client.DefaultRequestHeaders.Add("APIKEY",        token);
        client.DefaultRequestHeaders.Add("BusinessToken", token);
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
