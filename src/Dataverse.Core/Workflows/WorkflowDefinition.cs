namespace Dataverse.Core.Workflows;

/// <summary>
/// Declarative, agent-friendly description of a Classic Workflow's logic.
/// </summary>
/// <remarks>
/// <para>
/// This model is the only supported way to author workflow logic in this MCP server. Callers never
/// write XAML directly: <see cref="WorkflowXamlBuilder"/> derives step ids, DisplayNames and
/// variable names from the model, so the naming conventions the Dataverse designer depends on
/// cannot be violated by accident. Violating them is what produces error
/// <c>0x80045037</c> ("designer cannot render the process").
/// </para>
/// <para>See <c>docs/classic-workflows-reference.md</c> for the underlying XAML format.</para>
/// </remarks>
public sealed record WorkflowDefinition
{
    /// <summary>Logical name of the primary entity, e.g. "lead".</summary>
    public string PrimaryEntity { get; init; } = string.Empty;

    /// <summary>Top-level steps, executed in order.</summary>
    public List<WorkflowStep> Steps { get; init; } = [];
}

/// <summary>Step kinds this server can generate and parse.</summary>
public static class WorkflowStepKind
{
    public const string Condition = "condition";
    public const string Wait = "wait";
    public const string Stage = "stage";
    public const string CreateRecord = "createRecord";
    public const string UpdateRecord = "updateRecord";
    public const string AssignRecord = "assignRecord";
    public const string ChangeStatus = "changeStatus";
    public const string StopWorkflow = "stopWorkflow";
    public const string CustomActivity = "customActivity";
    public const string SendEmail = "sendEmail";
    public const string StartChildWorkflow = "startChildWorkflow";
    public const string PerformAction = "performAction";

    public static readonly string[] All =
    [
        Condition, Wait, Stage, CreateRecord, UpdateRecord, AssignRecord, ChangeStatus,
        StopWorkflow, CustomActivity, SendEmail, StartChildWorkflow, PerformAction
    ];

    /// <summary>
    /// Kinds the builder can emit. Others are recognised when parsing existing workflows but
    /// cannot be generated, because their configuration format is not fully verified.
    /// </summary>
    public static readonly string[] Writable =
    [
        Condition, Wait, Stage, CreateRecord, UpdateRecord, AssignRecord, ChangeStatus,
        StopWorkflow, CustomActivity, StartChildWorkflow
    ];

    /// <summary>Step-id prefix per kind. The numeric suffix runs across all kinds.</summary>
    public static string PrefixFor(string kind) => kind switch
    {
        Condition => "ConditionStep",
        Wait => "WaitStep",
        Stage => "StageStep",
        CreateRecord => "CreateStep",
        UpdateRecord => "UpdateStep",
        AssignRecord => "AssignStep",
        ChangeStatus => "SetStateStep",
        StopWorkflow => "StopWorkflowStep",
        CustomActivity => "CustomActivityStep",
        SendEmail => "SendEmailStep",
        StartChildWorkflow => "ChildWorkflowStep",
        PerformAction => "InvokeSdkMessageStep",
        _ => "Step"
    };
}

public sealed record WorkflowStep
{
    /// <summary>One of <see cref="WorkflowStepKind"/>.</summary>
    public string Kind { get; init; } = string.Empty;

    /// <summary>
    /// Optional description shown in the designer. Becomes part of the DisplayName as
    /// "&lt;StepId&gt;: &lt;Description&gt;".
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Step id. Ignored on input — the builder assigns it and writes it back here so callers can
    /// reference outputs of this step. Populated when parsing an existing workflow.
    /// </summary>
    public string? StepId { get; set; }

    // ---- condition / wait ----

    /// <summary>Comparisons of a condition or wait step. Combined via <see cref="LogicalOperator"/>.</summary>
    public List<WorkflowCondition>? Conditions { get; init; }

    /// <summary>"And" (default) or "Or". Only relevant with more than one condition.</summary>
    public string? LogicalOperator { get; init; }

    /// <summary>Steps executed when the condition is true.</summary>
    public List<WorkflowStep>? Then { get; init; }

    /// <summary>Steps of the default branch ("otherwise").</summary>
    public List<WorkflowStep>? Else { get; init; }

    // ---- stage ----

    /// <summary>Steps inside a stage.</summary>
    public List<WorkflowStep>? Children { get; init; }

    // ---- record steps ----

    /// <summary>
    /// Target entity. For <c>updateRecord</c>/<c>assignRecord</c>/<c>changeStatus</c> this is the
    /// primary entity; for <c>createRecord</c> any entity.
    /// </summary>
    public string? Entity { get; init; }

    /// <summary>Attribute assignments for create/update steps.</summary>
    public List<WorkflowAttributeAssignment>? Attributes { get; init; }

    // ---- changeStatus ----

    /// <summary>statecode value to set.</summary>
    public int? State { get; init; }

    /// <summary>statuscode value to set.</summary>
    public int? Status { get; init; }

    // ---- assignRecord ----

    /// <summary>Target owner id for an assign step.</summary>
    public string? OwnerId { get; init; }

