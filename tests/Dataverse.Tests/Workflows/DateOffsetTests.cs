namespace Dataverse.Tests.Workflows;

using Dataverse.Core.Workflows;
using NUnit.Framework;

/// <summary>
/// Deadlines ("now plus seven days") and cleared dates — the two constructs that made the dunning
/// workflows unwritable.
/// </summary>
/// <remarks>
/// <para>
/// Both were found by trying to rewrite "Mahnung1-Email verschicken" unchanged, which Dataverse refused
/// with <c>0x80040216</c>. The reading claimed to be complete, so nothing warned beforehand — that gap
/// is covered by <see cref="ClearingIsAValueButAnUnreadableExpressionIsNot"/>.
/// </para>
/// <para>
/// The distinction between the two is subtle and matters: a date that is <b>computed</b> by an
/// expression the reader does not understand must be reported, while a date that is deliberately
/// <b>emptied</b> must not. The designer writes the second one by pointing the assignment at a variable
/// nothing ever assigns.
/// </para>
/// </remarks>
[TestFixture]
public sealed class DateOffsetTests
{
    private static WorkflowDefinition Update(WorkflowValue value) => new()
    {
        PrimaryEntity = "invoice",
        Steps =
        [
            new WorkflowStep
            {
                Kind = WorkflowStepKind.UpdateRecord,
                Description = "Frist berechnen",
                Entity = "invoice",
                Attributes = [new WorkflowAttributeAssignment { Attribute = "sample_dunning2", Value = value }]
            }
        ]
    };

    [Test]
    public void ADeadlineIsBuiltAsAnAdditionOfADuration()
    {
        var xaml = WorkflowXamlBuilder.Build(Update(new WorkflowValue
        {
            Kind = WorkflowValueKind.Now,
            DataType = "DateTime",
            Offset = new WorkflowTimeOffset { Days = 7 }
        })).Xaml;

        Assert.Multiple(() =>
        {
            Assert.That(WorkflowDefinitionValidator.ValidateGeneratedXaml(xaml).CanSave, Is.True);
            Assert.That(xaml, Does.Contain("<mxsw:XrmTimeSpan Days=\"7\""));
            Assert.That(xaml, Does.Contain("RetrieveCurrentTime"));
            Assert.That(xaml, Does.Contain("clr-namespace:Microsoft.Xrm.Sdk.Workflow;"),
                "the mxsw prefix has to be declared or the document does not load");
        });
    }

    [Test]
    public void ADeadlineSurvivesARoundTrip()
    {
        var xaml = WorkflowXamlBuilder.Build(Update(new WorkflowValue
        {
            Kind = WorkflowValueKind.Now,
            DataType = "DateTime",
            Offset = new WorkflowTimeOffset { Days = 7, Hours = 12 }
        })).Xaml;

        var parsed = WorkflowXamlParser.Parse(xaml, "invoice");
        var value = parsed.Definition.Steps![0].Attributes![0].Value!;

        Assert.Multiple(() =>
        {
            Assert.That(parsed.FullyUnderstood, Is.True,
                "unrecognised: " + string.Join(", ", parsed.Unrecognised));
            Assert.That(value.Kind, Is.EqualTo(WorkflowValueKind.Now));
            Assert.That(value.Offset, Is.Not.Null);
            Assert.That(value.Offset!.Days, Is.EqualTo(7));
            Assert.That(value.Offset.Hours, Is.EqualTo(12));
        });
    }

    /// <summary>An all-zero offset is not written — it would be noise in the document.</summary>
    [Test]
    public void AZeroOffsetChangesNothing()
    {
        var xaml = WorkflowXamlBuilder.Build(Update(new WorkflowValue
        {
            Kind = WorkflowValueKind.Now,
            DataType = "DateTime",
            Offset = new WorkflowTimeOffset()
        })).Xaml;

        Assert.That(xaml, Does.Not.Contain("XrmTimeSpan"));
    }

    /// <summary>
    /// A cleared date must not be wrapped in CreateCrmType: Dataverse rejects the whole document for it,
    /// and only for some types, so an empty text would slip through while an empty date does not.
    /// </summary>
    [Test]
    public void AClearedDateIsNotWrappedInCreateCrmType()
    {
        var xaml = WorkflowXamlBuilder.Build(Update(new WorkflowValue
        {
            Kind = WorkflowValueKind.Literal,
            DataType = "DateTime",
            Literal = null
        })).Xaml;

        Assert.Multiple(() =>
        {
            Assert.That(WorkflowDefinitionValidator.ValidateGeneratedXaml(xaml).CanSave, Is.True);
            Assert.That(xaml, Does.Not.Contain("WorkflowPropertyType.DateTime"),
                "there is nothing to construct — the assignment points at an unassigned variable");
        });

        var parsed = WorkflowXamlParser.Parse(xaml, "invoice");
        var value = parsed.Definition.Steps![0].Attributes![0].Value!;

        Assert.Multiple(() =>
        {
            Assert.That(parsed.FullyUnderstood, Is.True, "clearing is a value, not a gap");
            Assert.That(value.Kind, Is.EqualTo(WorkflowValueKind.Literal));
            Assert.That(value.Literal, Is.Null);
        });
    }

    /// <summary>
    /// The safety net: an expression the reader cannot reduce has to reach <c>unrecognised</c>, because
    /// otherwise the caller is told the reading is complete and rewrites a computed value into nothing.
    /// </summary>
    [Test]
    public void ClearingIsAValueButAnUnreadableExpressionIsNot()
    {
        var fixtures = Path.Combine(TestContext.CurrentContext.TestDirectory, "Workflows", "Fixtures");
        var xaml = File.ReadAllText(Path.Combine(fixtures, "dunning-1.xaml"));

        // The real workflow has both in one place: a computed deadline and a cleared date.
        var parsed = WorkflowXamlParser.Parse(xaml, "invoice");

        Assert.That(parsed.FullyUnderstood, Is.True,
            "both constructs are understood now — " + string.Join(", ", parsed.Unrecognised));

        // Break the deadline: an operator nobody knows must be reported rather than silently dropped.
        var broken = xaml.Replace(">Add<", ">MultiplyByTheMoon<");
        var brokenParse = WorkflowXamlParser.Parse(broken, "invoice");

        Assert.Multiple(() =>
        {
            Assert.That(brokenParse.FullyUnderstood, Is.False,
                "an unknown expression must not pass as understood");
            Assert.That(brokenParse.Unrecognised, Has.Some.Contains("sample_dunning2"));
        });
    }
}
