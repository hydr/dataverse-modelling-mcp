namespace Dataverse.Core.Models;

/// <summary>
/// Where a classic ribbon button goes. Maps to the <c>Location</c> attribute of a
/// <c>&lt;CustomAction&gt;</c>, which is always
/// <c>Mscrm.&lt;scope&gt;.&lt;entity&gt;.&lt;tab&gt;.&lt;group&gt;.Controls._children</c>.
/// </summary>
public enum RibbonScope
{
    /// <summary>Main form command bar — <c>Mscrm.Form.&lt;entity&gt;.…</c>.</summary>
    Form = 0,

    /// <summary>Main/homepage grid command bar — <c>Mscrm.HomepageGrid.&lt;entity&gt;.…</c>.</summary>
    HomepageGrid = 1,

    /// <summary>Sub grid command bar — <c>Mscrm.SubGrid.&lt;entity&gt;.…</c>.</summary>
    SubGrid = 2
}

/// <summary>
/// <c>ribbondiff.difftype</c> — verified against the live option set.
/// </summary>
public enum RibbonDiffType
{
    Standard = 0,
    Tab = 1,
    LayoutTemplate = 2,
    LocalizedLabel = 3
}

/// <summary>One <c>ribbondiff</c> row — a single node of the entity's RibbonDiffXml.</summary>
public sealed record RibbonDiffEntry(
    Guid RibbonDiffId,
    string DiffId,
    int DiffType,
    string DiffTypeName,
    bool IsManaged,
    string? Xml);

/// <summary>One <c>ribboncommand</c> row — a <c>&lt;CommandDefinition&gt;</c>.</summary>
public sealed record RibbonCommandEntry(
    Guid RibbonCommandId,
    string CommandId,
    bool IsManaged,
    string? Xml);

/// <summary>One <c>ribbonrule</c> row — an <c>&lt;EnableRule&gt;</c> / <c>&lt;DisplayRule&gt;</c>.</summary>
public sealed record RibbonRuleEntry(
    Guid RibbonRuleId,
    string RuleId,
    int RuleType,
    bool IsManaged,
    string? Xml);

/// <summary>
/// The reconstructed ribbon <b>difference</b> of a table plus, optionally, the insert points that
/// exist in its <b>compiled</b> ribbon.
/// </summary>
public sealed record RibbonInfo(
    string TableLogicalName,
    int CustomActionCount,
    int CommandDefinitionCount,
    int RuleCount,
    IReadOnlyList<RibbonDiffEntry> CustomActions,
    IReadOnlyList<RibbonCommandEntry> CommandDefinitions,
    IReadOnlyList<RibbonRuleEntry> Rules,
    string RibbonDiffXml,
    IReadOnlyList<string>? InsertLocations,
    string Source);

public sealed record RibbonAddButtonResult(
    bool Success,
    string TableLogicalName,
    string ButtonId,
    string CustomActionId,
    string CommandId,
    string Location,
    string RibbonDiffXml,
    string TemporarySolutionUniqueName,
    bool TemporarySolutionDeleted,
    bool Published,
    bool Verified,
    IReadOnlyList<string> ImportComponentErrors,
    string? Warning,
    /// <summary>
    /// The other CustomActions that already existed and were re-sent with the import. A non-empty
    /// <c>&lt;CustomActions&gt;</c> replaces the whole collection, so anything omitted would be deleted.
    /// </summary>
    IReadOnlyList<string>? PreservedCustomActionIds = null,
    /// <summary>Those of <see cref="PreservedCustomActionIds"/> that did not survive — must be empty.</summary>
    IReadOnlyList<string>? LostCustomActionIds = null);

public sealed record RibbonRemoveButtonResult(
    bool Success,
    string TableLogicalName,
    string ButtonId,
    IReadOnlyList<string> DeletedDiffIds,
    IReadOnlyList<string> DeletedCommandIds,
    IReadOnlyList<string> DeletedRuleIds,
    bool Published,
    bool Verified,
    string? Warning);

/// <summary>
/// Parsed <c>parameters</c> entry for a classic <c>&lt;JavaScriptFunction&gt;</c>. Classic ribbons use
/// element names rather than the integer codes the modern <c>appaction</c> table stores:
/// <c>&lt;CrmParameter Value="PrimaryControl" /&gt;</c>, <c>&lt;StringParameter Value="abc" /&gt;</c>,
/// <c>&lt;BoolParameter Value="true" /&gt;</c>, <c>&lt;IntParameter Value="5" /&gt;</c>.
/// </summary>
public sealed record RibbonParameter(string ElementName, string Value);
