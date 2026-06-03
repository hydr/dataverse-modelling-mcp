namespace Dataverse.Tests.Integration;

using Dataverse.Core.Workflows;
using Dataverse.Tests.Infrastructure;
using NUnit.Framework;

/// <summary>
/// End-to-end coverage for template-based workflow create / interpret / update / delete on contoso-dev.
/// Opt-in (DATAVERSE_INTEGRATION_TESTS=true). Workflows are created in the Draft state (not
/// activated) to avoid side effects on real records, and deleted in teardown.
/// </summary>
[TestFixture]
[Category("Integration")]
public sealed class WorkflowTemplateIntegrationTests : IntegrationTestBase
{
    private Guid _createdId = Guid.Empty;

    [TearDown]
    public async Task TearDown()
    {
        if (_createdId != Guid.Empty)
        {
            try { await WorkflowService.SetStateAsync(OrgUrl, _createdId, activate: false); }
            catch { /* may already be draft */ }
            try { await WorkflowService.DeleteAsync(OrgUrl, _createdId); }
            catch { /* best-effort cleanup */ }
            _createdId = Guid.Empty;
        }
    }

    /// <summary>Cleanup helper for manual MCP-protocol testing: removes leftover test artifacts.</summary>
    [Test, Explicit]
    public async Task Cleanup_ManualTestArtifacts()
    {
        var workflows = await WorkflowService.ListAsync(OrgUrl, "contains(name,'(delete me)')", 50);
        foreach (var wf in workflows)
        {
            try { await WorkflowService.SetStateAsync(OrgUrl, wf.WorkflowId, activate: false); } catch { }
            try { await WorkflowService.DeleteAsync(OrgUrl, wf.WorkflowId); TestContext.WriteLine($"Deleted workflow {wf.Name} ({wf.WorkflowId})"); } catch (Exception ex) { TestContext.WriteLine($"Could not delete {wf.WorkflowId}: {ex.Message}"); }
        }

        try { await TableService.DeleteColumnAsync(OrgUrl, "account", "sample_mcprolluptest"); TestContext.WriteLine("Deleted rollup column sample_mcprolluptest"); }
        catch (Exception ex) { TestContext.WriteLine($"Rollup column cleanup: {ex.Message}"); }
    }

    [Test]
    public async Task Create_Interpret_Update_UpdateEntityWorkflow()
    {
        var def = new WorkflowDefinition(
            PrimaryEntity: "account",
            Mode: WorkflowMode.Realtime,
            Scope: WorkflowScope.Organization,
            Trigger: new WorkflowTrigger(OnDemand: true),
            Steps:
            [
                new UpdateEntityStep("account",
                [
                    new FieldAssignment("description", CrmValueType.String, new LiteralValue(CrmValueType.String, "Set by MCP integration test"))
                ])
            ]);

        var created = await WorkflowService.CreateFromDefinitionAsync(
            OrgUrl, "MCP Template Test (delete me)", def, "integration test", activate: false);

        Assert.That(created.Validation.IsValid, Is.True, string.Join("; ", created.Validation.Errors));
        Assert.That(created.Written, Is.True);
        Assert.That(created.WorkflowId, Is.Not.EqualTo(Guid.Empty));
        _createdId = created.WorkflowId;

        var interpreted = await WorkflowService.InterpretAsync(OrgUrl, _createdId);
        Assert.That(interpreted.IsWellFormed, Is.True);
        Assert.That(interpreted.IsTemplateRecognized, Is.True, string.Join("; ", interpreted.Notes));
        Assert.That(interpreted.Steps, Has.One.Matches<InterpretedStep>(s => s.Kind == "UpdateEntity"));

        // Edit: add a second field and re-write.
        var updatedDef = def with
        {
            Steps =
            [
                new UpdateEntityStep("account",
                [
                    new FieldAssignment("description", CrmValueType.String, new LiteralValue(CrmValueType.String, "Updated by MCP")),
                    new FieldAssignment("fax", CrmValueType.String, new LiteralValue(CrmValueType.String, "n/a")),
                ])
            ]
        };

        var updated = await WorkflowService.UpdateFromDefinitionAsync(OrgUrl, _createdId, updatedDef);
        Assert.That(updated.Validation.IsValid, Is.True, string.Join("; ", updated.Validation.Errors));
        Assert.That(updated.Written, Is.True);

        var reInterpreted = await WorkflowService.InterpretAsync(OrgUrl, _createdId);
        var step = reInterpreted.Steps.Single(s => s.Kind == "UpdateEntity");
        Assert.That(step.Fields, Does.Contain("fax"));
    }

    [Test]
    public async Task Create_CustomActivityWorkflow_Background()
    {
        // Uses a custom activity known to exist in contoso-dev (Sample.CrmPlugins). If it is not registered,
        // creation still succeeds (registration is a non-blocking warning).
        var def = new WorkflowDefinition(
            PrimaryEntity: "opportunity",
            Mode: WorkflowMode.Background,
            Scope: WorkflowScope.Organization,
            Trigger: new WorkflowTrigger(OnDemand: true),
            Steps:
            [
                new CustomActivityStep(
                    AssemblyQualifiedName: "Sample.CrmPlugins.Utils.GetEntityIdAsGuid, Sample.CrmPlugins, Version=1.0.0.0, Culture=neutral, PublicKeyToken=6cd3b47345c1c112",
                    Label: "GetOpportunityIdAsGuid",
                    InputArguments: [],
                    OutputArguments: [new ActivityArgument("EntityId", CrmValueType.String)])
            ]);

        // Activate to prove the generated XAML compiles for execution (stricter than draft create).
        var created = await WorkflowService.CreateFromDefinitionAsync(
            OrgUrl, "MCP Custom Activity Test (delete me)", def, null, activate: true);

        Assert.That(created.Written, Is.True, string.Join("; ", created.Validation.Errors));
        Assert.That(created.Activated, Is.True);
        _createdId = created.WorkflowId;

        var interpreted = await WorkflowService.InterpretAsync(OrgUrl, _createdId);
        Assert.That(interpreted.Steps, Has.One.Matches<InterpretedStep>(s => s.Kind == "CustomActivity"));
    }
}
