namespace Dataverse.Tests.Workflows;

using Dataverse.Core.Workflows;
using NUnit.Framework;

[TestFixture]
public sealed class WorkflowXamlTests
{
    private static readonly Guid SampleId = new("df18467c-1c90-4339-b64c-04fe7b4fbcee");

    private static string FixturePath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    // ---- Parser against a real exported workflow ------------------------------------------

    [Test]
    public void Interpret_RealWorkflow_IdentifiesCustomActivityConditionAndUpdate()
    {
        var xaml = File.ReadAllText(FixturePath("SetXvEntityId.xaml"));

        var result = WorkflowXamlParser.Interpret(xaml);

        Assert.That(result.IsWellFormed, Is.True);
        Assert.That(result.ClassName, Does.StartWith("XrmWorkflow"));
        Assert.That(result.Steps.Select(s => s.Kind),
            Is.EquivalentTo(new[] { "CustomActivity", "Condition", "UpdateEntity" }));

        // Contains a condition -> not safe for the MCP to regenerate.
        Assert.That(result.IsTemplateRecognized, Is.False);

        var custom = result.Steps.Single(s => s.Kind == "CustomActivity");
        Assert.That(custom.AssemblyQualifiedName, Does.Contain("Sample.CrmPlugins.Utils.GetEntityIdAsGuid"));

        var update = result.Steps.Single(s => s.Kind == "UpdateEntity");
        Assert.That(update.EntityName, Is.EqualTo("opportunity"));
        Assert.That(update.Fields, Does.Contain("sample_entityid"));
    }

    [Test]
    public void Interpret_Garbage_ReportsNotWellFormed()
    {
        var result = WorkflowXamlParser.Interpret("<not-xaml>");
        Assert.That(result.IsWellFormed, Is.False);
        Assert.That(result.Notes, Is.Not.Empty);
    }

    // ---- Builder + round-trip for the three buildable cases -------------------------------

    [Test]
    public void Build_UpdateEntity_RoundTripsToRecognisedTemplate()
    {
        var def = new WorkflowDefinition(
            PrimaryEntity: "account",
            Mode: WorkflowMode.Realtime,
            Scope: WorkflowScope.Organization,
            Trigger: new WorkflowTrigger(OnUpdate: true, UpdateAttributes: ["telephone1"]),
            Steps:
            [
                new UpdateEntityStep("account",
                [
                    new FieldAssignment("description", CrmValueType.String, new LiteralValue(CrmValueType.String, "Synced \"ok\"")),
                    new FieldAssignment("numberofemployees", CrmValueType.Integer, new LiteralValue(CrmValueType.Integer, "42")),
                    new FieldAssignment("primarycontactid", CrmValueType.EntityReference, new FieldValue("originatingleadid", CrmValueType.EntityReference)),
                ])
            ]);

        var xaml = WorkflowXamlBuilder.Build(def, SampleId);

        Assert.That(xaml, Does.Contain("XrmWorkflowdf18467c1c904339b64c04fe7b4fbcee"));
        Assert.That(xaml, Does.Contain("encoding=\"utf-16\""));

        var parsed = WorkflowXamlParser.Interpret(xaml);
        Assert.That(parsed.IsWellFormed, Is.True);
        Assert.That(parsed.IsTemplateRecognized, Is.True);
        var step = parsed.Steps.Single();
        Assert.That(step.Kind, Is.EqualTo("UpdateEntity"));
        Assert.That(step.EntityName, Is.EqualTo("account"));
        Assert.That(step.Fields, Is.EquivalentTo(new[] { "description", "numberofemployees", "primarycontactid" }));
    }

