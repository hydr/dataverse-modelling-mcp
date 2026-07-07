namespace Dataverse.Server.Tools;

using System.ComponentModel;
using System.Text.Json;
using Dataverse.Core.Auth;
using Dataverse.Core.Config;
using ModelContextProtocol.Server;

[McpServerToolType]
public sealed class AuthTools
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    [McpServerTool(Name = "auth_relogin")]
    [Description(
        "Force an interactive re-login for the Dataverse connection. Clears ALL cached accounts and " +
        "opens the system browser with an account picker (Prompt.SelectAccount), then blocks until " +
        "sign-in completes. Use this to switch the identity the MCP server acts as — e.g. sign in with " +
        "an admin account that owns the connection a flow must be activated with. Returns the " +
        "signed-in username. Scope is derived from the active environment's org URL.")]
    public static async Task<string> AuthRelogin(
        DataverseTokenProvider tokenProvider,
        ConfigProvider config,
        CancellationToken ct = default)
    {
        try
        {
            // Accessing the active environment also lazily configures the token provider
            // (clientId/tenantId) from config.json.
            var env = config.GetActiveEnvironment();
            var scope = $"{env.OrgUrl.TrimEnd('/')}/.default";

            var user = await tokenProvider.ReauthenticateAsync(scope, ct);

            return JsonSerializer.Serialize(
                new { success = true, signedInAs = user, environment = env.OrgUrl },
                JsonOptions);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message, details = ex.GetType().Name });
        }
    }
}
