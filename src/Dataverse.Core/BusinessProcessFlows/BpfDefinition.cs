namespace Dataverse.Core.BusinessProcessFlows;

using Dataverse.Core.Workflows;

/// <summary>
/// Declarative description of a business process flow: its stages, the steps inside them, the
/// path between them and the branches that change that path.
/// </summary>
/// <remarks>
/// <para>
/// A business process flow is a <c>workflow</c> row with <c>category = 4</c> whose XAML describes
/// stages instead of actions. The platform derives everything else from that XAML on save —
/// <c>clientdata</c>, <c>uidata</c> (what the form's process bar renders and where branch conditions
/// are evaluated) and the <c>processstage</c> rows. So the XAML is the only thing a caller has to get
/// right, and <see cref="BpfXamlBuilder"/> generates it from this model.
/// </para>
/// <para>See <c>docs/business-process-flows-reference.md</c> for the format itself.</para>
/// </remarks>
public sealed record BpfDefinition
{
    /// <summary>
    /// Logical name of the table the process starts on. Filled from the record when writing to an
    /// existing process; the first stage must be on this table.
    /// </summary>
    public string PrimaryEntity { get; init; } = string.Empty;

    /// <summary>Stages in display order. The path runs top to bottom unless a stage says otherwise.</summary>
    public List<BpfStage> Stages { get; init; } = [];

    /// <summary>
    /// Language of the stage and step labels. Defaults to the organisation's base language when
    /// writing; the designer writes every label in exactly one language.
    /// </summary>
    public int? LanguageCode { get; init; }

    /// <summary>
    /// Classic workflows run when an instance of the process changes state ("global workflows" in the
    /// designer): applied, reactivated, finished or abandoned. Each must be an activated, on-demand
    /// workflow on the primary table.
    /// </summary>
    public List<BpfWorkflowTrigger> Workflows { get; init; } = [];
}

/// <summary>One stage of the process bar.</summary>
public sealed record BpfStage
{
    /// <summary>
    /// The stage's id (a GUID). Keep it when rewriting an existing process: it is the
    /// <c>processstageid</c> that running instances point at with <c>activestageid</c>, and a new id
    /// for the same stage throws every instance standing on it off the path. Assigned when absent.
    /// </summary>
    public string? StageId { get; set; }

    /// <summary>
    /// Optional handle for <see cref="Next"/>, branch targets and <see cref="BpfRelationship.FromStage"/>.
    /// A stage can also be referred to by its name or its id; a key only helps when names repeat.
    /// </summary>
    public string? Key { get; init; }

    /// <summary>Label shown in the process bar.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Table of this stage. Defaults to the table of the stage before it.</summary>
    public string? Entity { get; init; }

    /// <summary>
    /// Stage category (<c>processstage.stagecategory</c>): Qualify, Develop, Propose, Close, Identify,
    /// Research, Resolve, Approval — by name or number. Omitted means none. Used for reporting only.
    /// </summary>
    public string? Category { get; init; }

    /// <summary>Data steps and other steps, in display order.</summary>
    public List<BpfStep> Steps { get; init; } = [];

    /// <summary>
    /// The stage that follows on the main path, by key, name or id. Defaults to the next stage in
    /// <see cref="BpfDefinition.Stages"/>; the last stage has none. Pass <c>"end"</c> to end the path
    /// here although more stages follow (they are reached through branches).
    /// </summary>
    public string? Next { get; init; }

    /// <summary>
    /// A condition evaluated when the user leaves this stage: the first branch whose comparisons hold
    /// decides the next stage, otherwise <see cref="BpfBranching.Else"/>. Comparisons read fields of
    /// this stage's table.
    /// </summary>
    public BpfBranching? Branch { get; init; }

    /// <summary>
    /// How the process gets from a stage on another table to this one. Required for the first stage
    /// on a new table: the platform follows this relationship to find (or create) the record.
    /// </summary>
    public BpfRelationship? Relationship { get; init; }

    /// <summary>
    /// Classic workflows run when an instance enters or leaves this stage ("triggered process" in the
    /// designer). Each must be an activated, on-demand workflow on this stage's table.
    /// </summary>
    public List<BpfWorkflowTrigger> Workflows { get; init; } = [];
}

/// <summary>When a <see cref="BpfWorkflowTrigger"/> fires.</summary>
public static class BpfTriggerEvent
{
    /// <summary>Stage level: the instance moves onto the stage.</summary>
    public const string StageEnter = "stageEnter";

    /// <summary>Stage level: the instance moves off the stage.</summary>
    public const string StageExit = "stageExit";

    /// <summary>Process level: the process is applied to a record.</summary>
    public const string Applied = "applied";

    /// <summary>Process level: a finished or abandoned instance is reactivated.</summary>
    public const string Reactivated = "reactivated";

    /// <summary>Process level: the instance is finished on its last stage.</summary>
    public const string Finished = "finished";

    /// <summary>Process level: the instance is abandoned.</summary>
    public const string Abandoned = "abandoned";

