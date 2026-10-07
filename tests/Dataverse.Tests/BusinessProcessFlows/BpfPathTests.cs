namespace Dataverse.Tests.BusinessProcessFlows;

using System.Text.RegularExpressions;
using Dataverse.Core.BusinessProcessFlows;
using Dataverse.Core.Services;
using Dataverse.Core.Workflows;
using NUnit.Framework;

/// <summary>
/// Tables, relationships and loops follow the path between stages — main path and branches — never
/// the order the stages are listed in.
/// </summary>
[TestFixture]
public sealed class BpfPathTests
{
    private static BpfStage Stage(string name, params string[] fields) =>
        new() { Name = name, Steps = fields.Select(f => new BpfStep { Attribute = f, Required = true }).ToList() };

    private static BpfBranching Branch(string field, string then, string otherwise) => new()
    {
        Branches =
        [
            new BpfBranch
            {
                Conditions = [new WorkflowCondition { Attribute = field, Operator = "NotNull" }],
                Next = then
            }
        ],
        Else = otherwise
    };

    private static readonly BpfRelationship ToContact = new() { Name = "contact_customer_accounts" };

    private static IReadOnlyList<WorkflowValidationIssue> Issues(BpfDefinition definition) =>
        BpfDefinitionValidator.Validate(definition).Issues;

    [Test]
    public void StageWithoutEntity_InheritsFromTheStageLeadingToIt_NotFromTheOneListedAbove()
    {
        // Listed: Qualify, Close, Contact — but the path is Qualify → Contact → Close.
        var definition = new BpfDefinition
        {
            PrimaryEntity = "account",
            Stages =
            [
                Stage("Qualify", "name") with { Next = "Contact" },
                Stage("Close", "jobtitle") with { Next = "end" },
                Stage("Contact", "lastname") with { Entity = "contact", Relationship = ToContact, Next = "Close" }
            ]
        };

        var resolved = BpfStageResolver.Resolve(definition, assignMissingIds: false).Stages;

        Assert.That(resolved[1].Entity, Is.EqualTo("contact"), "Close follows Contact, so it is on contact");
        Assert.That(Issues(definition).Where(i => i.Severity == "error"), Is.Empty,
            string.Join("; ", Issues(definition).Select(i => i.Code + " " + i.Problem)));
    }

    [Test]
    public void StageReachedFromDifferentTables_MustNameItsTable()
    {
        var definition = new BpfDefinition
        {
            PrimaryEntity = "account",
            Stages =
            [
                Stage("Qualify", "name") with { Branch = Branch("name", "Contact", "Account review") },
                Stage("Contact", "lastname") with { Entity = "contact", Relationship = ToContact, Next = "Wrap up" },
                Stage("Account review", "description") with { Next = "Wrap up" },
                Stage("Wrap up", "telephone1")
            ]
        };

        Assert.That(Issues(definition).Select(i => i.Code), Does.Contain("BPF044"));
    }

    [Test]
    public void BranchIntoAnotherTable_NeedsTheRelationship_LikeTheMainPath()
    {
        var definition = new BpfDefinition
        {
            PrimaryEntity = "account",
            Stages =
            [
                Stage("Qualify", "name") with { Branch = Branch("name", "Contact", "Close") },
                Stage("Close", "description") with { Next = "end" },
                Stage("Contact", "lastname") with { Entity = "contact" }
            ]
        };

        var issue = Issues(definition).Single(i => i.Code == "BPF040");

        Assert.That(issue.Path, Is.EqualTo("$.stages[2].relationship"));
        Assert.That(issue.Problem, Does.Contain("'Qualify' on 'account'"));
        Assert.That(issue.Fix, Does.Contain("bpf_find_relationships"));
    }

    [Test]
    public void TwoWaysFromOneTable_ShareTheRelationship_OneEntryEach_AndRoundTrip()
    {
        var definition = BpfTestData.Full();   // Large and Small both lead into Contact
        var build = BpfXamlBuilder.Build(definition, BpfTestData.ProcessId, BpfTestData.Catalog());

        var entries = Regex.Matches(build.Xaml, "<mcwb:StageRelationship [^>]*>");
        var large = build.Stages.Single(s => s.Name == "Large").StageId;
        var small = build.Stages.Single(s => s.Name == "Small").StageId;

        Assert.That(entries, Has.Count.EqualTo(2));
        Assert.That(entries.Select(m => m.Value), Has.Some.Contains($"SourceStageId=\"{large}\""));
        Assert.That(entries.Select(m => m.Value), Has.Some.Contains($"SourceStageId=\"{small}\""));
        Assert.That(BpfDefinitionValidator.Validate(definition).Issues.Select(i => i.Code), Does.Not.Contain("BPF045"));

        var parsed = BpfXamlParser.Parse(build.Xaml, "account");
        Assert.That(parsed.FullyUnderstood, Is.True, string.Join("; ", parsed.Unrecognised));
        Assert.That(parsed.Definition.Stages.Single(s => s.Name == "Contact").Relationship!.FromStage, Is.Null);
    }

