namespace Dataverse.Core.Workflows;

using System.Globalization;
using System.Text;

/// <summary>Result of a build run.</summary>
/// <param name="Xaml">The generated XAML, ready for <c>PATCH workflows(id) {"xaml": ...}</c>.</param>
/// <param name="StepIds">Assigned step ids in assignment order, for reporting back to the caller.</param>
public sealed record WorkflowBuildResult(string Xaml, IReadOnlyList<string> StepIds);

/// <summary>
/// Turns a <see cref="WorkflowDefinition"/> into Classic Workflow XAML.
/// </summary>
/// <remarks>
/// <para>
/// The builder owns every naming decision — step ids, DisplayNames, variable names — because the
/// Dataverse designer reconstructs its UI from exactly these conventions. Ids are handed out in
/// pre-order, with a single counter shared across all step kinds, matching the designer's own
/// numbering (ConditionStep1, ConditionBranchStep2, UpdateStep3, ...).
/// </para>
/// <para>
/// Namespace declarations are emitted on demand: <c>mcwa</c> only appears when an SDK message
/// activity is present, mirroring the designer's behaviour.
/// </para>
/// <para>Validate a definition with <see cref="WorkflowDefinitionValidator"/> before building.</para>
/// </remarks>
public static class WorkflowXamlBuilder
{
    private const string CrmWf = "Microsoft.Crm.Workflow";
    private const string CrmWfVersion = "Version=9.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35";

    private static string CrmActivity(string name) =>
        $"Microsoft.Crm.Workflow.Activities.{name}, {CrmWf}, {CrmWfVersion}";

    /// <param name="activities">
    /// Parameter metadata of the referenced code activities. Without it the caller's
    /// <c>dataType</c> is used for the arguments of a <c>customActivity</c> step, and the type of an
    /// output cannot be known at all.
    /// </param>
    public static WorkflowBuildResult Build(
        WorkflowDefinition definition, Guid? workflowId = null, WorkflowActivityCatalog? activities = null)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var state = new BuildState(definition.PrimaryEntity, activities ?? WorkflowActivityCatalog.Empty)
        {
            Realtime = definition.Realtime
        };
        AssignStepIds(definition.Steps, state);

        // Which activity outputs are read as records? Those need a RetrieveEntity next to the
        // activity, and that has to be known before the activity itself is emitted.
        CollectOutputsToLoad(definition.Steps, state);

        var body = new StringBuilder();
        foreach (var step in definition.Steps)
            EmitStep(step, state, body, indentLevel: 0);

