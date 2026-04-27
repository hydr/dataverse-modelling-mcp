namespace Dataverse.Core.Models;

public sealed record RoleSummary(
    Guid RoleId,
    string Name,
    string? Description,
    Guid? BusinessUnitId,
    string? BusinessUnitName);

public sealed record RoleDetail(
    Guid RoleId,
    string Name,
    string? Description,
    Guid? BusinessUnitId,
    string? BusinessUnitName,
    IReadOnlyList<RolePrivilege> Privileges);

public sealed record RolePrivilege(
    Guid PrivilegeId,
    string PrivilegeName,
    int Depth);
