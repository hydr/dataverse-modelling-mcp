namespace Dataverse.Server.Tools;

using System.ComponentModel;
using System.Text.Json;
using Dataverse.Core.Config;
using Dataverse.Core.Models;
using Dataverse.Core.Services;
using ModelContextProtocol.Server;

[McpServerToolType]
public sealed class SecurityRoleTools
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    [McpServerTool(Name = "role_list")]
    [Description("List Security Roles in the Dataverse environment.")]
    public static async Task<string> RoleList(
        SecurityRoleService svc,
        ConfigProvider config,
        [Description("Optional OData filter (e.g. \"name eq 'System Administrator'\")")] string? filter = null,
        CancellationToken ct = default)
    {
        try
        {
            var env = config.GetActiveEnvironment();
            var result = await svc.ListAsync(env.OrgUrl, filter, ct);
            return JsonSerializer.Serialize(result, JsonOptions);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message, details = ex.GetType().Name });
        }
    }

    [McpServerTool(Name = "role_get")]
    [Description("Get a Security Role including its privileges.")]
    public static async Task<string> RoleGet(
        SecurityRoleService svc,
        ConfigProvider config,
        [Description("The role GUID")] string roleId,
        CancellationToken ct = default)
    {
        try
        {
            if (!Guid.TryParse(roleId, out var id))
                return JsonSerializer.Serialize(new { error = "Invalid roleId GUID format." });

            var env = config.GetActiveEnvironment();
            var result = await svc.GetAsync(env.OrgUrl, id, ct);
            return result is null
                ? JsonSerializer.Serialize(new { error = "Role not found." })
                : JsonSerializer.Serialize(result, JsonOptions);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message, details = ex.GetType().Name });
        }
    }

    [McpServerTool(Name = "role_create")]
    [Description("Create a new Security Role in a business unit.")]
    public static async Task<string> RoleCreate(
        SecurityRoleService svc,
        ConfigProvider config,
        [Description("Role name")] string name,
        [Description("Business unit GUID")] string businessUnitId,
        [Description("Optional description")] string? description = null,
        CancellationToken ct = default)
    {
        try
        {
            if (!Guid.TryParse(businessUnitId, out var buId))
                return JsonSerializer.Serialize(new { error = "Invalid businessUnitId GUID format." });

            var env = config.GetActiveEnvironment();
            var id = await svc.CreateAsync(env.OrgUrl, name, buId, description, ct);
            return JsonSerializer.Serialize(new { roleId = id, name });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message, details = ex.GetType().Name });
        }
    }

    [McpServerTool(Name = "role_update")]
    [Description("Add or remove privileges on a Security Role.")]
    public static async Task<string> RoleUpdate(
        SecurityRoleService svc,
        ConfigProvider config,
        [Description("The role GUID")] string roleId,
        [Description("JSON array of privileges to add: [{\"privilegeId\": \"...\", \"privilegeName\": \"...\", \"depth\": 4}]")] string? privilegesToAddJson = null,
        [Description("JSON array of privilege GUIDs to remove: [\"guid1\", \"guid2\"]")] string? privilegeIdsToRemoveJson = null,
        CancellationToken ct = default)
    {
        try
        {
            if (!Guid.TryParse(roleId, out var id))
                return JsonSerializer.Serialize(new { error = "Invalid roleId GUID format." });

            var add = string.IsNullOrWhiteSpace(privilegesToAddJson)
                ? Array.Empty<RolePrivilege>()
                : JsonSerializer.Deserialize<RolePrivilege[]>(privilegesToAddJson)
                  ?? Array.Empty<RolePrivilege>();

            var remove = string.IsNullOrWhiteSpace(privilegeIdsToRemoveJson)
                ? Array.Empty<Guid>()
                : JsonSerializer.Deserialize<string[]>(privilegeIdsToRemoveJson)!
                    .Select(g => Guid.Parse(g))
                    .ToArray();

            var env = config.GetActiveEnvironment();
            await svc.UpdatePrivilegesAsync(env.OrgUrl, id, add, remove, ct);
            return JsonSerializer.Serialize(new { success = true, roleId });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message, details = ex.GetType().Name });
        }
    }
}
