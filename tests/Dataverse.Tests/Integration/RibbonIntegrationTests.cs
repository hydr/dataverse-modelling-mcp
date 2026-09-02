namespace Dataverse.Tests.Integration;

using System.Text;
using Dataverse.Core.Services;
using Dataverse.Tests.Infrastructure;
using NUnit.Framework;

/// <summary>
/// End-to-end proof for the classic-ribbon tools, run against <c>sample_mcptest</c>.
/// <para>
/// The removal test is the point of this fixture. Importing a solution whose
/// <c>&lt;CustomActions /&gt;</c> is empty does <b>not</b> remove anything — unmanaged solution import
/// merges node-by-node and never subtracts, and neither deleting the importing solution nor
/// <c>PublishAllXml</c> changes that. What does work, and what
/// <see cref="RibbonService.RemoveButtonAsync"/> does, is deleting the stored <c>ribbondiff</c> row
/// directly. <see cref="AddThenRemove_ButtonAppearsInAndDisappearsFromTheCompiledRibbon"/> asserts the
/// before/after state of the <b>compiled</b> ribbon, which is what the client renders, so it cannot pass
/// on a merge that merely looked successful.
/// </para>
/// </summary>
[TestFixture]
public sealed class RibbonIntegrationTests : IntegrationTestBase
{
    private const string Table = "sample_mcptest";
    private const string WebResourceName = "sample_mcp_ribbon_probe.js";

    private const string GridLocation =
        "Mscrm.HomepageGrid.sample_mcptest.MainTab.Management.Controls._children";

    private const string FormLocation =
        "Mscrm.Form.sample_mcptest.MainTab.Save.Controls._children";

    private const string GridButtonId = "sample.sample_mcptest.McpGridProbe.Button";
    private const string FormButtonId = "sample.sample_mcptest.McpFormProbe.Button";

    [OneTimeSetUp]
    public async Task EnsureWebResourceAndCleanSlate()
    {
        if (Environment.GetEnvironmentVariable("DATAVERSE_INTEGRATION_TESTS") != "true")
        {
            return;
        }

        await WebResourceService.UpsertAsync(
            OrgUrl,
            WebResourceName,
            Encoding.UTF8.GetBytes("var Sample=Sample||{};Sample.McpProbe={run:function(){}};"),
            displayName: "MCP ribbon probe",
            ct: CancellationToken.None);

        await PublishService.PublishAsync(
            OrgUrl, webResources: new[] { WebResourceName }, ct: CancellationToken.None);

        await TryRemoveAsync(GridButtonId);
        await TryRemoveAsync(FormButtonId);
    }

    [OneTimeTearDown]
    public async Task RemoveLeftovers()
    {
        if (Environment.GetEnvironmentVariable("DATAVERSE_INTEGRATION_TESTS") != "true")
        {
            return;
        }

        await TryRemoveAsync(GridButtonId);
        await TryRemoveAsync(FormButtonId);
    }

    [Test, Order(1)]
    public async Task ListInsertLocations_ReturnsTheGroupsOfTheCompiledRibbon()
    {
        var locations = await RibbonService.ListInsertLocationsAsync(OrgUrl, Table, CancellationToken.None);

        Assert.That(locations, Does.Contain(GridLocation));
        Assert.That(locations, Does.Contain(FormLocation));
        Assert.That(locations, Is.All.EndsWith(".Controls._children"));
    }

    [Test, Order(2)]
    public void AddButton_RejectsALocationTheCompiledRibbonDoesNotHave()
    {
        // Without this guard the import succeeds, the publish succeeds, and nothing renders.
        var ex = Assert.ThrowsAsync<InvalidOperationException>(() => RibbonService.AddButtonAsync(
            OrgUrl,
            Table,
            "sample.sample_mcptest.Nope.Button",
            "Mscrm.HomepageGrid.sample_mcptest.MainTab.ThisGroupDoesNotExist.Controls._children",
            "Nope",
            WebResourceName,
            "Sample.McpProbe.run",
            RibbonService.ParseParameterSpec("SelectedControl"),
            ct: CancellationToken.None));

        Assert.That(ex!.Message, Does.Contain("does not exist in the compiled ribbon"));
    }

    [Test, Order(3)]
    public async Task AddThenRemove_ButtonAppearsInAndDisappearsFromTheCompiledRibbon()
    {
        const string label = "MCP Grid Probe";

        // The compiled ribbon contains no <CustomAction> elements at all — the diff has been applied,
        // so what remains is the <Button> inlined into its group plus the <CommandDefinition> and any
        // referenced rules. Asserting on the CustomAction id would therefore always fail; the markers
        // that actually prove rendering are the button id and its caption.
        // --- BEFORE ------------------------------------------------------------------------------
        var before = await RibbonService.RetrieveCompiledRibbonAsync(OrgUrl, Table, CancellationToken.None);
        Assert.That(before, Does.Not.Contain(GridButtonId),
            "Precondition: the probe button must not exist yet.");

        // --- ADD ---------------------------------------------------------------------------------
        var added = await RibbonService.AddButtonAsync(
            OrgUrl,
            Table,
            GridButtonId,
            GridLocation,
            label,
            WebResourceName,
            "Sample.McpProbe.run",
            RibbonService.ParseParameterSpec("SelectedControlSelectedItemIds,SelectedControl"),
            enableRule: "OneSelected",
            ct: CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(added.Success, Is.True);
            // Verified against the stored row, not against the import result: the import job reports
            // <entityRibbon result="success"> even when nothing was written.
            Assert.That(added.Verified, Is.True, added.Warning);
            Assert.That(added.TemporarySolutionDeleted, Is.True);
            Assert.That(added.ImportComponentErrors, Is.Empty);
        });

