using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace PayrixLauncher.Services;

/// <summary>
/// Authenticates against BQE Core Admin Portal API.
///
/// Tries all known endpoint variants automatically and returns raw response for debugging.
/// </summary>
public static class BqeAuthService
{
    // ResponseType constants from BQECoreSharedLib.ResponseType
    private const int RT_OK                 = 0;
    private const int RT_INVALID_CREDS      = 1;
    private const int RT_INACTIVE_ACCOUNT   = 2;
    private const int RT_MULTIPLE_COMPANIES = 3;
    private const int RT_NO_COMPANY         = 4;
    private const int RT_TRIAL_EXPIRED      = 6;
    private const int RT_SUB_EXPIRED        = 7;
    private const int RT_2FA_REQUIRED       = 12;

    // All known endpoint paths — tried in order until one returns a parseable response
    private static readonly string[] Endpoints =
    [
        "/BQECoreAdminPortalAPI/API/Account/ValidateUser",
        "/BQECoreAdminPortalApi/API/Account/ValidateUser",
        "/coreapi/api/Account/ValidateUser",
        "/api/Account/ValidateUser",
        "/API/Account/ValidateUser",
        "/coreapi/api/Account/Login",
        "/api/auth/login",
    ];

    // ── Public API ────────────────────────────────────────────────────────

    public static Task<BqeLoginResult> LoginAsync(
        string baseUrl, string email, string password, string? companyId = null)
        => TryAllEndpointsAsync(baseUrl, email, password, companyId);

    public static Task<BqeLoginResult> LoginWithCompanyAsync(
        string baseUrl, string email, string password, string companyId)
        => LoginAsync(baseUrl, email, password, companyId);

    // ── Core logic ────────────────────────────────────────────────────────

    private static async Task<BqeLoginResult> TryAllEndpointsAsync(
        string baseUrl, string email, string password, string? companyId)
    {
        baseUrl = NormalizeBaseUrl(baseUrl);
        if (string.IsNullOrWhiteSpace(baseUrl))
            return BqeLoginResult.Fail("BQE Core URL is required.", "");

        using var client = MakeClient(baseUrl);

        object body = companyId != null
            ? (object)new { Email = email, Password = password, Company_ID = Guid.Parse(companyId) }
            : new { Email = email, Password = password };

        var log = new System.Text.StringBuilder();
        log.AppendLine($"Base URL (normalised): {baseUrl}");
        log.AppendLine($"Body: {{ Email={email}, Password=*** }}");
        log.AppendLine();

        foreach (var path in Endpoints)
        {
            var fullUrl = baseUrl.TrimEnd('/') + path;
            log.AppendLine($"→ POST {fullUrl}");
            var (statusCode, json, connErr) = await PostRawAsync(client, path, body);

            if (connErr != null)
            {
                log.AppendLine($"  ✗ {connErr}");
                // Any network failure on first attempt = server unreachable, abort
                if (path == Endpoints[0])
                {
                    log.AppendLine("  (aborting — server unreachable on first attempt)");
                    return BqeLoginResult.Fail(connErr, log.ToString());
                }
                continue;
            }

            log.AppendLine($"  HTTP {statusCode}");
            if (json != null)
                log.AppendLine($"  {json[..Math.Min(json.Length, 400)]}");

            // 404/405 = wrong path, try next
            if (statusCode == 404 || statusCode == 405) { log.AppendLine("  (not found, trying next)"); continue; }

            // 409 = endpoint found but BQE threw an exception (e.g. wrong password, account issue)
            // Stop trying — this IS the right endpoint, don't mask the real error
            if (statusCode == 409 || statusCode == 401 || statusCode == 403)
            {
                var msg = TryExtractMessage(json) ?? $"HTTP {statusCode}";
                log.AppendLine($"  ✗ Auth failure: {msg}");
                return BqeLoginResult.Fail(msg, log.ToString());
            }

            // Got a 2xx/3xx response — try to parse it
            var result = ParseLoginResponse(json, email);
            result.RawLog = log.ToString();

            if (result.Success || result.Companies != null ||
                result.Error?.Contains("password", StringComparison.OrdinalIgnoreCase) == true ||
                result.Error?.Contains("inactive",  StringComparison.OrdinalIgnoreCase) == true ||
                result.Error?.Contains("expired",   StringComparison.OrdinalIgnoreCase) == true ||
                result.Error?.Contains("2FA",       StringComparison.OrdinalIgnoreCase) == true)
            {
                return result;
            }

            log.AppendLine($"  (unparseable or generic error — trying next endpoint)");
        }

        return BqeLoginResult.Fail(
            "Login failed — no endpoint responded correctly.\n" +
            "Check the URL and ensure BQECoreAdminPortalAPI is running.",
            log.ToString());
    }

    // ── Response parsing ──────────────────────────────────────────────────

