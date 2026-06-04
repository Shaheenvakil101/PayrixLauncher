using Microsoft.Data.SqlClient;

namespace PayrixLauncher.Services;

/// <summary>
/// Queries the BQECoreHost payment-service database to resolve a Payrix
/// transaction ID → Core AccountID + CompanyID.
/// </summary>
public static class HostDbService
{
    /// <summary>
    /// Given a Payrix transaction/disbursement ID, returns the Core CompanyID
    /// and AccountID stored in the host DB.
    /// Returns (null, null, errorMessage) if the connection string is empty,
    /// the query fails, or no matching row is found.
    /// </summary>
    public static async Task<(string? companyId, string? accountId, string? error)>
        GetIdsForTransactionAsync(string? connectionString, string txnId)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            return (null, null, null);   // feature disabled — no error shown

        if (string.IsNullOrWhiteSpace(txnId))
            return (null, null, "No transaction ID to look up.");

        const string sql = @"
SELECT TOP 1
    CAST(c.CoreCompany_ID AS NVARCHAR(50)) AS CompanyID
FROM ServiceEntity se
INNER JOIN Company c ON se.Company_ID = c.ID
WHERE se.RequestID = @txnId
ORDER BY se.CreatedOn DESC";

        connectionString = SanitizeConnectionString(connectionString);

        // Microsoft.Data.SqlClient v5+ requires TrustServerCertificate for local/dev servers
        if (!connectionString.Contains("TrustServerCertificate", StringComparison.OrdinalIgnoreCase))
            connectionString = connectionString.TrimEnd(';') + ";TrustServerCertificate=True";

        try
        {
            await using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync();

            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.Add(new SqlParameter("@txnId", System.Data.SqlDbType.NVarChar, 100)
                { Value = txnId });

            using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                var companyId = reader["CompanyID"]?.ToString();
                return (companyId, null, null);   // AccountID not in payment-service DB
            }

