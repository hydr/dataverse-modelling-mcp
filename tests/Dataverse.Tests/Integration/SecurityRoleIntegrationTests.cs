namespace Dataverse.Tests.Integration;

using Dataverse.Tests.Infrastructure;
using NUnit.Framework;

[TestFixture]
[Category("Integration")]
public sealed class SecurityRoleIntegrationTests : IntegrationTestBase
{
    private static readonly Guid TestRoleId = Guid.Parse("9512667b-6242-f111-bec6-7c1e528730f7");
    private const string TestRoleName = "DV MCP Test Role";
    // In German-locale environments the built-in admin role is "Systemadministrator"
    private const string SystemAdminRoleName = "Systemadministrator";

    [Test]
    public async Task ListRoles_ReturnsResults()
    {
        var roles = await SecurityRoleService.ListAsync(OrgUrl);

        Assert.That(roles.Count, Is.GreaterThan(0),
            "Expected at least one security role in the environment.");
    }

    [Test]
    public async Task GetRole_ReturnsDvMcpTestRole()
    {
        var detail = await SecurityRoleService.GetAsync(OrgUrl, TestRoleId);

        Assert.That(detail, Is.Not.Null);
        Assert.That(detail!.Name, Is.EqualTo(TestRoleName));
        Assert.That(detail.RoleId, Is.EqualTo(TestRoleId));
    }

    [Test]
    public async Task ListRoles_ContainsSystemAdministrator()
    {
        var roles = await SecurityRoleService.ListAsync(OrgUrl);

        Assert.That(roles.Any(r => r.Name == SystemAdminRoleName || r.Name == "System Administrator"), Is.True,
            $"Expected to find '{SystemAdminRoleName}' (or 'System Administrator') in the roles list.");
    }

    [Test]
    public async Task GetRole_WithPrivileges_ReturnsPrivilegeList()
    {
        var detail = await SecurityRoleService.GetAsync(OrgUrl, TestRoleId);

        Assert.That(detail, Is.Not.Null);
        Assert.That(detail!.Privileges, Is.Not.Null);
    }
}
