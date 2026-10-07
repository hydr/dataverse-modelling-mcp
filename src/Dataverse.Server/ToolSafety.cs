using System.Reflection;
using Dataverse.Core.Config;
using Dataverse.Core.Safety;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Dataverse.Server;

/// <summary>
/// The two safety mechanisms on top of the tool annotations:
/// <list type="bullet">
/// <item>Read-only mode removes every tool that is not annotated <c>ReadOnly = true</c>, so a client
/// never even sees a write tool.</item>
/// <item>The production guard refuses write tools while the active environment is a Production or
/// Default environment, unless production writes are explicitly allowed.</item>
/// </list>
/// Both read the <c>ReadOnly</c> flag from the <see cref="McpServerToolAttribute"/>, which a test
/// requires on every tool.
/// </summary>
public static class ToolSafety
{
    /// <summary>Tool name → declared ReadOnly flag, for every tool in this assembly.</summary>
    public static IReadOnlyDictionary<string, bool> ReadOnlyByTool { get; } = typeof(ToolSafety).Assembly
        .GetTypes()
        .Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() is not null)
        .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance))
        .Select(m => m.GetCustomAttribute<McpServerToolAttribute>())
        .Where(a => a?.Name is not null)
        .ToDictionary(a => a!.Name!, a => a!.ReadOnly, StringComparer.Ordinal);

    public static bool IsReadOnlyTool(string name) => ReadOnlyByTool.TryGetValue(name, out var readOnly) && readOnly;

    /// <summary>Removes all write tools from the collection. Returns how many were removed.</summary>
    public static int RemoveWriteTools(McpServerPrimitiveCollection<McpServerTool> tools)
    {
        var writeTools = tools.Where(t => !IsReadOnlyTool(t.ProtocolTool.Name)).ToList();
        foreach (var tool in writeTools)
            tools.Remove(tool);
        return writeTools.Count;
    }

    public static SafetySettings Settings(ConfigProvider config) =>
        SafetySettings.Resolve(config.Config, Environment.GetEnvironmentVariable);

    /// <summary>Call-tool filter: refuses write tools against a protected environment.</summary>
    public static McpRequestHandler<CallToolRequestParams, CallToolResult> ProductionGuard(
        McpRequestHandler<CallToolRequestParams, CallToolResult> next) =>
        async (context, ct) =>
        {
            var name = context.Params?.Name;
            if (name is null || IsReadOnlyTool(name))
                return await next(context, ct);

            var services = context.Services!;
            var config = services.GetRequiredService<ConfigProvider>();
            if (Settings(config).AllowProductionWrites)
                return await next(context, ct);

            string orgUrl;
            try
            {
                orgUrl = config.GetActiveEnvironment().OrgUrl;
            }
            catch (InvalidOperationException)
            {
                // No environment configured — the tool itself reports that more helpfully.
                return await next(context, ct);
            }

            var type = await services.GetRequiredService<EnvironmentTypeService>().GetOrganizationTypeAsync(orgUrl, ct);
            if (type is null)
            {
                // Fail open: an unknown type must not lock out environments whose details the
                // signed-in user cannot read. The warning goes to stderr.
                services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(ToolSafety))
                    .LogWarning("Environment type of {OrgUrl} unknown; production guard not applied to {Tool}.", orgUrl, name);
                return await next(context, ct);
            }

            if (!EnvironmentTypeService.IsProtected(type))
                return await next(context, ct);

            return new CallToolResult
            {
                IsError = true,
                Content =
                [
                    new TextContentBlock
                    {
                        Text = $"Refused: '{name}' changes the environment, and {orgUrl} is a production environment (organization type {type}). " +
                               "Writes to production are blocked by default — model in a dev or sandbox " +
                               "environment and promote with a solution or pipeline. To allow writes here " +
                               $"anyway, set {SafetySettings.AllowProductionWritesVariable}=true or " +
                               "\"allowProductionWrites\": true in config.json, then reconnect the server.",
                    },
                ],
            };
        };
}
