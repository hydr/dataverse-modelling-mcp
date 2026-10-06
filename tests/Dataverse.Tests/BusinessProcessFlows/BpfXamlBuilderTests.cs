namespace Dataverse.Tests.BusinessProcessFlows;

using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Dataverse.Core.BusinessProcessFlows;
using NUnit.Framework;

[TestFixture]
public sealed class BpfXamlBuilderTests
{
    private static string Build(BpfDefinition definition) =>
        BpfXamlBuilder.Build(definition, BpfTestData.ProcessId, BpfTestData.Catalog()).Xaml;

    [Test]
    public void Build_ProducesWellFormedXaml_WithTheProcessIdAsClassName()
    {
        var xaml = Build(BpfTestData.Full());

        Assert.DoesNotThrow(() => XDocument.Parse(xaml));
        Assert.That(xaml, Does.Contain("x:Class=\"XrmWorkflow11111111111111111111111111110001\""));
    }

    [Test]
    public void Build_WritesOneEntityCompositePerStage_LinkedByNextStageId()
    {
        var definition = BpfTestData.Full();
        var result = BpfXamlBuilder.Build(definition, BpfTestData.ProcessId, BpfTestData.Catalog());

        Assert.That(Regex.Matches(result.Xaml, @"Activities\.EntityComposite").Count, Is.EqualTo(5));
        Assert.That(result.Stages.Select(s => s.Entity),
            Is.EqualTo(new[] { "account", "account", "account", "account", "contact" }));

        // "Large" names "Contact" explicitly; "Small" falls through to it; the last stage ends the path.
        var ids = result.Stages.ToDictionary(s => s.Name, s => s.StageId);
        Assert.That(result.Xaml, Does.Contain($"<x:String x:Key=\"NextStageId\">{ids["Contact"]}</x:String>"));
        Assert.That(result.Xaml, Does.Contain("<x:Null x:Key=\"NextStageId\" />"));
    }

    [Test]
    public void Build_AssignsMissingIds_AndKeepsGivenOnes()
    {
        var definition = BpfTestData.Full();
        definition.Stages[0].StageId = "11111111-1111-1111-1111-111111110201";

        var result = BpfXamlBuilder.Build(definition, BpfTestData.ProcessId, BpfTestData.Catalog());

        Assert.That(result.Stages[0].StageId, Is.EqualTo("11111111-1111-1111-1111-111111110201"));
        Assert.That(result.Stages.All(s => Guid.TryParse(s.StageId, out _)), Is.True);
        Assert.That(definition.Stages.SelectMany(s => s.Steps).All(s => s.StepId is not null), Is.True,
            "the ids are written back so the caller can keep them");
    }

    [Test]
    public void Build_DataStep_CarriesItsParameters()
    {
        // Without Control.Parameters the platform fails to derive uidata (0x80045037).
        var xaml = Build(BpfTestData.Full());
        var control = Regex.Match(xaml, "<mcwb:Control [^>]*DataFieldName=\"name\".*?</mcwb:Control>").Value;

        Assert.That(control, Does.Contain("<mcwb:Control.Parameters>"));
        Assert.That(control, Does.Contain("IsUnbound=\"False\""));
    }

    [Test]
    public void Build_CrossTableStage_IsListedInTheRelationshipCollection()
    {
        var result = BpfXamlBuilder.Build(BpfTestData.Full(), BpfTestData.ProcessId, BpfTestData.Catalog());
        var ids = result.Stages.ToDictionary(s => s.Name, s => s.StageId);

        // Two stages lead to "Contact"; without fromStage the one listed before it is the source.
        Assert.That(result.Xaml, Does.Contain(
            "<mcwb:StageRelationship AttributeName=\"parentcustomerid\" RelationshipName=\"contact_customer_accounts\" "
            + $"SourceStageId=\"{ids["Small"]}\" TargetStageId=\"{ids["Contact"]}\" />"));
    }

