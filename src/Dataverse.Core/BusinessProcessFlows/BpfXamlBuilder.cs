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

    /// <param name="fields">
    /// Attribute types and display names, for the control class and its caption. Without it every
    /// field is written as a text control, which the form still renders by attribute type.
    /// </param>
    /// <param name="languageCode">Language of all labels; the organisation's base language.</param>
    public static BpfBuildResult Build(
        BpfDefinition definition,
        Guid? workflowId = null,
        BpfFieldCatalog? fields = null,
        int languageCode = 1033)
    {
        ArgumentNullException.ThrowIfNull(definition);
        fields ??= BpfFieldCatalog.Empty;
        var language = definition.LanguageCode ?? languageCode;

        var resolved = BpfStageResolver.Resolve(definition, assignMissingIds: true);
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
                inner.Append(BuildStep(step, stage.Entity, fields, language, Next));
                stepIds.Add(step.StepId!);
            }

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

        return new BpfBuildResult(Envelope(workflowId ?? Guid.Empty, body.ToString()), built);
    }

    private static string BuildStep(
        BpfStep step, string entity, BpfFieldCatalog fields, int language, Func<int> next)
    {
        var stepNumber = next();
        var controlNumber = next();

        if (step.Kind != BpfStepKind.Field)
            throw new NotSupportedException(
                $"Step kind '{step.Kind}' cannot be generated yet. Writable kinds: {BpfStepKind.Field}.");

        var attribute = step.Attribute!;
        var info = fields.Find(entity, attribute);
        var label = string.IsNullOrWhiteSpace(step.Label) ? info?.DisplayName ?? attribute : step.Label!;
        var caption = info?.DisplayName ?? label;
        var classId = step.ClassId ?? BpfControlClass.For(info?.AttributeType);

        var parameters = string.IsNullOrEmpty(step.Parameters)
            ? " IsSystemControl=\"False\" IsUnbound=\"False\" SystemStepType=\"0\">"
              + "<mcwb:Control.Parameters><InArgument x:TypeArguments=\"x:String\">"
              + "<Literal x:TypeArguments=\"x:String\" Value=\"\" /></InArgument></mcwb:Control.Parameters>"
              + "</mcwb:Control>"
            : $" IsSystemControl=\"{(step.SystemControl ? "True" : "False")}\" IsUnbound=\"False\" "
              + $"Parameters=\"{Xml(step.Parameters!)}\" SystemStepType=\"0\" />";

        var control = $"<Sequence DisplayName=\"ControlStep{controlNumber}\">"
                      + $"<mcwb:Control ClassId=\"{classId}\" ControlDisplayName=\"{Xml(caption)}\" "
                      + $"ControlId=\"{Xml(attribute)}\" DataFieldName=\"{Xml(attribute)}\""
                      + parameters
                      + "</Sequence>";

        return Reference(Activity("StepComposite"), $"StepStep{stepNumber}: {label}",
            Collections(control)
            + Labels(step.StepId!, label, language)
            + $"<x:String x:Key=\"ProcessStepId\">{step.StepId}</x:String>"
            + $"<x:Boolean x:Key=\"IsProcessRequired\">{(step.Required ? "True" : "False")}</x:Boolean>");
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
