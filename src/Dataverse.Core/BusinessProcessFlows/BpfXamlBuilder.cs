namespace Dataverse.Core.BusinessProcessFlows;

using System.Text;
using Dataverse.Core.Workflows;

/// <param name="Xaml">The generated document.</param>
/// <param name="Stages">Every stage with the id it was written with, in definition order.</param>
public sealed record BpfBuildResult(string Xaml, IReadOnlyList<BpfBuiltStage> Stages);

/// <param name="StepIds">The <c>ProcessStepId</c> of each step, in order.</param>
public sealed record BpfBuiltStage(string Name, string Entity, string StageId, IReadOnlyList<string> StepIds);

/// <summary>
/// Turns a <see cref="BpfDefinition"/> into business-process-flow XAML, in the shape the current
/// designer writes.
/// </summary>
/// <remarks>
/// <para>
/// One <c>EntityComposite</c> per stage, each holding exactly one <c>StageComposite</c>; the path
/// between them is <c>NextStageId</c>, branches are a <c>ConditionSequence</c> at the end of the
/// stage whose cases each hold one <c>SetNextStage</c>, and cross-table transitions are listed in a
/// <c>StageRelationshipCollectionComposite</c> ahead of all stages. DisplayNames are numbered across
/// the whole document ("EntityStep3", "StageStep4", "StepStep5", "ControlStep6", …).
/// </para>
/// <para>
/// The builder expects a definition that passed <see cref="BpfDefinitionValidator"/>; stage
/// references are resolved here and an unresolvable one throws.
/// </para>
/// </remarks>
public static class BpfXamlBuilder
{
    private const string CrmWfVersion = "Version=9.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35";

    private static string Activity(string name) => $"Microsoft.Crm.Workflow.Activities.{name}, Microsoft.Crm.Workflow, {CrmWfVersion}";

    private static readonly string RelationshipCollection =
        $"Microsoft.Crm.Workflow.BusinessProcessFlowActivities.StageRelationshipCollectionComposite, Microsoft.Crm.Workflow, {CrmWfVersion}";

    /// <param name="catalog">
    /// Attribute types and display names, for the control class and its caption — without them every
    /// field is written as a text control, which the form still renders by attribute type. And the
    /// workflows, actions and flows the definition refers to, whose names the XAML has to carry.
    /// </param>
    /// <param name="languageCode">Language of all labels; the organisation's base language.</param>
    public static BpfBuildResult Build(
        BpfDefinition definition,
        Guid? workflowId = null,
        BpfCatalog? catalog = null,
        int languageCode = 1033)
    {
        ArgumentNullException.ThrowIfNull(definition);
        catalog ??= BpfCatalog.Empty;
        var processId = workflowId ?? Guid.Empty;
        var language = definition.LanguageCode ?? languageCode;

        var resolved = BpfStageResolver.Resolve(definition, assignMissingIds: true);
        foreach (var trigger in definition.Workflows.Concat(definition.Stages.SelectMany(s => s.Workflows)))
            trigger.TriggerId ??= Guid.NewGuid().ToString("D");
        var counter = 0;
        int Next() => ++counter;

        // Ids of the relationship collection come first, as in the designer's numbering.
        var collectionNumber = Next();
        var relationships = new StringBuilder();
        foreach (var stage in resolved.Stages.Where(s => s.Relationship is not null))
        {
            var rel = stage.Relationship!;
            relationships.Append($"<Sequence DisplayName=\"RelationshipStep{Next()}\">")
                .Append($"<mcwb:StageRelationship AttributeName=\"{Xml(rel.Attribute ?? string.Empty)}\" ")
                .Append($"RelationshipName=\"{Xml(rel.Name)}\" ")
                .Append($"SourceStageId=\"{rel.FromStageId}\" TargetStageId=\"{stage.StageId}\" />")
                .Append("</Sequence>");
        }

        var body = new StringBuilder();
        body.Append(Reference(RelationshipCollection, $"RelationshipCollectionStep{collectionNumber}",
            Collections(relationships.ToString())));

        var built = new List<BpfBuiltStage>();

        foreach (var stage in resolved.Stages)
        {
            var entityNumber = Next();
            var stageNumber = Next();
            var inner = new StringBuilder();
            var stepIds = new List<string>();

            foreach (var step in stage.Source.Steps)
            {
                inner.Append(BuildStep(step, stage.Entity, catalog, language, Next));
                stepIds.Add(step.StepId!);
            }

            // Workflows run on entering or leaving this stage.
            foreach (var trigger in stage.Source.Workflows)
                inner.Append(BuildTrigger(trigger, stage.StageId, processId, catalog, Next));

            // The designer keeps the process-level workflows in the first stage.
            if (stage.Index == 0)
                foreach (var trigger in definition.Workflows)
                    inner.Append(BuildTrigger(trigger, stage.StageId, processId, catalog, Next));

            if (stage.Source.Branch is { Branches.Count: > 0 } branching)
                inner.Append(BuildBranching(branching, stage, resolved, Next));

            var nextStage = stage.NextStageId is null
                ? "<x:Null x:Key=\"NextStageId\" />"
                : $"<x:String x:Key=\"NextStageId\">{stage.NextStageId}</x:String>";

            var stageXaml = Reference(Activity("StageComposite"), $"StageStep{stageNumber}: {stage.Source.Name}",
                Collections(inner.ToString())
                + Labels(stage.StageId, stage.Source.Name, language)
                + $"<x:String x:Key=\"StageId\">{stage.StageId}</x:String>"
                + $"<x:String x:Key=\"StageCategory\">{BpfStageCategory.ToNumber(stage.Source.Category)}</x:String>"
                + nextStage);

            body.Append(Reference(Activity("EntityComposite"), $"EntityStep{entityNumber}: {stage.Entity}",
                Collections(stageXaml)
                + "<x:Null x:Key=\"RelationshipName\" />"
                + "<x:Null x:Key=\"AttributeName\" />"
                + "<x:Boolean x:Key=\"IsClosedLoop\">False</x:Boolean>"));

            built.Add(new BpfBuiltStage(stage.Source.Name, stage.Entity, stage.StageId, stepIds));
        }

        return new BpfBuildResult(Envelope(processId, body.ToString()), built);
    }

