using Dataverse.Core.Auth;

using Dataverse.Core.Clients;
using Dataverse.Core.Config;
using Dataverse.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Dataverse.Core.Safety;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Dataverse.Server;

/// <summary>
/// Builds and runs the MCP server. Lives outside Program.cs so the dotnet tool can start the
/// same server with <c>dataverse-modelling-mcp server</c>.
/// </summary>
public static class ServerHost
{
    public static async Task RunAsync(string[] args)
    {
        // All diagnostic logging goes to stderr so it does not corrupt the MCP stdio stream.
        var builder = Host.CreateApplicationBuilder(args);

        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(options =>
        {
            options.LogToStandardErrorThreshold = LogLevel.Trace;
        });
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        // Core auth and HTTP clients
        builder.Services.AddSingleton<DataverseTokenProvider>();
        builder.Services.AddSingleton<ITokenProvider>(sp => sp.GetRequiredService<DataverseTokenProvider>());
        // HttpClient defaults to a 100 s timeout, which quietly undercuts every long-running operation here:
        // a ribbon round-trip asks for importTimeoutSeconds=900 and still died at 100 s with
        // "The request was canceled due to the configured HttpClient.Timeout of 100 seconds elapsing."
        // The per-operation timeouts are the ones meant to draw the line; this one just must not get there first.
        builder.Services.AddHttpClient<DataverseHttpClient>(c => c.Timeout = TimeSpan.FromMinutes(15));
        builder.Services.AddHttpClient<PowerAutomateHttpClient>(c => c.Timeout = TimeSpan.FromMinutes(5));

        // Domain services
        builder.Services.AddSingleton<WorkflowService>();
        builder.Services.AddSingleton<WorkflowAuthoringService>();
        builder.Services.AddSingleton<WorkflowActivationDiagnoser>();
        builder.Services.AddSingleton<CloudFlowService>();
        builder.Services.AddSingleton<FlowVersionService>();
        builder.Services.AddSingleton<TableService>();
        builder.Services.AddSingleton<ViewService>();
        builder.Services.AddSingleton<SolutionService>();
        builder.Services.AddSingleton<SecurityRoleService>();
        builder.Services.AddSingleton<EnvironmentVariableService>();
        builder.Services.AddSingleton<WebResourceService>();
        builder.Services.AddSingleton<PublishService>();
        builder.Services.AddSingleton<CommandService>();
        builder.Services.AddSingleton<RibbonService>();
        builder.Services.AddSingleton<ComponentDependencyService>();
        builder.Services.AddSingleton<EntitySolutionMapService>();
        builder.Services.AddSingleton<WebResourceUsageService>();
        builder.Services.AddSingleton<FormService>();
        builder.Services.AddSingleton<BusinessProcessFlowService>();
        builder.Services.AddSingleton<EnvironmentTypeService>();

        // Config provider — reads config.json and wires up the token provider
        builder.Services.AddSingleton<ConfigProvider>();

        // Application Insights: no-ops when APPLICATIONINSIGHTS_CONNECTION_STRING is unset (local dev).
        builder.Services.AddApplicationInsightsTelemetryWorkerService();

        // MCP server
        var mcpBuilder = builder.Services
            .AddMcpServer(options =>
            {
                // Pinned explicitly: the default is the entry assembly, which is Dataverse.Setup
                // when the server runs inside the dotnet tool.
                options.ServerInfo = new Implementation
                {
                    Name = "dataverse-modelling-mcp",
                    Version = typeof(ServerHost).Assembly.GetName().Version?.ToString(3) ?? "0.0.0",
                };
            })
            .WithToolsFromAssembly(typeof(ServerHost).Assembly)
            .WithRequestFilters(filters => filters
                .AddCallToolFilter(ToolSafety.ProductionGuard)
                .AddCallToolFilter(ToolSafety.DryRunFilter));

        // Read-only mode: drop every write tool before the server starts, so a client never sees one.
        builder.Services.AddOptions<McpServerOptions>().PostConfigure<ConfigProvider>((options, config) =>
        {
            if (ToolSafety.Settings(config).ReadOnly && options.ToolCollection is { } tools)
                Console.Error.WriteLine($"Read-only mode: {ToolSafety.RemoveWriteTools(tools)} write tools disabled.");
        });

        // Detect transport flag (default: stdio)
        if (args.Contains("--transport") &&
            args.SkipWhile(a => a != "--transport").Skip(1).FirstOrDefault() == "http")
        {
            // HTTP transport stub — full rollout not yet implemented
            // mcpBuilder.WithHttpTransport();
            Console.Error.WriteLine("HTTP transport is not yet implemented; falling back to stdio.");
        }

        mcpBuilder.WithStdioServerTransport();

        await builder.Build().RunAsync();
    }
}