            return (null, null, "No record found in host DB for this transaction.");
        }
        catch (Exception ex)
        {
            return (null, null, $"Host DB error: {ex.Message}");
        }
    }

    /// <summary>
    /// Queries the BQECoreHost MAIN DB to resolve a CoreCompany_ID → Core AccountID.
    /// BQECoreHost schema:
    ///   Company.ID  = CoreCompany_ID (from payment-service DB)
    ///   Account.ID  = Core AccountID
    ///   AccountCompany joins Company ↔ Account (if present), otherwise try Account.Company_ID direct.
    /// Returns (accountId, errorMessage) — error is non-null only when connection string is set but query fails.
    /// </summary>
    public static async Task<(string? accountId, string? via, string? error)> GetAccountIdAsync(
        string? connectionString, string coreCompanyId, string? email = null)
    {
        if (string.IsNullOrWhiteSpace(connectionString) || string.IsNullOrWhiteSpace(coreCompanyId))
            return (null, null, null);   // feature disabled

        connectionString = SanitizeConnectionString(connectionString);

        if (!connectionString.Contains("TrustServerCertificate", StringComparison.OrdinalIgnoreCase))
            connectionString = connectionString.TrimEnd(';') + ";TrustServerCertificate=True";

        // The @companyId parameter is the CoreCompany_ID value from the payment-service DB,
        // which equals Company.ID in the main BQECoreHost DB.
        //
        // Strategy 1: AccountCompany join table  (ac.Account_ID)
        // Strategy 2: Company has Account_ID column pointing to Account
        // Strategy 3: Account has Company_Id or CompanyId column pointing to Company
        // Build strategy list — email-based lookup first (most reliable)
        var strategyList = new List<(string sql, string label, bool useEmail)>();

        if (!string.IsNullOrWhiteSpace(email))
        {
            strategyList.Add((@"SELECT TOP 1 CAST(a.ID AS NVARCHAR(50)) AS AccountID
FROM   Account a
WHERE  a.Email = @email", "Account.Email", true));
        }

        // CompanyID-based fallbacks
        strategyList.AddRange(new[]
        {
            (@"SELECT TOP 1 CAST(a.ID AS NVARCHAR(50)) AS AccountID
FROM   Account a
INNER JOIN AccountCompany ac ON ac.Account_ID = a.ID
WHERE  ac.Company_ID = @companyId", "Account+AccountCompany", false),

            (@"SELECT TOP 1 CAST(ac.Account_ID AS NVARCHAR(50)) AS AccountID
FROM   AccountCompany ac
WHERE  ac.Company_ID = @companyId", "AccountCompany.Company_ID", false),

            (@"SELECT TOP 1 CAST(c.Account_ID AS NVARCHAR(50)) AS AccountID
FROM   Company c
WHERE  c.ID = @companyId", "Company.Account_ID", false),
        });

        if (!Guid.TryParse(coreCompanyId, out var companyGuid))
            return (null, null, $"Invalid CompanyID format: {coreCompanyId}");

        var errors = new System.Text.StringBuilder();
        try
        {
            await using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync();

            foreach (var (sql, label, useEmail) in strategyList)
            {
                try
                {
                    using var cmd = new SqlCommand(sql, conn);
                    if (useEmail)
                        cmd.Parameters.Add(new SqlParameter("@email", System.Data.SqlDbType.NVarChar, 200)
                            { Value = email! });
                    else
                        cmd.Parameters.Add(new SqlParameter("@companyId", System.Data.SqlDbType.UniqueIdentifier)
                            { Value = companyGuid });

                    var result = await cmd.ExecuteScalarAsync();
                    if (result is not null && result != DBNull.Value)
                        return (result.ToString(), label, null);
                    errors.Append($"[{label}: no row] ");
                }
                catch (SqlException ex)
                {
                    errors.Append($"[{label}: {ex.Message.Split('\n')[0].Trim()}] ");
                }
            }

            return (null, null, $"AccountID not found — tried: {errors}");
        }
        catch (Exception ex)
        {
            var preview = connectionString.Length > 60 ? connectionString[..60] + "…" : connectionString;
            return (null, null, $"Main DB error: {ex.Message}  |  ConnStr: {preview}");
        }
    }

    /// <summary>
    /// Given a CoreCompany_ID (GUID), returns the company Name from the BQECoreHost main DB.
    /// </summary>
    public static async Task<(string? companyName, string? error)> GetCompanyNameAsync(
        string? connectionString, string coreCompanyId)
    {
        if (string.IsNullOrWhiteSpace(connectionString) || string.IsNullOrWhiteSpace(coreCompanyId))
            return (null, null);

        if (!Guid.TryParse(coreCompanyId, out var companyGuid))
            return (null, $"Invalid CompanyID: {coreCompanyId}");

        connectionString = SanitizeConnectionString(connectionString);
        if (!connectionString.Contains("TrustServerCertificate", StringComparison.OrdinalIgnoreCase))
            connectionString = connectionString.TrimEnd(';') + ";TrustServerCertificate=True";

        const string sql = @"SELECT TOP 1 Name FROM Company WHERE ID = @companyId";

        try
        {
            await using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync();
            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.Add(new SqlParameter("@companyId", System.Data.SqlDbType.UniqueIdentifier)
                { Value = companyGuid });
            var result = await cmd.ExecuteScalarAsync();
            return result is not null && result != DBNull.Value
                ? (result.ToString(), null)
                : (null, "Company not found.");
        }
        catch (Exception ex)
        {
            return (null, $"Host DB error: {ex.Message}");
        }
    }

    /// <summary>
    /// Queries the BQECoreHost MAIN DB by email to return AccountID + CompanyID in one shot.
    /// Strategy: Account.Email → AccountID, then AccountCompany → CompanyID.
    /// </summary>
    public static async Task<(string? accountId, string? companyId, string? error)>
        GetAccountAndCompanyByEmailAsync(string? mainConnStr, string email)
    {
        if (string.IsNullOrWhiteSpace(mainConnStr) || string.IsNullOrWhiteSpace(email))
            return (null, null, "Connection string or email is empty.");

        mainConnStr = SanitizeConnectionString(mainConnStr);
        if (!mainConnStr.Contains("TrustServerCertificate", StringComparison.OrdinalIgnoreCase))
            mainConnStr = mainConnStr.TrimEnd(';') + ";TrustServerCertificate=True";

        try
        {
            await using var conn = new SqlConnection(mainConnStr);
            await conn.OpenAsync();

            // Step 1 — AccountID from Account.Email
            string? accountId = null;
            const string sqlAccount = @"SELECT TOP 1 CAST(ID AS NVARCHAR(50)) FROM Account WHERE Email = @email";
            using (var cmd = new SqlCommand(sqlAccount, conn))
            {
                cmd.Parameters.Add(new SqlParameter("@email", System.Data.SqlDbType.NVarChar, 200) { Value = email });
                var res = await cmd.ExecuteScalarAsync();
                if (res is not null && res != DBNull.Value)
                    accountId = res.ToString();
            }

            if (accountId is null)
                return (null, null, $"No Account found for email: {email}");

            // Step 2 — CompanyID from AccountCompany or Company.Account_ID
            string? companyId = null;
            var companyStrategies = new[]
            {
                (@"SELECT TOP 1 CAST(Company_ID AS NVARCHAR(50)) FROM AccountCompany WHERE Account_ID = @accountId",
                 "AccountCompany"),
                (@"SELECT TOP 1 CAST(ID AS NVARCHAR(50)) FROM Company WHERE Account_ID = @accountId",
                 "Company.Account_ID"),
            };

            if (Guid.TryParse(accountId, out var accountGuid))
            {
                foreach (var (sql, _) in companyStrategies)
                {
                    using var cmd = new SqlCommand(sql, conn);
                    cmd.Parameters.Add(new SqlParameter("@accountId", System.Data.SqlDbType.UniqueIdentifier) { Value = accountGuid });
                    var res = await cmd.ExecuteScalarAsync();
                    if (res is not null && res != DBNull.Value)
                    {
                        companyId = res.ToString();
                        break;
                    }
                }
            }

            return (accountId, companyId, null);
        }
        catch (Exception ex)
        {
            return (null, null, $"DB error: {ex.Message}");
        }
    }

    /// <summary>
    /// Queries the BQECoreHost MAIN DB by email and returns ALL (AccountID, CompanyID, CompanyName)
    /// tuples associated with that email — one per company the account is linked to via AccountCompany.
    /// Falls back to Company.Account_ID if AccountCompany has no rows for the account.
    /// </summary>
    public static async Task<(List<(string accountId, string companyId, string companyName)> results, string? error)>
        GetAllAccountCompaniesByEmailAsync(string? mainConnStr, string email)
    {
        var empty = new List<(string, string, string)>();

        if (string.IsNullOrWhiteSpace(mainConnStr) || string.IsNullOrWhiteSpace(email))
            return (empty, "Connection string or email is empty.");

        mainConnStr = SanitizeConnectionString(mainConnStr);
        if (!mainConnStr.Contains("TrustServerCertificate", StringComparison.OrdinalIgnoreCase))
            mainConnStr = mainConnStr.TrimEnd(';') + ";TrustServerCertificate=True";

        try
        {
            await using var conn = new SqlConnection(mainConnStr);
            await conn.OpenAsync();

            // Step 1 — get ALL AccountIDs matching this email (normally one, but handle duplicates)
            var accountIds = new List<string>();
            const string sqlAccounts = @"SELECT CAST(ID AS NVARCHAR(50)) FROM Account WHERE Email = @email";
            using (var cmd = new SqlCommand(sqlAccounts, conn))
            {
                cmd.Parameters.Add(new SqlParameter("@email", System.Data.SqlDbType.NVarChar, 200) { Value = email });
                using var rdr = await cmd.ExecuteReaderAsync();
                while (await rdr.ReadAsync())
                    if (!rdr.IsDBNull(0)) accountIds.Add(rdr.GetString(0));
            }

            if (accountIds.Count == 0)
                return (empty, $"No Account found for email: {email}");

            var results = new List<(string accountId, string companyId, string companyName)>();

            foreach (var accountId in accountIds)
            {
                if (!Guid.TryParse(accountId, out var accountGuid)) continue;

                // Strategy 1: AccountCompany join — may return multiple companies per account
                const string sqlAC = @"
SELECT CAST(ac.Company_ID AS NVARCHAR(50)),
       ISNULL(c.Name, '') AS CompanyName
FROM   AccountCompany ac
LEFT JOIN Company c ON c.ID = ac.Company_ID
WHERE  ac.Account_ID = @accountId";

                bool foundViaAC = false;
                using (var cmd = new SqlCommand(sqlAC, conn))
                {
                    cmd.Parameters.Add(new SqlParameter("@accountId", System.Data.SqlDbType.UniqueIdentifier) { Value = accountGuid });
                    using var rdr = await cmd.ExecuteReaderAsync();
                    while (await rdr.ReadAsync())
                    {
                        if (rdr.IsDBNull(0)) continue;
                        foundViaAC = true;
                        results.Add((accountId, rdr.GetString(0), rdr.IsDBNull(1) ? "" : rdr.GetString(1)));
                    }
                }

                if (foundViaAC) continue;

                // Strategy 2: Company.Account_ID fallback
                const string sqlComp = @"
SELECT CAST(ID AS NVARCHAR(50)),
       ISNULL(Name, '') AS CompanyName
FROM   Company
WHERE  Account_ID = @accountId";

                using var cmd2 = new SqlCommand(sqlComp, conn);
                cmd2.Parameters.Add(new SqlParameter("@accountId", System.Data.SqlDbType.UniqueIdentifier) { Value = accountGuid });
                using var rdr2 = await cmd2.ExecuteReaderAsync();
                while (await rdr2.ReadAsync())
                {
                    if (rdr2.IsDBNull(0)) continue;
                    results.Add((accountId, rdr2.GetString(0), rdr2.IsDBNull(1) ? "" : rdr2.GetString(1)));
                }
            }

            if (results.Count == 0)
                return (empty, $"Account found but no linked Company for email: {email}");

            return (results, null);
        }
        catch (Exception ex)
        {
            return (empty, $"DB error: {ex.Message}");
        }
    }

    /// <summary>
    /// Strips any accidental prefix before the first recognised SQL connection-string keyword
    /// (handles copy-paste of JSON key names, whitespace, quotes, etc.).
    /// </summary>
    private static string SanitizeConnectionString(string cs)
    {
        cs = cs.Trim().Trim('"').Trim('\'');

        // Find the first occurrence of a known keyword at the start of a token
        string[] startKeywords = ["Data Source=", "Server=", "data source=", "server="];
        foreach (var kw in startKeywords)
        {
            var idx = cs.IndexOf(kw, StringComparison.OrdinalIgnoreCase);
            if (idx > 0)
                return cs[idx..];   // strip garbage prefix
        }
        return cs;
    }
}
