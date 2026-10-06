namespace Dataverse.Core.Models;

/// <param name="UniqueName">
/// Also the logical name of the table that stores the process's instances, once activated.
/// </param>
/// <param name="ProcessOrder">
/// Position among the processes of its table; the lowest one a user may use is applied to new rows.
/// </param>
/// <param name="Type">"business process flow" or "task flow" (<c>businessprocesstype</c> 0 / 1).</param>
public sealed record BpfSummary(
    Guid ProcessId,
    string Name,
    string? UniqueName,
    string? PrimaryEntity,
    bool IsActivated,
    int? ProcessOrder,
    string Type,
    bool IsManaged,
    DateTime? ModifiedOn);

public sealed record BpfDetail(
    Guid ProcessId,
    string Name,
    string? UniqueName,
    string? PrimaryEntity,
    string? Description,
    int StateCode,
    int? ProcessOrder,
    int BusinessProcessType,
    bool IsManaged,
    string? Xaml,
    DateTime? ModifiedOn);

/// <summary>One stage as the platform stored it (<c>processstage</c>).</summary>
public sealed record BpfStageRow(Guid StageId, string Name, string? Entity, int? Category);

/// <summary>The table that holds a process's instances.</summary>
/// <param name="RecordLookups">Lookup columns to the records the process runs over, per table: <c>bpf_&lt;table&gt;id</c>.</param>
public sealed record BpfInstanceTable(
    string LogicalName,
    string EntitySetName,
    IReadOnlyDictionary<string, string> RecordLookups);

/// <summary>One running (or finished/abandoned) instance of a process.</summary>
/// <param name="TraversedPath">Comma-separated stage ids the instance has passed, ending with the active one.</param>
public sealed record BpfInstance(
    Guid InstanceId,
    Guid ProcessId,
    string? ProcessName,
    Guid? ActiveStageId,
    string? ActiveStageName,
    string? TraversedPath,
    string Status,
    DateTime? ModifiedOn);
