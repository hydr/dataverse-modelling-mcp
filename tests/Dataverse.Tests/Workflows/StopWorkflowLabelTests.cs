namespace Dataverse.Tests.Workflows;

using Dataverse.Core.Workflows;
using NUnit.Framework;

/// <summary>
/// The step label next to a cancellation message.
/// </summary>
/// <remarks>
/// <para>
/// A stop step whose reason is <b>composed</b> must not declare the step-label variables. Where it does,
/// the platform builds the cancellation message from the label instead of the reason. The observed
/// result, from a real run log:
/// </para>
/// <code>
/// WorkflowTerminatedException: Stop the workflow - dunning 1 already sent31.07.2026a1b2c3d4-…
///                              └ stepLabelDescription          ┘└ the field ┘└ stepLabelLabelId ┘
/// </code>
/// <para>
/// The intended sentence — "Dunning 1 was already sent on " plus the date — did not appear at
/// all. A reason that is a plain constant renders correctly with the label present, which is why the
/// designer keeps the label on four stop steps of the first dunning workflow and omits it on the
/// fifth, the only one with a composed reason.
/// </para>
/// </remarks>
[TestFixture]
public sealed class StopWorkflowLabelTests
{
    private static string Build(WorkflowValue reason) =>
        WorkflowXamlBuilder.Build(new WorkflowDefinition
        {
            PrimaryEntity = "invoice",
            Steps =
            [
                new WorkflowStep
                {
                    Kind = WorkflowStepKind.StopWorkflow,
                    Description = "Stop the workflow - dunning 1 already sent",
                    Outcome = "cancelled",
                    Reason = reason
                }
            ]
        }).Xaml;

    [Test]
    public void AComposedReasonGetsNoStepLabel()
    {
        var xaml = Build(new WorkflowValue
        {
            Kind = WorkflowValueKind.Concat,
            DataType = "String",
            Parts =
            [
                new WorkflowValue { DataType = "String", Literal = "Dunning 1 was already sent on " },
                new WorkflowValue
                {
                    Kind = WorkflowValueKind.Field, DataType = "String", Fields = ["invoice.sample_dunning1"]
                }
            ]
        });

        Assert.Multiple(() =>
        {
            Assert.That(WorkflowDefinitionValidator.ValidateGeneratedXaml(xaml).CanSave, Is.True);
            Assert.That(xaml, Does.Not.Contain("stepLabelLabelId"),
                "the label would replace the message the caller wrote");
            Assert.That(xaml, Does.Contain("Dunning 1 was already sent on "));
        });
    }

    [Test]
    public void APlainReasonKeepsTheStepLabel()
    {
        var xaml = Build(new WorkflowValue
        {
            DataType = "String",
            Literal = "The invoice must have the payment reminder status."
        });

        Assert.Multiple(() =>
        {
            Assert.That(WorkflowDefinitionValidator.ValidateGeneratedXaml(xaml).CanSave, Is.True);
            Assert.That(xaml, Does.Contain("stepLabelLabelId"), "the designer keeps it here");
        });
    }

    /// <summary>The abort element carries its description, as every other labelled element does.</summary>
    [Test]
    public void TheTerminateElementIsLabelled()
    {
        var xaml = Build(new WorkflowValue { DataType = "String", Literal = "Abbruch" });

        Assert.That(xaml, Does.Contain(
            "<TerminateWorkflow DisplayName=\"StopWorkflowStep1: Stop the workflow - dunning 1 already sent\""));
    }
}