        var afterAdd = await RibbonService.RetrieveCompiledRibbonAsync(OrgUrl, Table, CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(afterAdd, Does.Contain($"Id=\"{GridButtonId}\""), "button not in the compiled ribbon");
            Assert.That(afterAdd, Does.Contain(label), "caption not in the compiled ribbon");
            Assert.That(afterAdd, Does.Contain($"{GridButtonId}.Command"), "command not in the compiled ribbon");
        });

        var diff = await RibbonService.GetAsync(OrgUrl, Table, ct: CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(diff.CustomActions.Select(c => c.DiffId), Does.Contain($"{GridButtonId}.CustomAction"));
            Assert.That(diff.CommandDefinitions.Select(c => c.CommandId), Does.Contain($"{GridButtonId}.Command"));
            Assert.That(diff.Rules.Select(r => r.RuleId), Does.Contain($"{GridButtonId}.EnableRule"));
        });

        // --- REMOVE ------------------------------------------------------------------------------
        var removed = await RibbonService.RemoveButtonAsync(
            OrgUrl, Table, GridButtonId, ct: CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(removed.Success, Is.True);
            Assert.That(removed.Verified, Is.True);
            Assert.That(removed.DeletedDiffIds, Does.Contain($"{GridButtonId}.CustomAction"));
            Assert.That(removed.DeletedCommandIds, Does.Contain($"{GridButtonId}.Command"));
        });

        // --- AFTER -------------------------------------------------------------------------------
        var afterRemove = await RibbonService.RetrieveCompiledRibbonAsync(OrgUrl, Table, CancellationToken.None);
        Assert.Multiple(() =>
        {
            // The <Button> is no longer placed in any group, and its caption is gone with it. This is
            // the assertion the "empty <CustomActions />" approach could never satisfy.
            Assert.That(afterRemove, Does.Not.Contain($"Id=\"{GridButtonId}\""));
            Assert.That(afterRemove, Does.Not.Contain(label));
        });

        var diffAfter = await RibbonService.GetAsync(OrgUrl, Table, ct: CancellationToken.None);
        Assert.That(
            diffAfter.CustomActions.Select(c => c.DiffId),
            Does.Not.Contain($"{GridButtonId}.CustomAction"));
    }

    [Test, Order(4)]
    public async Task AddingASecondButton_LeavesTheFirstOneInPlace()
    {
        // Solution import merges rather than replaces, so a second round-trip must not wipe the first
        // button — even though the exported shell always comes back with an empty <RibbonDiffXml>.
        await RibbonService.AddButtonAsync(
            OrgUrl, Table, GridButtonId, GridLocation, "MCP Grid Probe", WebResourceName,
            "Sample.McpProbe.run",
            RibbonService.ParseParameterSpec("SelectedControlSelectedItemIds,SelectedControl"),
            ct: CancellationToken.None);

        await RibbonService.AddButtonAsync(
            OrgUrl, Table, FormButtonId, FormLocation, "MCP Form Probe", WebResourceName,
            "Sample.McpProbe.run",
            RibbonService.ParseParameterSpec("PrimaryControl"),
            ct: CancellationToken.None);

        var diff = await RibbonService.GetAsync(OrgUrl, Table, ct: CancellationToken.None);
        var diffIds = diff.CustomActions.Select(c => c.DiffId).ToList();

        Assert.That(diffIds, Does.Contain($"{GridButtonId}.CustomAction"));
        Assert.That(diffIds, Does.Contain($"{FormButtonId}.CustomAction"));

        // Removing one must not disturb the other.
        await RibbonService.RemoveButtonAsync(OrgUrl, Table, FormButtonId, ct: CancellationToken.None);

        var after = await RibbonService.GetAsync(OrgUrl, Table, ct: CancellationToken.None);
        var afterIds = after.CustomActions.Select(c => c.DiffId).ToList();

        Assert.That(afterIds, Does.Contain($"{GridButtonId}.CustomAction"));
        Assert.That(afterIds, Does.Not.Contain($"{FormButtonId}.CustomAction"));
    }

    [Test, Order(5)]
    public void RemoveButton_ExplainsItselfWhenNothingMatches()
    {
        var ex = Assert.ThrowsAsync<InvalidOperationException>(() => RibbonService.RemoveButtonAsync(
            OrgUrl, Table, "sample.sample_mcptest.NeverExisted.Button", ct: CancellationToken.None));

        Assert.That(ex!.Message, Does.Contain("No ribbon diff"));
    }

    /// <summary>
    /// Best-effort cleanup. Never fails the fixture: "nothing to remove" is the normal case, and a
    /// transient solution-operation conflict must not turn tear-down into a red test.
    /// </summary>
    private async Task TryRemoveAsync(string buttonId)
    {
        try
        {
            await RibbonService.RemoveButtonAsync(OrgUrl, Table, buttonId, ct: CancellationToken.None);
        }
        catch (InvalidOperationException)
        {
            // Nothing to clean up.
        }
        catch (HttpRequestException ex)
        {
            TestContext.Out.WriteLine($"Cleanup of '{buttonId}' skipped: {ex.Message}");
        }
    }
}
