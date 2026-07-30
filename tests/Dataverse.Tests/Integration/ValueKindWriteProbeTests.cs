namespace Dataverse.Tests.Integration;

using System.Text.RegularExpressions;
using Dataverse.Core.Workflows;
using Dataverse.Tests.Infrastructure;
using NUnit.Framework;

/// <summary>
/// Writes one value shape per workflow to find out which of them Dataverse accepts. A rejected XAML
/// answers <c>0x80045040</c> on the PATCH already, so no activation is needed.
/// </summary>
[TestFixture]
[Category("Integration")]
[Explicit("Diagnostic probe — creates and deletes workflows in the target environment.")]
public sealed class ValueKindWriteProbeTests : IntegrationTestBase
{
    private static WorkflowStep UpdateWith(string attribute, WorkflowValue value) => new()
    {
        Kind = WorkflowStepKind.UpdateRecord,
        Attributes = [new WorkflowAttributeAssignment { Attribute = attribute, Value = value }]
    };

    private static WorkflowStep ConditionOn(WorkflowCondition condition) => new()
    {
        Kind = WorkflowStepKind.Condition,
        Conditions = [condition],
        Then = [UpdateWith("description", new WorkflowValue { Literal = "x" })]
    };

    private static IEnumerable<(string Name, WorkflowDefinition Definition)> Cases()
    {
        yield return ("1 now as a written DateTime value", new WorkflowDefinition
        {
            PrimaryEntity = "salesorder",
            Steps = [UpdateWith("requestdeliveryby",
                new WorkflowValue { Kind = WorkflowValueKind.Now, DataType = "DateTime" })]
        });

        yield return ("2 now compared with OnOrAfter", new WorkflowDefinition
        {
            PrimaryEntity = "salesorder",
            Steps =
            [
                ConditionOn(new WorkflowCondition
                {
                    Attribute = "createdon",
                    Operator = "OnOrAfter",
                    Value = new WorkflowValue { Kind = WorkflowValueKind.Now, DataType = "DateTime" }
                })
            ]
        });

        yield return ("3 concat of constant and field", new WorkflowDefinition
        {
            PrimaryEntity = "salesorder",
            Steps =
            [
                UpdateWith("description", new WorkflowValue
                {
                    Kind = WorkflowValueKind.Concat,
                    DataType = "String",
                    Parts =
                    [
                        new WorkflowValue { Literal = "Auftrag " },
                        new WorkflowValue { Kind = WorkflowValueKind.Field, Fields = ["salesorder.ordernumber"] }
                    ]
                })
            ]
        });

        yield return ("4 In with three option values", new WorkflowDefinition
        {
            PrimaryEntity = "salesorder",
            Steps =
            [
                ConditionOn(new WorkflowCondition
                {
                    Attribute = "statuscode",
                    Operator = "In",
                    Value = new WorkflowValue { DataType = "OptionSetValue", Literals = ["1", "2", "3"] }
                })
            ]
        });

        // A plain DateTime constant — to tell a DateTime problem from a "now" problem.
        yield return ("5 plain DateTime literal", new WorkflowDefinition
        {
            PrimaryEntity = "salesorder",
            Steps = [UpdateWith("requestdeliveryby",
                new WorkflowValue { DataType = "DateTime", Literal = "2026-01-01T00:00:00Z" })]
        });
    }

    [Test]
    public async Task Probe_WhichValueKindsAreAccepted()
    {
        foreach (var (name, definition) in Cases())
        {
            var id = Guid.Empty;
            try
            {
                id = await WorkflowService.CreateAsync(OrgUrl, $"ZZ VK {Guid.NewGuid():N}", "salesorder");
                var save = await WorkflowAuthoringService.SetDefinitionAsync(OrgUrl, id, definition);

                TestContext.Out.WriteLine(save.Applied
                    ? $"VALUE {name}: WRITTEN"
                    : $"VALUE {name}: REFUSED -> " +
                      string.Join(" | ", save.Validation.Issues.Select(i => $"{i.Code} {i.Path}")));
            }
            catch (Exception ex)
            {
                var code = Regex.Match(ex.Message, @"0x[0-9a-fA-F]{8}").Value;
                TestContext.Out.WriteLine($"VALUE {name}: FAILED -> {code}");
            }
            finally
            {
                if (id != Guid.Empty)
                    try { await WorkflowService.DeleteAsync(OrgUrl, id); } catch { }
            }
        }
    }
}