    [Test]
    public void Build_Branch_SetsTheNextStagePerCase()
    {
        var result = BpfXamlBuilder.Build(BpfTestData.Full(), BpfTestData.ProcessId, BpfTestData.Catalog());
        var ids = result.Stages.ToDictionary(s => s.Name, s => s.StageId);

        Assert.That(result.Xaml, Does.Contain($"<mcwc:SetNextStage ParentStageId=\"{ids["Assess"]}\" StageId=\"{ids["Large"]}\" />"));
        Assert.That(result.Xaml, Does.Contain($"<mcwc:SetNextStage ParentStageId=\"{ids["Assess"]}\" StageId=\"{ids["Small"]}\" />"));
        Assert.That(result.Xaml, Does.Contain("<x:Boolean x:Key=\"ContainsElseBranch\">True</x:Boolean>"));
        Assert.That(result.Xaml, Does.Contain("<x:String x:Key=\"Description\">Large</x:String>"));
        Assert.That(result.Xaml, Does.Contain("x:Key=\"LogicalOperator\">Or<"));
        // Comparisons read the stage's own record.
        Assert.That(result.Xaml, Does.Contain("Attribute=\"numberofemployees\" Entity=\"[InputEntities(&quot;primaryEntity&quot;)]\" EntityName=\"account\""));
    }

    [Test]
    public void Build_ActionStep_ForAWorkflow_UsesActionType3AndNoUniqueName()
    {
        var xaml = Build(BpfTestData.Full());
        var action = Regex.Match(xaml, $"<x:Int32 x:Key=\"ActionType\">3</x:Int32><s:Guid x:Key=\"ProcessId\">{BpfTestData.RecalculateWorkflow}</s:Guid>.*?</Sequence>").Value;

        Assert.That(action, Does.Contain("<x:String x:Key=\"UniqueName\"></x:String>"));
        Assert.That(action, Does.Contain("<x:Null x:Key=\"TriggerEvents\" />"));
        Assert.That(action, Does.Match("ControlId=\"_Step_\\d+\""));
        Assert.That(action, Does.Contain($"ClassId=\"{BpfControlClass.Button}\""));
        Assert.That(action, Does.Contain("IsUnbound=\"True\""));
    }

    [Test]
    public void Build_ActionStep_ForAnAction_UsesActionType0AndTheMessageName()
    {
        var xaml = Build(BpfTestData.Full());

        Assert.That(xaml, Does.Contain($"<x:Int32 x:Key=\"ActionType\">0</x:Int32><s:Guid x:Key=\"ProcessId\">{BpfTestData.NotifyAction}</s:Guid>"
                                       + "<x:String x:Key=\"UniqueName\">sample_NotifyOwner</x:String>"));
        Assert.That(xaml, Does.Match("ControlId=\"sample_NotifyOwner_Step_\\d+\""));
    }

    [Test]
    public void Build_ActionStep_IsNeverRequired()
    {
        var definition = BpfTestData.Full();
        definition.Stages[0].Steps[1] = definition.Stages[0].Steps[1] with { Required = true };

        var xaml = Build(definition);
        var stepId = definition.Stages[0].Steps[1].StepId;

        Assert.That(xaml, Does.Contain($"<x:String x:Key=\"ProcessStepId\">{stepId}</x:String><x:Boolean x:Key=\"IsProcessRequired\">False</x:Boolean>"));
    }

    [Test]
    public void Build_FlowStep_UsesFlowCompositeWithDerivedUniqueName()
    {
        var definition = BpfTestData.Full();
        var xaml = Build(definition);
        var stepId = Guid.Parse(definition.Stages[4].Steps[1].StepId!);

        Assert.That(xaml, Does.Contain("Activities.FlowComposite"));
        Assert.That(xaml, Does.Contain($"<s:Guid x:Key=\"WorkflowId\">{BpfTestData.ResearchFlow}</s:Guid>"));
        Assert.That(xaml, Does.Contain($"<x:String x:Key=\"UniqueName\">FlowStep_{BpfTestData.ResearchFlow:N}_{stepId:N}</x:String>"));
        Assert.That(xaml, Does.Contain($"<x:String x:Key=\"ProcessStepId\">{stepId}</x:String><x:Boolean x:Key=\"IsProcessRequired\">True</x:Boolean>"));
    }

