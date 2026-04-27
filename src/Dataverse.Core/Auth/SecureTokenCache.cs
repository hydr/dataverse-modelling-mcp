namespace Dataverse.Core.Auth;

using Microsoft.Identity.Client.Extensions.Msal;

/// <summary>
/// Builds a cross-platform persistent token cache backed by the OS secret store.
/// </summary>
public static class SecureTokenCache
{
    private const string CacheFileName = "token.cache";
    private const string ServiceName = "dataverse-mcp";
    private const string KeyChainAccount = "dataverse-mcp-token";
    private const string LinuxCollection = "dataverse-mcp";

    public static async Task<MsalCacheHelper> CreateAsync()
    {
        var cacheDir = GetCacheDirectory();
        Directory.CreateDirectory(cacheDir);

        var storageProperties = new StorageCreationPropertiesBuilder(CacheFileName, cacheDir)
            .WithMacKeyChain(ServiceName, KeyChainAccount)
            .WithLinuxKeyring(
                schemaName: ServiceName,
                collection: LinuxCollection,
                secretLabel: "Dataverse MCP OAuth token cache",
                attribute1: new KeyValuePair<string, string>("version", "1"),
                attribute2: new KeyValuePair<string, string>("product", ServiceName))
            .WithLinuxUnprotectedFile()
            .Build();

        return await MsalCacheHelper.CreateAsync(storageProperties);
    }

    private static string GetCacheDirectory()
    {
        if (OperatingSystem.IsWindows())
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "dataverse-mcp");
        }

        if (OperatingSystem.IsMacOS())
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".dataverse-mcp");
        }

        // Linux: XDG_DATA_HOME or ~/.local/share
        var xdgData = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        return !string.IsNullOrEmpty(xdgData)
            ? Path.Combine(xdgData, "dataverse-mcp")
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".local", "share", "dataverse-mcp");
    }
}
