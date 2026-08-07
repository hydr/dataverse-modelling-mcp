namespace Dataverse.Tests.Workflows;

using System.Text.RegularExpressions;
using Dataverse.Core.Workflows;
using NUnit.Framework;

/// <summary>
/// The cancellation message, and why a field inside it must be read without a target type.
/// </summary>
/// <remarks>
/// A field read that names x:String as its target type returns nothing for a value that is not text.
/// In an attribute assignment that is harmless — the value is converted on the way in — but in a
/// cancellation message it empties the message, and Dataverse then shows the step's display name
/// instead of the sentence: a message ending in a date surfaced as the bare step name. Comparing the
/// generated XAML against a designer-authored workflow that prints its date showed a difference in
/// exactly these two target types and nowhere else.
/// </remarks>
[TestFixture]
public sealed class StopWorkflowReasonTests
{
    private static string BuildWith(WorkflowValue reason) => WorkflowXamlBuilder.Build(new WorkflowDefinition
    {
        PrimaryEntity = "account",
        Steps =
        [
            new WorkflowStep
            {
                Kind = WorkflowStepKind.StopWorkflow,
                Description = "Stop the workflow",
                Outcome = "cancelled",
                Reason = reason
            }
        ]
    }).Xaml;

    /// <summary>TargetType of every GetEntityProperty, by attribute.</summary>
    private static List<(string Attribute, string TargetType)> Reads(string xaml) =>
        [.. Regex.Matches(xaml,
                """<mxswa:GetEntityProperty Attribute="([^"]+)".*?<mxswa:GetEntityProperty\.TargetType>(.*?)</mxswa:GetEntityProperty\.TargetType>""",
                RegexOptions.Singleline)
            .Select(m => (m.Groups[1].Value,
                m.Groups[2].Value.Contains("Value=\"x:String\"", StringComparison.Ordinal)
                    ? "x:String" : "null"))];

    [Test]
    public void FieldInsideAComposedReason_IsReadUntyped()
    {
        var xaml = BuildWith(new WorkflowValue
        {
            Kind = WorkflowValueKind.Concat,
            Parts =
            [
                new WorkflowValue { Kind = WorkflowValueKind.Literal, Literal = "Already contacted on " },
                new WorkflowValue { Kind = WorkflowValueKind.Field, Fields = ["account.new_lastcontacted"] }
            ]
        });

        var read = Reads(xaml).Single(r => r.Attribute == "new_lastcontacted");

        Assert.That(read.TargetType, Is.EqualTo("null"),
            "a typed read empties a date and the message is lost");
    }

    [Test]
    public void SelectFirstNonNull_OfThatField_IsAlsoUntyped()
    {
        var xaml = BuildWith(new WorkflowValue
        {
            Kind = WorkflowValueKind.Concat,
            Parts =
            [
                new WorkflowValue { Kind = WorkflowValueKind.Literal, Literal = "on " },
                new WorkflowValue { Kind = WorkflowValueKind.Field, Fields = ["account.new_lastcontacted"] }
            ]
        });

        var selects = Regex.Matches(xaml,
                """ExpressionOperator">SelectFirstNonNull<.*?x:Key="TargetType">(.*?)</InArgument>""",
                RegexOptions.Singleline)
            .Select(m => m.Groups[1].Value)
            .ToList();

        Assert.That(selects, Is.Not.Empty);
        Assert.That(selects.All(s => s.Contains("<x:Null />", StringComparison.Ordinal)), Is.True,
            "the designer leaves this untyped too");
    }

    [Test]
    public void APlainFieldReason_IsReadUntyped()
    {
        var xaml = BuildWith(new WorkflowValue
        {
            Kind = WorkflowValueKind.Field,
            Fields = ["account.new_lastcontacted"]
        });

        Assert.That(Reads(xaml).Single().TargetType, Is.EqualTo("null"));
    }

    [Test]
    public void AttributeAssignments_KeepTheirTargetType()
    {
        // The rule is specific to the message; elsewhere the type is what makes the value land.
        var xaml = WorkflowXamlBuilder.Build(new WorkflowDefinition
        {
            PrimaryEntity = "account",
            Steps =
            [
                new WorkflowStep
                {
                    Kind = WorkflowStepKind.UpdateRecord,
                    Entity = "account",
                    Attributes =
                    [
                        new WorkflowAttributeAssignment
                        {
                            Attribute = "description",
                            Value = new WorkflowValue
                            {
                                Kind = WorkflowValueKind.Field,
                                Fields = ["account.new_lastcontacted"]
                            }
                        }
                    ]
                }
            ]
        }).Xaml;

        Assert.That(Reads(xaml).Single().TargetType, Is.EqualTo("x:String"));
    }
}
