namespace Dataverse.Core.Models;

public sealed record TableSummary(
    string LogicalName,
    string? DisplayName,
    string? EntitySetName,
    string? TableType,
    bool IsCustomEntity);

public sealed record TableDetail(
    string LogicalName,
    string? DisplayName,
    string? PluralDisplayName,
    string? EntitySetName,
    string? Description,
    string? TableType,
    bool IsCustomEntity,
    bool IsAuditEnabled,
    IReadOnlyList<ColumnSummary> Attributes);

public sealed record ColumnSummary(
    string LogicalName,
    string? DisplayName,
    string AttributeType,
    bool IsRequired,
    bool IsCustomAttribute);

public sealed record ColumnDetail(
    string LogicalName,
    string? DisplayName,
    string AttributeType,
    bool IsRequired,
    bool IsCustomAttribute,
    string? Description,
    int? MaxLength,
    decimal? MinValue,
    decimal? MaxValue);
