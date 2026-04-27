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
