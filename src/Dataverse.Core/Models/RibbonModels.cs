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
/// <para>
/// The <c>ribbondiff</c> rows arrive as one flat set and are split by <see cref="RibbonDiffType"/>,
/// because each kind belongs in a different section of the RibbonDiffXml. Sending them all as
/// <c>&lt;CustomActions&gt;</c> is what the import rejects with "Missing Location Attribute".
/// </para>
/// </summary>
public sealed record RibbonInfo(
    string TableLogicalName,
    int CustomActionCount,
    int CommandDefinitionCount,
    int RuleCount,
    /// <summary><c>difftype = Standard</c> — the <c>&lt;CustomAction&gt;</c> nodes, nothing else.</summary>
    IReadOnlyList<RibbonDiffEntry> CustomActions,
    IReadOnlyList<RibbonCommandEntry> CommandDefinitions,
    IReadOnlyList<RibbonRuleEntry> Rules,
    string RibbonDiffXml,
    IReadOnlyList<string>? InsertLocations,
    string Source,
    /// <summary>
    /// <c>difftype = LocalizedLabel</c> — <c>&lt;LocLabel&gt;</c> nodes carrying the per-language
    /// captions. The Ribbon Workbench writes one per label it localizes.
    /// </summary>
    IReadOnlyList<RibbonDiffEntry>? LocLabels = null,
    /// <summary>
    /// <c>difftype = Tab</c> / <c>LayoutTemplate</c>. Carried along so a round-trip does not silently
    /// drop them; this code does not place them into their sections.
    /// </summary>
    IReadOnlyList<RibbonDiffEntry>? OtherDiffs = null);

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
    IReadOnlyList<string>? LostCustomActionIds = null,
    /// <summary>
    /// The <c>languagecode</c> the caption LocLabels were written under. Worth checking against the
    /// language the org actually uses: a caption stored under a foreign language renders as nothing.
    /// </summary>
    int? CaptionLanguageCode = null,
    /// <summary>Where <see cref="CaptionLanguageCode"/> came from — a silent fallback says so here.</summary>
    string? CaptionLanguageSource = null);

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

// -------------------------------------------------------------------------------------------------
// Delivery: stored → compiled → delivered
// -------------------------------------------------------------------------------------------------

/// <summary>
/// What a command does, reduced to what can be compared across the three places a ribbon lives:
/// the stored rows (XML), the compiled ribbon (XML) and the client metadata (JSON). Each entry is
/// <c>js:&lt;FunctionName&gt;@&lt;Library&gt;</c> or <c>url:&lt;Address&gt;</c>, in action order.
/// </summary>
public sealed record RibbonActionSignature(IReadOnlyList<string> Actions)
{
    public static readonly RibbonActionSignature Empty = new(Array.Empty<string>());

    public bool SameAs(RibbonActionSignature? other) =>
        other is not null && Actions.SequenceEqual(other.Actions, StringComparer.OrdinalIgnoreCase);

    public override string ToString() => Actions.Count == 0 ? "(no actions)" : string.Join(" | ", Actions);
}

/// <summary>One solution layer's copy of a ribbon command.</summary>
public sealed record RibbonCommandLayer(
    string SolutionUniqueName,
    bool IsManaged,
    string Signature);

/// <summary>What one app's client receives for a command (form command bar).</summary>
public sealed record RibbonDeliveredCommand(
    Guid AppModuleId,
    string? AppModuleName,
    Guid FormId,
    string Context,
    long? RibbonVersionNumber,
    string? Signature,
    bool MatchesCompiled);

/// <summary>Diagnosis of one command across the three levels.</summary>
public sealed record RibbonCommandDiagnosis(
    string CommandId,
    IReadOnlyList<RibbonCommandLayer> StoredLayers,
    /// <summary>The unmanaged copy differs from every managed copy — the managed ones can never win.</summary>
    bool UnmanagedOverridesManaged,
    string? CompiledSignature,
    bool CompiledMatchesStored,
    IReadOnlyList<RibbonDeliveredCommand> Delivered,
    /// <summary><c>ok</c>, or the first failing level: <c>stored</c>, <c>compiled</c>, <c>delivered</c>.</summary>
    string Verdict,
    string? Repair);

public sealed record RibbonDiagnosisResult(
    string OrgUrl,
    string TableLogicalName,
    IReadOnlyList<RibbonCommandDiagnosis> Commands,
    bool DeliveredChecked,
    IReadOnlyList<string> Notes,
    string Summary);

/// <summary>One table with an unmanaged ribbon layer, from the environment-wide scan.</summary>
public sealed record RibbonUnmanagedLayerTable(
    string TableLogicalName,
    int UnmanagedCommandCount,
    IReadOnlyList<string> CommandsOverridingManaged,
    IReadOnlyList<string> UnmanagedOnlyCommands);

public sealed record RibbonUnmanagedLayerScanResult(
    string OrgUrl,
    int TablesWithUnmanagedRibbon,
    IReadOnlyList<RibbonUnmanagedLayerTable> TablesWithUnmanagedCommands,
    string Summary);

/// <summary>One node of the ribbon diff and what a sync would do with it.</summary>
public sealed record RibbonSyncChange(
    string Kind,
    string Id,
    /// <summary><c>add</c>, <c>update</c>, <c>keep</c> (unmanaged-only, preserved) or <c>unchanged</c>.</summary>
    string Action);

public sealed record RibbonSyncResult(
    string OrgUrl,
    string TableLogicalName,
    string SourceSolutionUniqueName,
    bool DryRun,
    IReadOnlyList<RibbonSyncChange> Changes,
    string RibbonDiffXml,
    bool Imported,
    bool Verified,
    IReadOnlyList<string> StillDifferent,
    string? TemporarySolutionUniqueName,
    string Summary);

public sealed record RibbonRefreshResult(
    string OrgUrl,
    string TableLogicalName,
    /// <summary>Whether the table already had an unmanaged layer before the bump.</summary>
    bool HadUnmanagedTableLayer,
    string RestoredBy,
    bool DescriptionRestored,
    RibbonDiagnosisResult? DiagnosisAfter,
    string Summary);

/// <summary>
/// Parsed <c>parameters</c> entry for a classic <c>&lt;JavaScriptFunction&gt;</c>. Classic ribbons use
/// element names rather than the integer codes the modern <c>appaction</c> table stores:
/// <c>&lt;CrmParameter Value="PrimaryControl" /&gt;</c>, <c>&lt;StringParameter Value="abc" /&gt;</c>,
/// <c>&lt;BoolParameter Value="true" /&gt;</c>, <c>&lt;IntParameter Value="5" /&gt;</c>.
/// </summary>
public sealed record RibbonParameter(string ElementName, string Value);
