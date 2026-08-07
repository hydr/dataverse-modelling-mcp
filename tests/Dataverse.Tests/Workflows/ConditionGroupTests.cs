namespace Dataverse.Tests.Workflows;

using System.Text.RegularExpressions;
using Dataverse.Core.Workflows;
using NUnit.Framework;

/// <summary>
/// Bracketed comparisons — "A And (B Or C)" — and the round trip that carries them.
/// </summary>
/// <remarks>
/// One level of a condition combines its comparisons with a single operator, so a mixture needs a
/// bracket. The XAML has always been able to express it: EvaluateLogicalCondition takes a left and a
/// right operand and either may be another node's result, which makes the combination a tree. The
/// reference is `condition-group.xaml`, a designer-authored workflow that brackets one pair.
/// </remarks>
[TestFixture]
public sealed class ConditionGroupTests
{
    private static string FixturePath(string name) => Path.Combine(
        TestContext.CurrentContext.TestDirectory, "Workflows", "Fixtures", name);

    private static WorkflowDefinition Group(string outerOperator, string innerOperator) => new()
    {
        PrimaryEntity = "account",
        Steps =
        [
            new WorkflowStep
            {
                Kind = WorkflowStepKind.Condition,
                Description = "Bracketed",
                LogicalOperator = outerOperator,
                Conditions =
                [
                    new WorkflowCondition { Attribute = "name", Operator = "NotNull" },
                    new WorkflowCondition
                    {
                        GroupOperator = innerOperator,
                        Conditions =
                        [
                            new WorkflowCondition { Attribute = "emailaddress1", Operator = "NotNull" },
                            new WorkflowCondition { Attribute = "websiteurl", Operator = "NotNull" }
                        ]
                    }
                ],
                Then = [new WorkflowStep { Kind = WorkflowStepKind.StopWorkflow, Outcome = "succeeded" }]
            }
        ]
    };

    /// <summary>Operator, left and right of every EvaluateLogicalCondition, in document order.</summary>
    private static List<(string Operator, string Left, string Right, string Result)> Combinations(string xaml) =>
        [.. Regex.Matches(xaml,
                """EvaluateLogicalCondition,.*?LogicalOperator">(\w+)<.*?LeftOperand">\[([^\]]+)\].*?RightOperand">\[([^\]]+)\].*?Result">\[([^\]]+)\]""",
                RegexOptions.Singleline)
            .Select(m => (m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value, m.Groups[4].Value))];

    [Test]
    public void Group_BecomesItsOwnNode_FeedingTheOuterOperator()
    {
        var xaml = WorkflowXamlBuilder.Build(Group("And", "Or")).Xaml;
        var nodes = Combinations(xaml);

        Assert.That(nodes, Has.Count.EqualTo(2), "one node for the group, one for the level above");

        var inner = nodes.Single(n => n.Operator == "Or");
        var outer = nodes.Single(n => n.Operator == "And");

        Assert.Multiple(() =>
        {
            Assert.That(outer.Right, Is.EqualTo(inner.Result),
                "the group's result must be an operand of the outer node — that is the bracket");
            Assert.That(outer.Result, Does.EndWith("_condition"),
                "the outer node writes the branch's condition variable");
        });
    }

    [Test]
    public void Validation_AcceptsAGroup_InsteadOfDemandingAnAttribute()
    {
        // A group carries no attribute and no operator of its own; without the recursion the model
        // check would reject it with WF071 and the feature would be unusable.
        var result = WorkflowDefinitionValidator.Validate(Group("And", "Or"));

        Assert.That(result.Issues.Where(i => i.Severity == "error"), Is.Empty,
            string.Join("; ", result.Issues.Select(i => $"{i.Code} {i.Path} {i.Problem}")));
    }

    [Test]
    public void Validation_RejectsAnUnknownGroupOperator()
    {
        var result = WorkflowDefinitionValidator.Validate(Group("And", "Xor"));

        Assert.That(result.Issues.Select(i => i.Code), Does.Contain("WF076"));
    }

    [Test]
    public void Validation_ReachesComparisonsInsideAGroup()
    {
        var definition = new WorkflowDefinition
        {
            PrimaryEntity = "account",
            Steps =
            [
                new WorkflowStep
                {
                    Kind = WorkflowStepKind.Condition,
                    Conditions =
                    [
                        new WorkflowCondition
                        {
                            GroupOperator = "Or",
                            Conditions =
                            [
                                // Equal without a value — must still be caught two levels down.
                                new WorkflowCondition { Attribute = "name", Operator = "Equal" }
                            ]
                        }
                    ],
                    Then = [new WorkflowStep { Kind = WorkflowStepKind.StopWorkflow }]
                }
            ]
        };

        var result = WorkflowDefinitionValidator.Validate(definition);

        Assert.That(result.Issues.Select(i => i.Code), Does.Contain("WF073"));
    }

