namespace Dataverse.Core.Models;

public sealed record FlowSummary(
    string FlowId,
    string DisplayName,
    string State,
    DateTime? CreatedTime,
    DateTime? LastModifiedTime,
    string? TriggerType);

public sealed record FlowDetail(
    string FlowId,
    string DisplayName,
    string State,
    DateTime? CreatedTime,
    DateTime? LastModifiedTime,
    string? TriggerType,
    object? Definition);

public sealed record FlowRun(
    string RunId,
    string Status,
    DateTime? StartTime,
    DateTime? EndTime,
    string? TriggerName,
    string? ErrorCode,
    string? ErrorMessage);

public sealed record FlowDescription(
    string FlowId,
    string DisplayName,
    string TriggerSummary,
    IReadOnlyList<string> ActionSummaries,
    string FullDescription);

public sealed record FlowVersionSummary(
    Guid VersionId,
    string VersionName,
    int Operation,
    string OperationName,
    DateTime? CreatedOn,
    string? CreatedByName,
    Guid? RestoredFromVersionId,
    string? ChangeSummary,
    string? SystemChangeSummary);

public sealed record FlowVersionDetail(
    Guid VersionId,
    string VersionName,
    int Operation,
    string OperationName,
    DateTime? CreatedOn,
    string? CreatedByName,
    DateTime? ModifiedOn,
    string? ModifiedByName,
    Guid? RestoredFromVersionId,
    Guid WorkflowId,
    string? WorkflowName,
    string? ChangeSummary,
    string? SystemChangeSummary);

public sealed record FlowRestoreResult(
    Guid NewVersionId,
    Guid RestoredFromVersionId,
    Guid WorkflowId,
    string Message);

public sealed record RunActionSummary(
    string Name,
    string Status,
    string? Code,
    DateTime? StartTime,
    DateTime? EndTime,
    string? ErrorCode,
    string? ErrorMessage,
    string? OutputsLink);

public sealed record FetchXmlValidationResult(
    bool IsValid,
    int ResultCount,
    IReadOnlyList<string> Sample,
    string? Error);