    [Test]
    public void Build_CustomActivity_WithLiteralAndFieldInputsAndOutput_RoundTrips()
    {
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
                    InputArguments:
                    [
                        new ActivityArgument("Prefix", CrmValueType.String, new LiteralValue(CrmValueType.String, "OPP-")),
                        new ActivityArgument("SourceName", CrmValueType.String, new FieldValue("name", CrmValueType.String)),
                    ],
                    OutputArguments:
                    [
                        new ActivityArgument("EntityId", CrmValueType.String),
                    ])
            ]);

        var xaml = WorkflowXamlBuilder.Build(def, SampleId);

        Assert.That(xaml, Does.Contain("Sample.CrmPlugins.Utils.GetEntityIdAsGuid"));
        Assert.That(xaml, Does.Contain("CustomActivityStep1EntityId_localParameter"));
        Assert.That(xaml, Does.Contain("DirectCast")); // field input cast

        var parsed = WorkflowXamlParser.Interpret(xaml);
        Assert.That(parsed.IsWellFormed, Is.True);
        Assert.That(parsed.IsTemplateRecognized, Is.True);
        var step = parsed.Steps.Single();
        Assert.That(step.Kind, Is.EqualTo("CustomActivity"));
        Assert.That(step.Arguments, Does.Contain("Prefix").And.Contain("SourceName").And.Contain("EntityId"));
    }

    [Test]
    public void Build_CreateEntity_RoundTrips()
    {
        var def = new WorkflowDefinition(
            PrimaryEntity: "lead",
            Mode: WorkflowMode.Background,
            Scope: WorkflowScope.Organization,
            Trigger: new WorkflowTrigger(OnCreate: true),
            Steps:
            [
                new CreateEntityStep("task",
                [
                    new FieldAssignment("subject", CrmValueType.String, new LiteralValue(CrmValueType.String, "Follow up")),
                    new FieldAssignment("prioritycode", CrmValueType.OptionSet, new LiteralValue(CrmValueType.OptionSet, "1")),
                ])
            ]);

        var xaml = WorkflowXamlBuilder.Build(def, SampleId);
        Assert.That(xaml, Does.Contain("mxswa:CreateEntity"));

        var parsed = WorkflowXamlParser.Interpret(xaml);
        Assert.That(parsed.IsTemplateRecognized, Is.True);
        Assert.That(parsed.Steps.Single().Kind, Is.EqualTo("CreateEntity"));
    }

    // ---- Validator ------------------------------------------------------------------------

    [Test]
    public void Validate_GoodDefinition_IsValid()
    {
        var def = new WorkflowDefinition("account", WorkflowMode.Realtime, WorkflowScope.Organization,
            new WorkflowTrigger(OnUpdate: true),
            [new UpdateEntityStep("account", [new FieldAssignment("description", CrmValueType.String, new LiteralValue(CrmValueType.String, "x"))])]);

        var result = WorkflowXamlValidator.Validate(def);
        Assert.That(result.IsValid, Is.True, string.Join("; ", result.Errors));
    }

    [Test]
    public void Validate_EntityReferenceLiteralInFieldAssignment_IsError()
    {
        var def = new WorkflowDefinition("account", WorkflowMode.Realtime, WorkflowScope.Organization,
            new WorkflowTrigger(OnUpdate: true),
            [new UpdateEntityStep("account", [new FieldAssignment("primarycontactid", CrmValueType.EntityReference,
                new LiteralValue(CrmValueType.EntityReference, Guid.NewGuid().ToString(), "contact"))])]);

        var result = WorkflowXamlValidator.Validate(def);
        Assert.That(result.IsValid, Is.False);
    }

    [Test]
    public void Validate_UpdateOfNonPrimaryEntity_IsError()
    {
        var def = new WorkflowDefinition("account", WorkflowMode.Realtime, WorkflowScope.Organization,
            new WorkflowTrigger(OnUpdate: true),
            [new UpdateEntityStep("contact", [new FieldAssignment("lastname", CrmValueType.String, new LiteralValue(CrmValueType.String, "x"))])]);

        var result = WorkflowXamlValidator.Validate(def);
        Assert.That(result.IsValid, Is.False);
    }

    [Test]
    public void Validate_BadInteger_IsError()
    {
        var def = new WorkflowDefinition("account", WorkflowMode.Realtime, WorkflowScope.Organization,
            new WorkflowTrigger(OnUpdate: true),
            [new UpdateEntityStep("account", [new FieldAssignment("numberofemployees", CrmValueType.Integer, new LiteralValue(CrmValueType.Integer, "notanumber"))])]);

        var result = WorkflowXamlValidator.Validate(def);
        Assert.That(result.IsValid, Is.False);
    }

    [Test]
    public void Validate_NullStepsAndEntity_IsInvalidNotThrow()
    {
        // Simulates deserialization of a minimal/garbage payload (e.g. "{}").
        var def = new WorkflowDefinition(null!, WorkflowMode.Realtime, WorkflowScope.Organization, new WorkflowTrigger(), null!);
        WorkflowDefinitionValidation result = null!;
        Assert.DoesNotThrow(() => result = WorkflowXamlValidator.Validate(def));
        Assert.That(result.IsValid, Is.False);
    }

    [Test]
    public void Validate_NoSteps_IsError()
    {
        var def = new WorkflowDefinition("account", WorkflowMode.Realtime, WorkflowScope.Organization,
            new WorkflowTrigger(OnUpdate: true), []);
        var result = WorkflowXamlValidator.Validate(def);
        Assert.That(result.IsValid, Is.False);
    }
}
