using Dataverse.Core.Auth;

using Dataverse.Core.Clients;
using Dataverse.Core.Config;
using Dataverse.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

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

// Config provider — reads config.json and wires up the token provider
builder.Services.AddSingleton<ConfigProvider>();

// Application Insights: no-ops when APPLICATIONINSIGHTS_CONNECTION_STRING is unset (local dev).
builder.Services.AddApplicationInsightsTelemetryWorkerService();

// MCP server
var mcpBuilder = builder.Services
    .AddMcpServer()
    .WithToolsFromAssembly();

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
