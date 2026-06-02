namespace Dataverse.Core.Workflows;

using System.Text.Json.Serialization;

/// <summary>
/// Intermediate representation (IR) of a "simple" Classic Workflow that the MCP can
/// safely generate and round-trip as WF4 XAML. This is deliberately a small subset of
/// what the Dataverse designer supports — only the cases we are confident we can build
/// correctly (see <see cref="WorkflowXamlBuilder"/>). The parser may additionally emit
/// step kinds that we recognise but cannot (yet) rebuild (e.g. <see cref="UnknownStep"/>).
/// </summary>
public sealed record WorkflowDefinition(
    string PrimaryEntity,
    WorkflowMode Mode,
    WorkflowScope Scope,
    WorkflowTrigger Trigger,
    IReadOnlyList<WorkflowStep> Steps);

public enum WorkflowMode
{
    /// <summary>Asynchronous background workflow (workflow.mode = 0).</summary>
    Background = 0,

    /// <summary>Synchronous real-time workflow (workflow.mode = 1).</summary>
    Realtime = 1
}

public enum WorkflowScope
{
    User = 1,
    BusinessUnit = 2,
    ParentChildBusinessUnit = 3,
    Organization = 4
}

public enum WorkflowStage
{
    PreOperation = 20,
    PostOperation = 40
}

public sealed record WorkflowTrigger(
    bool OnDemand = false,
    bool OnCreate = false,
    bool OnUpdate = false,
    bool OnDelete = false,
    IReadOnlyList<string>? UpdateAttributes = null,
    WorkflowStage Stage = WorkflowStage.PostOperation);

/// <summary>
/// CRM value categories supported by the template engine. Each maps to a concrete
/// WF4 target type, a <c>Microsoft.Xrm.Sdk.Workflow.WorkflowPropertyType</c> member and
/// the "CrmType" name used by the <c>CreateCrmType</c> expression. See
/// <see cref="WorkflowXamlBuilder"/> for the mapping table.
/// </summary>
public enum CrmValueType
{
    String,
    Integer,
    Decimal,
    Double,
    Money,
    Boolean,
    OptionSet,
    DateTime,
    EntityReference,
    Guid
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(LiteralValue), "literal")]
[JsonDerivedType(typeof(FieldValue), "field")]
public abstract record ArgumentValue;

/// <summary>A constant value, emitted via a typed <c>CreateCrmType</c> expression.</summary>
public sealed record LiteralValue(
    CrmValueType Type,
    string Value,
    /// <summary>Required when <see cref="Type"/> is <see cref="CrmValueType.EntityReference"/>.</summary>
    string? EntityReferenceLogicalName = null) : ArgumentValue;

/// <summary>A value read from an attribute of the primary entity via <c>GetEntityProperty</c>.</summary>
public sealed record FieldValue(
    string Attribute,
    CrmValueType Type) : ArgumentValue;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(UpdateEntityStep), "updateEntity")]
[JsonDerivedType(typeof(CustomActivityStep), "customActivity")]
[JsonDerivedType(typeof(CreateEntityStep), "createEntity")]
[JsonDerivedType(typeof(ConditionStep), "condition")]
[JsonDerivedType(typeof(UnknownStep), "unknown")]
public abstract record WorkflowStep;

public sealed record FieldAssignment(
    string Attribute,
    CrmValueType Type,
    ArgumentValue Value);

/// <summary>Updates the primary entity (or a related target) with the given field values.</summary>
public sealed record UpdateEntityStep(
    string EntityName,
    IReadOnlyList<FieldAssignment> Assignments) : WorkflowStep;

/// <summary>Creates a new entity record with the given field values.</summary>
public sealed record CreateEntityStep(
    string EntityName,
    IReadOnlyList<FieldAssignment> Assignments) : WorkflowStep;

public sealed record ActivityArgument(
    string Name,
    /// <summary>The CRM/CLR type of the activity parameter (drives the XAML argument type).</summary>
    CrmValueType Type,
    /// <summary>Set for input arguments. Null for output arguments.</summary>
    ArgumentValue? Value = null,
    /// <summary>Set for output arguments to capture the result into a named workflow variable.</summary>
    string? OutputVariable = null);

/// <summary>Invokes a registered Custom Workflow Activity (code activity) with arguments.</summary>
public sealed record CustomActivityStep(
    string AssemblyQualifiedName,
    string Label,
    IReadOnlyList<ActivityArgument> InputArguments,
    IReadOnlyList<ActivityArgument>? OutputArguments = null) : WorkflowStep;

/// <summary>
/// A condition block recognised by the parser. v1 does not rebuild conditions; this exists
/// so interpretation surfaces them and so the editability gate can reject workflows that
/// contain logic the builder cannot reproduce.
/// </summary>
public sealed record ConditionStep(
    string Description,
    IReadOnlyList<WorkflowStep> ThenSteps) : WorkflowStep;

/// <summary>An activity the parser recognised structurally but cannot map to a known step.</summary>
public sealed record UnknownStep(
    string ActivityType,
    string? Detail = null) : WorkflowStep;
