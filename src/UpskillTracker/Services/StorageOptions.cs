namespace UpskillTracker.Services;

public sealed class StorageOptions
{
    public const string SectionName = "Storage";

    public string Provider { get; set; } = "Sqlite";

    public string ConnectionString { get; set; } = string.Empty;

    public string LegacySqliteConnectionString { get; set; } = string.Empty;

    public bool EnableLegacySqliteImport { get; set; } = true;

    public bool UseManagedIdentity { get; set; }

    public string ManagedIdentityClientId { get; set; } = string.Empty;

    public string DatabaseUser { get; set; } = string.Empty;

    public string KeyBlobUri { get; set; } = string.Empty;

    public string DataProtectionApplicationName { get; set; } = "UpskillTracker";
}