    private static string BuildStep(
        BpfStep step, string entity, BpfCatalog catalog, int language, Func<int> next)
    {
        var stepNumber = next();

        return step.Kind switch
        {
            BpfStepKind.Field => BuildFieldStep(step, entity, catalog, language, stepNumber, next()),
            BpfStepKind.Action => BuildActionStep(step, catalog, language, stepNumber, next(), next()),
            BpfStepKind.Flow => BuildFlowStep(step, catalog, language, stepNumber, next(), next()),
            _ => throw new NotSupportedException(
                $"Step kind '{step.Kind}' cannot be generated. Kinds: {string.Join(", ", BpfStepKind.All)}.")
        };
    }

    private static string BuildFieldStep(
        BpfStep step, string entity, BpfCatalog catalog, int language, int stepNumber, int controlNumber)
    {
        var attribute = step.Attribute!;
        var info = catalog.Find(entity, attribute);
        var label = string.IsNullOrWhiteSpace(step.Label) ? info?.DisplayName ?? attribute : step.Label!;
        var caption = info?.DisplayName ?? label;
        var classId = step.ClassId ?? BpfControlClass.For(info?.AttributeType);

        // The control needs its Parameters, even empty: without them the platform's UiData generation
        // dereferences null (0x80045037 "Error generating UiData").
        var parameters = string.IsNullOrEmpty(step.Parameters)
            ? " IsSystemControl=\"False\" IsUnbound=\"False\" SystemStepType=\"0\">"
              + "<mcwb:Control.Parameters>" + EmptyString + "</mcwb:Control.Parameters>"
              + "</mcwb:Control>"
            : $" IsSystemControl=\"{(step.SystemControl ? "True" : "False")}\" IsUnbound=\"False\" "
              + $"Parameters=\"{Xml(step.Parameters!)}\" SystemStepType=\"0\" />";

        var control = $"<Sequence DisplayName=\"ControlStep{controlNumber}\">"
                      + $"<mcwb:Control ClassId=\"{classId}\" ControlDisplayName=\"{Xml(caption)}\" "
                      + $"ControlId=\"{Xml(attribute)}\" DataFieldName=\"{Xml(attribute)}\""
                      + parameters
                      + "</Sequence>";

        return StepComposite(stepNumber, label, label, step, language, control);
    }

