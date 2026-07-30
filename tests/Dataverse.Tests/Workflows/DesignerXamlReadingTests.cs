namespace Dataverse.Tests.Workflows;

using Dataverse.Core.Workflows;
using NUnit.Framework;

/// <summary>
/// Reads XAML that the Dataverse designer wrote, not this server.
/// </summary>
/// <remarks>
/// Every other round-trip test builds the XAML with <see cref="WorkflowXamlBuilder"/> first, so it
/// can only show that builder and parser agree with each other. This fixture is real designer output
/// (see <c>DesignerXamlFixtureTests</c> for how to refresh it) and is therefore the only evidence
/// that an existing, hand-configured workflow can be read — the prerequisite for editing one.
/// </remarks>
[TestFixture]
public sealed class DesignerXamlReadingTests
{
    private const string SalesTeamId = "a0000001-0000-4000-8000-000000000001";

    private static string DesignerXaml()
    {
        var path = Path.Combine(TestContext.CurrentContext.TestDirectory,
            "Workflows", "Fixtures", "designer-custom-activity.xaml");
        Assert.That(File.Exists(path), Is.True,
            $"fixture missing: {path} — run DesignerXamlFixtureTests to refresh it");
        return File.ReadAllText(path);
    }

    private static string PaymentReminderXaml()
    {
        var path = Path.Combine(TestContext.CurrentContext.TestDirectory,
            "Workflows", "Fixtures", "payment-reminder.xaml");
        Assert.That(File.Exists(path), Is.True, $"fixture missing: {path}");
        return File.ReadAllText(path);
    }

    /// <summary>
    /// "Zahlungserinnerung-Email verschicken": a real workflow whose first condition is an
    /// if/else-if chain with six cases, five of them cancelling.
    /// </summary>
    [Test]
    public void Parse_PaymentReminder_ReadsAllSixCasesWithTheirOwnComparisons()
    {
        var parsed = WorkflowXamlParser.Parse(PaymentReminderXaml(), "invoice");
        var chain = parsed.Definition.Steps[1];

        Assert.That(chain.Description, Is.EqualTo("Voraussetzungen prüfen"));
        Assert.That(chain.Branches, Has.Count.EqualTo(5), "five cases plus the default one");
        Assert.That(chain.Else, Is.Not.Null.And.Not.Empty);

        // Previously all 14 comparisons of all cases landed in one OR-chain. Each case must carry
        // exactly its own — that is the difference between reading it and mangling it.
        Assert.That(chain.Branches![0].Conditions.Select(c => c.Attribute),
            Is.EqualTo(new[] { "dc_invoicestatus", "dc_invoicenumber", "dc_invoice_type" }));
        Assert.That(chain.Branches[1].Conditions.Select(c => c.Attribute),
            Is.EqualTo(new[] { "dc_invoicedate", "dc_payduedate" }));
        Assert.That(chain.Branches[2].Conditions.Select(c => c.Attribute),
            Is.EqualTo(new[] { "sample_salutation", "lastname", "emailaddress1" }));
        Assert.That(chain.Branches[2].Conditions.All(c => c.Via == "customerid"), Is.True);
        Assert.That(chain.Branches[3].Conditions.Select(c => c.Attribute),
            Is.EqualTo(new[] { "internalemailaddress", "address1_telephone1", "fullname" }));
        Assert.That(chain.Branches[4].Conditions.Select(c => c.Attribute),
            Is.EqualTo(new[] { "dc_reminderdate" }));

        // Every case cancels; the default one does the work.
        Assert.That(chain.Branches.All(b => b.Steps!.Single().Kind == WorkflowStepKind.StopWorkflow), Is.True);
        Assert.That(chain.Branches.All(b => b.LogicalOperator is null or "Or"), Is.True);
    }

