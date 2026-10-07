namespace Dataverse.Tests.BusinessProcessFlows;

using Dataverse.Core.BusinessProcessFlows;
using NUnit.Framework;

[TestFixture]
public sealed class BpfXamlParserTests
{
    private const string Aqn = ", Microsoft.Crm.Workflow, Version=9.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35";

    private static string Envelope(string body) =>
        "<?xml version=\"1.0\" encoding=\"utf-16\"?>"
        + "<Activity x:Class=\"XrmWorkflow00000000000000000000000000000000\" xmlns=\"http://schemas.microsoft.com/netfx/2009/xaml/activities\""
        + " xmlns:mcwb=\"clr-namespace:Microsoft.Crm.Workflow.BusinessProcessFlowActivities;assembly=Microsoft.Crm.Workflow\""
        + " xmlns:mcwo=\"clr-namespace:Microsoft.Crm.Workflow.ObjectModel;assembly=Microsoft.Crm\""
        + " xmlns:mxswa=\"clr-namespace:Microsoft.Xrm.Sdk.Workflow.Activities;assembly=Microsoft.Xrm.Sdk.Workflow\""
        + " xmlns:sco=\"clr-namespace:System.Collections.ObjectModel;assembly=mscorlib\""
        + " xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\">"
        + $"<mxswa:Workflow>{body}</mxswa:Workflow></Activity>";

    private static string Step(string id, string label, string field) =>
        $"<mxswa:ActivityReference AssemblyQualifiedName=\"Microsoft.Crm.Workflow.Activities.StepComposite{Aqn}\" DisplayName=\"StepStep: {label}\">"
        + "<mxswa:ActivityReference.Properties><sco:Collection x:TypeArguments=\"Activity\" x:Key=\"Activities\">"
        + $"<Sequence DisplayName=\"ControlStep\"><mcwb:Control ClassId=\"4273EDBD-AC1D-40D3-9FB2-095C621B552D\" ControlId=\"{field}\" DataFieldName=\"{field}\" "
        + "IsSystemControl=\"True\" Parameters=\"&lt;parameters&gt;&lt;IsDeDupLookup&gt;true&lt;/IsDeDupLookup&gt;&lt;/parameters&gt;\" SystemStepType=\"0\" /></Sequence>"
        + "</sco:Collection>"
        + $"<sco:Collection x:TypeArguments=\"mcwo:StepLabel\" x:Key=\"StepLabels\"><mcwo:StepLabel Description=\"{label}\" LabelId=\"{id}\" LanguageCode=\"1033\" /></sco:Collection>"
        + $"<x:String x:Key=\"ProcessStepId\">{id}</x:String><x:Boolean x:Key=\"IsProcessRequired\">False</x:Boolean>"
        + "</mxswa:ActivityReference.Properties></mxswa:ActivityReference>";

    private static string Stage(string id, string name, string category, string steps) =>
        $"<mxswa:ActivityReference AssemblyQualifiedName=\"Microsoft.Crm.Workflow.Activities.StageComposite{Aqn}\" DisplayName=\"StageStep: {name}\">"
        + $"<mxswa:ActivityReference.Properties><sco:Collection x:TypeArguments=\"Activity\" x:Key=\"Activities\">{steps}</sco:Collection>"
        + $"<sco:Collection x:TypeArguments=\"mcwo:StepLabel\" x:Key=\"StepLabels\"><mcwo:StepLabel Description=\"{name}\" LabelId=\"{id}\" LanguageCode=\"1033\" /></sco:Collection>"
        + $"<x:String x:Key=\"StageId\">{id}</x:String><x:String x:Key=\"StageCategory\">{category}</x:String>"
        + "</mxswa:ActivityReference.Properties></mxswa:ActivityReference>";

    /// <summary>
    /// The shape of the system processes: one composite per table, several stages in path order, no
    /// NextStageId, and the relationship to the next table on the composite.
    /// </summary>
    private static string Legacy() => Envelope(
        $"<mxswa:ActivityReference AssemblyQualifiedName=\"Microsoft.Crm.Workflow.Activities.EntityComposite{Aqn}\" DisplayName=\"EntityStep1: lead\">"
        + "<mxswa:ActivityReference.Properties><sco:Collection x:TypeArguments=\"Activity\" x:Key=\"Activities\">"
        + Stage("11111111-1111-1111-1111-111111110301", "Qualify", "0",
            Step("11111111-1111-1111-1111-111111110311", "Existing contact?", "parentcontactid"))
        + "</sco:Collection>"
        + "<x:String x:Key=\"RelationshipName\">opportunity_originating_lead</x:String>"
        + "<x:String x:Key=\"AttributeName\">originatingleadid</x:String>"
        + "<x:Boolean x:Key=\"IsClosedLoop\">False</x:Boolean>"
        + "</mxswa:ActivityReference.Properties></mxswa:ActivityReference>"
        + $"<mxswa:ActivityReference AssemblyQualifiedName=\"Microsoft.Crm.Workflow.Activities.EntityComposite{Aqn}\" DisplayName=\"EntityStep2: opportunity\">"
        + "<mxswa:ActivityReference.Properties><sco:Collection x:TypeArguments=\"Activity\" x:Key=\"Activities\">"
        + Stage("11111111-1111-1111-1111-111111110302", "Develop", "1",
            Step("11111111-1111-1111-1111-111111110321", "Customer need", "customerneed"))
        + Stage("11111111-1111-1111-1111-111111110303", "Propose", "2",
            Step("11111111-1111-1111-1111-111111110331", "Proposed solution", "proposedsolution"))
        + "</sco:Collection>"
        + "<x:Null x:Key=\"RelationshipName\" /><x:Null x:Key=\"AttributeName\" /><x:Boolean x:Key=\"IsClosedLoop\">False</x:Boolean>"
        + "</mxswa:ActivityReference.Properties></mxswa:ActivityReference>");

