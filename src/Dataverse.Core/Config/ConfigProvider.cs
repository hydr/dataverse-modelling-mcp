namespace Dataverse.Core.Config;

using System.Text.Json;
using Dataverse.Core.Auth;
using Microsoft.Extensions.Logging;

/// <summary>
/// Loads and caches the config.json from the platform-specific data directory.
/// Wires up DataverseTokenProvider with the clientId/tenantId from config.
/// </summary>
public sealed class ConfigProvider
{
    private readonly DataverseTokenProvider _tokenProvider;
    private readonly ILogger<ConfigProvider> _logger;
    private DataverseMcpConfig? _config;

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public ConfigProvider(DataverseTokenProvider tokenProvider, ILogger<ConfigProvider> logger)
    {
        _tokenProvider = tokenProvider;
        _logger = logger;
    }

    public DataverseMcpConfig Config => _config ??= LoadAndConfigure();

    public EnvironmentConfig GetActiveEnvironment()
    {
        var cfg = Config;
        if (cfg.Environments is null || cfg.Environments.Count == 0)
            throw new InvalidOperationException("No environments configured. Run 'dataverse-mcp setup'.");

        var name = cfg.ActiveEnvironment ?? "default";
        if (cfg.Environments.TryGetValue(name, out var env))
            return env;

        // Fallback to first environment
        return cfg.Environments.Values.First();
    }

    private DataverseMcpConfig LoadAndConfigure()
    {
        var configPath = GetConfigPath();

        if (!File.Exists(configPath))
        {
            _logger.LogWarning("Config file not found at {Path}. Run 'dataverse-mcp setup'.", configPath);
            return new DataverseMcpConfig();
        }

        var json = File.ReadAllText(configPath);
        var cfg = JsonSerializer.Deserialize<DataverseMcpConfig>(json, JsonOptions)
                  ?? new DataverseMcpConfig();

        if (cfg.Auth is not null)
            _tokenProvider.Configure(cfg.Auth.ClientId ?? string.Empty, cfg.Auth.TenantId);

        return cfg;
    }

    public static string GetConfigPath()
    {
        if (OperatingSystem.IsWindows())
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "dataverse-mcp", "config.json");

        if (OperatingSystem.IsMacOS())
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".dataverse-mcp", "config.json");

        var xdgConfig = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        return !string.IsNullOrEmpty(xdgConfig)
            ? Path.Combine(xdgConfig, "dataverse-mcp", "config.json")
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".config", "dataverse-mcp", "config.json");
    }

    public static void Save(DataverseMcpConfig config)
    {
        var path = GetConfigPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json);
    }
}

public sealed class DataverseMcpConfig
{
    public AuthConfig? Auth { get; set; }
    public string? ActiveEnvironment { get; set; }
    public Dictionary<string, EnvironmentConfig>? Environments { get; set; }
}

public sealed class AuthConfig
{
    public string? ClientId { get; set; }
    public string? TenantId { get; set; }
}

public sealed class EnvironmentConfig
{
    public string OrgUrl { get; set; } = string.Empty;
    public string? EnvironmentId { get; set; }
    public string Region { get; set; } = "europe";
}