    private static BqeLoginResult ParseLoginResponse(string? json, string email)
    {
        if (string.IsNullOrWhiteSpace(json))
            return BqeLoginResult.Fail("Empty response.", "");

        try
        {
            var doc  = JsonDocument.Parse(json!);
            var root = doc.RootElement;

            // Read ResponseType (defaults to 1=invalid if missing)
            int rt = 1;
            if (root.TryGetProperty("ResponseType", out var rtp) && rtp.TryGetInt32(out var rv))
                rt = rv;

            var message = GetStr(root, "Message", "message") ?? "";

            switch (rt)
            {
                case RT_OK:
                    var token = GetStr(root, "BusinessToken", "Token", "token",
                                              "access_token", "AccessToken");
                    if (string.IsNullOrWhiteSpace(token))
                        return BqeLoginResult.Fail(
                            "Login returned OK but no token found in response.", "");
                    return BqeLoginResult.Ok(token!, ExtractUserName(root, email));

                case RT_MULTIPLE_COMPANIES:
                    var companies = ExtractCompanies(root);
                    return BqeLoginResult.MultiCompany(companies);

                case RT_INVALID_CREDS:
                    return BqeLoginResult.Fail("Invalid email or password.", "");

                case RT_INACTIVE_ACCOUNT:
                    return BqeLoginResult.Fail("Account not activated — check your email.", "");

                case RT_NO_COMPANY:
                    return BqeLoginResult.Fail("No company file found for this account.", "");

                case RT_TRIAL_EXPIRED:
                    return BqeLoginResult.Fail("Trial has expired.", "");

                case RT_SUB_EXPIRED:
                    return BqeLoginResult.Fail("Subscription has expired.", "");

                case RT_2FA_REQUIRED:
                    return BqeLoginResult.Fail("2FA required — not supported here.", "");

                default:
                    return BqeLoginResult.Fail(
                        string.IsNullOrWhiteSpace(message)
                            ? $"Login failed (ResponseType={rt})." : message, "");
            }
        }
        catch
        {
            // Not a Core-format JSON — this endpoint returned something else
            return BqeLoginResult.Fail("Unrecognised response format.", "");
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static async Task<(int statusCode, string? json, string? connError)>
        PostRawAsync(HttpClient client, string path, object body)
    {
        try
        {
            var content = new StringContent(
                JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            client.DefaultRequestHeaders.Accept.Clear();
            client.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/json"));

            var resp = await client.PostAsync(path, content).ConfigureAwait(false);
            var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            return ((int)resp.StatusCode, json, null);
        }
        catch (HttpRequestException ex) { return (0, null, $"Connection failed: {ex.Message}"); }
        catch (TaskCanceledException)   { return (0, null, "Request timed out."); }
        catch (Exception ex)            { return (0, null, ex.Message); }
    }

    private static HttpClient MakeClient(string baseUrl)
    {
        var inner = ProxyConfig.MakeHandler();
        var handler = new LoggingHandler("BQE Auth", inner);
        return new HttpClient(handler)
        {
            BaseAddress = new Uri(baseUrl),
            Timeout      = TimeSpan.FromSeconds(20)
        };
    }

    private static string? GetStr(JsonElement el, params string[] names)
    {
        foreach (var n in names)
            if (el.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String)
                return v.GetString();
        return null;
    }

    private static string ExtractUserName(JsonElement root, string email)
    {
        if (root.TryGetProperty("UserInfo", out var ui))
        {
            var full = GetStr(ui, "FullName", "fullName", "DisplayName", "Name");
            if (!string.IsNullOrWhiteSpace(full)) return full;
            var fn = GetStr(ui, "FirstName", "firstName") ?? "";
            var ln = GetStr(ui, "LastName",  "lastName")  ?? "";
            var nm = $"{fn} {ln}".Trim();
            if (!string.IsNullOrWhiteSpace(nm)) return nm;
        }
        var local = email.Split('@')[0];
        return string.Join(" ", local.Split('.').Select(p =>
            p.Length > 0 ? char.ToUpper(p[0]) + p[1..].ToLower() : p));
    }

    private static List<BqeCompany> ExtractCompanies(JsonElement root)
    {
        var list = new List<BqeCompany>();
        if (!root.TryGetProperty("CompanyFiles", out var arr) ||
            arr.ValueKind != JsonValueKind.Array) return list;
        foreach (var item in arr.EnumerateArray())
        {
            var id   = GetStr(item, "Company_ID", "CompanyId", "ID") ?? "";
            var name = GetStr(item, "CompanyName", "Name",     "companyName") ?? id;
            if (!string.IsNullOrEmpty(id))
                list.Add(new BqeCompany { Id = id, Name = name });
        }
        return list;
    }

    private static string? TryExtractMessage(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var doc = System.Text.Json.JsonDocument.Parse(json!);
            return GetStr(doc.RootElement,
                "Message", "message", "error", "Error",
                "title",   "Title",   "detail", "Detail",
                "DetailMessage");
        }
        catch { return null; }
    }

    private static string NormalizeBaseUrl(string url)
    {
        url = url.Trim().TrimEnd('/');

        // Strip any known app/api sub-paths so we always work from the host root
        foreach (var suffix in new[]
        {
            "/BQECoreAdminPortalAPI/API/Account/ValidateUser",
            "/BQECoreAdminPortalAPI/API/Account",
            "/BQECoreAdminPortalAPI/API",
            "/BQECoreAdminPortalAPI",
            "/BQECoreAdminPortalWebApp",
            "/BQECoreWebApp",           // ← user entered web-app URL, strip it
            "/coreapi/api/Account/ValidateUser",
            "/coreapi/api/Account",
            "/coreapi/api",
            "/coreapi",
        })
        {
            if (url.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return url[..^suffix.Length];
        }
        return url;
    }
}

// ── Result model ──────────────────────────────────────────────────────────────

public class BqeLoginResult
{
    public bool              Success   { get; private set; }
    public string?           Token     { get; private set; }
    public string?           UserName  { get; private set; }
    public string?           Error     { get; private set; }
    public List<BqeCompany>? Companies { get; private set; }
    public string            RawLog    { get; set; } = "";

    public static BqeLoginResult Ok(string token, string userName) =>
        new() { Success = true, Token = token, UserName = userName };

    public static BqeLoginResult MultiCompany(List<BqeCompany> companies) =>
        new() { Companies = companies };

    public static BqeLoginResult Fail(string error, string log) =>
        new() { Error = error, RawLog = log };
}

public class BqeCompany
{
    public string Id   { get; set; } = "";
    public string Name { get; set; } = "";
    public override string ToString() => Name;
}
