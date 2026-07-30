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

        var state = new BuildState(definition.PrimaryEntity, activities ?? WorkflowActivityCatalog.Empty);
        AssignStepIds(definition.Steps, state);

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
                // A condition owns a branch node; the "then" steps live inside that branch.
                state.AssignBranchId(step, isElse: false);
                AssignStepIds(step.Then, state);

                if (step.Else is { Count: > 0 })
                {
                    state.AssignBranchId(step, isElse: true);
                    AssignStepIds(step.Else, state);
                }
            }
            else if (step.Kind == WorkflowStepKind.Stage)
            {
                AssignStepIds(step.Children, state);
            }
        }
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

    private static void EmitCondition(WorkflowStep step, BuildState state, StringBuilder sb)
    {
        state.UsesQueryTypes = true;
        var ctx = new StepScope(step.StepId!);
        var branchId = state.BranchIdOf(step, isElse: false)!;
        var conditionVar = $"{branchId}_condition";
        ctx.DeclareVariable(conditionVar, "x:Boolean", defaultFalse: true);

        // Each comparison yields a boolean; several are folded with EvaluateLogicalCondition.
        var boolVars = new List<string>();
        var activities = new StringBuilder();

        foreach (var condition in step.Conditions ?? [])
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
                    EntityExpression(condition.Entity, state, condition.Via),
                    condition.Entity ?? state.PrimaryEntity,
                    left,
                    targetType: null));
            }

            string? rightVar = null;
            if (condition.Value is { } value)
                rightVar = EmitValue(value, ctx, activities, state, convertForCodeActivity: false,
                    // In a comparison the designer reads a single field straight into the operand
                    // variable, without the SelectFirstNonNull indirection used for field values.
                    simplifyFieldRead: true);

            var resultVar = (step.Conditions!.Count == 1) ? conditionVar : ctx.NextVariable("x:Boolean", defaultFalse: true);
            if (step.Conditions.Count > 1)
                boolVars.Add(resultVar);

            // Value-less operators (Null / NotNull) must emit an explicit null for Parameters.
            // An empty array initialiser is rejected by the platform with 0x80045040.
            var parameters = rightVar is null
                ? "<x:Null x:Key=\"Parameters\" />"
                : $"<InArgument x:TypeArguments=\"s:Object[]\" x:Key=\"Parameters\">[New Object() {{ {rightVar} }}]</InArgument>";

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
            var op = string.Equals(step.LogicalOperator, "Or", StringComparison.OrdinalIgnoreCase) ? "Or" : "And";
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

        // The branch itself, carrying then/else.
        activities.Append(Branch(branchId, $"[{conditionVar}]", step.Then, state));

        var hasElse = step.Else is { Count: > 0 };
        if (hasElse)
        {
            var elseBranchId = state.BranchIdOf(step, isElse: true)!;
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
            + ctx.RenderVariableCollection()
            + $"<sco:Collection x:TypeArguments=\"Activity\" x:Key=\"Activities\">{activities}</sco:Collection>"
            + containsElse
            + "</mxswa:ActivityReference.Properties></mxswa:ActivityReference>");
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
        inner.Append("<Persist />");

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
        activities.Append("<Persist />");

        sb.Append($"<Sequence DisplayName=\"{DisplayName(step)}\">{ctx.RenderSequenceVariables()}{activities}</Sequence>");
    }

    private static void EmitCreate(WorkflowStep step, BuildState state, StringBuilder sb)
    {
        var entity = step.Entity!;
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
        activities.Append("<Persist />");

        sb.Append($"<Sequence DisplayName=\"{DisplayName(step)}\">{ctx.RenderSequenceVariables()}{activities}</Sequence>");
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
            + "<Persist /></Sequence>");
    }

    private static void EmitChangeStatus(WorkflowStep step, BuildState state, StringBuilder sb)
    {
        var entity = step.Entity ?? state.PrimaryEntity;
        sb.Append($"<mxswa:SetState DisplayName=\"{DisplayName(step)}\" "
            + "Entity=\"[InputEntities(&quot;primaryEntity&quot;)]\" EntityId=\"[InputEntities(&quot;primaryEntity&quot;).Id]\" "
            + $"EntityName=\"{entity}\">"
            + OptionSetArgument("State", step.State ?? 0)
            + OptionSetArgument("Status", step.Status ?? 1)
            + "</mxswa:SetState><Persist />");
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
            $"<TerminateWorkflow DisplayName=\"{step.StepId}\" "
            + $"Exception=\"[New Microsoft.Xrm.Sdk.InvalidPluginExecutionException(Microsoft.Xrm.Sdk.OperationStatus.{status})]\" "
            + $"Reason=\"[DirectCast({reasonVar}, System.String)]\" />");

        sb.Append($"<Sequence DisplayName=\"{DisplayName(step)}\">{ctx.RenderSequenceVariables()}{activities}</Sequence>");
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

            var v = EmitValue(effective, ctx, prep, state, convertForCodeActivity: true);
            var typeArg = XamlTypeFor(dataType) ?? "x:String";
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

        // The value-preparation activities live in this composite, so their variables must be
        // declared here — not in a Sequence.Variables block that does not exist at this level.
        sb.Append($"<mxswa:ActivityReference AssemblyQualifiedName=\"{CrmActivity("Composite")}\" DisplayName=\"{step.StepId}\">"
            + "<mxswa:ActivityReference.Properties>"
            + ctx.RenderVariableCollection()
            + $"<sco:Collection x:TypeArguments=\"Activity\" x:Key=\"Activities\">{prep}{inner}</sco:Collection>"
            + "</mxswa:ActivityReference.Properties></mxswa:ActivityReference>");
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
            + "</Sequence><Persist />");
    }

    // ---------------------------------------------------------------- values

    /// <summary>
    /// Emits the activities that materialise <paramref name="value"/> and returns the name of the
    /// variable holding it.
    /// </summary>
    private static string EmitValue(
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
                EntityExpression(fieldEntity, state, value.Via),
                fieldEntity, operand, targetType: null));
            return operand;
        }

        switch (value.Kind)
        {
            case WorkflowValueKind.Literal:
            {
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
                        EntityExpression(entity, state, value.Via),
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
        var parameters = $"[New Object() {{ Microsoft.Xrm.Sdk.Workflow.WorkflowPropertyType.{crmType}, &quot;{Xml(literal)}&quot;, &quot;{crmType}&quot; }}]";
        return EvaluateExpression("CreateCrmType", parameters, typeArgument, target);
    }

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

    private static string EvaluateExpression(string op, string parameters, string typeArgument, string target) =>
        $"<mxswa:ActivityReference AssemblyQualifiedName=\"{CrmActivity("EvaluateExpression")}\" DisplayName=\"EvaluateExpression\">"
        + "<mxswa:ActivityReference.Arguments>"
        + $"<InArgument x:TypeArguments=\"x:String\" x:Key=\"ExpressionOperator\">{op}</InArgument>"
        + $"<InArgument x:TypeArguments=\"s:Object[]\" x:Key=\"Parameters\">{parameters}</InArgument>"
        + $"<InArgument x:TypeArguments=\"s:Type\" x:Key=\"TargetType\"><mxswa:ReferenceLiteral x:TypeArguments=\"s:Type\" Value=\"{typeArgument}\" /></InArgument>"
        + $"<OutArgument x:TypeArguments=\"x:Object\" x:Key=\"Result\">[{target}]</OutArgument>"
        + "</mxswa:ActivityReference.Arguments></mxswa:ActivityReference>";

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
    private static string EntityExpression(string? entity, BuildState state, string? via = null)
    {
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
        "Integer" or "Int32" => "Int",
        "Boolean" or "Bool" => "Boolean",
        "DateTime" => "DateTime",
        "Decimal" => "Decimal",
        "Double" or "Float" => "Double",
        "Money" => "Money",
        "OptionSetValue" or "OptionSet" or "Picklist" => "OptionSetValue",
        "EntityReference" or "Lookup" => "EntityReference",
        "Guid" or "UniqueIdentifier" => "Guid",
        _ => "String"
    };

    internal static string? XamlTypeFor(string? dataType) => (dataType ?? "String") switch
    {
        "String" => "x:String",
        "Integer" or "Int32" => "x:Int32",
        "Boolean" or "Bool" => "x:Boolean",
        "DateTime" => "x:DateTime",
        "Decimal" => "x:Decimal",
        "Double" or "Float" => "x:Double",
        "Money" => "mxs:Money",
        "OptionSetValue" or "OptionSet" or "Picklist" => "mxs:OptionSetValue",
        "EntityReference" or "Lookup" => "mxs:EntityReference",
        "Guid" or "UniqueIdentifier" => "s:Guid",
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
        "x:Int32" or "x:Int64" or "x:Decimal" or "x:Double" => "0",
        "x:DateTime" or "s:Guid" => null,
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
        public bool UsesCrmActivities { get; set; }
        public bool UsesQueryTypes { get; set; }
        public List<(string Name, string TypeArgument, string? Default)> WorkflowVariables { get; } = [];

        /// <summary>Output parameter name -> variable holding it, filled while emitting.</summary>
        public Dictionary<string, string> OutputVariables { get; } = new(StringComparer.OrdinalIgnoreCase);
        public IReadOnlyList<string> AssignedStepIds => _assigned;

        public void AssignId(WorkflowStep step)
        {
            step.StepId = $"{WorkflowStepKind.PrefixFor(step.Kind)}{++_counter}";
            _assigned.Add(step.StepId);
        }

        public void AssignBranchId(WorkflowStep step, bool isElse)
        {
            var id = $"ConditionBranchStep{++_counter}";
            if (isElse) _elseBranchIds[step] = id; else _branchIds[step] = id;
            _assigned.Add(id);
        }

        public string? BranchIdOf(WorkflowStep step, bool isElse) =>
            isElse
                ? _elseBranchIds.TryGetValue(step, out var e) ? e : null
                : _branchIds.TryGetValue(step, out var b) ? b : null;

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

        public string RenderSequenceVariables() =>
            _declarations.Count == 0 ? string.Empty
                : $"<Sequence.Variables>{string.Concat(_declarations)}</Sequence.Variables>";

        public string RenderVariableCollection() =>
            $"<sco:Collection x:TypeArguments=\"Variable\" x:Key=\"Variables\">{string.Concat(_declarations)}</sco:Collection>";
    }
}