    [Test]
    public void GeneratedXaml_PassesItsSelfCheck()
    {
        var result = WorkflowXamlBuilder.Build(Group("And", "Or"));
        var check = WorkflowDefinitionValidator.ValidateGeneratedXaml(result.Xaml);

        Assert.That(check.CanSave, Is.True,
            string.Join("; ", check.Issues.Select(i => $"{i.Code} {i.Problem}")));
    }

    [Test]
    public void Group_SurvivesTheRoundTrip()
    {
        var xaml = WorkflowXamlBuilder.Build(Group("And", "Or")).Xaml;

        var parsed = WorkflowXamlParser.Parse(xaml, "account").Definition;
        var step = parsed.Steps.Single(s => s.Kind == WorkflowStepKind.Condition);
        var conditions = step.Conditions ?? step.Branches![0].Conditions;
        var logical = step.LogicalOperator ?? step.Branches![0].LogicalOperator;

        Assert.Multiple(() =>
        {
            Assert.That(logical, Is.EqualTo("And"));
            Assert.That(conditions, Has.Count.EqualTo(2), "the bracket must not be flattened away");
            Assert.That(conditions[0].IsGroup, Is.False);
            Assert.That(conditions[0].Attribute, Is.EqualTo("name"));
            Assert.That(conditions[1].IsGroup, Is.True);
            Assert.That(conditions[1].GroupOperator, Is.EqualTo("Or"));
            Assert.That(conditions[1].Conditions!.Select(c => c.Attribute),
                Is.EqualTo(new[] { "emailaddress1", "websiteurl" }));
        });
    }

    [Test]
    public void PlainChain_IsStillReadAsAFlatList()
    {
        // Same operator throughout: no group, however deep the tree is that produced it.
        var definition = new WorkflowDefinition
        {
            PrimaryEntity = "account",
            Steps =
            [
                new WorkflowStep
                {
                    Kind = WorkflowStepKind.Condition,
                    LogicalOperator = "Or",
                    Conditions =
                    [
                        new WorkflowCondition { Attribute = "name", Operator = "NotNull" },
                        new WorkflowCondition { Attribute = "emailaddress1", Operator = "NotNull" },
                        new WorkflowCondition { Attribute = "websiteurl", Operator = "NotNull" }
                    ],
                    Then = [new WorkflowStep { Kind = WorkflowStepKind.StopWorkflow, Outcome = "succeeded" }]
                }
            ]
        };

        var xaml = WorkflowXamlBuilder.Build(definition).Xaml;
        var parsed = WorkflowXamlParser.Parse(xaml, "account").Definition;
        var step = parsed.Steps.Single(s => s.Kind == WorkflowStepKind.Condition);
        var conditions = step.Conditions ?? step.Branches![0].Conditions;

        Assert.Multiple(() =>
        {
            Assert.That(conditions, Has.Count.EqualTo(3));
            Assert.That(conditions.Any(c => c.IsGroup), Is.False, "a uniform chain is not a group");
        });
    }

    [Test]
    public void TheDesignerAuthoredGroup_IsReadAsAGroup()
    {
        // The real thing: And(name-check, Or(e-mail, website)) as the designer wrote it.
        var parsed = WorkflowXamlParser.Parse(File.ReadAllText(FixturePath("condition-group.xaml")), "account");

        var groups = AllConditions(parsed.Definition).Where(c => c.IsGroup).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(groups, Has.Count.EqualTo(1), "the fixture brackets exactly one pair");
            Assert.That(groups[0].GroupOperator, Is.EqualTo("Or"));
            Assert.That(groups[0].Conditions, Has.Count.EqualTo(2));
            Assert.That(parsed.FullyUnderstood, Is.True,
                "a group must not push the workflow into 'not understood': "
                + string.Join("; ", parsed.Unrecognised));
        });
    }

    private static IEnumerable<WorkflowCondition> AllConditions(WorkflowDefinition definition)
    {
        foreach (var step in definition.Steps.SelectMany(Flatten))
        {
            foreach (var condition in (step.Conditions ?? [])
                         .Concat((step.Branches ?? []).SelectMany(b => b.Conditions)))
                foreach (var nested in Expand(condition))
                    yield return nested;
        }

        static IEnumerable<WorkflowCondition> Expand(WorkflowCondition condition)
        {
            yield return condition;
            foreach (var child in condition.Conditions ?? [])
                foreach (var nested in Expand(child))
                    yield return nested;
        }

        static IEnumerable<WorkflowStep> Flatten(WorkflowStep step)
        {
            yield return step;
            foreach (var child in (step.Then ?? []).Concat(step.Else ?? []).Concat(step.Children ?? [])
                         .Concat((step.Branches ?? []).SelectMany(b => b.Steps ?? [])))
                foreach (var nested in Flatten(child))
                    yield return nested;
        }
    }
}
