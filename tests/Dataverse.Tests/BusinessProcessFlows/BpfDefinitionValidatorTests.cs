namespace Dataverse.Tests.BusinessProcessFlows;

using Dataverse.Core.BusinessProcessFlows;
using Dataverse.Core.Services;
using Dataverse.Core.Workflows;
using NUnit.Framework;

[TestFixture]
public sealed class BpfDefinitionValidatorTests
{
    private static IEnumerable<string> Codes(BpfDefinition definition, string severity = "error") =>
        BpfDefinitionValidator.Validate(definition).Issues.Where(i => i.Severity == severity).Select(i => i.Code);

    private static BpfDefinition Simple(params BpfStage[] stages) => new() { PrimaryEntity = "account", Stages = [.. stages] };

    private static BpfStage Stage(string name, params string[] fields) =>
        new() { Name = name, Steps = fields.Select(f => new BpfStep { Attribute = f }).ToList() };

    [Test]
    public void Full_IsValid()
    {
        var result = BpfDefinitionValidator.Validate(BpfTestData.Full());

        Assert.That(result.CanSave, Is.True, string.Join("; ", result.Issues.Select(i => $"{i.Code} {i.Path} {i.Problem}")));
        Assert.That(result.Issues, Is.Empty);
    }

    [Test]
    public void MissingPrimaryEntity_AndStages()
    {
        Assert.That(Codes(new BpfDefinition()), Is.SupersetOf(new[] { "BPF001", "BPF002" }));
    }

    [Test]
    public void FirstStageOnAnotherTable() =>
        Assert.That(Codes(Simple(Stage("A", "name") with { Entity = "contact" })), Does.Contain("BPF004"));

    [Test]
    public void StageWithoutName() =>
        Assert.That(Codes(Simple(Stage("", "name"))), Does.Contain("BPF003"));

    [Test]
    public void DuplicateKeysAndIds()
    {
        var a = Stage("A", "name") with { Key = "x" };
        var b = Stage("B", "name") with { Key = "x" };
        Assert.That(Codes(Simple(a, b)), Does.Contain("BPF005"));
    }

    [Test]
    public void MalformedId()
    {
        var a = Stage("A", "name");
        a.StageId = "not-a-guid";
        Assert.That(Codes(Simple(a)), Does.Contain("BPF006"));
    }

    [Test]
    public void UnknownCategory() =>
        Assert.That(Codes(Simple(Stage("A", "name") with { Category = "Later" })), Does.Contain("BPF007"));

    [Test]
    public void NextNamesNoStage_OrItself()
    {
        Assert.That(Codes(Simple(Stage("A", "name") with { Next = "Nowhere" })), Does.Contain("BPF010"));
        Assert.That(Codes(Simple(Stage("A", "name") with { Next = "A" }, Stage("B", "name"))), Does.Contain("BPF011"));
    }

    [Test]
    public void BranchProblems()
    {
        var empty = Stage("A", "name") with { Branch = new BpfBranching() };
        Assert.That(Codes(Simple(empty, Stage("B", "name"))), Does.Contain("BPF013"));

        var nowhere = Stage("A", "name") with
        {
            Branch = new BpfBranching
            {
                Branches = [new BpfBranch { Conditions = [new WorkflowCondition { Attribute = "name", Operator = "NotNull" }], Next = "Gone" }],
                Else = "A"
            }
        };
        Assert.That(Codes(Simple(nowhere, Stage("B", "name"))), Is.SupersetOf(new[] { "BPF012", "BPF015" }));
    }

    [Test]
    public void BranchOnAFieldThatIsNoStep()
    {
        var stage = Stage("A", "name") with
        {
            Branch = new BpfBranching
            {
                Branches = [new BpfBranch { Conditions = [new WorkflowCondition { Attribute = "revenue", Operator = "NotNull" }], Next = "B" }],
                Else = "C"
            }
        };
        Assert.That(Codes(Simple(stage, Stage("B", "name"), Stage("C", "name"))), Does.Contain("BPF024"));
    }

    [Test]
    public void BranchWithoutElse_IsAWarning()
    {
        var stage = Stage("A", "name") with
        {
            Branch = new BpfBranching
            {
                Branches = [new BpfBranch { Conditions = [new WorkflowCondition { Attribute = "name", Operator = "NotNull" }], Next = "B" }]
            }
        };
        var definition = Simple(stage, Stage("B", "name"));
        Assert.That(Codes(definition, "warning"), Does.Contain("BPF026"));
        Assert.That(BpfDefinitionValidator.Validate(definition).CanSave, Is.True);
    }

    [Test]
    public void ComparisonProblems()
    {
        var stage = Stage("A", "name") with
        {
            Branch = new BpfBranching
            {
                Branches =
                [
                    new BpfBranch
                    {
                        LogicalOperator = "Xor",
                        Conditions =
                        [
                            new WorkflowCondition { Attribute = "", Operator = "NotNull" },
                            new WorkflowCondition { Attribute = "name", Operator = "Equal", Via = "parentaccountid" },
                            new WorkflowCondition { Attribute = "name", Operator = "Last7Days" },
                            new WorkflowCondition { Attribute = "name", Operator = "Equal" }
                        ],
                        Next = "B"
                    }
                ],
                Else = "B"
            }
        };

        Assert.That(Codes(Simple(stage, Stage("B", "name"))), Is.SupersetOf(new[] { "BPF016", "BPF017", "BPF018", "BPF019" }));
    }