        var guid = (workflowId ?? Guid.Empty).ToString("N");
        var xaml = Envelope(guid, state, body.ToString());
        return new WorkflowBuildResult(xaml, state.AssignedStepIds);
    }

    // ---------------------------------------------------------------- id assignment

    private static void AssignStepIds(List<WorkflowStep>? steps, BuildState state)
    {
        if (steps is null)
            return;

        foreach (var step in steps)
        {
            state.AssignId(step);

            if (step.Kind is WorkflowStepKind.Condition or WorkflowStepKind.Wait)
            {
                // Each case owns a branch node, and its steps live inside it. Ids run in document
                // order — branch, its contents, next branch — matching the designer's numbering.
                foreach (var branch in EffectiveBranches(step, state))
                {
                    state.AssignBranchId(branch);
                    AssignStepIds(branch.Steps, state);
                }

                if (step.Else is { Count: > 0 })
                {
                    state.AssignElseBranchId(step);
                    AssignStepIds(step.Else, state);
                }
            }
            else if (step.Kind == WorkflowStepKind.Stage)
            {
                AssignStepIds(step.Children, state);
            }
        }
    }

    /// <summary>
    /// Walks the whole definition for values that read fields of a record behind an activity output,
    /// and notes the parameter names. Emission then knows which outputs to load.
    /// </summary>
    private static void CollectOutputsToLoad(List<WorkflowStep>? steps, BuildState state)
    {
        if (steps is null)
            return;

        void Note(string? reference)
        {
            if (string.IsNullOrWhiteSpace(reference))
                return;
            state.OutputsToLoad.Add(reference.Contains('.') ? reference.Split('.', 2)[1] : reference);
        }

        void FromValue(WorkflowValue? value)
        {
            if (value is null)
                return;
            Note(value.FromStepOutput);
            foreach (var part in value.Parts ?? [])
                FromValue(part);
        }

        foreach (var step in steps)
        {
            foreach (var condition in step.Conditions ?? [])
            {
                Note(condition.FromStepOutput);
                FromValue(condition.Value);
            }

            foreach (var branch in step.Branches ?? [])
            {
                foreach (var condition in branch.Conditions)
                {
                    Note(condition.FromStepOutput);
                    FromValue(condition.Value);
                }
                CollectOutputsToLoad(branch.Steps, state);
            }

            foreach (var assignment in step.Attributes ?? [])
                FromValue(assignment.Value);

            foreach (var (_, input) in step.Inputs ?? [])
                FromValue(input);

            FromValue(step.Reason);

            CollectOutputsToLoad(step.Then, state);
            CollectOutputsToLoad(step.Else, state);
            CollectOutputsToLoad(step.Children, state);
        }
    }

    /// <summary>
    /// The cases of a condition step, whichever form the caller used: <c>branches</c> as given, or the
    /// short form <c>conditions</c> + <c>then</c> folded into a single case.
    /// </summary>
    /// <remarks>
    /// The result is cached per step: the short form is wrapped in a branch object that the id
    /// assignment writes into, and a second call must hand back that same object.
    /// </remarks>
    private static List<WorkflowConditionBranch> EffectiveBranches(WorkflowStep step, BuildState state)
    {
        if (step.Branches is { Count: > 0 })
            return step.Branches;

        return state.ShortFormBranchOf(step);
    }

    // ---------------------------------------------------------------- envelope

    private static string Envelope(string guidN, BuildState state, string body)
    {
        var cls = $"XrmWorkflow{guidN}";

        var ns = new List<string>
        {
            "xmlns=\"http://schemas.microsoft.com/netfx/2009/xaml/activities\"",
            "xmlns:mva=\"clr-namespace:Microsoft.VisualBasic.Activities;assembly=System.Activities, Version=4.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35\""
        };

        if (state.UsesCrmActivities)
            ns.Add("xmlns:mcwa=\"clr-namespace:Microsoft.Crm.Workflow.Activities;assembly=Microsoft.Crm.Workflow, Version=9.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35\"");

        ns.Add("xmlns:mxs=\"clr-namespace:Microsoft.Xrm.Sdk;assembly=Microsoft.Xrm.Sdk, Version=9.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35\"");

        if (state.UsesQueryTypes)
            ns.Add("xmlns:mxsq=\"clr-namespace:Microsoft.Xrm.Sdk.Query;assembly=Microsoft.Xrm.Sdk, Version=9.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35\"");

        // Only when a duration is written: the designer omits the prefix in workflows without one.
        if (state.UsesTimeSpan)
            ns.Add("xmlns:mxsw=\"clr-namespace:Microsoft.Xrm.Sdk.Workflow;assembly=Microsoft.Xrm.Sdk.Workflow, Version=9.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35\"");

        ns.Add("xmlns:mxswa=\"clr-namespace:Microsoft.Xrm.Sdk.Workflow.Activities;assembly=Microsoft.Xrm.Sdk.Workflow, Version=9.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35\"");
        ns.Add("xmlns:s=\"clr-namespace:System;assembly=mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089\"");
        ns.Add("xmlns:scg=\"clr-namespace:System.Collections.Generic;assembly=mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089\"");
        ns.Add("xmlns:sco=\"clr-namespace:System.Collections.ObjectModel;assembly=mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089\"");
        ns.Add("xmlns:srs=\"clr-namespace:System.Runtime.Serialization;assembly=System.Runtime.Serialization, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089\"");
        ns.Add("xmlns:this=\"clr-namespace:\"");
        ns.Add("xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"");

        var workflowVars = state.WorkflowVariables.Count == 0
            ? string.Empty
            : "<mxswa:Workflow.Variables>"
              + string.Concat(state.WorkflowVariables.Select(v =>
                  $"<Variable x:TypeArguments=\"{v.TypeArgument}\""
                  + (v.Default is null ? string.Empty : $" Default=\"{v.Default}\"")
                  + $" Name=\"{v.Name}\" />"))
              + "</mxswa:Workflow.Variables>";

        var workflowElement = string.IsNullOrEmpty(body) && string.IsNullOrEmpty(workflowVars)
            ? "<mxswa:Workflow />"
            : $"<mxswa:Workflow>{workflowVars}{body}</mxswa:Workflow>";

        return "<?xml version=\"1.0\" encoding=\"utf-16\"?>"
             + $"<Activity x:Class=\"{cls}\" {string.Join(" ", ns)}>"
             + "<x:Members>"
             + "<x:Property Name=\"InputEntities\" Type=\"InArgument(scg:IDictionary(x:String, mxs:Entity))\" />"
             + "<x:Property Name=\"CreatedEntities\" Type=\"InArgument(scg:IDictionary(x:String, mxs:Entity))\" />"
             + "</x:Members>"
             + $"<this:{cls}.InputEntities><InArgument x:TypeArguments=\"scg:IDictionary(x:String, mxs:Entity)\" /></this:{cls}.InputEntities>"
             + $"<this:{cls}.CreatedEntities><InArgument x:TypeArguments=\"scg:IDictionary(x:String, mxs:Entity)\" /></this:{cls}.CreatedEntities>"
             + "<mva:VisualBasic.Settings>Assembly references and imported namespaces for internal implementation</mva:VisualBasic.Settings>"
             + workflowElement
             + "</Activity>";
    }

    // ---------------------------------------------------------------- steps

    private static void EmitStep(WorkflowStep step, BuildState state, StringBuilder sb, int indentLevel)
    {
        switch (step.Kind)
        {
            case WorkflowStepKind.Condition:
            case WorkflowStepKind.Wait:
                EmitCondition(step, state, sb);
                break;
            case WorkflowStepKind.Stage:
                EmitStage(step, state, sb);
                break;
            case WorkflowStepKind.UpdateRecord:
                EmitUpdate(step, state, sb);
                break;
            case WorkflowStepKind.CreateRecord:
                EmitCreate(step, state, sb);
                break;
            case WorkflowStepKind.AssignRecord:
                EmitAssign(step, state, sb);
                break;
            case WorkflowStepKind.ChangeStatus:
                EmitChangeStatus(step, state, sb);
                break;
            case WorkflowStepKind.StopWorkflow:
                EmitStopWorkflow(step, state, sb);
                break;
            case WorkflowStepKind.CustomActivity:
                EmitCustomActivity(step, state, sb);
                break;
            case WorkflowStepKind.StartChildWorkflow:
                EmitChildWorkflow(step, state, sb);
                break;
            case WorkflowStepKind.SendEmail:
                EmitSendEmail(step, state, sb);
                break;
            default:
                throw new NotSupportedException(
                    $"Step kind '{step.Kind}' cannot be generated. Writable kinds: " +
                    string.Join(", ", WorkflowStepKind.Writable));
        }
    }

    private static string DisplayName(WorkflowStep step) =>
        string.IsNullOrWhiteSpace(step.Description)
            ? step.StepId!
            : $"{step.StepId}: {Xml(step.Description!)}";

    /// <remarks>
    /// One <c>ConditionSequence</c> carries every case of the chain: the comparisons of all branches
    /// followed by their <c>ConditionBranch</c> nodes, with the variables of all branches declared in
    /// a single collection. Helper variables are named after the branch
    /// (<c>ConditionBranchStep9_2</c>), not the step — that naming is what makes it possible to tell
    /// afterwards which comparison belongs to which case.
    /// </remarks>
    private static void EmitCondition(WorkflowStep step, BuildState state, StringBuilder sb)
    {
        state.UsesQueryTypes = true;

        var branches = EffectiveBranches(step, state);
        var scopes = new List<StepScope>();
        var activities = new StringBuilder();

        foreach (var branch in branches)
            activities.Append(EmitBranch(branch, state, scopes));

        var hasElse = step.Else is { Count: > 0 };
        if (hasElse)
        {
            var elseBranchId = state.ElseBranchIdOf(step)!;
            activities.Append(Branch(elseBranchId, "True", step.Else, state));
        }

        var wait = step.Kind == WorkflowStepKind.Wait ? "True" : "False";
        var containsElse = step.Kind == WorkflowStepKind.Wait
            ? "<x:Null x:Key=\"ContainsElseBranch\" />"
            : $"<x:Boolean x:Key=\"ContainsElseBranch\">{(hasElse ? "True" : "False")}</x:Boolean>";

        sb.Append(
            $"<mxswa:ActivityReference AssemblyQualifiedName=\"{CrmActivity("ConditionSequence")}\" DisplayName=\"{DisplayName(step)}\">"
            + "<mxswa:ActivityReference.Arguments>"
            + $"<InArgument x:TypeArguments=\"x:Boolean\" x:Key=\"Wait\">{wait}</InArgument>"
            + "</mxswa:ActivityReference.Arguments>"
            + "<mxswa:ActivityReference.Properties>"
            + RenderSharedVariables(scopes)
            + $"<sco:Collection x:TypeArguments=\"Activity\" x:Key=\"Activities\">{activities}</sco:Collection>"
            + containsElse
            + "</mxswa:ActivityReference.Properties></mxswa:ActivityReference>");
    }

    /// <summary>The variables of every case in one collection, self-closing when there are none.</summary>
    private static string RenderSharedVariables(List<StepScope> scopes)
    {
        var declarations = string.Concat(scopes.Select(s => s.RenderDeclarations()));
        return declarations.Length == 0
            ? "<sco:Collection x:TypeArguments=\"Variable\" x:Key=\"Variables\" />"
            : $"<sco:Collection x:TypeArguments=\"Variable\" x:Key=\"Variables\">{declarations}</sco:Collection>";
    }

    /// <summary>Comparisons of one case plus its branch node.</summary>
    private static string EmitBranch(
        WorkflowConditionBranch branch, BuildState state, List<StepScope> scopes)
    {
        var branchId = branch.BranchId!;
        var ctx = new StepScope(branchId);
        scopes.Add(ctx);

        var conditionVar = $"{branchId}_condition";
        ctx.DeclareVariable(conditionVar, "x:Boolean", defaultFalse: true);

        // Each comparison yields a boolean; several are folded with EvaluateLogicalCondition.
        var boolVars = new List<string>();
        var activities = new StringBuilder();

        foreach (var condition in branch.Conditions)
        {
            string left;
            if (!string.IsNullOrWhiteSpace(condition.StepOutput))
            {
                // Comparing the output of an earlier code activity: its variable is the operand,
                // so no GetEntityProperty is emitted.
                left = StepOutputVariable(condition.StepOutput!, state);
            }
            else
            {
                left = ctx.NextVariable("x:Object");
                activities.Append(GetEntityProperty(
                    condition.Attribute,
                    EntityExpression(condition.Entity, state, condition.Via,
                        condition.FromStep, condition.FromStepOutput),
                    condition.Entity ?? state.PrimaryEntity,
                    left,
                    targetType: null));
            }

            // In / NotIn compare against a set, so the parameter array can hold several values.
            var rightVars = new List<string>();
            if (condition.Value is { } value)
            {
                if (value.Kind == WorkflowValueKind.Literal && value.Literals is { Count: > 0 })
                {
                    foreach (var literal in value.Literals)
                    {
                        var target = ctx.NextVariable("x:Object");
                        activities.Append(CreateCrmType(literal, value.DataType,
                            XamlTypeFor(value.DataType) ?? "x:String", target));
                        rightVars.Add(target);
                    }
                }
                else
                {
                    rightVars.Add(EmitValue(value, ctx, activities, state, convertForCodeActivity: false,
                        // In a comparison the designer reads a single field straight into the operand
                        // variable, without the SelectFirstNonNull indirection used for field values.
                        simplifyFieldRead: true));
                }
            }

            var resultVar = branch.Conditions.Count == 1 ? conditionVar : ctx.NextVariable("x:Boolean", defaultFalse: true);
            if (branch.Conditions.Count > 1)
                boolVars.Add(resultVar);

            // Value-less operators (Null / NotNull) must emit an explicit null for Parameters.
            // An empty array initialiser is rejected by the platform with 0x80045040.
            var parameters = rightVars.Count == 0
                ? "<x:Null x:Key=\"Parameters\" />"
                : $"<InArgument x:TypeArguments=\"s:Object[]\" x:Key=\"Parameters\">[New Object() {{ {string.Join(", ", rightVars)} }}]</InArgument>";

            activities.Append(
                $"<mxswa:ActivityReference AssemblyQualifiedName=\"{CrmActivity("EvaluateCondition")}\" DisplayName=\"EvaluateCondition\">"
                + "<mxswa:ActivityReference.Arguments>"
                + $"<InArgument x:TypeArguments=\"mxsq:ConditionOperator\" x:Key=\"ConditionOperator\">{MapOperator(condition.Operator)}</InArgument>"
                + parameters
                + $"<InArgument x:TypeArguments=\"x:Object\" x:Key=\"Operand\">[{left}]</InArgument>"
                + $"<OutArgument x:TypeArguments=\"x:Boolean\" x:Key=\"Result\">[{resultVar}]</OutArgument>"
                + "</mxswa:ActivityReference.Arguments></mxswa:ActivityReference>");
        }

        // Fold multiple comparisons pairwise into the condition variable.
        if (boolVars.Count > 1)
        {
            var op = string.Equals(branch.LogicalOperator, "Or", StringComparison.OrdinalIgnoreCase) ? "Or" : "And";
            var current = boolVars[0];
            for (var i = 1; i < boolVars.Count; i++)
            {
                var isLast = i == boolVars.Count - 1;
                var target = isLast ? conditionVar : ctx.NextVariable("x:Boolean", defaultFalse: true);
                activities.Append(
                    $"<mxswa:ActivityReference AssemblyQualifiedName=\"{CrmActivity("EvaluateLogicalCondition")}\" DisplayName=\"EvaluateLogicalCondition\">"
                    + "<mxswa:ActivityReference.Arguments>"
                    + $"<InArgument x:TypeArguments=\"mxsq:LogicalOperator\" x:Key=\"LogicalOperator\">{op}</InArgument>"
                    + $"<InArgument x:TypeArguments=\"x:Boolean\" x:Key=\"LeftOperand\">[{current}]</InArgument>"
                    + $"<InArgument x:TypeArguments=\"x:Boolean\" x:Key=\"RightOperand\">[{boolVars[i]}]</InArgument>"
                    + $"<OutArgument x:TypeArguments=\"x:Boolean\" x:Key=\"Result\">[{target}]</OutArgument>"
                    + "</mxswa:ActivityReference.Arguments></mxswa:ActivityReference>");
                current = target;
            }
        }

        // The branch node itself, carrying the steps of this case.
        activities.Append(Branch(branchId, $"[{conditionVar}]", branch.Steps, state));
        return activities.ToString();
    }

    private static string Branch(string branchId, string conditionExpression, List<WorkflowStep>? steps, BuildState state)
    {
        var then = "<x:Null x:Key=\"Then\" />";
        if (steps is { Count: > 0 })
        {
            var inner = new StringBuilder();
            foreach (var child in steps)
                EmitStep(child, state, inner, 0);

            then = $"<mxswa:ActivityReference x:Key=\"Then\" AssemblyQualifiedName=\"{CrmActivity("Composite")}\" DisplayName=\"{branchId}\">"
                 + "<mxswa:ActivityReference.Properties>"
                 + "<sco:Collection x:TypeArguments=\"Variable\" x:Key=\"Variables\" />"
                 + $"<sco:Collection x:TypeArguments=\"Activity\" x:Key=\"Activities\">{inner}</sco:Collection>"
                 + "</mxswa:ActivityReference.Properties></mxswa:ActivityReference>";
        }

        return $"<mxswa:ActivityReference AssemblyQualifiedName=\"{CrmActivity("ConditionBranch")}\" DisplayName=\"{branchId}\">"
             + "<mxswa:ActivityReference.Arguments>"
             + $"<InArgument x:TypeArguments=\"x:Boolean\" x:Key=\"Condition\">{conditionExpression}</InArgument>"
             + "</mxswa:ActivityReference.Arguments>"
             + "<mxswa:ActivityReference.Properties>"
             + then
             + "<x:Null x:Key=\"Else\" /><x:Null x:Key=\"Description\" />"
             + "</mxswa:ActivityReference.Properties></mxswa:ActivityReference>";
    }

    private static void EmitStage(WorkflowStep step, BuildState state, StringBuilder sb)
    {
        var inner = new StringBuilder();
        foreach (var child in step.Children ?? [])
            EmitStep(child, state, inner, 0);
        inner.Append(state.Persist);

        sb.Append(
            $"<mxswa:ActivityReference AssemblyQualifiedName=\"{CrmActivity("Composite")}\" DisplayName=\"{DisplayName(step)}\">"
            + "<mxswa:ActivityReference.Properties>"
            + "<sco:Collection x:TypeArguments=\"Variable\" x:Key=\"Variables\" />"
            + $"<sco:Collection x:TypeArguments=\"Activity\" x:Key=\"Activities\">{inner}</sco:Collection>"
            + "</mxswa:ActivityReference.Properties></mxswa:ActivityReference>");
    }

    private static void EmitUpdate(WorkflowStep step, BuildState state, StringBuilder sb)
    {
        var entity = step.Entity ?? state.PrimaryEntity;
        var ctx = new StepScope(step.StepId!);
        var activities = new StringBuilder();
        var temp = "primaryEntity#Temp";

        activities.Append($"<Assign x:TypeArguments=\"mxs:Entity\" To=\"[CreatedEntities(&quot;{temp}&quot;)]\" Value=\"[New Entity(&quot;{entity}&quot;)]\" />");
        activities.Append($"<Assign x:TypeArguments=\"s:Guid\" To=\"[CreatedEntities(&quot;{temp}&quot;).Id]\" Value=\"[InputEntities(&quot;primaryEntity&quot;).Id]\" />");

        foreach (var assignment in step.Attributes ?? [])
        {
            var v = EmitValue(assignment.Value, ctx, activities, state, convertForCodeActivity: false);
            activities.Append(SetEntityProperty(assignment.Attribute, $"[CreatedEntities(&quot;{temp}&quot;)]", entity, v,
                XamlTypeFor(assignment.Value.DataType)));
        }

        activities.Append($"<mxswa:UpdateEntity DisplayName=\"{step.StepId}\" Entity=\"[CreatedEntities(&quot;{temp}&quot;)]\" EntityName=\"{entity}\" />");
        activities.Append($"<Assign x:TypeArguments=\"mxs:Entity\" To=\"[InputEntities(&quot;primaryEntity&quot;)]\" Value=\"[CreatedEntities(&quot;{temp}&quot;)]\" />");
        activities.Append(state.Persist);

        sb.Append($"<Sequence DisplayName=\"{DisplayName(step)}\">{ctx.RenderSequenceVariables()}{activities}</Sequence>");
    }

    private static void EmitCreate(WorkflowStep step, BuildState state, StringBuilder sb)
    {
        var entity = step.Entity!;
        state.RegisterCreatedRecord(step);

        var ctx = new StepScope(step.StepId!);
        var activities = new StringBuilder();
        var localParam = $"{step.StepId}_localParameter";
        var temp = $"{localParam}#Temp";

        activities.Append($"<Assign x:TypeArguments=\"mxs:Entity\" To=\"[CreatedEntities(&quot;{temp}&quot;)]\" Value=\"[New Entity(&quot;{entity}&quot;)]\" />");

        foreach (var assignment in step.Attributes ?? [])
        {
            var v = EmitValue(assignment.Value, ctx, activities, state, convertForCodeActivity: false);
            activities.Append(SetEntityProperty(assignment.Attribute, $"[CreatedEntities(&quot;{temp}&quot;)]", entity, v,
                XamlTypeFor(assignment.Value.DataType)));
        }

        activities.Append($"<mxswa:CreateEntity EntityId=\"{{x:Null}}\" DisplayName=\"{step.StepId}\" Entity=\"[CreatedEntities(&quot;{temp}&quot;)]\" EntityName=\"{entity}\" />");
        activities.Append($"<Assign x:TypeArguments=\"mxs:Entity\" To=\"[CreatedEntities(&quot;{localParam}&quot;)]\" Value=\"[CreatedEntities(&quot;{temp}&quot;)]\" />");
        activities.Append(state.Persist);

        sb.Append($"<Sequence DisplayName=\"{DisplayName(step)}\">{ctx.RenderSequenceVariables()}{activities}</Sequence>");
    }

    /// <summary>
    /// The platform's own "send e-mail" step: an <c>email</c> record is assembled like any create
    /// step, but closed with <c>SendEmail</c> instead of <c>CreateEntity</c> — so it is sent, not just
    /// stored.
    /// </summary>
    /// <remarks>
    /// The recipient fields (<c>from</c>, <c>to</c>, <c>cc</c>, <c>bcc</c>) are party lists, so their
    /// values carry <c>dataType: "PartyList"</c> and land in the XAML as <c>mxs:EntityCollection</c>.
    /// Unlike a create step the record is not written back into <c>CreatedEntities</c> under its own
    /// name; the temporary instance is all there is.
    /// </remarks>
    private static void EmitSendEmail(WorkflowStep step, BuildState state, StringBuilder sb)
    {
        var ctx = new StepScope(step.StepId!);
        var activities = new StringBuilder();
        var temp = $"{step.StepId}_localParameter#Temp";

        activities.Append($"<Assign x:TypeArguments=\"mxs:Entity\" To=\"[CreatedEntities(&quot;{temp}&quot;)]\" Value=\"[New Entity(&quot;email&quot;)]\" />");

        foreach (var assignment in step.Attributes ?? [])
        {
            var v = EmitValue(assignment.Value, ctx, activities, state, convertForCodeActivity: false);
            activities.Append(SetEntityProperty(assignment.Attribute, $"[CreatedEntities(&quot;{temp}&quot;)]", "email", v,
                XamlTypeFor(assignment.Value.DataType)));
        }

        activities.Append($"<mxswa:SendEmail EntityId=\"{{x:Null}}\" DisplayName=\"{step.StepId}\" Entity=\"[CreatedEntities(&quot;{temp}&quot;)]\" />");
        activities.Append(state.Persist);

        sb.Append($"<Sequence DisplayName=\"{DisplayName(step)}\">"
            + ctx.RenderSequenceVariables(StepLabel(step))
            + $"{activities}</Sequence>");
    }

    private static void EmitAssign(WorkflowStep step, BuildState state, StringBuilder sb)
    {
        var entity = step.Entity ?? state.PrimaryEntity;
        var ownerEntity = string.Equals(step.OwnerType, "team", StringComparison.OrdinalIgnoreCase) ? "team" : "systemuser";
        var owner = string.IsNullOrWhiteSpace(step.OwnerId)
            ? "{x:Null}"
            : $"[New EntityReference(&quot;{ownerEntity}&quot;, New Guid(&quot;{step.OwnerId}&quot;))]";

        sb.Append($"<Sequence DisplayName=\"{DisplayName(step)}\">"
            + $"<mxswa:AssignEntity Owner=\"{owner}\" DisplayName=\"{step.StepId}\" "
            + "Entity=\"[InputEntities(&quot;primaryEntity&quot;)]\" EntityId=\"[InputEntities(&quot;primaryEntity&quot;).Id]\" "
            + $"EntityName=\"{entity}\" />"
            + state.Persist + "</Sequence>");
    }

    private static void EmitChangeStatus(WorkflowStep step, BuildState state, StringBuilder sb)
    {
        var entity = step.Entity ?? state.PrimaryEntity;
        sb.Append($"<mxswa:SetState DisplayName=\"{DisplayName(step)}\" "
            + "Entity=\"[InputEntities(&quot;primaryEntity&quot;)]\" EntityId=\"[InputEntities(&quot;primaryEntity&quot;).Id]\" "
            + $"EntityName=\"{entity}\">"
            + OptionSetArgument("State", step.State ?? 0)
            + OptionSetArgument("Status", step.Status ?? 1)
            + "</mxswa:SetState>" + state.Persist);
    }

    private static string OptionSetArgument(string property, int value) =>
        $"<mxswa:SetState.{property}><InArgument x:TypeArguments=\"mxs:OptionSetValue\">"
        + "<mxswa:ReferenceLiteral x:TypeArguments=\"mxs:OptionSetValue\">"
        + $"<mxs:OptionSetValue ExtensionData=\"{{x:Null}}\" Value=\"{value}\" />"
        + $"</mxswa:ReferenceLiteral></InArgument></mxswa:SetState.{property}>";

    private static void EmitStopWorkflow(WorkflowStep step, BuildState state, StringBuilder sb)
    {
        var ctx = new StepScope(step.StepId!);
        var activities = new StringBuilder();
        var reason = step.Reason ?? new WorkflowValue { Kind = WorkflowValueKind.Literal, Literal = string.Empty };
        var reasonVar = EmitValue(reason, ctx, activities, state, convertForCodeActivity: false);

        var status = string.Equals(step.Outcome, "cancelled", StringComparison.OrdinalIgnoreCase)
            ? "Canceled" : "Succeeded";

        activities.Append(
            $"<TerminateWorkflow DisplayName=\"{DisplayName(step)}\" "
            + $"Exception=\"[New Microsoft.Xrm.Sdk.InvalidPluginExecutionException(Microsoft.Xrm.Sdk.OperationStatus.{status})]\" "
            + $"Reason=\"[DirectCast({reasonVar}, System.String)]\" />");

        // The step label must not be declared next to a composed reason. Where it is, the platform
        // builds the cancellation message out of the label variables instead of the reason: the user
        // sees "<step description><the field><the label's guid>" and the actual sentence is gone. The
        // designer omits the label on exactly those steps and keeps it on the ones whose reason is a
        // plain constant, where it renders fine.
        var composedReason = reason.Kind == WorkflowValueKind.Concat;

        sb.Append($"<Sequence DisplayName=\"{DisplayName(step)}\">"
            + ctx.RenderSequenceVariables(composedReason ? string.Empty : StepLabel(step))
            + $"{activities}</Sequence>");
    }

    /// <summary>
    /// The designer's step label: three variables carrying the localised caption of the step.
    /// </summary>
    /// <remarks>
    /// Nothing references them — they exist so the designer can show the step's own wording. The label
    /// id is derived from the step so a rebuild stays stable instead of inventing a new id each time.
    /// Language 1031 (German) matches the environment this is authored in.
    /// </remarks>
    private static string StepLabel(WorkflowStep step)
    {
        if (string.IsNullOrWhiteSpace(step.Description))
            return string.Empty;

        var id = DeterministicGuid($"{step.StepId}|{step.Description}");

        return $"<Variable x:TypeArguments=\"x:String\" Default=\"{id}\" Name=\"stepLabelLabelId\" />"
             + $"<Variable x:TypeArguments=\"x:String\" Default=\"{Xml(step.Description!)}\" Name=\"stepLabelDescription\" />"
             + "<Variable x:TypeArguments=\"x:Int32\" Default=\"1031\" Name=\"stepLabelLanguageCode\" />";
    }

    /// <summary>A stable id for a piece of text — the builder must stay deterministic.</summary>
    private static Guid DeterministicGuid(string source)
    {
        var hash = System.Security.Cryptography.MD5.HashData(Encoding.UTF8.GetBytes(source));
        return new Guid(hash);
    }

    private static void EmitCustomActivity(WorkflowStep step, BuildState state, StringBuilder sb)
    {
        var ctx = new StepScope(step.StepId!);
        var prep = new StringBuilder();
        var arguments = new StringBuilder();

        foreach (var (key, value) in step.Inputs ?? [])
        {
            // The parameter's own type wins over the caller's dataType: the argument must match the
            // activity's signature, and so must the conversion chain feeding it.
            var dataType = state.Activities.Parameter(step.AssemblyQualifiedName, key, "Input")?.DataType
                           ?? value.DataType;
            var effective = value.DataType == dataType ? value : value with { DataType = dataType };
            var typeArg = XamlTypeFor(dataType) ?? "x:String";

            var v = EmitValue(effective, ctx, prep, state, convertForCodeActivity: true);
            var clrType = ClrTypeFor(dataType);
            arguments.Append($"<InArgument x:TypeArguments=\"{typeArg}\" x:Key=\"{Xml(key)}\">[DirectCast({v}, {clrType})]</InArgument>");
        }

        foreach (var output in step.Outputs ?? [])
        {
            // An output has no dataType in the model, so its type can only come from the metadata.
            // x:String would be wrong for anything else and is rejected as an invalid property bag.
            var dataType = state.Activities.Parameter(step.AssemblyQualifiedName, output, "Output")?.DataType;
            var typeArg = XamlTypeFor(dataType) ?? "x:String";

            var variable = $"{step.StepId}{output}_localParameter";
            state.DeclareWorkflowVariable(variable, typeArg);
            // Make it referencable by parameter name, since callers cannot know the step id.
            state.OutputVariables[output] = variable;
            arguments.Append($"<OutArgument x:TypeArguments=\"{typeArg}\" x:Key=\"{Xml(output)}\">[{variable}]</OutArgument>");
        }

        var inner = $"<mxswa:ActivityReference AssemblyQualifiedName=\"{Xml(step.AssemblyQualifiedName!)}\" DisplayName=\"{DisplayName(step)}\">"
                  + (arguments.Length > 0 ? $"<mxswa:ActivityReference.Arguments>{arguments}</mxswa:ActivityReference.Arguments>" : string.Empty)
                  + "</mxswa:ActivityReference>";

        // Outputs whose record is read from later are loaded right here, as the designer does.
        var loads = new StringBuilder();
        foreach (var output in step.Outputs ?? [])
        {
            if (!state.OutputsToLoad.Contains(output))
                continue;

            var parameter = state.Activities.Parameter(step.AssemblyQualifiedName, output, "Output");
            var entity = parameter?.EntityNames is { Count: > 0 } targets ? targets[0] : null;

            loads.Append(LoadRecordOfOutput(step.StepId!, output, entity, state));
        }

        // The value-preparation activities live in this composite, so their variables must be
        // declared here — not in a Sequence.Variables block that does not exist at this level.
        //
        // The composite carries the same label as the activity inside it. It is the element the
        // designer shows as the step, so a bare step id here leaves the step without a description in
        // the UI even though the inner reference is labelled correctly.
        sb.Append($"<mxswa:ActivityReference AssemblyQualifiedName=\"{CrmActivity("Composite")}\" DisplayName=\"{DisplayName(step)}\">"
            + "<mxswa:ActivityReference.Properties>"
            + ctx.RenderVariableCollection()
            + $"<sco:Collection x:TypeArguments=\"Activity\" x:Key=\"Activities\">{prep}{inner}{loads}</sco:Collection>"
            + "</mxswa:ActivityReference.Properties></mxswa:ActivityReference>");
    }

    /// <summary>
    /// Loads the record a code activity's output points at, so its fields become readable.
    /// </summary>
    /// <remarks>
    /// The output is only a reference. The designer guards the load with an <c>If</c>: when the
    /// reference is nothing, an empty entity is put in place, otherwise <c>RetrieveEntity</c> fetches
    /// the record. Without the guard a missing reference would fail at run time.
    /// </remarks>
    private static string LoadRecordOfOutput(string stepId, string output, string? entity, BuildState state)
    {
        state.RegisterLoadedRecord(stepId, output);

        var variable = $"{stepId}{output}_localParameter";
        var key = $"{stepId}{output}_entity";
        var entityName = entity ?? string.Empty;

        return $"<If Condition=\"[Microsoft.VisualBasic.IsNothing({variable})]\">"
             + "<If.Then>"
             + $"<Assign x:TypeArguments=\"mxs:Entity\" To=\"[CreatedEntities(&quot;{Xml(key)}&quot;)]\" Value=\"[New Entity()]\" />"
             + "</If.Then>"
             + "<If.Else>"
             + $"<mxswa:RetrieveEntity Attributes=\"{{x:Null}}\" Entity=\"[CreatedEntities(&quot;{Xml(key)}&quot;)]\" "
             + $"EntityId=\"[DirectCast({variable}.Id, System.Guid)]\" EntityName=\"{Xml(entityName)}\" "
             + "ThrowIfNotExists=\"False\" />"
             + "</If.Else></If>";
    }

    private static void EmitChildWorkflow(WorkflowStep step, BuildState state, StringBuilder sb)
    {
        var entity = step.Entity ?? state.PrimaryEntity;
        var ctx = new StepScope(step.StepId!);
        var paramsVar = ctx.NextVariable("scg:Dictionary(x:String, x:Object)",
            defaultExpression: "[New Dictionary(Of System.String, System.Object)]");
        var childId = string.IsNullOrWhiteSpace(step.ChildWorkflowId)
            ? Guid.Empty.ToString()
            : Guid.Parse(step.ChildWorkflowId).ToString();

        sb.Append($"<Sequence DisplayName=\"{DisplayName(step)}\">{ctx.RenderSequenceVariables()}"
            + $"<mxswa:StartChildWorkflow DisplayName=\"{step.StepId}\" "
            + "EntityId=\"[InputEntities(&quot;primaryEntity&quot;).Id]\" "
            + $"EntityName=\"{entity}\" InputParameters=\"[{paramsVar}]\" WorkflowId=\"{childId}\" />"
            + "</Sequence>" + state.Persist);
    }

    // ---------------------------------------------------------------- values

    /// <summary>
    /// Emits the activities that materialise <paramref name="value"/> and returns the name of the
    /// variable holding it.
    /// </summary>
    /// <summary>
    /// Emits a value and, if it carries one, the duration added to it.
    /// </summary>
    /// <remarks>
    /// The offset is a second <c>Add</c> on top of whatever produced the date — that is how the designer
    /// builds "Frist berechnen": <c>RetrieveCurrentTime</c>, then <c>Add</c> of the base and an
    /// <c>XrmTimeSpan</c>, this time with the date as the target type (a plain concatenation declares
    /// none).
    /// </remarks>
    private static string EmitValue(
        WorkflowValue value, StepScope ctx, StringBuilder sb, BuildState state,
        bool convertForCodeActivity, bool simplifyFieldRead = false)
    {
        if (value.Offset is not { IsZero: false } offset)
            return EmitPlainValue(value, ctx, sb, state, convertForCodeActivity, simplifyFieldRead);

        // The base must not be converted yet: the conversion belongs after the addition.
        var typeArgumentOfOffset = XamlTypeFor(value.DataType) ?? "s:DateTime";
        var baseVariable = EmitPlainValue(
            value with { Offset = null }, ctx, sb, state, convertForCodeActivity: false, simplifyFieldRead);

        state.UsesTimeSpan = true;
        var span = ctx.NextElementVariable("mxsw:XrmTimeSpan", XrmTimeSpanLiteral(offset));
        var shifted = ctx.NextVariable("x:Object");

        sb.Append(EvaluateExpression("Add",
            $"[New Object() {{ {baseVariable}, {span} }}]", typeArgumentOfOffset, shifted));

        return convertForCodeActivity ? Convert(shifted, typeArgumentOfOffset, ctx, sb) : shifted;
    }

    /// <summary>The <c>Variable.Default</c> of a duration, in the designer's element form.</summary>
    private static string XrmTimeSpanLiteral(WorkflowTimeOffset offset) =>
        "<Literal x:TypeArguments=\"mxsw:XrmTimeSpan\">"
        + $"<mxsw:XrmTimeSpan Days=\"{offset.Days}\" Hours=\"{offset.Hours}\" "
        + $"Minutes=\"{offset.Minutes}\" Months=\"{offset.Months}\" Years=\"{offset.Years}\" />"
        + "</Literal>";

    private static string EmitPlainValue(
        WorkflowValue value, StepScope ctx, StringBuilder sb, BuildState state,
        bool convertForCodeActivity, bool simplifyFieldRead = false)
    {
        var typeArgument = XamlTypeFor(value.DataType) ?? "x:String";

        // Comparison operands: a lone field reference is read directly, matching the designer.
        if (simplifyFieldRead
            && value.Kind == WorkflowValueKind.Field
            && value.Fallback is null
            && value.Fields is { Count: 1 })
        {
            var (fieldEntity, fieldAttribute) =
                SplitFieldReference(value.Fields[0], state.PrimaryEntity);
            var operand = ctx.NextVariable("x:Object");
            sb.Append(GetEntityProperty(fieldAttribute,
                EntityExpression(fieldEntity, state, value.Via, value.FromStep, value.FromStepOutput),
                fieldEntity, operand, targetType: null));
            return operand;
        }

        switch (value.Kind)
        {
            case WorkflowValueKind.Literal:
            {
                // No value at all means "clear this attribute". The designer expresses that by pointing
                // at a variable it never assigns — at run time that is Nothing. Building a CreateCrmType
                // around an empty string instead is what Dataverse rejects with 0x80040216, and only for
                // some types, which is why an empty text slips through but an empty date does not.
                if (value.Literal is null && value.Literals is null && value.Parts is null)
                    return ctx.NextVariable("x:Object");

                // A fixed recipient is a record reference wrapped into a party list.
                if (string.Equals(CrmPropertyType(value.DataType), "PartyList", StringComparison.Ordinal))
                {
                    var party = ctx.NextVariable("x:Object");
                    var recipient = CreateEntityReferenceLiteral(value.Literal ?? string.Empty, ctx, sb);
                    sb.Append(EvaluateExpression("CreateCrmType",
                        $"[New Object() {{ Microsoft.Xrm.Sdk.Workflow.WorkflowPropertyType.PartyList, {recipient} }}]",
                        "mxs:EntityCollection", party));
                    return convertForCodeActivity ? Convert(party, typeArgument, ctx, sb) : party;
                }

                // A fixed record reference needs the two-stage form, not a plain CreateCrmType.
                if (string.Equals(CrmPropertyType(value.DataType), "EntityReference", StringComparison.Ordinal))
                {
                    var reference = CreateEntityReferenceLiteral(value.Literal ?? string.Empty, ctx, sb);
                    return convertForCodeActivity ? Convert(reference, typeArgument, ctx, sb) : reference;
                }

                var target = ctx.NextVariable("x:Object");
                sb.Append(CreateCrmType(value.Literal ?? string.Empty, value.DataType, typeArgument, target));
                return convertForCodeActivity ? Convert(target, typeArgument, ctx, sb) : target;
            }

            case WorkflowValueKind.Field:
            {
                // The result slot is allocated first — this mirrors the designer, where _1 holds the
                // result and _2.._n the sources.
                var result = ctx.NextVariable("x:Object");
                var sources = new List<string>();

                foreach (var reference in value.Fields ?? [])
                {
                    var (entity, attribute) = SplitFieldReference(reference, state.PrimaryEntity);
                    var sourceVar = ctx.NextVariable("x:Object");
                    sb.Append(GetEntityProperty(attribute,
                        EntityExpression(entity, state, value.Via, value.FromStep, value.FromStepOutput),
                        entity, sourceVar, typeArgument));
                    sources.Add(sourceVar);
                }

                if (value.Fallback is not null)
                {
                    var fallbackVar = ctx.NextVariable("x:Object");
                    sb.Append(CreateCrmType(value.Fallback, value.DataType, typeArgument, fallbackVar));
                    sources.Add(fallbackVar);
                }

                sb.Append(EvaluateExpression("SelectFirstNonNull",
                    $"[New Object() {{ {string.Join(", ", sources)} }}]", typeArgument, result));

                return convertForCodeActivity ? Convert(result, typeArgument, ctx, sb) : result;
            }

            case WorkflowValueKind.StepOutput:
            {
                var variable = StepOutputVariable(value.StepOutput!, state);
                var result = ctx.NextVariable("x:Object");
                sb.Append(EvaluateExpression("SelectFirstNonNull",
                    $"[New Object() {{ {variable} }}]", typeArgument, result));
                return convertForCodeActivity ? Convert(result, typeArgument, ctx, sb) : result;
            }

            case WorkflowValueKind.Now:
            {
                // In a comparison the designer feeds RetrieveCurrentTime straight into the operand;
                // as a value it goes through SelectFirstNonNull like any other source.
                if (simplifyFieldRead)
                {
                    var operandVar = ctx.NextVariable("x:Object");
                    sb.Append(RetrieveCurrentTime(operandVar));
                    return operandVar;
                }

                var result = ctx.NextVariable("x:Object");
                var now = ctx.NextVariable("x:Object");
                sb.Append(RetrieveCurrentTime(now));
                sb.Append(EvaluateExpression("SelectFirstNonNull",
                    $"[New Object() {{ {now} }}]", typeArgument, result));
                return convertForCodeActivity ? Convert(result, typeArgument, ctx, sb) : result;
            }

            case WorkflowValueKind.Concat:
            {
                // The result slot first, then one variable per part, joined with Add.
                var result = ctx.NextVariable("x:Object");
                var parts = new List<string>();

                foreach (var part in value.Parts ?? [])
                    parts.Add(EmitValue(part, ctx, sb, state, convertForCodeActivity: false));

                // Add declares no target type — the parts determine it.
                sb.Append(EvaluateExpression("Add",
                    $"[New Object() {{ {string.Join(", ", parts)} }}]", typeArgument: null, result));

                return convertForCodeActivity ? Convert(result, typeArgument, ctx, sb) : result;
            }

            default:
                throw new NotSupportedException(
                    $"Value kind '{value.Kind}' is unknown. Allowed: {string.Join(", ", WorkflowValueKind.All)}");
        }
    }

    /// <summary>
    /// Resolves a step-output reference to the variable holding it.
    /// </summary>
    /// <remarks>
    /// Callers cannot know the generated step id, so the reference is matched on the parameter name
    /// (the part after the dot, or the whole string). The most recently declared output of that name
    /// wins. Falls back to literal concatenation only if nothing was registered, which the
    /// generated-XAML self-check would then flag.
    /// </remarks>
    private static string StepOutputVariable(string reference, BuildState state)
    {
        var parameter = reference.Contains('.') ? reference.Split('.', 2)[1] : reference;

        if (state.OutputVariables.TryGetValue(parameter, out var variable))
            return variable;

        var parts = reference.Split('.', 2);
        return parts.Length == 2 ? $"{parts[0]}{parts[1]}_localParameter" : $"{reference}_localParameter";
    }

    /// <remarks>
    /// The converted value is named after its source (<c>…_1</c> → <c>…_1_converted</c>) rather than
    /// taking the next index. The designer does it that way, and it matters: activation rebuilds the
    /// step from these names, and a <c>_3_converted</c> without a declared <c>_3</c> is rejected as
    /// <c>InvalidPropertyBag</c>.
    /// </remarks>
    private static string Convert(string source, string typeArgument, StepScope ctx, StringBuilder sb)
    {
        var converted = ctx.DeriveVariable(source, "_converted", "x:Object");
        sb.Append($"<mxswa:ActivityReference AssemblyQualifiedName=\"{CrmActivity("ConvertCrmXrmTypes")}\" DisplayName=\"ConvertCrmXrmTypes\">"
            + "<mxswa:ActivityReference.Arguments>"
            + $"<InArgument x:TypeArguments=\"x:Object\" x:Key=\"Value\">[{source}]</InArgument>"
            + $"<InArgument x:TypeArguments=\"s:Type\" x:Key=\"TargetType\"><mxswa:ReferenceLiteral x:TypeArguments=\"s:Type\" Value=\"{typeArgument}\" /></InArgument>"
            + $"<OutArgument x:TypeArguments=\"x:Object\" x:Key=\"Result\">[{converted}]</OutArgument>"
            + "</mxswa:ActivityReference.Arguments></mxswa:ActivityReference>");
        return converted;
    }

    private static string CreateCrmType(string literal, string? dataType, string typeArgument, string target)
    {
        var crmType = CrmPropertyType(dataType);
        var parameters = $"[New Object() {{ Microsoft.Xrm.Sdk.Workflow.WorkflowPropertyType.{crmType}, &quot;{Xml(MaskCommas(literal))}&quot;, &quot;{CrmTypeMarker(dataType)}&quot; }}]";
        return EvaluateExpression("CreateCrmType", parameters, typeArgument, target);
    }

    /// <summary>
    /// Escapes a constant so it survives inside the VB expression of a parameter array.
    /// </summary>
    /// <remarks>
    /// Two characters need care, and both are common in an HTML e-mail body:
    /// <list type="bullet">
    /// <item>A <b>comma</b> becomes <c>&amp;#44;</c> — the array is split on commas before the string
    /// literals are honoured, so a plain comma would break the argument list apart.</item>
    /// <item>A <b>double quote</b> is doubled, the VB way of escaping it inside a string literal.
    /// Left alone it would end the literal early and the document would be rejected.</item>
    /// </list>
    /// The parser reverses both, so a value survives a round trip unchanged.
    /// </remarks>
    internal static string MaskCommas(string literal) =>
        literal.Replace("\"", "\"\"").Replace(",", "&#44;");

    /// <summary>
    /// The third parameter of a CreateCrmType call — the CRM attribute type, which is not always the
    /// name of the WorkflowPropertyType.
    /// </summary>
    /// <remarks>
    /// An option set is marked <c>Picklist</c>, an id <c>UniqueIdentifier</c> and a record reference
    /// <c>Lookup</c>. Getting this wrong makes Dataverse refuse the XAML outright with
    /// <c>0x80045040</c> — it is not a cosmetic difference. Everything else shares the name of its
    /// <see cref="CrmPropertyType">property type</see>; the designer fixtures witness
    /// <c>Picklist</c>, <c>UniqueIdentifier</c> and <c>String</c>.
    /// </remarks>
    internal static string CrmTypeMarker(string? dataType) => (dataType ?? "String") switch
    {
        "OptionSetValue" or "OptionSet" or "Picklist" => "Picklist",
        "Guid" or "UniqueIdentifier" => "UniqueIdentifier",
        "EntityReference" or "Lookup" => "Lookup",
        _ => CrmPropertyType(dataType)
    };

    /// <summary>
    /// Emits a fixed record reference, written as "entity:guid" (optionally "entity:guid:label").
    /// </summary>
    /// <remarks>
    /// Two stages, as the designer does it: first the id as <c>WorkflowPropertyType.Guid</c> with the
    /// marker "UniqueIdentifier", then the reference itself as
    /// <c>WorkflowPropertyType.EntityReference</c> taking entity name, display label, the id variable
    /// and the marker "Lookup". The designer leaves the label empty even for a named record, so an
    /// omitted one is written as "" rather than filled in with the entity name.
    /// </remarks>
    private static string CreateEntityReferenceLiteral(string literal, StepScope ctx, StringBuilder sb)
    {
        var parts = literal.Split(':');
        if (parts.Length < 2 || !Guid.TryParse(parts[1], out var id))
            throw new NotSupportedException(
                $"'{literal}' is not a valid record reference. Expected \"entity:guid\", " +
                "e.g. \"team:a0000001-0000-4000-8000-000000000001\".");

        var entity = parts[0];
        var label = parts.Length > 2 ? parts[2] : string.Empty;

        // The result slot comes first, the id second — the designer's numbering, which the
        // convention-based reconstruction on activation depends on.
        var refVar = ctx.NextVariable("x:Object");
        var idVar = ctx.NextVariable("x:Object");

        sb.Append(EvaluateExpression("CreateCrmType",
            $"[New Object() {{ Microsoft.Xrm.Sdk.Workflow.WorkflowPropertyType.Guid, &quot;{id}&quot;, &quot;UniqueIdentifier&quot; }}]",
            "mxs:EntityReference", idVar));

        sb.Append(EvaluateExpression("CreateCrmType",
            $"[New Object() {{ Microsoft.Xrm.Sdk.Workflow.WorkflowPropertyType.EntityReference, "
            + $"&quot;{Xml(entity)}&quot;, &quot;{Xml(label)}&quot;, {idVar}, &quot;Lookup&quot; }}]",
            "mxs:EntityReference", refVar));

        return refVar;
    }

    /// <param name="typeArgument">
    /// The target type, or null to write an explicit null — which is what the designer does for
    /// <c>Add</c>, where the result type follows from the parts.
    /// </param>
    /// <summary>
    /// "Now". Takes no parameters and declares no target type — both exactly as the designer writes
    /// them; a declared type here is rejected with <c>0x80045040</c>. The empty array keeps its
    /// whitespace, hence xml:space.
    /// </summary>
    private static string RetrieveCurrentTime(string target) =>
        $"<mxswa:ActivityReference AssemblyQualifiedName=\"{CrmActivity("EvaluateExpression")}\" DisplayName=\"EvaluateExpression\">"
        + "<mxswa:ActivityReference.Arguments>"
        + "<InArgument x:TypeArguments=\"x:String\" x:Key=\"ExpressionOperator\">RetrieveCurrentTime</InArgument>"
        + "<InArgument x:TypeArguments=\"s:Object[]\" x:Key=\"Parameters\" xml:space=\"preserve\">[New Object() {  }]</InArgument>"
        + "<InArgument x:TypeArguments=\"s:Type\" x:Key=\"TargetType\"><mxswa:ReferenceLiteral x:TypeArguments=\"s:Type\"><x:Null /></mxswa:ReferenceLiteral></InArgument>"
        + $"<OutArgument x:TypeArguments=\"x:Object\" x:Key=\"Result\">[{target}]</OutArgument>"
        + "</mxswa:ActivityReference.Arguments></mxswa:ActivityReference>";

    private static string EvaluateExpression(string op, string parameters, string? typeArgument, string target)
    {
        var targetType = typeArgument is null
            ? "<mxswa:ReferenceLiteral x:TypeArguments=\"s:Type\"><x:Null /></mxswa:ReferenceLiteral>"
            : $"<mxswa:ReferenceLiteral x:TypeArguments=\"s:Type\" Value=\"{typeArgument}\" />";

        return $"<mxswa:ActivityReference AssemblyQualifiedName=\"{CrmActivity("EvaluateExpression")}\" DisplayName=\"EvaluateExpression\">"
            + "<mxswa:ActivityReference.Arguments>"
            + $"<InArgument x:TypeArguments=\"x:String\" x:Key=\"ExpressionOperator\">{op}</InArgument>"
            + $"<InArgument x:TypeArguments=\"s:Object[]\" x:Key=\"Parameters\">{parameters}</InArgument>"
            + $"<InArgument x:TypeArguments=\"s:Type\" x:Key=\"TargetType\">{targetType}</InArgument>"
            + $"<OutArgument x:TypeArguments=\"x:Object\" x:Key=\"Result\">[{target}]</OutArgument>"
            + "</mxswa:ActivityReference.Arguments></mxswa:ActivityReference>";
    }

    private static string GetEntityProperty(
        string attribute, string entityExpression, string entityName, string target, string? targetType)
    {
        // In conditions the designer emits a null TargetType; when preparing values it names the type.
        var targetTypeElement = targetType is null
            ? "<mxswa:ReferenceLiteral x:TypeArguments=\"s:Type\"><x:Null /></mxswa:ReferenceLiteral>"
            : $"<mxswa:ReferenceLiteral x:TypeArguments=\"s:Type\" Value=\"{targetType}\" />";

        return $"<mxswa:GetEntityProperty Attribute=\"{Xml(attribute)}\" Entity=\"{entityExpression}\" EntityName=\"{Xml(entityName)}\" Value=\"[{target}]\">"
             + "<mxswa:GetEntityProperty.TargetType>"
             + $"<InArgument x:TypeArguments=\"s:Type\">{targetTypeElement}</InArgument>"
             + "</mxswa:GetEntityProperty.TargetType></mxswa:GetEntityProperty>";
    }

    private static string SetEntityProperty(
        string attribute, string entityExpression, string entityName, string valueVariable, string? targetType)
    {
        var type = targetType ?? "x:String";
        return $"<mxswa:SetEntityProperty Attribute=\"{Xml(attribute)}\" Entity=\"{entityExpression}\" EntityName=\"{Xml(entityName)}\" Value=\"[{valueVariable}]\">"
             + "<mxswa:SetEntityProperty.TargetType>"
             + $"<InArgument x:TypeArguments=\"s:Type\"><mxswa:ReferenceLiteral x:TypeArguments=\"s:Type\" Value=\"{type}\" /></InArgument>"
             + "</mxswa:SetEntityProperty.TargetType></mxswa:SetEntityProperty>";
    }

    /// <summary>
    /// The entity expression to read from. Fields of the primary record come from
    /// <c>primaryEntity</c>; fields of a related record use the platform-provided key
    /// <c>related_&lt;lookupAttribute&gt;#&lt;entity&gt;</c> (one level only).
    /// </summary>
    private static string EntityExpression(
        string? entity, BuildState state, string? via = null,
        string? fromStep = null, string? fromStepOutput = null)
    {
        // A record this workflow created or loaded lives in CreatedEntities under a key derived from
        // the step that produced it.
        if (!string.IsNullOrWhiteSpace(fromStep))
            return $"[CreatedEntities(&quot;{Xml(state.CreatedRecordKey(fromStep!, entity))}&quot;)]";

        if (!string.IsNullOrWhiteSpace(fromStepOutput))
            return $"[CreatedEntities(&quot;{Xml(state.LoadedRecordKey(fromStepOutput!))}&quot;)]";

        var isPrimary = entity is null
            || string.Equals(entity, state.PrimaryEntity, StringComparison.OrdinalIgnoreCase);

        if (isPrimary || string.IsNullOrWhiteSpace(via))
            return "[InputEntities(&quot;primaryEntity&quot;)]";

        return $"[InputEntities(&quot;related_{Xml(via)}#{Xml(entity!)}&quot;)]";
    }

    internal static (string Entity, string Attribute) SplitFieldReference(string reference, string primaryEntity)
    {
        var parts = reference.Split('.', 2);
        return parts.Length == 2 ? (parts[0], parts[1]) : (primaryEntity, reference);
    }

    // ---------------------------------------------------------------- type mapping

    internal static string CrmPropertyType(string? dataType) => (dataType ?? "String") switch
    {
        "String" => "String",
        // "Int" is what the type is colloquially called, but the enum member is "Integer". A wrong name
        // makes the VB expression fail to compile and Dataverse rejects the document with 0x80045040 —
        // with the generic "created outside the web application" message, which points nowhere.
        "Integer" or "Int32" => "Integer",
        "Boolean" or "Bool" => "Boolean",
        "DateTime" => "DateTime",
        "Decimal" => "Decimal",
        "Double" or "Float" => "Double",
        "Money" => "Money",
        "OptionSetValue" or "OptionSet" or "Picklist" => "OptionSetValue",
        "EntityReference" or "Lookup" => "EntityReference",
        "Guid" or "UniqueIdentifier" => "Guid",
        "PartyList" => "PartyList",
        _ => "String"
    };

    /// <remarks>
    /// <c>DateTime</c> maps to <c>s:DateTime</c>, not <c>x:DateTime</c>: the XAML 2006 namespace has no
    /// DateTime primitive, so the type has to come from <c>System</c> — as the designer writes it.
    /// A <c>x:DateTime</c> makes Dataverse reject the whole document with <c>0x80045040</c>.
    /// </remarks>
    internal static string? XamlTypeFor(string? dataType) => (dataType ?? "String") switch
    {
        "String" => "x:String",
        "Integer" or "Int32" => "x:Int32",
        "Boolean" or "Bool" => "x:Boolean",
        "DateTime" => "s:DateTime",
        "Decimal" => "x:Decimal",
        "Double" or "Float" => "x:Double",
        "Money" => "mxs:Money",
        "OptionSetValue" or "OptionSet" or "Picklist" => "mxs:OptionSetValue",
        "EntityReference" or "Lookup" => "mxs:EntityReference",
        "Guid" or "UniqueIdentifier" => "s:Guid",
        // Recipient fields of an e-mail are collections, not single references.
        "PartyList" => "mxs:EntityCollection",
        _ => "x:String"
    };

    internal static string ClrTypeFor(string? dataType) => (dataType ?? "String") switch
    {
        "String" => "System.String",
        "Integer" or "Int32" => "System.Int32",
        "Boolean" or "Bool" => "System.Boolean",
        "DateTime" => "System.DateTime",
        "Decimal" => "System.Decimal",
        "Double" or "Float" => "System.Double",
        "Money" => "Microsoft.Xrm.Sdk.Money",
        "OptionSetValue" or "OptionSet" or "Picklist" => "Microsoft.Xrm.Sdk.OptionSetValue",
        "EntityReference" or "Lookup" => "Microsoft.Xrm.Sdk.EntityReference",
        "Guid" or "UniqueIdentifier" => "System.Guid",
        "PartyList" => "Microsoft.Xrm.Sdk.EntityCollection",
        _ => "System.String"
    };

    /// <summary>
    /// The <c>Default</c> of a declared variable, or null when none should be written.
    /// </summary>
    /// <remarks>
    /// <c>[Nothing]</c> is only valid for reference types. On a value type — the usual case for a
    /// code activity's output, e.g. <c>x:Boolean</c> — it is a type error, which the designer avoids
    /// by writing <c>Default="False"</c>. Types without a safe literal get no Default at all and
    /// start out as <c>default(T)</c>.
    /// </remarks>
    internal static string? DefaultFor(string typeArgument) => typeArgument switch
    {
        "x:Boolean" => "False",
        "mxs:EntityReference" => "[New EntityReference()]",
        "x:Int32" or "x:Int64" or "x:Decimal" or "x:Double" => "0",
        "s:DateTime" or "s:Guid" => null,
        _ => "[Nothing]"
    };

    internal static string MapOperator(string op) => op switch
    {
        "Equal" or "eq" => "Equal",
        "NotEqual" or "ne" => "NotEqual",
        "Contains" or "contains" => "Contains",
        "DoesNotContain" or "doesnotcontain" => "DoesNotContain",
        "BeginsWith" or "beginswith" => "BeginsWith",
        "DoesNotBeginWith" or "doesnotbeginwith" => "DoesNotBeginWith",
        "EndsWith" or "endswith" => "EndsWith",
        "DoesNotEndWith" or "doesnotendwith" => "DoesNotEndWith",
        "NotNull" or "not-null" => "NotNull",
        "Null" or "null" => "Null",
        "GreaterThan" or "gt" => "GreaterThan",
        "GreaterEqual" or "ge" => "GreaterEqual",
        "LessThan" or "lt" => "LessThan",
        "LessEqual" or "le" => "LessEqual",
        "In" or "in" => "In",
        "NotIn" or "notin" => "NotIn",
        _ => op
    };

    private static string Xml(string value) =>
        value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    // ---------------------------------------------------------------- state

    private sealed class BuildState(string primaryEntity, WorkflowActivityCatalog activities)
    {
        private int _counter;
        private readonly Dictionary<WorkflowStep, string> _branchIds = [];
        private readonly Dictionary<WorkflowStep, string> _elseBranchIds = [];
        private readonly List<string> _assigned = [];

        public string PrimaryEntity { get; } = primaryEntity;
        public WorkflowActivityCatalog Activities { get; } = activities;

        /// <summary>A real-time workflow gets no Persist — the platform rejects persistence points there.</summary>
        public bool Realtime { get; init; }

        public string Persist => Realtime ? string.Empty : "<Persist />";
        public bool UsesCrmActivities { get; set; }
        public bool UsesQueryTypes { get; set; }

        /// <summary>Set when a duration is emitted, so the <c>mxsw</c> prefix gets declared.</summary>
        public bool UsesTimeSpan { get; set; }
        public List<(string Name, string TypeArgument, string? Default)> WorkflowVariables { get; } = [];

        /// <summary>Output parameter name -> variable holding it, filled while emitting.</summary>
        public Dictionary<string, string> OutputVariables { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Step id and created entity of every createRecord step, for FromStep references.</summary>
        private readonly Dictionary<string, string> _createdRecords = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Records that have to be loaded from a code activity's output, by "stepId.param".</summary>
        private readonly Dictionary<string, string> _loadedRecords = new(StringComparer.OrdinalIgnoreCase);

        public void RegisterCreatedRecord(WorkflowStep step)
        {
            // Addressable by step id and, because callers cannot know that id, by entity name.
            _createdRecords[step.StepId!] = step.StepId!;
            if (!string.IsNullOrWhiteSpace(step.Entity))
                _createdRecords.TryAdd(step.Entity!, step.StepId!);
        }

        /// <summary>The CreatedEntities key of a record created earlier in this workflow.</summary>
        /// <remarks>
        /// A definition read from an existing workflow carries the step id it had there, and rebuilding
        /// renumbers the steps — so that id usually no longer exists. The entity being read is the
        /// reliable fallback: it identifies the create step regardless of numbering. Without it the key
        /// would point at a record that was never created, which activation rejects with
        /// <c>0x80040216</c>.
        /// </remarks>
        public string CreatedRecordKey(string reference, string? entity = null)
        {
            if (_createdRecords.TryGetValue(reference, out var stepId))
                return $"{stepId}_localParameter";

            if (!string.IsNullOrWhiteSpace(entity) && _createdRecords.TryGetValue(entity!, out var byEntity))
                return $"{byEntity}_localParameter";

            return $"{reference}_localParameter";
        }

        /// <summary>True when a 'fromStep' reference cannot be resolved to a create step.</summary>
        public bool CanResolveCreatedRecord(string reference, string? entity) =>
            _createdRecords.ContainsKey(reference)
            || (!string.IsNullOrWhiteSpace(entity) && _createdRecords.ContainsKey(entity!));

        /// <summary>
        /// The CreatedEntities key of a record loaded for an activity output, and a note that it has
        /// to be loaded. Accepts "stepId.param" as well as the bare parameter name.
        /// </summary>
        public string LoadedRecordKey(string reference)
        {
            var parameter = reference.Contains('.') ? reference.Split('.', 2)[1] : reference;

            if (_loadedRecords.TryGetValue(parameter, out var key))
                return key;

            // Not emitted yet: derive from the reference so the name is stable either way.
            var stepId = reference.Contains('.') ? reference.Split('.', 2)[0] : string.Empty;
            return $"{stepId}{parameter}_entity";
        }

        /// <summary>Registers the record a code activity output is loaded into.</summary>
        public void RegisterLoadedRecord(string stepId, string parameter) =>
            _loadedRecords[parameter] = $"{stepId}{parameter}_entity";

        /// <summary>Outputs that some value reads fields from, so they need a RetrieveEntity.</summary>
        public HashSet<string> OutputsToLoad { get; } = new(StringComparer.OrdinalIgnoreCase);
        public IReadOnlyList<string> AssignedStepIds => _assigned;

        public void AssignId(WorkflowStep step)
        {
            step.StepId = $"{WorkflowStepKind.PrefixFor(step.Kind)}{++_counter}";
            _assigned.Add(step.StepId);
        }

        private readonly Dictionary<WorkflowStep, List<WorkflowConditionBranch>> _shortForm = [];

        /// <summary>The single branch standing in for 'conditions' + 'then', created once per step.</summary>
        public List<WorkflowConditionBranch> ShortFormBranchOf(WorkflowStep step)
        {
            if (_shortForm.TryGetValue(step, out var cached))
                return cached;

            var branches = new List<WorkflowConditionBranch>
            {
                new()
                {
                    Conditions = step.Conditions ?? [],
                    LogicalOperator = step.LogicalOperator,
                    Steps = step.Then
                }
            };

            _shortForm[step] = branches;
            return branches;
        }

        public void AssignBranchId(WorkflowConditionBranch branch)
        {
            branch.BranchId = $"ConditionBranchStep{++_counter}";
            _assigned.Add(branch.BranchId);
        }

        public void AssignElseBranchId(WorkflowStep step)
        {
            var id = $"ConditionBranchStep{++_counter}";
            _elseBranchIds[step] = id;
            _assigned.Add(id);
        }

        public string? ElseBranchIdOf(WorkflowStep step) =>
            _elseBranchIds.TryGetValue(step, out var e) ? e : null;

        public void DeclareWorkflowVariable(string name, string typeArgument)
        {
            if (WorkflowVariables.All(v => v.Name != name))
                WorkflowVariables.Add((name, typeArgument, DefaultFor(typeArgument)));
        }
    }

    /// <summary>Per-step variable scope. Names follow "&lt;StepId&gt;_&lt;n&gt;".</summary>
    private sealed class StepScope(string stepId)
    {
        private int _index;
        private readonly List<string> _declarations = [];

        public string NextVariable(string typeArgument, bool defaultFalse = false, string? defaultExpression = null, string? suffix = null)
        {
            var name = $"{stepId}_{++_index}{suffix ?? string.Empty}";
            DeclareVariable(name, typeArgument, defaultFalse, defaultExpression);
            return name;
        }

        /// <summary>A variable derived from an existing one, without consuming an index.</summary>
        public string DeriveVariable(string source, string suffix, string typeArgument)
        {
            var name = $"{source}{suffix}";
            DeclareVariable(name, typeArgument);
            return name;
        }

        public void DeclareVariable(string name, string typeArgument, bool defaultFalse = false, string? defaultExpression = null)
        {
            var defaultAttr = defaultFalse ? " Default=\"False\""
                : defaultExpression is not null ? $" Default=\"{defaultExpression.Replace("\"", "&quot;")}\""
                : string.Empty;
            _declarations.Add($"<Variable x:TypeArguments=\"{typeArgument}\"{defaultAttr} Name=\"{name}\" />");
        }

        /// <summary>
        /// A variable whose default is markup rather than an expression — the only form a duration has.
        /// </summary>
        /// <remarks>
        /// <c>Default="…"</c> takes an attribute value; an <c>XrmTimeSpan</c> is an element with five
        /// attributes of its own and has to be nested in <c>Variable.Default</c>.
        /// </remarks>
        public string NextElementVariable(string typeArgument, string defaultElement)
        {
            var name = $"{stepId}_{++_index}";
            _declarations.Add($"<Variable x:TypeArguments=\"{typeArgument}\" Name=\"{name}\">"
                + $"<Variable.Default>{defaultElement}</Variable.Default></Variable>");
            return name;
        }

        /// <param name="extra">Further declarations to append, e.g. the designer's step label.</param>
        public string RenderSequenceVariables(string extra = "") =>
            _declarations.Count == 0 && extra.Length == 0
                ? string.Empty
                : $"<Sequence.Variables>{string.Concat(_declarations)}{extra}</Sequence.Variables>";

        public string RenderVariableCollection() =>
            _declarations.Count == 0
                ? "<sco:Collection x:TypeArguments=\"Variable\" x:Key=\"Variables\" />"
                : $"<sco:Collection x:TypeArguments=\"Variable\" x:Key=\"Variables\">{string.Concat(_declarations)}</sco:Collection>";

        /// <summary>Just the declarations, for a collection shared by several scopes.</summary>
        public string RenderDeclarations() => string.Concat(_declarations);
    }
}
