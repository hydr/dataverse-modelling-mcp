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
            throw new InvalidOperationException("No environments configured. Run 'dataverse-modelling-mcp setup'.");

        var name = cfg.ActiveEnvironment ?? "default";
        if (cfg.Environments.TryGetValue(name, out var env))
            return env;

        // Fallback to first environment
        return cfg.Environments.Values.First();
    }

    private DataverseMcpConfig LoadAndConfigure()
    {
        var configPath = GetConfigPath();
        DataverseMcpConfig cfg;

        if (File.Exists(configPath))
        {
            var json = File.ReadAllText(configPath);
            cfg = JsonSerializer.Deserialize<DataverseMcpConfig>(json, JsonOptions)
                  ?? new DataverseMcpConfig();
        }
        else if (FromEnvironment(Environment.GetEnvironmentVariable) is { } envConfig)
        {
            // The Claude Code plugin passes its user config (dataverse_url, client_id, tenant_id)
            // as environment variables — enough to run without the setup wizard.
            _logger.LogInformation("Config file not found at {Path}; using DATAVERSE_URL / AZURE_CLIENT_ID.", configPath);
            cfg = envConfig;
        }
        else
        {
            _logger.LogWarning(
                "Config file not found at {Path} and DATAVERSE_URL / AZURE_CLIENT_ID are not set. Run the setup wizard 'dataverse-modelling-mcp'.",
                configPath);
            return new DataverseMcpConfig();
        }

        if (cfg.Auth is not null)
            _tokenProvider.Configure(cfg.Auth.ClientId ?? string.Empty, cfg.Auth.TenantId);

        return cfg;
    }

    /// <summary>
    /// Builds a single-environment config from DATAVERSE_URL, AZURE_CLIENT_ID and the optional
    /// AZURE_TENANT_ID, DATAVERSE_ENVIRONMENT_ID and POWER_PLATFORM_REGION. Returns null unless
    /// both required variables are set. A value that is still an unexpanded placeholder
    /// (e.g. "${user_config.tenant_id}" for an optional plugin setting left empty) counts as unset.
    /// </summary>
    public static DataverseMcpConfig? FromEnvironment(Func<string, string?> getVariable)
    {
        string? Read(string name)
        {
            var value = getVariable(name)?.Trim();
            return string.IsNullOrEmpty(value) || value.StartsWith("${", StringComparison.Ordinal) ? null : value;
        }

        var orgUrl = Read("DATAVERSE_URL");
        var clientId = Read("AZURE_CLIENT_ID");
        if (orgUrl is null || clientId is null)
            return null;

        return new DataverseMcpConfig
        {
            Auth = new AuthConfig { ClientId = clientId, TenantId = Read("AZURE_TENANT_ID") },
            ActiveEnvironment = "default",
            Environments = new Dictionary<string, EnvironmentConfig>
            {
                ["default"] = new()
                {
                    OrgUrl = orgUrl.TrimEnd('/'),
                    EnvironmentId = Read("DATAVERSE_ENVIRONMENT_ID"),
                    Region = Read("POWER_PLATFORM_REGION") ?? "europe",
                },
            },
        };
    }

    public static string GetConfigPath()
    {
        if (OperatingSystem.IsWindows())
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "dataverse-modelling-mcp", "config.json");

        if (OperatingSystem.IsMacOS())
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".dataverse-modelling-mcp", "config.json");

        var xdgConfig = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        return !string.IsNullOrEmpty(xdgConfig)
            ? Path.Combine(xdgConfig, "dataverse-modelling-mcp", "config.json")
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".config", "dataverse-modelling-mcp", "config.json");
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