    [Test]
    public void StepProblems()
    {
        var stage = new BpfStage
        {
            Name = "A",
            Steps =
            [
                new BpfStep { Kind = "dialog" },
                new BpfStep(),
                new BpfStep { Kind = BpfStepKind.Action },
                new BpfStep { Kind = BpfStepKind.Action, ProcessId = BpfTestData.RecalculateWorkflow.ToString(), Required = true }
            ]
        };

        var definition = Simple(stage);
        Assert.That(Codes(definition), Is.SupersetOf(new[] { "BPF020", "BPF021", "BPF023" }));
        Assert.That(Codes(definition, "warning"), Does.Contain("BPF027"));
    }

    [Test]
    public void TriggerProblems()
    {
        var stage = Stage("A", "name") with
        {
            Workflows = [new BpfWorkflowTrigger { WorkflowId = "x", On = BpfTriggerEvent.Finished }]
        };

        Assert.That(Codes(Simple(stage)), Is.SupersetOf(new[] { "BPF023", "BPF025" }));
    }

    [Test]
    public void CrossTableStageWithoutRelationship()
    {
        var definition = Simple(Stage("A", "name"), Stage("B", "lastname") with { Entity = "contact" });
        Assert.That(Codes(definition), Does.Contain("BPF040"));
    }

    [Test]
    public void RelationshipProblems()
    {
        var b = Stage("B", "lastname") with
        {
            Entity = "contact",
            Relationship = new BpfRelationship { Name = "", FromStage = "Nowhere" }
        };
        Assert.That(Codes(Simple(Stage("A", "name"), b)), Is.SupersetOf(new[] { "BPF041", "BPF042" }));

        var sameTable = Stage("B", "description") with { Relationship = new BpfRelationship { Name = "x" } };
        Assert.That(Codes(Simple(Stage("A", "name"), sameTable), "warning"), Does.Contain("BPF043"));
    }

    [Test]
    public void Limits()
    {
        var many = Enumerable.Range(0, 31).Select(i => Stage($"S{i}", "name")).ToArray();
        Assert.That(Codes(Simple(many)), Does.Contain("BPF030"));

        var crowded = new BpfStage { Name = "A", Steps = Enumerable.Range(0, 31).Select(i => new BpfStep { Attribute = $"f{i}" }).ToList() };
        Assert.That(Codes(Simple(crowded)), Does.Contain("BPF031"));

        var tables = new[] { "account", "contact", "lead", "opportunity", "quote", "salesorder" }
            .Select((t, i) => Stage($"S{i}", "name") with
            {
                Entity = t,
                Relationship = i == 0 ? null : new BpfRelationship { Name = "r" }
            }).ToArray();
        Assert.That(Codes(Simple(tables)), Does.Contain("BPF032"));
    }

    [Test]
    public void UnreachableStage_AndLoop()
    {
        var unreachable = Simple(Stage("A", "name") with { Next = "end" }, Stage("B", "name"));
        Assert.That(Codes(unreachable, "warning"), Does.Contain("BPF050"));

        var loop = Simple(Stage("A", "name"), Stage("B", "name") with { Next = "A" });
        Assert.That(Codes(loop), Does.Contain("BPF051"));
    }

    [Test]
    public void AdoptExistingIds_MatchesStagesStepsAndTriggersByMeaning()
    {
        var existing = BpfTestData.Full();
        BpfXamlBuilder.Build(existing, BpfTestData.ProcessId, BpfTestData.Catalog());   // assigns every id

        var rewritten = BpfTestData.Full();   // same process written from scratch, without ids
        rewritten.Stages[4].Steps.Add(new BpfStep { Attribute = "lastname" });

        BusinessProcessFlowService.AdoptExistingIds(rewritten, existing);

        Assert.That(rewritten.Stages.Select(s => s.StageId), Is.EqualTo(existing.Stages.Select(s => s.StageId)));
        Assert.That(rewritten.Stages[0].Steps.Select(s => s.StepId), Is.EqualTo(existing.Stages[0].Steps.Select(s => s.StepId)));
        Assert.That(rewritten.Stages[4].Steps[1].StepId, Is.EqualTo(existing.Stages[4].Steps[1].StepId), "flow step matched by its flow");
        Assert.That(rewritten.Stages[4].Steps[2].StepId, Is.Null, "a new step gets a new id");
        Assert.That(rewritten.Stages[0].Workflows[0].TriggerId, Is.EqualTo(existing.Stages[0].Workflows[0].TriggerId));
        Assert.That(rewritten.Workflows[0].TriggerId, Is.EqualTo(existing.Workflows[0].TriggerId));
    }
}
