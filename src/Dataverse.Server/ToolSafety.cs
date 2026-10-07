using System.Reflection;
using System.Text.Json;
using Dataverse.Core.Clients;
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

    public const string DryRunDescription =
        "true: change nothing — return the requests this call would send (method, URL, headers, body) " +
        "instead. Reads still run, so an update shows the real definition it would write. Allowed on " +
        "production environments.";

    /// <summary>
    /// Every tool with a <c>dryRun</c> parameter → its default value. Some tools had their own
    /// validate-and-report dry run before the generic one existed (<c>workflow_set_definition</c>,
    /// <c>bpf_set_definition</c>, <c>solution_uninstall</c> — the last defaults to <c>true</c>).
    /// </summary>
    public static IReadOnlyDictionary<string, bool> DryRunDefaults { get; } = ToolMethods()
        .Select(m => (Tool: m.GetCustomAttribute<McpServerToolAttribute>()?.Name,
                      Parameter: m.GetParameters().FirstOrDefault(p => p.Name == "dryRun" && p.ParameterType == typeof(bool))))
        .Where(x => x.Tool is not null && x.Parameter is not null)
        .ToDictionary(x => x.Tool!, x => x.Parameter!.HasDefaultValue && x.Parameter.DefaultValue is true, StringComparer.Ordinal);

    /// <summary>
    /// Tools whose <c>dryRun</c> is the generic one (described by <see cref="DryRunDescription"/>):
    /// the <see cref="DryRunFilter"/> records their writes. Tools with their own dry run are left alone.
    /// </summary>
    public static IReadOnlySet<string> DryRunTools { get; } = ToolMethods()
        .Where(m => m.GetParameters().Any(p =>
            p.Name == "dryRun" &&
            p.GetCustomAttribute<System.ComponentModel.DescriptionAttribute>()?.Description == DryRunDescription))
        .Select(m => m.GetCustomAttribute<McpServerToolAttribute>()?.Name)
        .OfType<string>()
        .ToHashSet(StringComparer.Ordinal);

    private static IEnumerable<MethodInfo> ToolMethods() => typeof(ToolSafety).Assembly
        .GetTypes()
        .Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() is not null)
        .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance))
        .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() is not null);

    /// <summary>
    /// True when the call runs a tool in dry-run mode — <c>dryRun: true</c> passed, or left out on a
    /// tool whose dry run is the default. Such a call changes nothing.
    /// </summary>
    public static bool IsDryRunRequest(string? name, IDictionary<string, JsonElement>? arguments)
    {
        if (name is null || !DryRunDefaults.TryGetValue(name, out var byDefault))
            return false;
        if (arguments is not null && arguments.TryGetValue("dryRun", out var value))
            return value.ValueKind == JsonValueKind.True;
        return byDefault;
    }

    /// <summary>
    /// Call-tool filter: runs a dry-run request inside a <see cref="DryRun"/> scope, so the tool's
    /// writes are recorded instead of sent, and returns the recorded requests.
    /// </summary>
    public static McpRequestHandler<CallToolRequestParams, CallToolResult> DryRunFilter(
        McpRequestHandler<CallToolRequestParams, CallToolResult> next) =>
        async (context, ct) =>
        {
            var name = context.Params?.Name;
            if (name is null || !DryRunTools.Contains(name) || !IsDryRunRequest(name, context.Params?.Arguments))
                return await next(context, ct);

            CallToolResult toolResult;
            IReadOnlyList<PlannedRequest> planned;
            using (var scope = DryRun.Begin())
            {
                toolResult = await next(context, ct);
                planned = scope.Requests;
            }

            var toolText = string.Concat(toolResult.Content.OfType<TextContentBlock>().Select(c => c.Text));
            object? toolOutput;
            try { toolOutput = JsonSerializer.Deserialize<JsonElement>(toolText); }
            catch (JsonException) { toolOutput = toolText; }

            var report = new
            {
                dryRun = true,
                sent = false,
                note = planned.Count == 0
                    ? "Dry run: this call would not send any change — check toolOutput for an error."
                    : "Dry run: nothing was changed. These are the requests the call would send, in order. " +
                      "Ids of records created within the dry run show as 00000000-0000-0000-0000-000000000000.",
                plannedRequests = planned,
                toolOutput,
            };

            return new CallToolResult
            {
                IsError = toolResult.IsError,
                Content = [new TextContentBlock { Text = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }) }],
            };
        };

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
            if (name is null || IsReadOnlyTool(name) || IsDryRunRequest(name, context.Params?.Arguments))
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
                               "environment and promote with a solution or pipeline. Tools that offer dryRun " +
                               "may still be called with dryRun: true here to see what they would change. " +
                               "To allow writes here " +
                               $"anyway, set {SafetySettings.AllowProductionWritesVariable}=true or " +
                               "\"allowProductionWrites\": true in config.json, then reconnect the server.",
                    },
                ],
            };
        };
}
