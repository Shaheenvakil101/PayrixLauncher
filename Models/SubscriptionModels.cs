using System.Text.Json.Serialization;

namespace PayrixLauncher.Models;

/// <summary>
/// Maps to BQECoreHostModel.UserSubscribePackage returned by
/// POST /BQECoreAdminPortalAPI/API/CoreHost/SubscribePackages
/// </summary>
public class UserSubscribePackage
{
    [JsonPropertyName("ID")]
    public Guid Id { get; set; }

    [JsonPropertyName("Parent_ID")]
    public Guid? ParentId { get; set; }

    [JsonPropertyName("Package_ID")]
    public Guid? PackageId { get; set; }

    [JsonPropertyName("PackageName")]
    public string? PackageName { get; set; }

    [JsonPropertyName("PlanName")]
    public string? PlanName { get; set; }

    [JsonPropertyName("PackageEnum")]
    public string? PackageEnum { get; set; }

    [JsonPropertyName("PackageType")]
    [JsonConverter(typeof(FlexibleIntConverter))]
    public int? PackageType { get; set; }

    [JsonPropertyName("NumberOfLicense")]
    [JsonConverter(typeof(FlexibleIntConverter))]
    public int? NumberOfLicense { get; set; }

    [JsonPropertyName("LicenseUsed")]
    [JsonConverter(typeof(FlexibleIntConverter))]
    public int? LicenseUsed { get; set; }

    [JsonPropertyName("AutoRenew")]
    public bool AutoRenew { get; set; }

    [JsonPropertyName("Status")]
    [JsonConverter(typeof(FlexibleIntConverter))]
    public int? Status { get; set; }

    [JsonPropertyName("StartsOn")]
    public string? StartsOn { get; set; }

    [JsonPropertyName("ExpiresOn")]
    public string? ExpiresOn { get; set; }

    // ── Computed display helpers ───────────────────────────────────────────

    [JsonIgnore]
    public int LicenseRemaining => (NumberOfLicense ?? 0) - (LicenseUsed ?? 0);

    [JsonIgnore]
    public string AutoRenewLabel => AutoRenew ? "✓" : "—";

    [JsonIgnore]
    public string StatusLabel => Status switch
    {
        1 => "Active",
        2 => "Inactive",
        3 => "Expired",
        4 => "Cancelled",
        _ => Status.HasValue ? $"Status {Status}" : "—"
    };

    [JsonIgnore]
    public string StatusColor => Status switch
    {
        1 => "#17A34A",
        2 or 4 => "#64748B",
        3 => "#DC2625",
        _ => "#64748B"
    };

    [JsonIgnore]
    public string ExpiryFormatted
    {
        get
        {
            if (string.IsNullOrWhiteSpace(ExpiresOn)) return "—";
            if (DateTime.TryParse(ExpiresOn, out var d)) return d.ToString("dd MMM yyyy");
            return ExpiresOn;
        }
    }

    [JsonIgnore]
    public string StartFormatted
    {
        get
        {
            if (string.IsNullOrWhiteSpace(StartsOn)) return "—";
            if (DateTime.TryParse(StartsOn, out var d)) return d.ToString("dd MMM yyyy");
            return StartsOn;
        }
    }

    [JsonIgnore]
    public bool IsExpiringSoon
    {
        get
        {
            if (!DateTime.TryParse(ExpiresOn, out var d)) return false;
            return d <= DateTime.Now.AddDays(30) && d >= DateTime.Now;
        }
    }

    [JsonIgnore]
    public bool IsExpired
    {
        get
        {
            if (!DateTime.TryParse(ExpiresOn, out var d)) return false;
            return d < DateTime.Now;
        }
    }
}

/// <summary>BQEParameters wrapper used by SubscribePackages endpoint.</summary>
public class BqeParameters
{
    [JsonPropertyName("Company_ID")]
    public Guid CompanyId { get; set; }
}

/// <summary>ActionHelper used by ChangePackageDates endpoint.</summary>
public class ChangePackageDateRequest
{
    [JsonPropertyName("ID")]
    public Guid SubscriptionId { get; set; }

    [JsonPropertyName("Company_ID")]
    public Guid CompanyId { get; set; }

    [JsonPropertyName("ExpiresOn")]
    public string ExpiresOn { get; set; } = "";

    [JsonPropertyName("AutoRenew")]
    public bool AutoRenew { get; set; }
}