    [Test]
    public void FromStage_PinsTheRelationshipToOneWay_AndWarnsAboutTheOther()
    {
        var definition = BpfTestData.Full();
        definition.Stages[4] = definition.Stages[4] with { Relationship = ToContact with { FromStage = "Large" } };

        var build = BpfXamlBuilder.Build(definition, BpfTestData.ProcessId, BpfTestData.Catalog());

        Assert.That(Regex.Matches(build.Xaml, "<mcwb:StageRelationship "), Has.Count.EqualTo(1));
        Assert.That(BpfDefinitionValidator.Validate(definition).Issues.Single(i => i.Code == "BPF045").Severity, Is.EqualTo("warning"));
    }

    [Test]
    public void WaysFromTwoTables_CannotShareOneRelationship()
    {
        var definition = new BpfDefinition
        {
            PrimaryEntity = "account",
            Stages =
            [
                Stage("Qualify", "name") with { Branch = Branch("name", "Lead", "Contact") },
                Stage("Lead", "subject") with { Entity = "lead", Relationship = new BpfRelationship { Name = "lead_parent_account" }, Next = "Order" },
                Stage("Contact", "lastname") with { Entity = "contact", Relationship = ToContact, Next = "Order" },
                Stage("Order", "name") with { Entity = "salesorder", Relationship = new BpfRelationship { Name = "lead_orders", FromStage = "Lead" } }
            ]
        };

        Assert.That(Issues(definition).Where(i => i.Code == "BPF045").Select(i => i.Severity), Does.Contain("error"));
    }

    [Test]
    public void BranchBackToAnEarlierStage_IsALoop()
    {
        var definition = new BpfDefinition
        {
            PrimaryEntity = "account",
            Stages =
            [
                Stage("Qualify", "name"),
                Stage("Review", "description") with { Branch = Branch("description", "Close", "Qualify") },
                Stage("Close", "telephone1")
            ]
        };

        Assert.That(Issues(definition).Select(i => i.Code), Does.Contain("BPF052"));
    }

    [Test]
    public void ElseEnd_IsRefusedWithTheWayToEndABranch()
    {
        var definition = new BpfDefinition
        {
            PrimaryEntity = "account",
            Stages = [Stage("Qualify", "name") with { Branch = Branch("name", "Close", "end") }, Stage("Close", "description")]
        };

        Assert.That(Issues(definition).Single(i => i.Path == "$.stages[0].branch.else").Fix, Does.Contain("\"next\": \"end\""));
    }

    [Test]
    public void BranchOnAnOptionalField_Warns()
    {
        var definition = new BpfDefinition
        {
            PrimaryEntity = "account",
            Stages =
            [
                new BpfStage { Name = "Qualify", Steps = [new BpfStep { Attribute = "name" }], Branch = Branch("name", "Close", "Small") },
                Stage("Small", "description") with { Next = "end" },
                Stage("Close", "telephone1")
            ]
        };

        Assert.That(Issues(definition).Single(i => i.Code == "BPF028").Severity, Is.EqualTo("warning"));
    }

    [TestCase("AND")]
    [TestCase("or")]
    public void LogicalOperator_IgnoresCase(string op)
    {
        var definition = BpfTestData.Full();
        definition.Stages[1] = definition.Stages[1] with
        {
            Branch = definition.Stages[1].Branch! with
            {
                Branches = [definition.Stages[1].Branch!.Branches[0] with { LogicalOperator = op }]
            }
        };

        Assert.That(Issues(definition).Select(i => i.Code), Does.Not.Contain("BPF016"));
    }

    [Test]
    public void ComparedValue_ReadingAnotherTable_IsRefused()
    {
        var definition = BpfTestData.Full();
        var branch = definition.Stages[1].Branch!.Branches[0];
        definition.Stages[1] = definition.Stages[1] with
        {
            Branch = definition.Stages[1].Branch! with
            {
                Branches =
                [
                    branch with
                    {
                        Conditions =
                        [
                            new WorkflowCondition
                            {
                                Attribute = "numberofemployees", Operator = "Equal",
                                Value = new WorkflowValue { Kind = WorkflowValueKind.Field, Fields = ["contact.numberofchildren"] }
                            },
                            new WorkflowCondition
                            {
                                Attribute = "sample_score", Operator = "Equal",
                                Value = new WorkflowValue { Kind = WorkflowValueKind.Field, Fields = ["numberofemployees"], Via = "primarycontactid" }
                            }
                        ]
                    }
                ]
            }
        };

        Assert.That(Issues(definition).Count(i => i.Code == "BPF018"), Is.EqualTo(2));
    }

