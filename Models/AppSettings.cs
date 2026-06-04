namespace PayrixLauncher.Models;

public class AppSettings
{
    // Environment
    public bool   IsSandbox            { get; set; } = true;

    // API keys
    public string SandboxApiKey        { get; set; } = "";
    public string ProductionApiKey     { get; set; } = "";

    // Transaction search
    public string UserEmail            { get; set; } = "";
    public string SearchLimit          { get; set; } = "20";
    public int    LatestCountIndex     { get; set; } = 0;
    public string TransactionIds       { get; set; } = "";
    public string DisbursementId       { get; set; } = "";
    public string DisbursementLimit    { get; set; } = "20";

    // Webhook Tests
    public int    WebhookEnvIndex      { get; set; } = 0;
    public string WebhookUrl           { get; set; } = "";
    public int    WebhookTypeIndex     { get; set; } = 0;
    public string EntityCustomField     { get; set; } = "";
    public string AchTxnId             { get; set; } = "";
    public string AchAmount            { get; set; } = "898.00";
    public string RefundTxnId          { get; set; } = "";
    public string RefundAmount         { get; set; } = "74.00";
    public string CcRefundTxnId        { get; set; } = "";
    public string CcRefundAmount       { get; set; } = "74.00";
    public string CcReturnTxnId        { get; set; } = "";
    public string CcReturnAmount       { get; set; } = "74.00";

    // BQECoreHost payment-service DB (ServiceEntity → CoreCompany_ID)
    public string HostDbConnectionString              { get; set; } = "";  // legacy / fallback
    public string LocalHostDbConnectionString         { get; set; } = "";
    public string StagingHostDbConnectionString       { get; set; } = "";
    public string SprintHostDbConnectionString        { get; set; } = "";
    public string ProductionHostDbConnectionString    { get; set; } = "";

    // BQECoreHost main DB (AccountCompany → Account_ID)
    public string CoreAccountEmail                    { get; set; } = "";
    public string LocalMainDbConnectionString         { get; set; } = "";
    public string StagingMainDbConnectionString       { get; set; } = "";
    public string SprintMainDbConnectionString        { get; set; } = "";
    public string ProductionMainDbConnectionString    { get; set; } = "";

    // OAuth client IDs (register apps at console.cloud.google.com / portal.azure.com)
    public string GoogleClientId     { get; set; } = "";
    public string GoogleClientSecret { get; set; } = "";
    public string MicrosoftClientId  { get; set; } = "";
    public string AppleClientId      { get; set; } = "";

    // Core DB Utility — Section 1: Host DB credentials
    public string CoreHostServer         { get; set; } = "";
    public string CoreHostUser           { get; set; } = "sa";
    public string CoreHostPassword       { get; set; } = "";
    public string CoreHostDatabase       { get; set; } = "BQECoreHost";

    // Core DB Utility — Section 2: Core DB credentials
    public string CoreDbServer           { get; set; } = "";
    public string CoreDbUser             { get; set; } = "sa";
    public string CoreDbPassword         { get; set; } = "";

    // UI state
    public bool   KpiVisible              { get; set; } = true;
    public bool   EmailSectionCollapsed   { get; set; } = false;  // Email open by default
    public bool   TxnSectionCollapsed     { get; set; } = false;
    public bool   DisbSectionCollapsed    { get; set; } = true;   // Disb collapsed by default
    public bool   SearchPanelCollapsed    { get; set; } = false;
    public bool   WebhookConfigExpanded   { get; set; } = false;
    public bool   EmailSectionPinned      { get; set; } = false;
    public bool   TxnSectionPinned        { get; set; } = false;
    public bool   DisbSectionPinned       { get; set; } = false;
    public bool   IsDarkMode           { get; set; } = true;
    public double WindowWidth          { get; set; } = 1250;
    public double WindowHeight         { get; set; } = 960;
    public double WindowLeft           { get; set; } = double.NaN;
    public double WindowTop            { get; set; } = double.NaN;
}
