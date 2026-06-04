using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using PayrixLauncher.Models;

namespace PayrixLauncher.Services;

public enum PayrixEnvironment
{
    Sandbox,
    Production
}

public class PayrixService
{
    private readonly HttpClient _client;
    private readonly PayrixEnvironment _environment;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new PayrixLauncher.Models.FlexibleStringConverter(), new PayrixLauncher.Models.FlexibleIntConverter() }
    };

    public static string SandboxBaseUrl    => "https://test-api.payrix.com";
    public static string ProductionBaseUrl => "https://epaymentsapi.bqecore.com";

    // ── Login: exchange username+password for an API token ───────────────────

    /// <summary>
    /// Returns (token, rawJson, error).
    /// rawJson is always populated so the caller can inspect/log the full response.
    /// </summary>
    public static async Task<(string? token, string rawJson, string? error)> LoginAsync(
        string username, string password, PayrixEnvironment environment)
    {
        var baseUrl = environment == PayrixEnvironment.Production ? ProductionBaseUrl : SandboxBaseUrl;
        using var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback =
                HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        };
        using var client = new HttpClient(handler) { BaseAddress = new Uri(baseUrl) };
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        // Payrix login accepts both { login, password } and { username, password }
        var body = new StringContent(
            JsonSerializer.Serialize(new { login = username, password }),
            System.Text.Encoding.UTF8,
            "application/json");

        string json = "";
        try
        {
            var response = await client.PostAsync("/login", body).ConfigureAwait(false);
            json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                return (null, json, $"HTTP {(int)response.StatusCode}: {json}");

            using var doc = JsonDocument.Parse(json);

            // Try every known response shape Payrix uses:
            // Shape 1:  { "response": { "data": [ { "token": "..." } ] } }
            // Shape 2:  { "response": { "data": [ { "apiKey": "..." } ] } }
            // Shape 3:  { "token": "..." }
            // Shape 4:  { "apiKey": "..." }
            // Shape 5:  { "data": { "token": "..." } }
            string? token = null;

            if (doc.RootElement.TryGetProperty("response", out var resp))
            {
                if (resp.TryGetProperty("data", out var data) &&
                    data.ValueKind == JsonValueKind.Array &&
                    data.GetArrayLength() > 0)
                {
                    var first = data[0];
                    token = TryGetString(first, "token")
                         ?? TryGetString(first, "apiKey")
                         ?? TryGetString(first, "api_key")
                         ?? TryGetString(first, "key");
                }
                // Also check directly under "response"
                token ??= TryGetString(resp, "token")
                       ?? TryGetString(resp, "apiKey");
            }

            // Top-level fallbacks
            token ??= TryGetString(doc.RootElement, "token")
                   ?? TryGetString(doc.RootElement, "apiKey")
                   ?? TryGetString(doc.RootElement, "api_key");

            return string.IsNullOrEmpty(token)
                ? (null, json, $"Login OK but no token found in response")
                : (token, json, null);
        }
        catch (Exception ex)
        {
            return (null, json, $"Login exception: {ex.Message}");
        }
    }

    private static string? TryGetString(JsonElement el, string name)
    {
        if (el.TryGetProperty(name, out var prop) &&
            prop.ValueKind == JsonValueKind.String)
            return prop.GetString();
        return null;
    }

    // ── Shared connection pools — one per environment (never disposed) ────────
    private static readonly HttpClient _sandboxClient    = CreatePooledClient(isSandbox: true);
    private static readonly HttpClient _productionClient = CreatePooledClient(isSandbox: false);

    private static HttpClient CreatePooledClient(bool isSandbox)
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime    = TimeSpan.FromMinutes(10),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            MaxConnectionsPerServer     = 10,
            SslOptions = new System.Net.Security.SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, _, _, _) => true
            },
            AutomaticDecompression =
                System.Net.DecompressionMethods.GZip |
                System.Net.DecompressionMethods.Deflate |
                System.Net.DecompressionMethods.Brotli
        };
        var baseUrl = isSandbox ? SandboxBaseUrl : ProductionBaseUrl;
        return new HttpClient(handler)
        {
            BaseAddress = new Uri(baseUrl),
            Timeout     = TimeSpan.FromSeconds(60)
        };
    }

    public PayrixService(string apiKey, PayrixEnvironment environment)
    {
        _environment = environment;
        // Pick the right shared client and clone with per-request APIKEY header
        var baseClient = environment == PayrixEnvironment.Sandbox ? _sandboxClient : _productionClient;

        // We can't mutate DefaultRequestHeaders on a shared client,
        // so we use a delegating handler wrapper that injects the key per-request.
        var handler = new ApiKeyHandler(apiKey, environment == PayrixEnvironment.Sandbox
            ? SandboxBaseUrl : ProductionBaseUrl);
        _client = new HttpClient(handler)
        {
            BaseAddress = new Uri(environment == PayrixEnvironment.Sandbox ? SandboxBaseUrl : ProductionBaseUrl),
            Timeout     = TimeSpan.FromSeconds(60)
        };
        _client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _ = baseClient; // suppress unused warning
    }

    // ── Fetch single transaction by ID ────────────────────────────────────────

    public async Task<(Transaction? transaction, string rawJson, string? error)> GetTransactionAsync(string txnId)
    {
        var json = "";
        try
        {
            // ── Strategy 1: exact-ID path (works on both envs for standard Payrix API) ──
            var directResponse = await _client.GetAsync($"/txns/{Uri.EscapeDataString(txnId)}?expand[items][]").ConfigureAwait(false);
            json = await directResponse.Content.ReadAsStringAsync().ConfigureAwait(false);

            if (directResponse.IsSuccessStatusCode)
            {
                var directParsed = JsonSerializer.Deserialize<PayrixResponse>(json, JsonOptions);
                // Must verify the returned ID exactly matches — the API can return a different record
                var directTxn = directParsed?.Response?.Data?.FirstOrDefault(t => t.Id == txnId);
                if (directTxn?.Id != null)
                    return (directTxn, json, null);
            }

            // ── Strategy 2: query-string exact search (sandbox: search param in URL) ──
            var qsResponse = await _client.GetAsync(
                $"/txns?search[id][eq]={Uri.EscapeDataString(txnId)}&page[limit]=1&page[number]=1&expand[items][]").ConfigureAwait(false);
            var qsJson = await qsResponse.Content.ReadAsStringAsync().ConfigureAwait(false);

            if (qsResponse.IsSuccessStatusCode)
            {
                var qsParsed = JsonSerializer.Deserialize<PayrixResponse>(qsJson, JsonOptions);
                if (qsParsed?.Response?.Errors is not { Count: > 0 })
                {
                    // Only accept an exact ID match — never silently return a different transaction
                    var qsTxn = qsParsed?.Response?.Data?.FirstOrDefault(t => t.Id == txnId);
                    if (qsTxn?.Id != null)
                        return (qsTxn, qsJson, null);
                }
            }

            // ── Strategy 3: header-based exact search (Production BQECore proxy) ──
            var hdrRequest = new HttpRequestMessage(
                HttpMethod.Get, "/txns?page[limit]=1&page[number]=1&expand[items][]");
            hdrRequest.Headers.Add("search", $"id[eq]={txnId}");
            var hdrResponse = await _client.SendAsync(hdrRequest).ConfigureAwait(false);
            var hdrJson     = await hdrResponse.Content.ReadAsStringAsync().ConfigureAwait(false);

            if (hdrResponse.IsSuccessStatusCode)
            {
                var hdrParsed = JsonSerializer.Deserialize<PayrixResponse>(hdrJson, JsonOptions);
                if (hdrParsed?.Response?.Errors is not { Count: > 0 })
                {
                    // Only accept an exact ID match — never silently return a different transaction
                    var hdrTxn = hdrParsed?.Response?.Data?.FirstOrDefault(t => t.Id == txnId);
                    if (hdrTxn?.Id != null)
                        return (hdrTxn, hdrJson, null);
                }
            }

            // Nothing found — return last raw response for diagnostics
            var errors = JsonSerializer.Deserialize<PayrixResponse>(json, JsonOptions)?.Response?.Errors;
            var errMsg = errors is { Count: > 0 }
                ? string.Join("  |  ", errors.Select(e => e.Summary))
                : $"Transaction '{txnId}' not found (tried path, query-string and header search).";
            return (null, json, errMsg);
        }
        catch (Exception ex)
        {
            return (null, json, $"Fetch error: {ex.Message}");
        }
    }

    // ── Fetch payment info (card brand / last 4) ─────────────────────────────

    public async Task<PaymentInfo?> GetPaymentAsync(string paymentId)
    {
        HttpResponseMessage response;

        if (_environment == PayrixEnvironment.Production)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, $"/payments?page[limit]=1&page[number]=1");
            request.Headers.Add("search", $"id[eq]={paymentId}");
            response = await _client.SendAsync(request).ConfigureAwait(false);
        }
        else
        {
            response = await _client.GetAsync($"/payments/{paymentId}").ConfigureAwait(false);
        }

        if (!response.IsSuccessStatusCode)
            return null;

        var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        var parsed = JsonSerializer.Deserialize<PayrixPaymentResponse>(json, JsonOptions);
        return parsed?.Response?.Data.FirstOrDefault();
    }


    // ── Fetch line items for a transaction ───────────────────────────────────

    public async Task<List<TransactionItem>> GetTransactionItemsAsync(string txnId)
    {
        HttpResponseMessage response;

        if (_environment == PayrixEnvironment.Production)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, "/txnItems?page[limit]=50&page[number]=1");
            request.Headers.Add("search", $"txn[eq]={txnId}");
            response = await _client.SendAsync(request).ConfigureAwait(false);
        }
        else
        {
            // Sandbox: try fetching transaction with expanded items
            response = await _client.GetAsync($"/txns/{txnId}?expand[items][]").ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                var txnJson = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                var txnParsed = JsonSerializer.Deserialize<PayrixResponse>(txnJson, JsonOptions);
                var items = txnParsed?.Response?.Data.FirstOrDefault()?.Items;
                if (items is { Count: > 0 })
                    return items;
            }
            // Fallback: query txnItems directly
            response = await _client.GetAsync($"/txnItems?page[limit]=50&page[number]=1&search[txn][eq]={txnId}").ConfigureAwait(false);
        }

        if (!response.IsSuccessStatusCode)
            return [];

        var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        var parsed = JsonSerializer.Deserialize<PayrixItemsResponse>(json, JsonOptions);
        return parsed?.Response?.Data ?? [];
    }

    // ── Date helpers ──────────────────────────────────────────────────────────

    /// <summary>Formats a date as the Payrix created-field format "YYYY-MM-DD HH:MM:SS".</summary>
    private static string DateToPayrixString(DateTime dt, bool endOfDay = false)
        => endOfDay
            ? dt.Date.ToString("yyyy-MM-dd") + " 23:59:59"
            : dt.Date.ToString("yyyy-MM-dd") + " 00:00:00";

    /// <summary>
    /// Builds the date-range search fragment to append to a query-string or header.
    /// Returns e.g. "search[created][gte]=2024-01-01 00:00:00&amp;search[created][lte]=2024-12-31 23:59:59"
    /// or an empty string if both dates are null.
    /// </summary>
    private static string DateQsFragment(DateTime? from, DateTime? to)
    {
        var parts = new List<string>();
        if (from.HasValue)
            parts.Add($"search[created][gte]={Uri.EscapeDataString(DateToPayrixString(from.Value))}");
        if (to.HasValue)
            parts.Add($"search[created][lte]={Uri.EscapeDataString(DateToPayrixString(to.Value, endOfDay: true))}");
        return parts.Count > 0 ? string.Join("&", parts) + "&" : "";
    }

    /// <summary>Same fragment but for the production header (no "search[...]" prefix, comma-separated).</summary>
    private static string DateHeaderFragment(DateTime? from, DateTime? to)
    {
        var parts = new List<string>();
        if (from.HasValue)
            parts.Add($"created[gte]={DateToPayrixString(from.Value)}");
        if (to.HasValue)
            parts.Add($"created[lte]={DateToPayrixString(to.Value, endOfDay: true)}");
        return parts.Count > 0 ? string.Join(",", parts) : "";
    }

    // ── Fetch transactions filtered by payment category ──────────────────────

    /// <summary>
    /// Fetches transactions filtered to a specific payment category.
    /// <paramref name="category"/> is "ach" (type=7,8), "card" (type=1-6), etc.
    /// When email is provided it is used as an additional server-side filter.
    /// All paths do a multi-type fetch + client-side filter so no results are missed.
    /// </summary>
    public async Task<(List<Transaction> transactions, string rawJson, string? error)>
        SearchByPaymentCategoryAsync(string? email, string category, int limit = 20,
                                     DateTime? fromDate = null, DateTime? toDate = null)
    {
        bool IsMatch(Transaction t) => category switch
        {
            "ach"          => t.Type == 7 || t.Type == 8,
            // ACH Return = eCheck Sale (type 7) that came back — status 5 or returned date set
            "achreturn"    => t.Type == 7 && (t.Status == 5 || t.Returned != null),
            // ACH Refund = a separate eCheck Refund transaction (type 8)
            "achrefund"    => t.Type == 8,
            "card"         => t.Type >= 1 && t.Type <= 5,   // types 1–5 = CC; type 6 = Disbursement (excluded)
            // CC Refund = type 5 (Credit_Card_Refund_Transaction) — Payrix sends subject "captured" + type=5
            "ccrefund"     => t.Type == 5,
            // CC Return = type 4 with a returned date (chargeback on a Reverse Auth)
            "ccreturn"     => t.Type == 4 && t.Returned != null,
            "disbursement" => t.Type == 6,
            _              => true
        };

        var fetchLimit = Math.Max(limit * 5, 100);
        bool hasEmail   = !string.IsNullOrWhiteSpace(email);

        string json = "{}";
        // Accumulate de-duped transactions from all API calls
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var all  = new List<Transaction>();

        void Merge(List<Transaction> batch)
        {
            foreach (var t in batch)
                if (t.Id is not null && seen.Add(t.Id))
                    all.Add(t);
        }

        var dateQs  = DateQsFragment(fromDate, toDate);
        var dateHdr = DateHeaderFragment(fromDate, toDate);

        try
        {
            if (_environment == PayrixEnvironment.Production)
            {
                // ── Production: header-based search ──────────────────────────────
                // When searchHeader is null/empty, no search header is added (broad fetch).
                async Task<List<Transaction>> ProdFetch(string? searchHeader = null)
                {
                    var parts = new List<string>();
                    if (!string.IsNullOrEmpty(searchHeader)) parts.Add(searchHeader);
                    if (!string.IsNullOrEmpty(dateHdr))      parts.Add(dateHdr);
                    var req = new HttpRequestMessage(HttpMethod.Get,
                        $"/txns?page[limit]={fetchLimit}&page[number]=1&expand[items][]");
                    if (parts.Count > 0)
                        req.Headers.Add("search", string.Join(",", parts));
                    var resp = await _client.SendAsync(req).ConfigureAwait(false);
                    if (!resp.IsSuccessStatusCode) return [];
                    json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    var p = JsonSerializer.Deserialize<PayrixResponse>(json, JsonOptions);
                    if (p?.Response?.Errors is { Count: > 0 }) return [];
                    return p?.Response?.Data ?? [];
                }

                if (hasEmail)
                {
                    // Try all email header variants Production uses; stop on first hit
                    foreach (var fmt in new[] { $"email[eq]={email}", $"email[equals]={email}", $"email[EQUALS]={email}" })
                    {
                        var batch = await ProdFetch(fmt);
                        Merge(batch);
                        if (all.Count > 0) break;
                    }
                }
                else
                {
                    // No email — broad fetch first, filter client-side.
                    // This is the most reliable path: Production may not honour type[eq] headers.
                    Merge(await ProdFetch());
                }

                // If the broad/email fetch yielded nothing, try type + status header variants.
                if (all.Count == 0)
                {
                    foreach (var typeVal in CategoryTypes(category))
                    {
                        foreach (var op in new[] { "eq", "equals", "EQUALS" })
                        {
                            // Add status filter for ACH Return and CC Return
                            var statusHeader = category switch
                            {
                                "achreturn" => $",status[{op}]=5",
                                "ccreturn"  => $",returned[gt]=0",
                                _           => ""
                            };
                            var batch = await ProdFetch($"type[{op}]={typeVal}{statusHeader}");
                            Merge(batch);
                            if (batch.Count > 0) break;
                        }
                    }
                }
            }
            else
            {
                // ── Sandbox: query-string based search ───────────────────────────
                async Task<List<Transaction>> SboxFetch(string qs)
                {
                    var sep  = string.IsNullOrEmpty(qs) || string.IsNullOrEmpty(dateQs) ? "" : "&";
                    var resp = await _client.GetAsync(
                        $"/txns?{dateQs}{sep}{qs}&page[limit]={fetchLimit}&page[number]=1&expand[items][]").ConfigureAwait(false);
                    if (!resp.IsSuccessStatusCode) return [];
                    json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    return JsonSerializer.Deserialize<PayrixResponse>(json, JsonOptions)?.Response?.Data ?? [];
                }

                // Build the per-category type+status qualifier (e.g. achreturn adds status=5)
                string StatusQs(int typeVal) => category switch
                {
                    "achreturn" => $"search[type][eq]={typeVal}&search[status][eq]=5",
                    "achrefund" => $"search[type][eq]={typeVal}",
                    "ccreturn"  => $"search[type][eq]={typeVal}&search[returned][gt]=0",
                    _           => $"search[type][eq]={typeVal}"
                };

                if (hasEmail)
                {
                    var enc = Uri.EscapeDataString(email!);
                    foreach (var typeVal in CategoryTypes(category))
                        Merge(await SboxFetch($"search[email][EQUALS]={enc}&{StatusQs(typeVal)}"));

                    // Fallback: email only (type/status filter may not work on all sandbox endpoints)
                    if (all.Count == 0)
                        Merge(await SboxFetch($"search[email][EQUALS]={Uri.EscapeDataString(email!)}"));
                }
                else
                {
                    // No email: type + status-specific fetches
                    foreach (var typeVal in CategoryTypes(category))
                        Merge(await SboxFetch(StatusQs(typeVal)));

                    // Broad fallback: no filter — rely on client-side IsMatch
                    if (all.Count == 0)
                        Merge(await SboxFetch(""));
                }
            }
        }
        catch (Exception ex)
        {
            return ([], json, $"Fetch error: {ex.Message}");
        }

        var filtered = all
            .Where(IsMatch)
            .OrderByDescending(t => t.Created)
            .Take(limit)
            .ToList();

        return (filtered, json, filtered.Count == 0 ? $"No {CategoryLabel(category)} transactions found." : null);
    }

    // All Payrix type values to query for a given category
    private static int[] CategoryTypes(string category) => category switch
    {
        "ach"          => [7, 8],
        "achreturn"    => [7],   // eCheck Sale (type 7) that was returned — status 5
        "achrefund"    => [8],   // eCheck Refund (type 8) — a separate refund record
        "card"         => [1, 2, 3, 4, 5],   // type 6 = Disbursement — not a CC txn
        "ccrefund"     => [5],   // Credit_Card_Refund_Transaction = type 5
        "ccreturn"     => [4],
        "disbursement" => [6],
        _              => [1]
    };

    private static int CategoryFirstType(string category) => CategoryTypes(category)[0];

    private static string CategoryLabel(string category) => category switch
    {
        "ach"          => "ACH/eCheck",
        "achreturn"    => "ACH Return (eCheck Returned)",
        "achrefund"    => "ACH Refund (eCheck Refund)",
        "ccrefund"     => "CC Refund",
        "ccreturn"     => "CC Return",
        "disbursement" => "Disbursement",
        _              => "Credit Card"
    };

    // ── Fetch real ACH funded webhook from Payrix (reconstructed from transaction) ──

    /// <summary>
    /// Fetches the most recent funded eCheck Sale transaction (type=7, status=4 / funded≠null)
    /// from Payrix and reconstructs the "Your eCheck sale has been funded" webhook payload
    /// from its fields.  This is more reliable than querying notification logs.
    /// Returns (webhookPayload, rawTxnJson, transaction, error).
    /// </summary>
    public async Task<(string? webhookPayload, string rawJson, Transaction? txn, string? error)>
        GetAchFundedWebhookAsync(string? email = null, int limit = 20,
                                 DateTime? fromDate = null, DateTime? toDate = null)
    {
        string json = "{}";
        List<Transaction> candidates = [];

        var fetchLimit = Math.Max(limit * 5, 100);
        var dateQs  = DateQsFragment(fromDate, toDate);
        var dateHdr = DateHeaderFragment(fromDate, toDate);

        // Strategy A: search type=7 by email (if provided) or broad fetch
        async Task TryFetch(string endpoint, string? searchHeader)
        {
            try
            {
                HttpResponseMessage resp;
                if (searchHeader is not null)
                {
                    var req = new HttpRequestMessage(HttpMethod.Get, endpoint);
                    req.Headers.Add("search", searchHeader);
                    resp = await _client.SendAsync(req).ConfigureAwait(false);
                }
                else
                {
                    resp = await _client.GetAsync(endpoint).ConfigureAwait(false);
                }
                if (!resp.IsSuccessStatusCode) return;
                var j = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                json = j;
                var p = JsonSerializer.Deserialize<PayrixResponse>(j, JsonOptions);
                var data = p?.Response?.Data ?? [];
                foreach (var t in data)
                    if (!candidates.Any(c => c.Id == t.Id))
                        candidates.Add(t);
            }
            catch { /* ignore, try next */ }
        }

        var emailParam = string.IsNullOrEmpty(email) ? null : Uri.EscapeDataString(email);

        var baseEmail = emailParam is null ? "" : $"search[email][EQUALS]={emailParam}&";

        if (_environment == PayrixEnvironment.Production)
        {
            // type=7, all statuses — one broad call + one per-status call to ensure none are skipped
            var hdrBase = string.IsNullOrEmpty(dateHdr) ? "type[eq]=7" : $"type[eq]=7,{dateHdr}";
            var tasks = new List<Task>
            {
                TryFetch($"/txns?page[limit]={fetchLimit}&page[number]=1", hdrBase),
            };
            if (!string.IsNullOrEmpty(email))
            {
                var hdrEmail = string.IsNullOrEmpty(dateHdr) ? $"email[eq]={email}" : $"email[eq]={email},{dateHdr}";
                tasks.Add(TryFetch($"/txns?page[limit]={fetchLimit}&page[number]=1", hdrEmail));
            }
            foreach (var status in new[] { 1, 2, 3, 4 })
                tasks.Add(TryFetch($"/txns?page[limit]={fetchLimit}&page[number]=1",
                                   hdrBase));   // production proxy may need separate hits
            await Task.WhenAll(tasks);
        }
        else
        {
            // Sandbox: broad type=7 + one call per status to catch everything
            var tasks = new List<Task>
            {
                TryFetch($"/txns?{dateQs}{baseEmail}search[type][eq]=7&page[limit]={fetchLimit}&page[number]=1", null),
            };
            foreach (var status in new[] { 1, 2, 3, 4 })
                tasks.Add(TryFetch(
                    $"/txns?{dateQs}{baseEmail}search[type][eq]=7&search[status][eq]={status}&page[limit]={fetchLimit}&page[number]=1",
                    null));
            await Task.WhenAll(tasks);

            // Broad fallback: email only, filter type=7 client-side
            if (candidates.Count == 0 && !string.IsNullOrEmpty(email))
                await TryFetch(
                    $"/txns?{dateQs}search[email][EQUALS]={emailParam}&page[limit]={fetchLimit}&page[number]=1",
                    null);
        }

        // Pick the best candidate: type=7, funded or status=4, newest first
        // Accept any type=7 regardless of status (Approved/Captured/Settled/Funded).
        // Sort: funded first, then settled, then by newest created.
        var funded = candidates
            .Where(t => t.Type == 7)
            .OrderByDescending(t => (
                !string.IsNullOrEmpty(t.Funded) ? 3 :         // fully funded
                (t.Status == 4 || t.Status == 3)  ? 2 :       // settled / captured
                1,                                              // approved — still processable
                t.Created ?? ""))
            .FirstOrDefault();

        if (funded is null)
            return (null, json, null, "No eCheck Sale (type=7) transactions found for this account.");

        var payload = WebhookTestService.BuildAchFundedPayloadFromTransaction(funded);
        return (payload, json, funded, null);
    }

    // ── Search full transactions (optionally filtered by email) ───────────────

    public async Task<(List<Transaction> transactions, string rawJson, string? error)> SearchByEmailAsync(string? email, int limit = 20)
    {
        bool hasEmail = !string.IsNullOrWhiteSpace(email);

        // ── No email supplied: just fetch the latest N transactions ────────────
        if (!hasEmail)
        {
            var endpoint = $"/txns?page[limit]={limit}&page[number]=1&expand[items][]";
            var resp = await _client.GetAsync(endpoint).ConfigureAwait(false);
            var j    = resp.IsSuccessStatusCode ? await resp.Content.ReadAsStringAsync().ConfigureAwait(false) : "{}";
            var p    = JsonSerializer.Deserialize<PayrixResponse>(j, JsonOptions);

            if (!resp.IsSuccessStatusCode)
                return ([], j, $"HTTP {(int)resp.StatusCode}");

            var errors = p?.Response?.Errors;
            if (errors is { Count: > 0 })
                return ([], j, string.Join("  |  ", errors.Select(e => e.Summary)));

            var txns = (p?.Response?.Data ?? [])
                .OrderByDescending(t => t.Created)
                .Take(limit)
                .ToList();
            return (txns, j, null);
        }

        // ── Email supplied: filter by email ────────────────────────────────────
        if (_environment == PayrixEnvironment.Production)
        {
            var formats = new[] { $"email[eq]={email}", $"email[equals]={email}", $"email[EQUALS]={email}" };
            foreach (var searchVal in formats)
            {
                var req = new HttpRequestMessage(HttpMethod.Get, $"/txns?page[limit]={limit}&page[number]=1&expand[items][]");
                req.Headers.Add("search", searchVal);
                var resp = await _client.SendAsync(req).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) continue;

                var j = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                var p = JsonSerializer.Deserialize<PayrixResponse>(j, JsonOptions);
                if (p?.Response?.Errors is { Count: > 0 }) continue;
                var data = p?.Response?.Data ?? [];
                if (data.Count > 0)
                {
                    // Strict: only return transactions that actually match the email
                    var filtered = data
                        .Where(t => string.Equals(t.Email, email, StringComparison.OrdinalIgnoreCase))
                        .OrderByDescending(t => t.Created)
                        .Take(limit)
                        .ToList();
                    return (filtered, j, filtered.Count == 0
                        ? $"No transactions found for {email}."
                        : null);
                }
            }
            // All formats returned 0 — return the last response so Raw JSON tab shows it
            var lastReq = new HttpRequestMessage(HttpMethod.Get, $"/txns?page[limit]={limit}&page[number]=1&expand[items][]");
            lastReq.Headers.Add("search", $"email[eq]={email}");
            var lastResp = await _client.SendAsync(lastReq).ConfigureAwait(false);
            var lastJson = await lastResp.Content.ReadAsStringAsync().ConfigureAwait(false);
            return ([], lastJson, $"No transactions found for {email}.");
        }

        // Sandbox: fetch with email filter then apply strict client-side match
        {
            var endpoint = $"/txns?search[email][EQUALS]={Uri.EscapeDataString(email!)}&page[limit]=100&page[number]=1&expand[items][]";
            var resp = await _client.GetAsync(endpoint).ConfigureAwait(false);
            var j = resp.IsSuccessStatusCode ? await resp.Content.ReadAsStringAsync().ConfigureAwait(false) : "{}";

            if (!resp.IsSuccessStatusCode)
                return ([], j, $"HTTP {(int)resp.StatusCode}: {j}");

            var p = JsonSerializer.Deserialize<PayrixResponse>(j, JsonOptions);

            var errors2 = p?.Response?.Errors;
            if (errors2 is { Count: > 0 })
                return ([], j, string.Join("  |  ", errors2.Select(e => e.Summary)));

            // Strict: only return transactions that actually match the email
            var filtered = (p?.Response?.Data ?? [])
                .Where(t => string.Equals(t.Email, email, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(t => t.Created)
                .Take(limit)
                .ToList();

            return (filtered, j, filtered.Count == 0
                ? $"No transactions found for {email}."
                : null);
        }
    }

    // ── Fetch latest N transaction IDs (optionally filtered by email) ─────────

    public async Task<(string[] txnIds, string? error)> GetLatestTransactionIdsAsync(string? email, int count = 5)
    {
        bool hasEmail = !string.IsNullOrWhiteSpace(email);
        HttpResponseMessage response;

        if (_environment == PayrixEnvironment.Production)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, $"/txns?page[limit]={count}&page[number]=1");
            if (hasEmail)
                request.Headers.Add("search", $"email[equals]={email}");
            response = await _client.SendAsync(request).ConfigureAwait(false);
        }
        else
        {
            var endpoint = hasEmail
                ? $"/txns?search[email][EQUALS]={Uri.EscapeDataString(email!)}&page[limit]={count}&page[number]=1"
                : $"/txns?page[limit]={count}&page[number]=1";
            response = await _client.GetAsync(endpoint).ConfigureAwait(false);
        }

        var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            return ([], $"HTTP {(int)response.StatusCode} {response.StatusCode} — {json}");

        var parsed = JsonSerializer.Deserialize<PayrixResponse>(json, JsonOptions);
        var errors = parsed?.Response?.Errors;

        if (errors is { Count: > 0 })
            return ([], string.Join("  |  ", errors.Select(e => e.Summary)));

        var ids = parsed?.Response?.Data
            .OrderByDescending(t => t.Created)
            .Select(t => t.Id)
            .Where(id => id is not null)
            .Cast<string>()
            .ToArray() ?? [];

        return (ids, null);
    }

    // ── Fetch withdrawal / disbursement webhook ──────────────────────────────

    // ── Fetch disbursement entries (line items) for a disbursement ID ────────────

    /// <summary>
    /// Fetches all disbursement entries (line items) for a given disbursement ID
    /// from /disbursementEntries, with the entry sub-object expanded.
    /// Returns an empty list if none found.
    /// </summary>
    public async Task<List<DisbursementEntry>> GetDisbursementEntriesAsync(string disbId, int limit = 200)
    {
        var escaped = Uri.EscapeDataString(disbId);
        try
        {
            // Strategy 1: sub-resource path — URL-scoped, most reliable.
            var resp1 = await _client.GetAsync(
                $"/disbursements/{escaped}/disbursementEntries?page[limit]={limit}&page[number]=1&expand[entry][]").ConfigureAwait(false);

            if (resp1.IsSuccessStatusCode)
            {
                var j1 = await resp1.Content.ReadAsStringAsync().ConfigureAwait(false);
                var p1 = JsonSerializer.Deserialize<DisbursementEntryResponse>(j1, JsonOptions);
                var d1 = p1?.Response?.Data ?? [];
                if (d1.Count > 0) return d1;
            }

            // Strategy 2: query-string search with "equals" operator + expand
            var resp2 = await _client.GetAsync(
                $"/disbursementEntries?search[disbursement][equals]={escaped}&page[limit]={limit}&page[number]=1&expand[entry][]").ConfigureAwait(false);

            if (resp2.IsSuccessStatusCode)
            {
                var j2 = await resp2.Content.ReadAsStringAsync().ConfigureAwait(false);
                var p2 = JsonSerializer.Deserialize<DisbursementEntryResponse>(j2, JsonOptions);
                var d2 = p2?.Response?.Data ?? [];
                if (d2.Count > 0) return d2;
            }

            // Strategy 3: query-string search with "eq" operator (no expand)
            var resp3 = await _client.GetAsync(
                $"/disbursementEntries?search[disbursement][eq]={escaped}&page[limit]={limit}&page[number]=1").ConfigureAwait(false);

            if (!resp3.IsSuccessStatusCode) return [];

            var j3 = await resp3.Content.ReadAsStringAsync().ConfigureAwait(false);
            var p3 = JsonSerializer.Deserialize<DisbursementEntryResponse>(j3, JsonOptions);
            return p3?.Response?.Data ?? [];
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    /// Given a list of transaction IDs, returns a dictionary mapping each txnId → disbursementId.
    /// Makes a single bulk call to /disbursementEntries (no server-side filter) and matches client-side,
    /// which is more reliable on sandbox than per-txn eventId queries.
    /// </summary>
    public async Task<Dictionary<string, string>> GetDisbursementIdMapAsync(List<string> txnIds)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (txnIds.Count == 0) return map;

        var idSet = new HashSet<string>(txnIds, StringComparer.OrdinalIgnoreCase);

        try
        {
            // Strategy 1: bulk fetch recent entries (no filter) and match client-side
            foreach (var page in new[] { 1, 2, 3 })
            {
                var resp = await _client.GetAsync(
                    $"/disbursementEntries?page[limit]=200&page[number]={page}").ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) break;

                var json   = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                var parsed = JsonSerializer.Deserialize<DisbursementEntryResponse>(json, JsonOptions);
                var data   = parsed?.Response?.Data ?? [];

                foreach (var e in data)
                    if (e.EventId is not null && e.Disbursement is not null && idSet.Contains(e.EventId))
                        map.TryAdd(e.EventId, e.Disbursement);

                // Stop paging once all transactions are resolved or no more data
                if (data.Count < 200 || map.Count == idSet.Count) break;
            }

            // Strategy 2: for any still-unresolved txnIds, try individual eventId queries
            var unresolved = txnIds.Where(id => !map.ContainsKey(id)).ToList();
            foreach (var txnId in unresolved)
            {
                var disbId = await GetDisbursementIdByTxnIdAsync(txnId).ConfigureAwait(false);
                if (disbId is not null)
                    map[txnId] = disbId;
            }
        }
        catch { /* best-effort */ }

        return map;
    }

    /// <summary>
    /// Given a transaction ID, searches /disbursementEntries where eventId == txnId
    /// to find the disbursement that contains that transaction.
    /// Returns the disbursement ID string, or null if not found.
    /// </summary>
    public async Task<string?> GetDisbursementIdByTxnIdAsync(string txnId)
    {
        var escaped = Uri.EscapeDataString(txnId);
        try
        {
            // eventId on a disbursement entry stores the txn/refund/chargeback ID
            foreach (var op in new[] { "equals", "eq" })
            {
                var resp = await _client.GetAsync(
                    $"/disbursementEntries?search[eventId][{op}]={escaped}&page[limit]=5&page[number]=1").ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) continue;

                var json   = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                var parsed = JsonSerializer.Deserialize<DisbursementEntryResponse>(json, JsonOptions);
                // Prefer an exact eventId match; fall back to first entry with a disbursement ID
                var entries = parsed?.Response?.Data ?? [];
                var disbId = entries.FirstOrDefault(e => e.EventId == txnId)?.Disbursement
                          ?? entries.FirstOrDefault(e => !string.IsNullOrEmpty(e.Disbursement))?.Disbursement;
                if (!string.IsNullOrEmpty(disbId)) return disbId;
            }
            return null;
        }
        catch { return null; }
    }

    /// <summary>
    /// Fetches the most recent N disbursement records from /disbursements.
    /// </summary>
    public async Task<List<DisbursementRecord>> GetLatestDisbursementsAsync(int limit = 20)
    {
        try
        {
            var resp = await _client.GetAsync(
                $"/disbursements?page[limit]={limit}&page[number]=1").ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return [];

            var json   = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            var parsed = JsonSerializer.Deserialize<DisbursementResponse>(json, JsonOptions);
            return parsed?.Response?.Data
                       ?.OrderByDescending(r => r.Created)
                       .ToList() ?? [];
        }
        catch { return []; }
    }

    /// <summary>
    /// Fetches a disbursement record from /disbursements AND its line items from
    /// /disbursementEntries, then builds the withdrawal webhook payload with the
    /// entries embedded so Core can process the deposit without a second Payrix call.
    /// If <paramref name="disbId"/> is supplied, fetches that specific record; otherwise returns
    /// the most-recent one (filtered by email if provided).
    /// </summary>
    public async Task<(string? webhookPayload, string rawJson, DisbursementRecord? record, List<DisbursementEntry> entries, string? error)>
        GetWithdrawalWebhookAsync(string? disbId = null, string? email = null, int limit = 20,
                                  DateTime? fromDate = null, DateTime? toDate = null)
    {
        string json = "{}";
        var dateQs = DateQsFragment(fromDate, toDate);
        try
        {
            DisbursementRecord? rec = null;

            if (!string.IsNullOrEmpty(disbId))
            {
                // Try direct ID lookup first
                var resp = await _client.GetAsync($"/disbursements/{disbId}").ConfigureAwait(false);
                json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

                if (resp.IsSuccessStatusCode)
                {
                    var p = JsonSerializer.Deserialize<DisbursementResponse>(json, JsonOptions);
                    rec = p?.Response?.Data?.FirstOrDefault(r => r.Id == disbId)
                       ?? p?.Response?.Data?.FirstOrDefault();
                }

                // Fallback: query-string search
                if (rec is null)
                {
                    var qs = await _client.GetAsync(
                        $"/disbursements?search[id][eq]={disbId}&page[limit]=5&page[number]=1").ConfigureAwait(false);
                    if (qs.IsSuccessStatusCode)
                    {
                        json = await qs.Content.ReadAsStringAsync().ConfigureAwait(false);
                        var p = JsonSerializer.Deserialize<DisbursementResponse>(json, JsonOptions);
                        rec = p?.Response?.Data?.FirstOrDefault();
                    }
                }

                if (rec is null)
                    return (null, json, null, [], $"Disbursement '{disbId}' not found.");
            }
            else
            {
                // Fetch most-recent disbursement (with optional date filter)
                var endpoint = string.IsNullOrEmpty(email)
                    ? $"/disbursements?{dateQs}page[limit]={limit}&page[number]=1"
                    : $"/disbursements?{dateQs}search[email][EQUALS]={Uri.EscapeDataString(email)}&page[limit]={limit}&page[number]=1";

                var resp = await _client.GetAsync(endpoint).ConfigureAwait(false);
                json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

                if (!resp.IsSuccessStatusCode)
                    return (null, json, null, [], $"HTTP {(int)resp.StatusCode} fetching disbursements.");

                var p = JsonSerializer.Deserialize<DisbursementResponse>(json, JsonOptions);
                if (p?.Response?.Errors is { Count: > 0 })
                    return (null, json, null, [], string.Join("; ", p.Response.Errors.Select(e => e.Msg)));

                rec = p?.Response?.Data?.OrderByDescending(r => r.Created).FirstOrDefault();
                if (rec is null)
                    return (null, json, null, [], "No disbursements found.");
            }

            // Fetch line items for this disbursement ID
            var entries = await GetDisbursementEntriesAsync(rec.Id!).ConfigureAwait(false);

            // Build payload with entries embedded
            var wp = WebhookTestService.BuildWithdrawalPayloadFromRecord(rec, entries: entries);
            return (wp, json, rec, entries, null);
        }
        catch (Exception ex)
        {
            return (null, json, null, [], $"Fetch error: {ex.Message}");
        }
    }

    // ── Fetch entity → parse custom (AccountID,CompanyID) ────────────────────

    /// <summary>
    /// Calls GET /entities/{entityId} and returns the parsed AccountID and CompanyID
    /// from the entity's <c>custom</c> field ("AccountID,CompanyID" format).
    /// </summary>
    /// <summary>
    /// Fetches all transactions belonging to a specific Payrix disbursement ID.
    /// Tries /disbursements/{id}/txns first, then falls back to /txns?search[disbursement][id][eq]=.
    /// </summary>
    public async Task<(List<Transaction> transactions, string rawJson, string? error)>
        GetTransactionsByDisbursementAsync(string disbursementId, int limit = 100)
    {
        string json = "{}";
        try
        {
            // Strategy 1: sub-resource path
            var resp1 = await _client.GetAsync(
                $"/disbursements/{Uri.EscapeDataString(disbursementId)}/txns?page[limit]={limit}&page[number]=1&expand[items][]").ConfigureAwait(false);
            json = await resp1.Content.ReadAsStringAsync().ConfigureAwait(false);

            if (resp1.IsSuccessStatusCode)
            {
                var p1 = JsonSerializer.Deserialize<PayrixResponse>(json, JsonOptions);
                if (p1?.Response?.Data is { Count: > 0 })
                    return (p1.Response.Data, json, null);
            }

            // Strategy 2: filter param
            var resp2 = await _client.GetAsync(
                $"/txns?search[disbursement][id][eq]={Uri.EscapeDataString(disbursementId)}&page[limit]={limit}&page[number]=1&expand[items][]").ConfigureAwait(false);
            json = await resp2.Content.ReadAsStringAsync().ConfigureAwait(false);

            if (resp2.IsSuccessStatusCode)
            {
                var p2 = JsonSerializer.Deserialize<PayrixResponse>(json, JsonOptions);
                if (p2?.Response?.Data is { Count: > 0 })
                    return (p2.Response.Data, json, null);
            }

            return ([], json, $"No transactions found for disbursement '{disbursementId}'.");
        }
        catch (Exception ex)
        {
            return ([], json, $"Disbursement fetch error: {ex.Message}");
        }
    }

    /// <summary>
    /// Fetches a single entity record and returns its id, login, custom and raw JSON.
    /// If <paramref name="entityId"/> is null/empty, fetches the most recent entity.
    /// </summary>
    public async Task<(string? id, string? login, string? name, string? custom, string rawJson, string? error)>
        GetEntityAsync(string? entityId = null)
    {
        string json = "{}";
        try
        {
            HttpResponseMessage resp;
            if (!string.IsNullOrWhiteSpace(entityId))
                resp = await _client.GetAsync($"/entities/{Uri.EscapeDataString(entityId)}").ConfigureAwait(false);
            else
                resp = await _client.GetAsync("/entities?page[limit]=1&page[number]=1").ConfigureAwait(false);

            json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

            if (resp.IsSuccessStatusCode)
            {
                var p = JsonSerializer.Deserialize<EntityResponse>(json, JsonOptions);
                var rec = p?.Response?.Data?.FirstOrDefault();
                if (rec is not null)
                    return (rec.Id, rec.Login, rec.Name, rec.Custom, json, null);
            }

            return (null, null, null, null, json, $"Entity not found (HTTP {(int)resp.StatusCode}).");
        }
        catch (Exception ex)
        {
            return (null, null, null, null, json, $"Entity fetch error: {ex.Message}");
        }
    }

    public async Task<(string? accountId, string? companyId, string? entityName, string rawJson, string? error)>
        GetEntityCustomAsync(string entityId)
    {
        string json = "{}";
        try
        {
            // Strategy 1: direct path
            var resp = await _client.GetAsync($"/entities/{Uri.EscapeDataString(entityId)}").ConfigureAwait(false);
            json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

            if (resp.IsSuccessStatusCode)
            {
                var p = JsonSerializer.Deserialize<EntityResponse>(json, JsonOptions);
                var rec = p?.Response?.Data?.FirstOrDefault();
                if (rec is not null)
                {
                    var (accountId, companyId) = rec.ParseCustom();
                    return (accountId, companyId, rec.Name, json, null);
                }
            }

            // Strategy 2: query-string search
            var qs = await _client.GetAsync(
                $"/entities?search[id][eq]={Uri.EscapeDataString(entityId)}&page[limit]=1&page[number]=1").ConfigureAwait(false);
            json = await qs.Content.ReadAsStringAsync().ConfigureAwait(false);

            if (qs.IsSuccessStatusCode)
            {
                var p2 = JsonSerializer.Deserialize<EntityResponse>(json, JsonOptions);
                var rec2 = p2?.Response?.Data?.FirstOrDefault();
                if (rec2 is not null)
                {
                    var (accountId, companyId) = rec2.ParseCustom();
                    return (accountId, companyId, rec2.Name, json, null);
                }
            }

            return (null, null, null, json, $"Entity {entityId} not found or has no custom field.");
        }
        catch (Exception ex)
        {
            return (null, null, null, json, $"Entity fetch error: {ex.Message}");
        }
    }

    // ── Merchants ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Fetches ALL transactions in the given date range, auto-paginating through all pages.
    /// Used by the Reports tab to work on user-supplied dates rather than the pre-loaded cache.
    /// </summary>
    public async Task<(List<Transaction> transactions, string? error)>
        FetchTransactionsForReportAsync(DateTime? from, DateTime? to,
                                        string? merchantId = null,
                                        int pageSize = 200,
                                        IProgress<string>? progress = null)
    {
        var all  = new List<Transaction>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var lastError = (string?)null;
        int page = 1;

        var dateQs  = DateQsFragment(from, to);
        var dateHdr = DateHeaderFragment(from, to);

        // Merchant ID filter fragments
        var merchQs  = string.IsNullOrWhiteSpace(merchantId) ? "" :
                       $"search[merchant][eq]={Uri.EscapeDataString(merchantId.Trim())}&";
        var merchHdr = string.IsNullOrWhiteSpace(merchantId) ? "" :
                       $"merchant[eq]={merchantId.Trim()}";

        try
        {
            while (true)
            {
                progress?.Report($"Fetching page {page}… ({all.Count} so far)");

                List<Transaction> batch;

                if (_environment == PayrixEnvironment.Production)
                {
                    var req = new HttpRequestMessage(HttpMethod.Get,
                        $"/txns?page[limit]={pageSize}&page[number]={page}&expand[items][]");

                    // Combine date + merchant into one search header
                    var hdrParts = new List<string>();
                    if (!string.IsNullOrEmpty(dateHdr))  hdrParts.Add(dateHdr);
                    if (!string.IsNullOrEmpty(merchHdr)) hdrParts.Add(merchHdr);
                    if (hdrParts.Count > 0)
                        req.Headers.Add("search", string.Join(",", hdrParts));

                    var resp = await _client.SendAsync(req).ConfigureAwait(false);
                    if (!resp.IsSuccessStatusCode)
                    {
                        lastError = $"HTTP {(int)resp.StatusCode} on page {page}";
                        break;
                    }
                    var j = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    batch = JsonSerializer.Deserialize<PayrixResponse>(j, JsonOptions)?.Response?.Data ?? [];
                }
                else
                {
                    // Sandbox: date range + merchant in query-string
                    var url = $"/txns?{dateQs}{merchQs}page[limit]={pageSize}&page[number]={page}&expand[items][]";
                    var resp = await _client.GetAsync(url).ConfigureAwait(false);
                    if (!resp.IsSuccessStatusCode)
                    {
                        lastError = $"HTTP {(int)resp.StatusCode} on page {page}";
                        break;
                    }
                    var j = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    batch = JsonSerializer.Deserialize<PayrixResponse>(j, JsonOptions)?.Response?.Data ?? [];
                }

                foreach (var t in batch)
                    if (t.Id is not null && seen.Add(t.Id))
                        all.Add(t);

                if (batch.Count < pageSize) break;   // last page
                page++;
            }
        }
        catch (Exception ex)
        {
            lastError = ex.Message;
        }

        return (all, lastError);
    }

    /// <summary>
    /// Fetches up to <paramref name="limit"/> merchants from /merchants.
    /// Returns (list, rawJson, error).
    /// </summary>
    public async Task<(List<Merchant> merchants, string rawJson, string? error)>
        GetMerchantsAsync(int pageSize = 100)
    {
        var lastJson = "{}";
        try
        {
            var all    = new List<Merchant>();
            int page   = 1;
            int total  = int.MaxValue; // will be set after first page

            while (all.Count < total)
            {
                var resp = await _client.GetAsync(
                    $"/merchants?page[limit]={pageSize}&page[number]={page}").ConfigureAwait(false);
                lastJson = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

                if (!resp.IsSuccessStatusCode)
                    return (all.Count > 0 ? all : [], lastJson,
                            $"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase} (page {page})");

                var parsed = JsonSerializer.Deserialize<PayrixMerchantResponse>(lastJson, JsonOptions);
                var data   = parsed?.Response?.Data ?? [];
                total      = parsed?.Response?.Total ?? data.Count; // use server total on first page

                if (data.Count == 0) break; // no more results
                all.AddRange(data);

                if (all.Count >= total || data.Count < pageSize) break; // fetched everything
                page++;
            }

            return (all, lastJson, null);
        }
        catch (Exception ex)
        {
            return ([], lastJson, $"Merchant fetch error: {ex.Message}");
        }
    }

    /// <summary>Gets a single merchant by ID.</summary>
    public async Task<(Merchant? merchant, string rawJson, string? error)>
        GetMerchantAsync(string merchantId)
    {
        var json = "{}";
        try
        {
            var resp = await _client.GetAsync(
                $"/merchants/{Uri.EscapeDataString(merchantId)}").ConfigureAwait(false);
            json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode)
                return (null, json, $"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}");

            // Response may be wrapped: { response: { data: [...] } } or direct object
            try
            {
                var wrapped = JsonSerializer.Deserialize<PayrixMerchantResponse>(json, JsonOptions);
                var m = wrapped?.Response?.Data?.FirstOrDefault();
                if (m != null) return (m, json, null);
            }
            catch { /* fall through to direct */ }

            var direct = JsonSerializer.Deserialize<Merchant>(json, JsonOptions);
            return (direct, json, direct == null ? "Could not parse merchant." : null);
        }
        catch (Exception ex)
        {
            return (null, json, $"Merchant fetch error: {ex.Message}");
        }
    }

    /// <summary>
    /// Sets a merchant's status via PUT /merchants/{id} (falls back to PATCH on 405).
    /// status: 1 = Active, 2 = Inactive, 3 = Suspended.
    /// Returns (updatedMerchant, rawJson, error).
    /// </summary>
    public async Task<(Merchant? merchant, string rawJson, string? error)>
        UpdateMerchantStatusAsync(string merchantId, int newStatus)
    {
        var json = "{}";
        try
        {
            var url = $"/merchants/{Uri.EscapeDataString(merchantId)}";
            var payload = JsonSerializer.Serialize(new { status = newStatus });

            StringContent MakeBody() => new(payload, System.Text.Encoding.UTF8, "application/json");

            // Try PUT first; fall back to PATCH if the server returns 405 Method Not Allowed
            var resp = await _client.PutAsync(url, MakeBody()).ConfigureAwait(false);
            if (resp.StatusCode == System.Net.HttpStatusCode.MethodNotAllowed)
            {
                var patchReq = new HttpRequestMessage(HttpMethod.Patch, url) { Content = MakeBody() };
                resp = await _client.SendAsync(patchReq).ConfigureAwait(false);
            }

            json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode)
            {
                // Surface the Payrix error body so the caller can show it
                string detail = ExtractPayrixError(json)
                                ?? $"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}";
                return (null, json, detail);
            }

            // Parse wrapped { response: { data: [...] } } or direct object
            try
            {
                var wrapped = JsonSerializer.Deserialize<PayrixMerchantResponse>(json, JsonOptions);
                var m = wrapped?.Response?.Data?.FirstOrDefault();
                if (m != null) return (m, json, null);
            }
            catch { /* fall through to direct */ }

            var direct = JsonSerializer.Deserialize<Merchant>(json, JsonOptions);
            return (direct, json, direct == null ? "Could not parse updated merchant." : null);
        }
        catch (Exception ex)
        {
            return (null, json, $"Update error: {ex.Message}");
        }
    }

    /// <summary>
    /// Pulls ALL error messages out of a Payrix response body, including the field name.
    /// Returns a formatted string like "[token] The referenced resource does not exist (code 5)"
    /// or null if no errors found / JSON is unparseable.
    /// </summary>
    private static string? ExtractPayrixError(string json)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);

            static string FormatErr(System.Text.Json.JsonElement e)
            {
                var field = e.TryGetProperty("field", out var f) ? f.GetString() : null;
                var msg   = e.TryGetProperty("msg",   out var m) ? m.GetString() : null;
                var code  = e.TryGetProperty("code",  out var c) ? (int?)c.GetInt32() : null;
                var sb = new System.Text.StringBuilder();
                if (!string.IsNullOrEmpty(field)) sb.Append($"[{field}] ");
                sb.Append(!string.IsNullOrEmpty(msg) ? msg : "(unknown error)");
                if (code is > 0) sb.Append($" (code {code})");
                return sb.ToString();
            }

            // { "response": { "errors": [ ... ] } }
            if (doc.RootElement.TryGetProperty("response", out var resp) &&
                resp.TryGetProperty("errors", out var errs) &&
                errs.GetArrayLength() > 0)
            {
                return string.Join("  |  ", errs.EnumerateArray().Select(FormatErr));
            }
            // { "errors": [ ... ] }  (flat form)
            if (doc.RootElement.TryGetProperty("errors", out var errs2) &&
                errs2.GetArrayLength() > 0)
            {
                return string.Join("  |  ", errs2.EnumerateArray().Select(FormatErr));
            }
        }
        catch { /* ignore parse failures */ }
        return null;
    }

    // ── Per-request API key injection ─────────────────────────────────────────
    private sealed class ApiKeyHandler : DelegatingHandler
    {
        private readonly string _apiKey;
        public ApiKeyHandler(string apiKey, string baseUrl)
            : base(new SocketsHttpHandler
            {
                PooledConnectionLifetime    = TimeSpan.FromMinutes(10),
                PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
                MaxConnectionsPerServer     = 10,
                SslOptions = new System.Net.Security.SslClientAuthenticationOptions
                {
                    RemoteCertificateValidationCallback = (_, _, _, _) => true
                },
                AutomaticDecompression =
                    System.Net.DecompressionMethods.GZip |
                    System.Net.DecompressionMethods.Deflate |
                    System.Net.DecompressionMethods.Brotli
            })
        {
            _apiKey = apiKey;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            request.Headers.TryAddWithoutValidation("APIKEY", _apiKey);
            return base.SendAsync(request, ct);
        }
    }
}