    [Test]
    public void Parse_LegacyShape_ReadsThePathFromDocumentOrder()
    {
        var parsed = BpfXamlParser.Parse(Legacy(), "lead");

        Assert.That(parsed.FullyUnderstood, Is.True, string.Join("; ", parsed.Unrecognised));
        Assert.That(parsed.Definition.Stages.Select(s => s.Name), Is.EqualTo(new[] { "Qualify", "Develop", "Propose" }));
        Assert.That(parsed.Definition.Stages.All(s => s.Next is null), Is.True, "document order is the default path");
        Assert.That(parsed.Notes, Has.Some.Contains("older format"));
    }

    [Test]
    public void Parse_LegacyShape_MovesTheRelationshipOntoTheFirstStageOfTheNextTable()
    {
        var stages = BpfXamlParser.Parse(Legacy(), "lead").Definition.Stages;

        Assert.That(stages[1].Entity, Is.EqualTo("opportunity"));
        Assert.That(stages[1].Relationship!.Name, Is.EqualTo("opportunity_originating_lead"));
        Assert.That(stages[1].Relationship!.Attribute, Is.EqualTo("originatingleadid"));
        Assert.That(stages[1].Relationship!.FromStage, Is.Null, "the stage before is the default source");
        Assert.That(stages[2].Entity, Is.Null, "an inherited table stays implicit");
    }

    [Test]
    public void Parse_KeepsSystemControlParameters()
    {
        var step = BpfXamlParser.Parse(Legacy(), "lead").Definition.Stages[0].Steps[0];

        Assert.That(step.SystemControl, Is.True);
        Assert.That(step.Parameters, Is.EqualTo("<parameters><IsDeDupLookup>true</IsDeDupLookup></parameters>"));
        Assert.That(step.Label, Is.EqualTo("Existing contact?"));
    }

    [Test]
    public void Parse_LegacyShape_RebuildsIntoTheCurrentShape()
    {
        var parsed = BpfXamlParser.Parse(Legacy(), "lead");
        var xaml = BpfXamlBuilder.Build(parsed.Definition).Xaml;

        Assert.That(xaml, Does.Contain("StageRelationshipCollectionComposite"));
        Assert.That(xaml, Does.Contain("<x:String x:Key=\"NextStageId\">11111111-1111-1111-1111-111111110302</x:String>"));
        Assert.That(xaml, Does.Contain("Parameters=\"&lt;parameters&gt;&lt;IsDeDupLookup&gt;true&lt;/IsDeDupLookup&gt;&lt;/parameters&gt;\""));

        var reparsed = BpfXamlParser.Parse(xaml, "lead");
        Assert.That(reparsed.Notes, Is.Empty, "the rebuilt process is in the current shape");
        Assert.That(reparsed.Definition.Stages.Select(s => s.StageId), Is.EqualTo(parsed.Definition.Stages.Select(s => s.StageId)));
    }

    [Test]
    public void Parse_TaskFlowPage_IsReportedNotDropped()
    {
        var xaml = Envelope(
            $"<mxswa:ActivityReference AssemblyQualifiedName=\"Microsoft.Crm.Workflow.Activities.EntityComposite{Aqn}\" DisplayName=\"EntityStep1: contact\">"
            + "<mxswa:ActivityReference.Properties><sco:Collection x:TypeArguments=\"Activity\" x:Key=\"Activities\">"
            + $"<mxswa:ActivityReference AssemblyQualifiedName=\"Microsoft.Crm.Workflow.Activities.PageComposite{Aqn}\" DisplayName=\"PageStep2: Details\" />"
            + "</sco:Collection></mxswa:ActivityReference.Properties></mxswa:ActivityReference>");

        var parsed = BpfXamlParser.Parse(xaml, "contact");

        Assert.That(parsed.FullyUnderstood, Is.False);
        Assert.That(parsed.Unrecognised, Has.Some.Contains("task flows"));
    }

    [Test]
    public void Parse_EmptyOrBroken_IsReported()
    {
        Assert.That(BpfXamlParser.Parse(null, "lead").Notes, Has.Some.Contains("no XAML"));
        Assert.That(BpfXamlParser.Parse("<Activity", "lead").FullyUnderstood, Is.False);
    }

    [Test]
    public void Parse_Branch_KeepsCasesElseAndDescription()
    {
        var xaml = BpfXamlBuilder.Build(BpfTestData.Full(), BpfTestData.ProcessId, BpfTestData.Catalog()).Xaml;
        var assess = BpfXamlParser.Parse(xaml, "account").Definition.Stages[1];

        var branch = assess.Branch!.Branches.Single();
        Assert.That(branch.Description, Is.EqualTo("Large"));
        Assert.That(branch.LogicalOperator, Is.EqualTo("Or"));
        Assert.That(branch.Conditions.Select(c => c.Attribute), Is.EqualTo(new[] { "numberofemployees", "sample_score" }));
        Assert.That(branch.Conditions.All(c => c.Entity is null), Is.True, "the stage's table is implied");
        Assert.That(assess.Branch.Else, Is.Not.Null);
    }
}
