namespace Dataverse.Core.Services;

using System.Text.Json;
using Dataverse.Core.Clients;
using Dataverse.Core.Json;
using Dataverse.Core.Models;
using Microsoft.Extensions.Logging;

public sealed class SecurityRoleService
{
    private readonly DataverseHttpClient _client;
    private readonly ILogger<SecurityRoleService> _logger;

    public SecurityRoleService(DataverseHttpClient client, ILogger<SecurityRoleService> logger)
    {
        _client = client;
        _logger = logger;
    }

    public async Task<IReadOnlyList<RoleSummary>> ListAsync(
        string orgUrl,
        string? filter = null,
        CancellationToken ct = default)
    {
        var url = "api/data/v9.2/roles?$select=roleid,name,description,_businessunitid_value&$expand=businessunitid($select=name)";
        if (!string.IsNullOrWhiteSpace(filter))
            url += $"&$filter={Uri.EscapeDataString(filter)}";

        var raw = await _client.GetRawAsync(orgUrl, url, ct);
        var doc = JsonDocument.Parse(raw);
        var results = new List<RoleSummary>();

        if (doc.RootElement.TryGetProperty("value", out var items))
        {
            foreach (var item in items.EnumerateArray())
            {
                string? buName = null;
                if (item.TryGetProperty("businessunitid", out var buEl) && buEl.ValueKind == JsonValueKind.Object)
                    buName = buEl.GetStringOrNull("name");

                results.Add(new RoleSummary(
                    RoleId: item.TryGetGuid("roleid"),
                    Name: item.GetStringOrEmpty("name"),
                    Description: item.GetStringOrNull("description"),
                    BusinessUnitId: item.TryGetProperty("_businessunitid_value", out var buid)
                        ? (Guid.TryParse(buid.GetString(), out var g) ? g : null)
                        : null,
                    BusinessUnitName: buName));
            }
        }

        return results;
    }

    public async Task<RoleDetail?> GetAsync(
        string orgUrl,
        Guid roleId,
        CancellationToken ct = default)
    {
        var url = $"api/data/v9.2/roles({roleId})" +
                  "?$select=roleid,name,description,_businessunitid_value" +
                  "&$expand=businessunitid($select=name),roleprivileges_association($select=privilegeid,name,accessright)";

        var raw = await _client.GetRawAsync(orgUrl, url, ct);
        var item = JsonDocument.Parse(raw).RootElement;

        var privileges = new List<RolePrivilege>();
        if (item.TryGetProperty("roleprivileges_association", out var privs))
        {
            foreach (var priv in privs.EnumerateArray())
            {
                privileges.Add(new RolePrivilege(
                    PrivilegeId: priv.TryGetGuid("privilegeid"),
                    PrivilegeName: priv.GetStringOrEmpty("name"),
                    Depth: priv.GetInt32OrZero("accessright")));
            }
        }

        string? roleDetailBuName = null;
        if (item.TryGetProperty("businessunitid", out var roleDetailBuEl) && roleDetailBuEl.ValueKind == JsonValueKind.Object)
            roleDetailBuName = roleDetailBuEl.GetStringOrNull("name");

        return new RoleDetail(
            RoleId: item.TryGetGuid("roleid"),
            Name: item.GetStringOrEmpty("name"),
            Description: item.GetStringOrNull("description"),
            BusinessUnitId: item.TryGetProperty("_businessunitid_value", out var buid)
                ? (Guid.TryParse(buid.GetString(), out var g) ? g : null)
                : null,
            BusinessUnitName: roleDetailBuName,
            Privileges: privileges);
    }

    public async Task<Guid> CreateAsync(
        string orgUrl,
        string name,
        Guid businessUnitId,
        string? description = null,
        CancellationToken ct = default)
    {
        var body = new
        {
            name,
            description,
            businessunitid = $"/businessunits({businessUnitId})"
        };

        await _client.PostAsync(orgUrl, "api/data/v9.2/roles", body, ct);

        // Re-query to get the created role ID
        var created = await ListAsync(orgUrl, $"name eq '{name}'", ct);
        return created.Count > 0 ? created[0].RoleId : Guid.Empty;
    }

    public async Task UpdatePrivilegesAsync(
        string orgUrl,
        Guid roleId,
        IEnumerable<RolePrivilege> privilegesToAdd,
        IEnumerable<Guid> privilegeIdsToRemove,
        CancellationToken ct = default)
    {
        foreach (var privId in privilegeIdsToRemove)
        {
            var removeParams = new { RoleId = roleId, PrivilegeId = privId };
            await _client.ExecuteActionAsync(orgUrl, "RemovePrivilegeRole", removeParams, ct);
        }

        if (privilegesToAdd.Any())
        {
            var addParams = new
            {
                RoleId = roleId,
                Privileges = privilegesToAdd.Select(p => new
                {
                    PrivilegeId = p.PrivilegeId,
                    Depth = p.Depth
                }).ToArray()
            };

            await _client.ExecuteActionAsync(orgUrl, "AddPrivilegesRole", addParams, ct);
        }
    }
}