    /// <summary>
    /// A button running a workflow or an action. The designer stores the process id and — for an
    /// action — its message name; the button's control id is derived from that name.
    /// </summary>
    private static string BuildActionStep(
        BpfStep step, BpfCatalog catalog, int language, int stepNumber, int actionNumber, int controlNumber)
    {
        var target = Process(catalog, step.ProcessId);
        var isAction = target.Category == 3;
        var uniqueName = isAction ? target.UniqueName ?? string.Empty : string.Empty;
        var label = string.IsNullOrWhiteSpace(step.Label) ? target.Name : step.Label!;

        var action = Reference(Activity("ActionComposite"), $"ActionStep{actionNumber}: Step_{actionNumber}",
            EmptyCollections
            + $"<x:String x:Key=\"ActionId\">{step.StepId}</x:String>"
            // 3 = classic workflow, 0 = custom process action.
            + $"<x:Int32 x:Key=\"ActionType\">{(isAction ? 0 : 3)}</x:Int32>"
            + $"<s:Guid x:Key=\"ProcessId\">{target.Id:D}</s:Guid>"
            + $"<x:String x:Key=\"UniqueName\">{Xml(uniqueName)}</x:String>"
            + "<x:Null x:Key=\"TriggerEvents\" />"
            + $"<Sequence x:Key=\"ActionControl\" DisplayName=\"ControlStep{controlNumber}\">"
            + ButtonControl(label, $"{uniqueName}_Step_{controlNumber}")
            + "</Sequence>");

        // An action step cannot be required; the designer offers no such option.
        return StepComposite(stepNumber, target.Name, label, step with { Required = false }, language, action);
    }

    private static string BuildFlowStep(
        BpfStep step, BpfCatalog catalog, int language, int stepNumber, int flowNumber, int controlNumber)
    {
        var flow = Process(catalog, step.ProcessId);
        var label = string.IsNullOrWhiteSpace(step.Label) ? flow.Name : step.Label!;
        var stepId = Guid.Parse(step.StepId!);

        var composite = Reference(Activity("FlowComposite"), $"FlowStep{flowNumber}: Step_{flowNumber}",
            EmptyCollections
            + $"<s:Guid x:Key=\"WorkflowId\">{flow.Id:D}</s:Guid>"
            + $"<s:Guid x:Key=\"ActionId\">{stepId:D}</s:Guid>"
            + $"<x:String x:Key=\"UniqueName\">FlowStep_{flow.Id:N}_{stepId:N}</x:String>"
            + $"<Sequence x:Key=\"FlowControl\" DisplayName=\"ControlStep{controlNumber}\">"
            + ButtonControl(label, $"Step_{controlNumber}")
            + "</Sequence>");

        return StepComposite(stepNumber, flow.Name, label, step, language, composite);
    }

    /// <summary>A workflow started by a stage or process event — an ActionComposite without a button.</summary>
    private static string BuildTrigger(
        BpfWorkflowTrigger trigger, string stageId, Guid processId, BpfCatalog catalog, Func<int> next)
    {
        var number = next();
        var workflow = Process(catalog, trigger.WorkflowId);

        var (eventName, filterId, pipelineStage) = trigger.On switch
        {
            BpfTriggerEvent.StageEnter => ("STAGEENTER", stageId, 40),
            BpfTriggerEvent.StageExit => ("STAGEEXIT", stageId, 20),
            BpfTriggerEvent.Applied => ("PROCESSAPPLIED", processId.ToString("D").ToUpperInvariant(), 40),
            BpfTriggerEvent.Reactivated => ("PROCESSSTATUSCHANGE", "1", 40),
            BpfTriggerEvent.Finished => ("PROCESSSTATUSCHANGE", "2", 40),
            BpfTriggerEvent.Abandoned => ("PROCESSSTATUSCHANGE", "3", 40),
            _ => throw new NotSupportedException($"Unknown trigger '{trigger.On}'.")
        };

        return Reference(Activity("ActionComposite"), $"ActionStep{number}: Step_{number}",
            EmptyCollections
            + $"<x:String x:Key=\"ActionId\">{trigger.TriggerId}</x:String>"
            + "<x:Int32 x:Key=\"ActionType\">3</x:Int32>"
            + $"<s:Guid x:Key=\"ProcessId\">{workflow.Id:D}</s:Guid>"
            // For a triggered workflow the designer stores its display name here.
            + $"<x:String x:Key=\"UniqueName\">{Xml(workflow.Name)}</x:String>"
            + "<x:Array x:Key=\"TriggerEvents\" Type=\"mcwo:ProcessTriggerData\">"
            + $"<mcwo:ProcessTriggerData Event=\"{eventName}\" FilterId=\"{filterId}\" PipelineStageId=\"{pipelineStage}\" />"
            + "</x:Array>"
            + "<x:Null x:Key=\"ActionControl\" />");
    }