    [Test]
    public void Parse_LeavesOutOnlyTheTablesThePathImplies()
    {
        var definition = new BpfDefinition
        {
            PrimaryEntity = "account",
            Stages =
            [
                Stage("Qualify", "name") with { Next = "Contact" },
                Stage("Close", "jobtitle") with { Next = "end" },
                Stage("Contact", "lastname") with { Entity = "contact", Relationship = ToContact with { Attribute = "parentcustomerid" }, Next = "Close" }
            ]
        };

        var xaml = BpfXamlBuilder.Build(definition, BpfTestData.ProcessId).Xaml;
        var parsed = BpfXamlParser.Parse(xaml, "account").Definition;

        Assert.That(parsed.Stages.Select(s => s.Entity), Is.EqualTo(new string?[] { null, null, "contact" }));
        Assert.That(BpfStageResolver.Resolve(parsed, assignMissingIds: false).Stages.Select(s => s.Entity),
            Is.EqualTo(new[] { "account", "contact", "contact" }));
    }

    [Test]
    public void StageWithoutSteps_IsRefused() =>
        Assert.That(Issues(new BpfDefinition { PrimaryEntity = "account", Stages = [new BpfStage { Name = "Empty" }] })
            .Single(i => i.Code == "BPF022").Severity, Is.EqualTo("error"));

    [Test]
    public void StageNamedEnd_Warns()
    {
        var definition = new BpfDefinition { PrimaryEntity = "account", Stages = [Stage("Start", "name"), Stage("End", "description")] };

        Assert.That(Issues(definition).Single(i => i.Code == "BPF053").Path, Is.EqualTo("$.stages[1].name"));
    }

    [Test]
    public void CaseLeadingWhereElseLeads_Warns()
    {
        var definition = new BpfDefinition
        {
            PrimaryEntity = "account",
            Stages = [Stage("Qualify", "name") with { Branch = Branch("name", "Close", "Close") }, Stage("Close", "description")]
        };

        Assert.That(Issues(definition).Single(i => i.Code == "BPF029").Severity, Is.EqualTo("warning"));
    }

    // ---------------------------------------------------------------- rewriting

    [Test]
    public void LooksRenamed_SameTableSamePosition_PointsAtTheOldId()
    {
        var before = new BpfDefinition
        {
            PrimaryEntity = "account",
            Stages = [Stage("A", "name") with { StageId = "11111111-1111-1111-1111-111111110201" }, Stage("B", "description") with { StageId = "11111111-1111-1111-1111-111111110202" }]
        };
        var after = new BpfDefinition
        {
            PrimaryEntity = "account",
            Stages = [Stage("A", "name") with { StageId = "11111111-1111-1111-1111-111111110201" }, Stage("B2", "description")]
        };

        var issue = BusinessProcessFlowService.LooksRenamed(before, after).Single();

        Assert.That(issue.Code, Is.EqualTo("BPF062"));
        Assert.That(issue.Fix, Does.Contain("11111111-1111-1111-1111-111111110202"));
    }

    [Test]
    public void DescribeChange_NamesRenamesStepsBranchesAndTriggers()
    {
        var before = BpfTestData.Full();
        BpfXamlBuilder.Build(before, BpfTestData.ProcessId, BpfTestData.Catalog());   // assigns ids
        var after = BpfXamlParser.Parse(BpfXamlBuilder.Build(before, BpfTestData.ProcessId, BpfTestData.Catalog()).Xaml, "account").Definition;

        after.Stages[0] = after.Stages[0] with { Name = "Capture data" };
        after.Stages[1] = after.Stages[1] with { Branch = after.Stages[1].Branch! with { Else = "Large" } };
        after.Stages[2] = after.Stages[2] with { Steps = [.. after.Stages[2].Steps.Select(s => s with { Required = true })] };
        after.Stages[0].Workflows.Clear();

        var diff = BusinessProcessFlowService.DescribeChange(before, after);

        Assert.That(diff, Has.Some.Contains("'Capture' renamed to 'Capture data'"));
        Assert.That(diff, Has.Some.Contains("'Assess': branching changed"));
        Assert.That(diff, Has.Some.Contains("step description now required"));
        Assert.That(diff, Has.Some.Contains("- stage 'Capture data': workflow"));
    }
}