    /// <summary>
    /// The records this workflow makes and loads itself: the e-mail it creates, and the user record
    /// loaded for the GetInitiatingUser output.
    /// </summary>
    [Test]
    public void Parse_PaymentReminder_KeepsTheOriginOfSelfMadeRecords()
    {
        var parsed = WorkflowXamlParser.Parse(PaymentReminderXaml(), "invoice");
        var work = parsed.Definition.Steps[1].Else!;

        // Three code activities read the id of the e-mail created a step earlier.
        var emailReaders = work
            .Where(s => s.Kind == WorkflowStepKind.CustomActivity)
            .SelectMany(s => s.Inputs ?? [])
            .Where(i => i.Value.Fields is { Count: > 0 } f && f[0] == "email.activityid")
            .ToList();

        Assert.That(emailReaders, Is.Not.Empty);
        Assert.That(emailReaders.All(i => i.Value.FromStep == "CreateStep17"), Is.True,
            "the e-mail id must be traced back to the create step, not to the triggering record");

        // The created e-mail takes its sender from the loaded user record.
        var create = work.Single(s => s.Kind == WorkflowStepKind.CreateRecord);
        var from = create.Attributes!.Single(a => a.Attribute == "from").Value;
        Assert.That(from.Fields, Is.EqualTo(new[] { "systemuser.systemuserid" }));
        Assert.That(from.FromStepOutput, Is.EqualTo("CustomActivityStep6.InitiatingUser"));

        // And so do the guard conditions on that user.
        var userGuard = parsed.Definition.Steps[1].Branches![3];
        Assert.That(userGuard.Conditions.All(c => c.FromStepOutput == "CustomActivityStep6.InitiatingUser"),
            Is.True);
    }

    [Test]
    public void Parse_DesignerCustomActivity_ReducesBothInputsToValues()
    {
        var parsed = WorkflowXamlParser.Parse(DesignerXaml(), "salesorder");

        Assert.That(parsed.Definition.Steps, Has.Count.EqualTo(1));
        var step = parsed.Definition.Steps[0];

        Assert.That(step.Kind, Is.EqualTo(WorkflowStepKind.CustomActivity));
        Assert.That(step.StepId, Is.EqualTo("CustomActivityStep1"));
        Assert.That(step.AssemblyQualifiedName, Does.StartWith("msdyncrmWorkflowTools.CheckUserInTeam,"));
        Assert.That(step.Description, Is.EqualTo("Ist der Besiter der Kunde-Firma im Salesteam?"));

        // The fixed team, written by the designer as two CreateCrmType stages.
        Assert.That(step.Inputs!["Team"].Kind, Is.EqualTo(WorkflowValueKind.Literal));
        Assert.That(step.Inputs["Team"].Literal, Is.EqualTo($"team:{SalesTeamId}"));
        Assert.That(step.Inputs["Team"].DataType, Is.EqualTo("EntityReference"));

        // The owner of the related account, reached through the lookup sample_kundefirma.
        Assert.That(step.Inputs["User"].Kind, Is.EqualTo(WorkflowValueKind.Field));
        Assert.That(step.Inputs["User"].Fields, Is.EqualTo(new[] { "account.ownerid" }));
        Assert.That(step.Inputs["User"].Via, Is.EqualTo("sample_kundefirma"));

        Assert.That(step.Outputs, Is.EqualTo(new[] { "isUserInTeam" }));

        Assert.That(parsed.FullyUnderstood, Is.True,
            "unrecognised: " + string.Join(", ", parsed.Unrecognised));
    }

    [Test]
    public void Parse_DesignerCustomActivity_ReadingIsGoodEnoughToWriteBack()
    {
        var parsed = WorkflowXamlParser.Parse(DesignerXaml(), "salesorder");
        var definition = parsed.Definition with { PrimaryEntity = "salesorder" };

        var validation = WorkflowDefinitionValidator.Validate(definition, CustomActivityFixture.Catalog());
        Assert.That(validation.CanSave, Is.True,
            "actual: " + string.Join(", ", validation.Issues.Select(i => $"{i.Code} @ {i.Path}: {i.Problem}")));

        // Rebuilding must reproduce what the designer wrote: same markers, same variable naming, same
        // argument types. This is the check that would have caught the InvalidPropertyBag defect.
        var rebuilt = WorkflowXamlBuilder.Build(definition, null, CustomActivityFixture.Catalog()).Xaml;

        Assert.That(rebuilt, Does.Contain($"WorkflowPropertyType.Guid, &quot;{SalesTeamId}&quot;, &quot;UniqueIdentifier&quot;"));
        Assert.That(rebuilt, Does.Contain("WorkflowPropertyType.EntityReference, &quot;team&quot;, &quot;&quot;,"));
        Assert.That(rebuilt, Does.Contain("Name=\"CustomActivityStep1_1_converted\""));
        Assert.That(rebuilt, Does.Contain("related_sample_kundefirma#account"));
        Assert.That(rebuilt, Does.Contain(
            "<OutArgument x:TypeArguments=\"x:Boolean\" x:Key=\"isUserInTeam\">"));
        Assert.That(WorkflowDefinitionValidator.ValidateGeneratedXaml(rebuilt).CanSave, Is.True);
    }
}
