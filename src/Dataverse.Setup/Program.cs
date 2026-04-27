using System.Text.Json;
using Dataverse.Core.Auth;
using Dataverse.Core.Config;
using Microsoft.Identity.Client;
using Spectre.Console;

AnsiConsole.Write(new FigletText("Dataverse MCP").Color(Color.Blue));
AnsiConsole.MarkupLine("[bold cyan]Setup Wizard[/]");
AnsiConsole.WriteLine();

// Step 1: Check Azure CLI
AnsiConsole.MarkupLine("[yellow]Step 1:[/] Checking Azure CLI...");
bool azCliAvailable = false;
try
{
    var proc = new System.Diagnostics.Process
    {
        StartInfo = new System.Diagnostics.ProcessStartInfo("az", "--version")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        }
    };
    proc.Start();
    await proc.WaitForExitAsync();
    azCliAvailable = proc.ExitCode == 0;
}
catch
{
    azCliAvailable = false;
}

if (azCliAvailable)
    AnsiConsole.MarkupLine("[green]  Azure CLI is installed.[/]");
else
    AnsiConsole.MarkupLine("[yellow]  Azure CLI not found. You can still proceed without it.[/]");

// Step 2: Determine clientId
AnsiConsole.MarkupLine("[yellow]Step 2:[/] Azure AD App Registration...");

string? clientId = null;
string? tenantId = null;

if (azCliAvailable)
{
    AnsiConsole.MarkupLine("  Searching for existing 'Dataverse MCP' app registration...");
    try
    {
        var listProc = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo(
                "az", "ad app list --display-name \"Dataverse MCP\" --query \"[0].appId\" -o tsv")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            }
        };
        listProc.Start();
        var output = await listProc.StandardOutput.ReadToEndAsync();
        await listProc.WaitForExitAsync();
        clientId = output.Trim();
        if (string.IsNullOrWhiteSpace(clientId) || clientId == "null")
            clientId = null;
    }
    catch { clientId = null; }
}

if (!string.IsNullOrEmpty(clientId))
{
    AnsiConsole.MarkupLine($"[green]  Found existing app: {clientId}[/]");
}
else
{
    AnsiConsole.MarkupLine("  No existing app found.");
    if (azCliAvailable &&
        AnsiConsole.Confirm("  Create a new 'Dataverse MCP' app registration via Azure CLI?"))
    {
        AnsiConsole.MarkupLine("  Creating app registration...");
        try
        {
            var createProc = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo(
                    "az",
                    "ad app create " +
                    "--display-name \"Dataverse MCP\" " +
                    "--public-client-redirect-uris http://localhost " +
                    "--query appId -o tsv")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false
                }
            };
            createProc.Start();
            clientId = (await createProc.StandardOutput.ReadToEndAsync()).Trim();
            await createProc.WaitForExitAsync();
            AnsiConsole.MarkupLine($"[green]  Created app: {clientId}[/]");
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]  Failed to create app: {ex.Message}[/]");
        }
    }
}

if (string.IsNullOrEmpty(clientId))
{
    clientId = AnsiConsole.Ask<string>("  Enter your Azure AD Application (client) ID:");
}

tenantId = AnsiConsole.Ask<string>("  Enter your Tenant ID (or press Enter to use common):", string.Empty);
if (string.IsNullOrWhiteSpace(tenantId)) tenantId = null;

// Step 3: Acquire token interactively
AnsiConsole.MarkupLine("[yellow]Step 3:[/] Acquiring initial token (browser will open)...");

var tokenProvider = new DataverseTokenProvider(
    new Microsoft.Extensions.Logging.Abstractions.NullLogger<DataverseTokenProvider>());
tokenProvider.Configure(clientId, tenantId);

string orgUrl = AnsiConsole.Ask<string>(
    "  Enter your Dataverse org URL (e.g. https://org.crm4.dynamics.com):");

string? accessToken = null;
try
{
    var scope = $"{orgUrl.TrimEnd('/')}/.default";
    accessToken = await tokenProvider.GetTokenAsync(scope);
    AnsiConsole.MarkupLine("[green]  Token acquired and cached.[/]");
}
catch (Exception ex)
{
    AnsiConsole.MarkupLine($"[red]  Failed to acquire token: {ex.Message}[/]");
}

// Step 4: Pick default environment
AnsiConsole.MarkupLine("[yellow]Step 4:[/] Configuring environment...");

var envName = AnsiConsole.Ask<string>("  Enter a name for this environment (e.g. 'prod'):", "prod");
var environmentId = AnsiConsole.Ask<string>("  Enter the Power Platform Environment ID (GUID or leave blank):", string.Empty);
var region = AnsiConsole.Ask<string>("  Enter the Power Automate region (e.g. 'europe', 'unitedstates'):", "europe");

// Step 5: Write config
var configData = new DataverseMcpConfig
{
    Auth = new AuthConfig { ClientId = clientId, TenantId = tenantId },
    ActiveEnvironment = envName,
    Environments = new Dictionary<string, EnvironmentConfig>
    {
        [envName] = new EnvironmentConfig
        {
            OrgUrl = orgUrl,
            EnvironmentId = string.IsNullOrWhiteSpace(environmentId) ? null : environmentId,
            Region = region
        }
    }
};

ConfigProvider.Save(configData);
AnsiConsole.MarkupLine($"[green]  Config saved to: {ConfigProvider.GetConfigPath()}[/]");

// Step 6: Print Claude Code mcp add command
AnsiConsole.WriteLine();
AnsiConsole.MarkupLine("[bold green]Setup complete![/]");
AnsiConsole.WriteLine();
AnsiConsole.MarkupLine("To add this MCP server to Claude Code, run:");
AnsiConsole.MarkupLine("[bold]  claude mcp add dataverse-mcp -- dotnet run --project /path/to/src/Dataverse.Server[/]");
AnsiConsole.WriteLine();
AnsiConsole.MarkupLine("Or, if installed as a dotnet tool:");
AnsiConsole.MarkupLine("[bold]  claude mcp add dataverse-mcp -- dataverse-mcp server[/]");