    public static readonly string[] StageEvents = [StageEnter, StageExit];
    public static readonly string[] ProcessEvents = [Applied, Reactivated, Finished, Abandoned];
}

/// <summary>A classic workflow started by the process.</summary>
public sealed record BpfWorkflowTrigger
{
    /// <summary>Id of the classic workflow (category 0, on-demand, activated).</summary>
    public string WorkflowId { get; init; } = string.Empty;

    /// <summary>One of <see cref="BpfTriggerEvent"/>; stage or process events depending on where it sits.</summary>
    public string On { get; init; } = string.Empty;

    /// <summary>
    /// Id of the trigger inside the process (<c>ActionId</c>). Kept when rewriting; assigned when absent.
    /// </summary>
    public string? TriggerId { get; set; }
}

/// <summary>Step kinds a stage can hold.</summary>
public static class BpfStepKind
{
    /// <summary>A field the user fills in (the designer's "Data Step").</summary>
    public const string Field = "field";

    /// <summary>
    /// A button that runs an on-demand classic workflow or a custom process action on the record
    /// (the designer's "Action Step").
    /// </summary>
    public const string Action = "action";

    /// <summary>A button that runs an instant Power Automate flow (the designer's "Flow Step").</summary>
    public const string Flow = "flow";

    public static readonly string[] All = [Field, Action, Flow];
}

/// <summary>One step inside a stage.</summary>
public sealed record BpfStep
{
    /// <summary>One of <see cref="BpfStepKind"/>; defaults to <c>field</c>.</summary>
    public string Kind { get; init; } = BpfStepKind.Field;

    /// <summary>
    /// The step's id (a GUID, <c>ProcessStepId</c>). Keep it when rewriting; assigned when absent.
    /// </summary>
    public string? StepId { get; set; }

    /// <summary>Label shown next to the field. Defaults to the attribute's display name.</summary>
    public string? Label { get; init; }

    /// <summary>For <c>field</c>: logical name of the attribute on the stage's table.</summary>
    public string? Attribute { get; init; }

    /// <summary>
    /// For <c>field</c>: the stage cannot be left while the field is empty. For <c>flow</c>: the flow
    /// must have run. Not available for <c>action</c>.
    /// </summary>
    public bool Required { get; init; }

    /// <summary>
    /// For <c>action</c>: id of an activated, on-demand classic workflow or of a custom process action
    /// on the stage's table. For <c>flow</c>: id of the flow's <c>workflow</c> row (category 5, an
    /// instant flow).
    /// </summary>
    public string? ProcessId { get; init; }

    /// <summary>
    /// For <c>field</c>: the form control class. Derived from the attribute type when omitted, which is
    /// what the designer does too; only kept so a rewrite does not change an existing step.
    /// </summary>
    public string? ClassId { get; init; }

    /// <summary>
    /// For <c>field</c>: the control's raw <c>Parameters</c> XML, e.g. the duplicate-detection
    /// settings of the lookups in the system processes. Read from existing processes and written back
    /// unchanged; not meant to be authored.
    /// </summary>
    public string? Parameters { get; init; }

    /// <summary>For <c>field</c>: the control is a system control. Kept from existing processes.</summary>
    public bool SystemControl { get; init; }
}

/// <summary>A condition on leaving a stage.</summary>
public sealed record BpfBranching
{
    /// <summary>Cases tested in order (if / else-if); the first that holds decides.</summary>
    public List<BpfBranch> Branches { get; init; } = [];

    /// <summary>Stage taken when no case holds, by key, name or id. Optional.</summary>
    public string? Else { get; init; }
}

/// <summary>One case of a <see cref="BpfBranching"/>.</summary>
public sealed record BpfBranch
{
    /// <summary>
    /// Comparisons on fields of the stage's table, in the shape the classic-workflow definitions use
    /// (<c>attribute</c>, <c>operator</c>, <c>value</c>, bracketed groups).
    /// </summary>
    public List<WorkflowCondition> Conditions { get; init; } = [];

    /// <summary>"And" (default) or "Or".</summary>
    public string? LogicalOperator { get; init; }

    /// <summary>Stage taken when the comparisons hold, by key, name or id.</summary>
    public string Next { get; init; } = string.Empty;

    /// <summary>Name of the case shown in the designer.</summary>
    public string? Description { get; init; }
}

/// <summary>The relationship a cross-table stage is reached through.</summary>
public sealed record BpfRelationship
{
    /// <summary>Schema name of the 1:N relationship, e.g. <c>opportunity_originating_lead</c>.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// The lookup on this stage's table that points back to the previous table, e.g.
    /// <c>originatingleadid</c>. Looked up from the relationship when omitted.
    /// </summary>
    public string? Attribute { get; init; }

    /// <summary>
    /// The stage the process comes from. Defaults to the stage whose path leads here; only needed
    /// when several stages lead here.
    /// </summary>
    public string? FromStage { get; init; }
}
