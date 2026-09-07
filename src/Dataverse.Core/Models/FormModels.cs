namespace Dataverse.Core.Models;

/// <summary>
/// A form as <c>form_list</c> reports it.
/// </summary>
/// <param name="VersionNumber">
/// <c>systemform</c> has no <c>modifiedon</c> (and no <c>modifiedby</c>) — a <c>$select</c> of it
/// fails with <c>0x80060888</c>. This is what you compare states with.
/// </param>
public sealed record FormSummary(
    Guid FormId,
    string Name,
    string? TableLogicalName,
    int Type,
    string TypeName,
    int? FormActivationState,
    string? FormActivationStateName,
    bool IsManaged,
    long? VersionNumber);

/// <summary>
/// A form's structure, as a tree rather than as XML.
/// </summary>
/// <remarks>
/// FormXML is not readable by hand at any useful size, and every question about a form — what sits
/// in which section, which control is bound to which column, which cell a code component hangs off —
/// is a question about that structure.
/// </remarks>
/// <param name="ControlDescriptions">
/// The <c>&lt;controlDescriptions&gt;</c> block, which is where code components actually live. A
/// cell only carries a generic control with a <c>uniqueid</c>; the description says which component
/// renders it, once per form factor.
/// </param>
/// <param name="FormXml">The raw XML, included only when asked for.</param>
public sealed record FormDetail(
    Guid FormId,
    string Name,
    string? TableLogicalName,
    int Type,
    string TypeName,
    int? FormActivationState,
    string? FormActivationStateName,
    bool IsManaged,
    long? VersionNumber,
    IReadOnlyList<FormTab> Tabs,
    IReadOnlyList<FormControlDescription> ControlDescriptions,
    string? FormXml);

public sealed record FormTab(
    string? Id,
    string? Name,
    string? Label,
    bool Visible,
    IReadOnlyList<FormColumn> Columns);

public sealed record FormColumn(string? Width, IReadOnlyList<FormSection> Sections);

public sealed record FormSection(
    string? Id,
    string? Name,
    string? Label,
    bool ShowLabel,
    bool Visible,
    IReadOnlyList<FormCell> Cells);

public sealed record FormCell(
    string? Id,
    string? Label,
    int? RowSpan,
    int? ColSpan,
    IReadOnlyList<FormControlInfo> Controls);

/// <param name="UniqueId">
/// Present on a cell that hosts a code component; it is what the matching
/// <c>controlDescription</c> points at with <c>forControl</c>.
/// </param>
/// <param name="CustomControlNames">
/// Resolved from the control descriptions, so a code component shows up here as its stored name
/// (with the publisher prefix) instead of only as a GUID classid.
/// </param>
public sealed record FormControlInfo(
    string? Id,
    string? ClassId,
    string? ClassName,
    string? DataFieldName,
    string? UniqueId,
    bool IsUnbound,
    bool IsCustomControl,
    IReadOnlyList<string> CustomControlNames);

public sealed record FormControlDescription(
    string ForControl,
    IReadOnlyList<FormCustomControl> CustomControls);

/// <param name="FormFactor">0 = web, 1 = tablet, 2 = phone.</param>
public sealed record FormCustomControl(
    string Name,
    int? FormFactor,
    IReadOnlyDictionary<string, string> Parameters);

/// <summary>Result of a change to a form.</summary>
/// <param name="Published">
/// Whether the form was published. It matters more here than elsewhere: a <c>PATCH</c> writes the
/// unpublished form while a <c>GET</c> returns the published one, so without a publish the change is
/// invisible even to a read-back.
/// </param>
public sealed record FormEditResult(
    bool Success,
    Guid FormId,
    string Name,
    string? TableLogicalName,
    string Change,
    bool Published,
    long? VersionNumberBefore,
    long? VersionNumberAfter,
    string? Note);
