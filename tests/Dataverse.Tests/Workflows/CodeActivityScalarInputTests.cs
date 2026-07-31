namespace Dataverse.Tests.Workflows;

using Dataverse.Core.Workflows;
using NUnit.Framework;

/// <summary>
/// The property-type names a code activity's scalar inputs are built with.
/// </summary>
/// <remarks>
/// <para>
/// These names go into a VB expression. A wrong one does not produce a helpful message: Dataverse
/// refuses the whole document with <c>0x80045040</c> and the text "created outside the Microsoft
/// Dynamics 365 web application. This workflow type is not allowed in the organization", which points
/// nowhere near the actual cause.
/// </para>
/// <para>
/// <c>Integer</c> is the one that was wrong — spelled <c>Int</c>, the way the type is usually called.
/// It was found by feeding a whole number to <c>msdyncrmWorkflowTools.StringFunctions</c>; string and
/// boolean inputs had happened to be right and so hid the gap. None of the ten designer fixtures
/// contains a scalar input to a code activity, which is why no earlier test caught it.
/// </para>
/// </remarks>
[TestFixture]
public sealed class CodeActivityScalarInputTests
{
    /// <summary>
    /// Each scalar type must reach the XAML as the enum member Dataverse knows, with the CRM attribute
    /// type as the third parameter.
    /// </summary>
    [TestCase("String", "String", "String")]
    [TestCase("Integer", "Integer", "Integer")]
    [TestCase("Boolean", "Boolean", "Boolean")]
    [TestCase("Decimal", "Decimal", "Decimal")]
    [TestCase("OptionSetValue", "OptionSetValue", "Picklist")]
    [TestCase("Guid", "Guid", "UniqueIdentifier")]
    public void ThePropertyTypeIsNamedAsTheEnumDoes(string dataType, string propertyType, string marker)
    {
        var xaml = WorkflowXamlBuilder.Build(Activity(new Dictionary<string, WorkflowValue>
        {
            ["InputText"] = new() { DataType = dataType, Literal = Sample(dataType) }
        })).Xaml;

        Assert.Multiple(() =>
        {
            Assert.That(xaml, Does.Contain($"WorkflowPropertyType.{propertyType},"));
            Assert.That(xaml, Does.Contain($"&quot;{marker}&quot;"));
        });
    }

    private static string Sample(string dataType) => dataType switch
    {
        "Integer" => "0",
        "Boolean" => "false",
        "Decimal" => "1.5",
        "OptionSetValue" => "948170003",
        "Guid" => "8adde851-f7f5-438f-8608-f7ba823760e4",
        _ => "abc"
    };

    private static WorkflowDefinition Activity(Dictionary<string, WorkflowValue> inputs) => new()
    {
        PrimaryEntity = "invoice",
        Steps =
        [
            new WorkflowStep
            {
                Kind = WorkflowStepKind.CustomActivity,
                Description = "Skalare Eingaben",
                AssemblyQualifiedName = DunningPaymentNote.StringFunctionsActivity,
                Inputs = inputs,
                Outputs = ["TrimmedText"]
            }
        ]
    };

    /// <summary>
    /// A code activity fed a string, a boolean and a whole number must build, and the whole number must
    /// not carry the old <c>Int</c> spelling.
    /// </summary>
    [Test]
    public void AllThreeScalarKindsBuild()
    {
        var built = WorkflowXamlBuilder.Build(Activity(new Dictionary<string, WorkflowValue>
        {
            ["InputText"] = new() { DataType = "String", Literal = "abc" },
            ["CapitalizeAllWords"] = new() { DataType = "Boolean", Literal = "false" },
            ["StartIndex"] = new() { DataType = "Integer", Literal = "0" }
        }));

        Assert.Multiple(() =>
        {
            Assert.That(WorkflowDefinitionValidator.ValidateGeneratedXaml(built.Xaml).CanSave, Is.True);
            Assert.That(built.Xaml, Does.Contain("WorkflowPropertyType.Integer"));
            Assert.That(built.Xaml, Does.Not.Contain("WorkflowPropertyType.Int,"),
                "the enum has no member called 'Int' — Dataverse would reject the document");
        });
    }
}
