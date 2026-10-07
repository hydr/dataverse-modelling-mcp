namespace Dataverse.Server.Tools;

using System.ComponentModel;
using System.Text.Json;
using Dataverse.Core.Config;
using Dataverse.Core.Services;
using ModelContextProtocol.Server;

[McpServerToolType]
public sealed class EnvironmentVariableTools
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    [McpServerTool(Name = "envvar_list", ReadOnly = true)]
    [Description("List all environment variable definitions and their current values.")]
    public static async Task<string> EnvVarList(
        EnvironmentVariableService svc,
        ConfigProvider config,
        CancellationToken ct = default)
    {
        try
        {
            var env = config.GetActiveEnvironment();
            var result = await svc.ListAsync(env.OrgUrl, ct);
            return JsonSerializer.Serialize(result, JsonOptions);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message, details = ex.GetType().Name });
        }
    }

    [McpServerTool(Name = "envvar_get", ReadOnly = true)]
    [Description("Get an environment variable definition and its current value by schema name.")]
    public static async Task<string> EnvVarGet(
        EnvironmentVariableService svc,
        ConfigProvider config,
        [Description("Schema name of the environment variable (e.g. 'new_MyVariable')")] string schemaName,
        CancellationToken ct = default)
    {
        try
        {
            var env = config.GetActiveEnvironment();
            var result = await svc.GetAsync(env.OrgUrl, schemaName, ct);
            return result is null
                ? JsonSerializer.Serialize(new { error = "Environment variable not found." })
                : JsonSerializer.Serialize(result, JsonOptions);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message, details = ex.GetType().Name });
        }
    }

    [McpServerTool(Name = "envvar_set", ReadOnly = false, Destructive = true, Idempotent = true)]
    [Description("Set the current value of an environment variable.")]
    public static async Task<string> EnvVarSet(
        EnvironmentVariableService svc,
        ConfigProvider config,
        [Description("Schema name of the environment variable")] string schemaName,
        [Description("New value to set")] string value,
        CancellationToken ct = default)
    {
        try
        {
            var env = config.GetActiveEnvironment();
            await svc.SetAsync(env.OrgUrl, schemaName, value, ct);
            return JsonSerializer.Serialize(new { success = true, schemaName, value });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message, details = ex.GetType().Name });
        }
    }
}
