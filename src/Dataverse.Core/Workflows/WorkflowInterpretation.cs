namespace Dataverse.Core.Workflows;

/// <summary>
/// Best-effort structured interpretation of a Classic Workflow's XAML, produced by
/// <see cref="WorkflowXamlParser"/>. Designed to be robust against arbitrary real-world
/// workflows: anything not recognised is surfaced as an <see cref="InterpretedStep"/> of
/// kind <c>Unknown</c> rather than causing a failure.
/// </summary>
public sealed record WorkflowInterpretation(
    bool IsWellFormed,
    string? ClassName,
    /// <summary>True when every top-level step is a kind the builder can regenerate
    /// (UpdateEntity / CreateEntity / CustomActivity). Drives the edit gate.</summary>
    bool IsTemplateRecognized,
    IReadOnlyList<InterpretedStep> Steps,
    IReadOnlyList<string> Notes);

public sealed record InterpretedStep(
    string Kind,
    string Summary,
    string? EntityName = null,
    IReadOnlyList<string>? Fields = null,
    string? AssemblyQualifiedName = null,
    IReadOnlyList<string>? Arguments = null);

/// <summary>Outcome of generating/writing a workflow from a <see cref="WorkflowDefinition"/>.</summary>
public sealed record WorkflowWriteResult(
    Guid WorkflowId,
    bool Written,
    bool Activated,
    WorkflowDefinitionValidation Validation);