    /// <param name="displayDescription">Part of the DisplayName ("StepStep7: …"); the designer puts the target's name there.</param>
    /// <param name="label">The label shown in the stage.</param>
    private static string StepComposite(
        int stepNumber, string displayDescription, string label, BpfStep step, int language, string activity) =>
        Reference(Activity("StepComposite"), $"StepStep{stepNumber}: {displayDescription}",
            Collections(activity)
            + Labels(step.StepId!, label, language)
            + $"<x:String x:Key=\"ProcessStepId\">{step.StepId}</x:String>"
            + $"<x:Boolean x:Key=\"IsProcessRequired\">{(step.Required ? "True" : "False")}</x:Boolean>");

    /// <summary>The unbound button control of an action or flow step.</summary>
    private static string ButtonControl(string caption, string controlId) =>
        $"<mcwb:Control ClassId=\"{BpfControlClass.Button}\" ControlDisplayName=\"{Xml(caption)}\" "
        + $"ControlId=\"{Xml(controlId)}\" IsSystemControl=\"False\" IsUnbound=\"True\" SystemStepType=\"0\">"
        + "<mcwb:Control.DataFieldName>" + EmptyString + "</mcwb:Control.DataFieldName>"
        + "<mcwb:Control.Parameters>" + EmptyString + "</mcwb:Control.Parameters>"
        + "</mcwb:Control>";

    private const string EmptyString =
        "<InArgument x:TypeArguments=\"x:String\"><Literal x:TypeArguments=\"x:String\" Value=\"\" /></InArgument>";

    private const string EmptyCollections =
        "<sco:Collection x:TypeArguments=\"Variable\" x:Key=\"Variables\" />"
        + "<sco:Collection x:TypeArguments=\"Activity\" x:Key=\"Activities\" />";

    private static BpfProcessInfo Process(BpfCatalog catalog, string? id)
    {
        if (!Guid.TryParse(id, out var guid))
            throw new InvalidOperationException($"'{id}' is not a process id.");

        return catalog.FindProcess(guid)
               ?? throw new InvalidOperationException(
                   $"Process {guid} is not in the catalog; its name is needed for the XAML. Validate first.");
    }

    private static string BuildBranching(
        BpfBranching branching, BpfResolvedStage stage, BpfResolvedDefinition resolved, Func<int> next)
    {
        var conditionNumber = next();

        string SetNextStage(string target) =>
            $"<Sequence DisplayName=\"SetNextStageStep{next()}\">"
            + $"<mcwc:SetNextStage ParentStageId=\"{stage.StageId}\" StageId=\"{resolved.IdOf(target)}\" />"
            + "</Sequence>";

        var cases = new List<WorkflowXamlBuilder.ConditionCase>();
        foreach (var branch in branching.Branches)
        {
            var branchId = $"ConditionBranchStep{next()}";
            cases.Add(new WorkflowXamlBuilder.ConditionCase(
                branchId,
                // A comparison names no table of its own: it reads the stage's table.
                branch.Conditions.Select(c => WithEntity(c, stage.Entity)).ToList(),
                branch.LogicalOperator,
                branch.Description,
                SetNextStage(branch.Next)));
        }

        WorkflowXamlBuilder.ConditionCase? otherwise = null;
        if (!string.IsNullOrWhiteSpace(branching.Else))
        {
            var branchId = $"ConditionBranchStep{next()}";
            otherwise = new WorkflowXamlBuilder.ConditionCase(branchId, [], null, null, SetNextStage(branching.Else!));
        }

        return WorkflowXamlBuilder.BuildConditionSequence(
            $"ConditionStep{conditionNumber}", stage.Entity, cases, otherwise);
    }

    private static WorkflowCondition WithEntity(WorkflowCondition condition, string entity) =>
        condition.IsGroup
            ? condition with { Conditions = condition.Conditions!.Select(c => WithEntity(c, entity)).ToList() }
            : condition with { Entity = entity };

    // ---------------------------------------------------------------- rendering helpers