    [TestCase(BpfTriggerEvent.StageEnter, "STAGEENTER", "{stage}", "40")]
    [TestCase(BpfTriggerEvent.StageExit, "STAGEEXIT", "{stage}", "20")]
    public void Build_StageTrigger_EncodesEventAndStage(string on, string eventName, string filter, string pipeline)
    {
        var definition = BpfTestData.Full();
        definition.Stages[0].Workflows[0] = definition.Stages[0].Workflows[0] with { On = on };
        var result = BpfXamlBuilder.Build(definition, BpfTestData.ProcessId, BpfTestData.Catalog());

        Assert.That(result.Xaml, Does.Contain(
            $"<mcwo:ProcessTriggerData Event=\"{eventName}\" FilterId=\"{filter.Replace("{stage}", result.Stages[0].StageId)}\" PipelineStageId=\"{pipeline}\" />"));
        // A triggered workflow carries its display name as UniqueName and has no button.
        Assert.That(result.Xaml, Does.Contain("<x:String x:Key=\"UniqueName\">Write audit entry</x:String>"));
    }

    [TestCase(BpfTriggerEvent.Applied, "PROCESSAPPLIED", "11111111-1111-1111-1111-111111110001")]
    [TestCase(BpfTriggerEvent.Reactivated, "PROCESSSTATUSCHANGE", "1")]
    [TestCase(BpfTriggerEvent.Finished, "PROCESSSTATUSCHANGE", "2")]
    [TestCase(BpfTriggerEvent.Abandoned, "PROCESSSTATUSCHANGE", "3")]
    public void Build_ProcessTrigger_EncodesEventAndFilter(string on, string eventName, string filter)
    {
        var definition = BpfTestData.Full() with
        {
            Workflows = [new BpfWorkflowTrigger { WorkflowId = BpfTestData.AuditWorkflow.ToString(), On = on }]
        };

        var xaml = Build(definition);

        Assert.That(xaml, Does.Contain(
            $"<mcwo:ProcessTriggerData Event=\"{eventName}\" FilterId=\"{filter}\" PipelineStageId=\"40\" />"));
    }

    [Test]
    public void Build_ProcessTriggers_SitInTheFirstStage()
    {
        var xaml = Build(BpfTestData.Full());
        var firstStage = Regex.Match(xaml, "DisplayName=\"StageStep\\d+: Capture\".*?x:Key=\"StageId\"").Value;

        Assert.That(firstStage, Does.Contain("PROCESSSTATUSCHANGE"));
    }

    [Test]
    public void Build_UnknownProcess_Throws()
    {
        Assert.Throws<InvalidOperationException>(() =>
            BpfXamlBuilder.Build(BpfTestData.Full(), BpfTestData.ProcessId, new BpfCatalog()));
    }

    [Test]
    public void Build_ThenParse_RoundTripsTheDefinition()
    {
        var definition = BpfTestData.Full();
        var xaml = Build(definition);
        var parsed = BpfXamlParser.Parse(xaml, "account");

        Assert.That(parsed.FullyUnderstood, Is.True, string.Join("; ", parsed.Unrecognised));

        var rebuilt = BpfXamlBuilder.Build(parsed.Definition, BpfTestData.ProcessId, BpfTestData.Catalog()).Xaml;
        var reparsed = BpfXamlParser.Parse(rebuilt, "account");

        Assert.That(Json(reparsed.Definition), Is.EqualTo(Json(parsed.Definition)));
        Assert.That(parsed.Definition.Stages.Select(s => s.StageId), Is.EqualTo(definition.Stages.Select(s => s.StageId)));
        Assert.That(parsed.Definition.Workflows.Single().On, Is.EqualTo(BpfTriggerEvent.Finished));
        Assert.That(parsed.Definition.Stages[0].Workflows.Single().On, Is.EqualTo(BpfTriggerEvent.StageExit));
    }

    private static string Json(object o) => JsonSerializer.Serialize(o);
}