    /// <summary>"systemuser" (default) or "team".</summary>
    public string? OwnerType { get; init; }

    // ---- customActivity ----

    /// <summary>
    /// AssemblyQualifiedName of the code activity. Take it verbatim from
    /// <c>workflow_get_activity_parameters</c> — it contains the real PublicKeyToken.
    /// </summary>
    public string? AssemblyQualifiedName { get; init; }

    /// <summary>
    /// Input arguments keyed by <c>DependencyPropertyName</c> (not the display name).
    /// </summary>
    public Dictionary<string, WorkflowValue>? Inputs { get; init; }

    /// <summary>
    /// Output parameter names (<c>DependencyPropertyName</c>) to capture. Later steps reference them
    /// via a value of kind <c>stepOutput</c>.
    /// </summary>
    public List<string>? Outputs { get; init; }

    // ---- startChildWorkflow ----

    /// <summary>Id of the child workflow to start.</summary>
    public string? ChildWorkflowId { get; init; }

    // ---- stopWorkflow ----

    /// <summary>Reason text; may be a dynamic value.</summary>
    public WorkflowValue? Reason { get; init; }

    /// <summary>"succeeded" (default) or "cancelled".</summary>
    public string? Outcome { get; init; }
}

/// <summary>A single comparison inside a condition.</summary>
public sealed record WorkflowCondition
{
    /// <summary>Entity holding the attribute. Defaults to the primary entity.</summary>
    public string? Entity { get; init; }

    /// <summary>
    /// Lookup attribute of the primary entity that leads to <see cref="Entity"/>. Required when
    /// comparing a field of a related record, e.g. Entity="opportunity", Via="opportunityid".
    /// </summary>
    public string? Via { get; init; }

    /// <summary>Logical name of the attribute being compared.</summary>
    public string Attribute { get; init; } = string.Empty;

    /// <summary>
    /// Instead of <see cref="Attribute"/>: compare the output of an earlier custom activity step,
    /// as "&lt;stepId&gt;.&lt;ParameterName&gt;" (e.g. "CustomActivityStep4.isUserInTeam").
    /// </summary>
    public string? StepOutput { get; init; }

    /// <summary>
    /// One of: Equal, NotEqual, Contains, DoesNotContain, BeginsWith, DoesNotBeginWith, EndsWith,
    /// DoesNotEndWith, NotNull, Null, GreaterThan, GreaterEqual, LessThan, LessEqual, In, NotIn.
    /// </summary>
    public string Operator { get; init; } = "Equal";

    /// <summary>
    /// Right-hand side. Omit for the Null/NotNull operators, which take no value.
    /// </summary>
    public WorkflowValue? Value { get; init; }
}

/// <summary>Assignment of a value to an attribute of a create/update step.</summary>
public sealed record WorkflowAttributeAssignment
{
    public string Attribute { get; init; } = string.Empty;
    public WorkflowValue Value { get; init; } = new();
}

/// <summary>Value kinds usable anywhere a value is expected.</summary>
public static class WorkflowValueKind
{
    /// <summary>A constant. Emitted as EvaluateExpression/CreateCrmType.</summary>
    public const string Literal = "literal";

    /// <summary>
    /// One or more field references with an optional fallback. Emitted as GetEntityProperty per
    /// field plus EvaluateExpression/SelectFirstNonNull; the first non-empty value wins.
    /// </summary>
    public const string Field = "field";

    /// <summary>An output parameter of an earlier custom activity step.</summary>
    public const string StepOutput = "stepOutput";

    public static readonly string[] All = [Literal, Field, StepOutput];
}

public sealed record WorkflowValue
{
    /// <summary>One of <see cref="WorkflowValueKind"/>.</summary>
    public string Kind { get; init; } = WorkflowValueKind.Literal;

    /// <summary>
    /// CRM data type of the value: String, Integer, Boolean, DateTime, Decimal, Double, Money,
    /// OptionSetValue, EntityReference, Guid. Defaults to String.
    /// </summary>
    public string? DataType { get; init; }

    /// <summary>The constant, for <c>literal</c>.</summary>
    public string? Literal { get; init; }

    /// <summary>
    /// Field references for <c>field</c>, each as "entity.attribute" (e.g. "lead.companyname").
    /// Evaluated in order; the first non-empty one is used.
    /// </summary>
    public List<string>? Fields { get; init; }

    /// <summary>
    /// Lookup attribute of the primary entity leading to the related record, when
    /// <see cref="Fields"/> points at another entity — e.g. Fields=["opportunity.sample_salesma"] with
    /// Via="opportunityid". One level of traversal only.
    /// </summary>
    public string? Via { get; init; }

    /// <summary>Optional fallback constant used when all <see cref="Fields"/> are empty.</summary>
    public string? Fallback { get; init; }

    /// <summary>
    /// For <c>stepOutput</c>: reference of the form "&lt;stepId&gt;.&lt;ParameterName&gt;",
    /// e.g. "CustomActivityStep4.Domain".
    /// </summary>
    public string? StepOutput { get; init; }
}