    private static string Reference(string assemblyQualifiedName, string displayName, string properties) =>
        $"<mxswa:ActivityReference AssemblyQualifiedName=\"{assemblyQualifiedName}\" DisplayName=\"{Xml(displayName)}\">"
        + $"<mxswa:ActivityReference.Properties>{properties}</mxswa:ActivityReference.Properties>"
        + "</mxswa:ActivityReference>";

    private static string Collections(string activities) =>
        "<sco:Collection x:TypeArguments=\"Variable\" x:Key=\"Variables\" />"
        + (activities.Length == 0
            ? "<sco:Collection x:TypeArguments=\"Activity\" x:Key=\"Activities\" />"
            : $"<sco:Collection x:TypeArguments=\"Activity\" x:Key=\"Activities\">{activities}</sco:Collection>");

    private static string Labels(string labelId, string text, int language) =>
        "<sco:Collection x:TypeArguments=\"mcwo:StepLabel\" x:Key=\"StepLabels\">"
        + $"<mcwo:StepLabel Description=\"{Xml(text)}\" LabelId=\"{labelId}\" LanguageCode=\"{language}\" />"
        + "</sco:Collection>";

    private static string Envelope(Guid workflowId, string body)
    {
        var cls = $"XrmWorkflow{workflowId:N}";
        const string sdk = "Version=9.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35";
        const string mscorlib = "Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089";

        return $"<Activity x:Class=\"{cls}\""
               + " xmlns=\"http://schemas.microsoft.com/netfx/2009/xaml/activities\""
               + $" xmlns:mcwb=\"clr-namespace:Microsoft.Crm.Workflow.BusinessProcessFlowActivities;assembly=Microsoft.Crm.Workflow, {sdk}\""
               + $" xmlns:mcwc=\"clr-namespace:Microsoft.Crm.Workflow.ClientActivities;assembly=Microsoft.Crm.Workflow, {sdk}\""
               + $" xmlns:mcwo=\"clr-namespace:Microsoft.Crm.Workflow.ObjectModel;assembly=Microsoft.Crm, {sdk}\""
               + " xmlns:mva=\"clr-namespace:Microsoft.VisualBasic.Activities;assembly=System.Activities, Version=4.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35\""
               + $" xmlns:mxs=\"clr-namespace:Microsoft.Xrm.Sdk;assembly=Microsoft.Xrm.Sdk, {sdk}\""
               + $" xmlns:mxsq=\"clr-namespace:Microsoft.Xrm.Sdk.Query;assembly=Microsoft.Xrm.Sdk, {sdk}\""
               + $" xmlns:mxswa=\"clr-namespace:Microsoft.Xrm.Sdk.Workflow.Activities;assembly=Microsoft.Xrm.Sdk.Workflow, {sdk}\""
               + $" xmlns:s=\"clr-namespace:System;assembly=mscorlib, {mscorlib}\""
               + $" xmlns:scg=\"clr-namespace:System.Collections.Generic;assembly=mscorlib, {mscorlib}\""
               + $" xmlns:sco=\"clr-namespace:System.Collections.ObjectModel;assembly=mscorlib, {mscorlib}\""
               + $" xmlns:srs=\"clr-namespace:System.Runtime.Serialization;assembly=System.Runtime.Serialization, {mscorlib}\""
               + " xmlns:this=\"clr-namespace:\""
               + " xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\">"
               + "<x:Members>"
               + "<x:Property Name=\"InputEntities\" Type=\"InArgument(scg:IDictionary(x:String, mxs:Entity))\" />"
               + "<x:Property Name=\"CreatedEntities\" Type=\"InArgument(scg:IDictionary(x:String, mxs:Entity))\" />"
               + "</x:Members>"
               + $"<this:{cls}.InputEntities><InArgument x:TypeArguments=\"scg:IDictionary(x:String, mxs:Entity)\" /></this:{cls}.InputEntities>"
               + $"<this:{cls}.CreatedEntities><InArgument x:TypeArguments=\"scg:IDictionary(x:String, mxs:Entity)\" /></this:{cls}.CreatedEntities>"
               + "<mva:VisualBasic.Settings>Assembly references and imported namespaces for internal implementation</mva:VisualBasic.Settings>"
               + $"<mxswa:Workflow>{body}</mxswa:Workflow>"
               + "</Activity>";
    }

    private static string Xml(string value) =>
        value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
}
