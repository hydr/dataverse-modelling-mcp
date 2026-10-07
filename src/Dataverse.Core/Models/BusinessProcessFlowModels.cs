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
/// <param name="RecordLookups">
/// Navigation properties of the lookups to the records the process runs over, per table (for binding).
/// </param>
/// <param name="RecordColumns">The same lookups as columns, <c>bpf_&lt;table&gt;id</c> (for filtering).</param>
public sealed record BpfInstanceTable(
    string LogicalName,
    string EntitySetName,
    IReadOnlyDictionary<string, string> RecordLookups,
    IReadOnlyDictionary<string, string>? RecordColumns = null);

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

/// <summary>A 1:N relationship a cross-table stage can be reached through.</summary>
/// <param name="Name">Schema name — what <c>relationship.name</c> takes.</param>
/// <param name="Attribute">The lookup on <paramref name="ToEntity"/> pointing at <paramref name="FromEntity"/>.</param>
public sealed record BpfRelationshipCandidate(string Name, string Attribute, string FromEntity, string ToEntity);
