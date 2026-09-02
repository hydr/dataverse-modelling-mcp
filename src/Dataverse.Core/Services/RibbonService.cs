namespace Dataverse.Core.Services;

using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Dataverse.Core.Clients;
using Dataverse.Core.Json;
using Dataverse.Core.Models;
using Microsoft.Extensions.Logging;

/// <summary>
/// Classic ribbons (<c>RibbonDiffXml</c>) — the pre-Unified-Interface way of putting a button on a
/// table's command bar, and still the more practical one for anything that has to live in source
/// control and travel through solutions. See <c>docs/tools/buttons-classic-vs-modern.md</c>.
/// <para>
/// Three storage facts drive everything in this class; all were verified against a live org:
/// </para>
/// <list type="number">
/// <item><b><c>RetrieveEntityRibbon</c> returns the compiled ribbon, not the diff.</b> It merges the
/// out-of-the-box ribbon with every managed and unmanaged customization, so it proves what the client
/// will render but says nothing about what <i>this</i> org added.</item>
/// <item><b>The diff is stored row-wise.</b> Every <c>&lt;CustomAction&gt;</c> becomes one
/// <c>ribbondiff</c> row (<c>diffid</c> = the CustomAction id, <c>rdx</c> = the XML), every
/// <c>&lt;CommandDefinition&gt;</c> a <c>ribboncommand</c> row, every rule a <c>ribbonrule</c> row.
/// Those rows are readable — and, crucially, <b>deletable</b> — through the Web API.</item>
/// <item><b>Solution import replaces a section, but only when the section is non-empty.</b> This is
/// the subtle one, and getting it wrong destroys buttons. An <b>empty</b> <c>&lt;CustomActions /&gt;</c>
/// is treated as "not specified" and changes nothing — not even after <c>PublishAllXml</c>. A
/// <b>non-empty</b> <c>&lt;CustomActions&gt;</c> <i>replaces the whole collection</i>, so importing a
/// diff that mentions only button B silently removes button A. Both halves were verified against a
/// live org. Hence <see cref="AddButtonAsync"/> reads the current diff and re-sends every existing
/// node alongside the new one, and removal goes through the row-level delete above rather than
/// through an omission.</item>
/// </list>
/// </summary>
public sealed class RibbonService
{
    /// <summary>Solution component type of a table, for AddSolutionComponent.</summary>
    public const int EntityComponentType = 1;

    private const string DiffSet = "api/data/v9.2/ribbondiffs";
    private const string CommandSet = "api/data/v9.2/ribboncommands";
    private const string RuleSet = "api/data/v9.2/ribbonrules";

    private readonly DataverseHttpClient _client;
    private readonly SolutionService _solutions;
    private readonly PublishService _publish;
    private readonly ILogger<RibbonService> _logger;

    public RibbonService(
        DataverseHttpClient client,
        SolutionService solutions,
        PublishService publish,
        ILogger<RibbonService> logger)
    {
        _client = client;
        _solutions = solutions;
        _publish = publish;
        _logger = logger;
    }

    // ---------------------------------------------------------------------------------------------
    // Read
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Reconstruct a table's RibbonDiffXml from the <c>ribbondiff</c> / <c>ribboncommand</c> /
    /// <c>ribbonrule</c> rows.
    /// <para>
    /// This is deliberately <b>not</b> <c>RetrieveEntityRibbon</c> (which returns the compiled ribbon —
    /// half a megabyte of out-of-the-box XML) and <b>not</b> a solution export either: exporting a table
    /// that was added to the solution without subcomponents returns an <i>empty</i>
    /// <c>&lt;RibbonDiffXml&gt;</c> scaffold, and exporting it with subcomponents drags in every form,
    /// view and column. The row tables are the only cheap, exact source.
    /// </para>
    /// </summary>
    /// <param name="includeManaged">Include the diff nodes that came from managed solutions.</param>
    /// <param name="includeLocations">Also list the insert points of the compiled ribbon (extra call).</param>
    public async Task<RibbonInfo> GetAsync(
        string orgUrl,
        string tableLogicalName,
        bool includeManaged = false,
        bool includeLocations = false,
        CancellationToken ct = default)
    {
        var escaped = tableLogicalName.Replace("'", "''");
        var filter = $"entity eq '{escaped}'";
        if (!includeManaged)
        {
            filter += " and ismanaged eq false";
        }

        var encoded = Uri.EscapeDataString(filter);

        var diffs = new List<RibbonDiffEntry>();
        var locLabels = new List<RibbonDiffEntry>();
        var otherDiffs = new List<RibbonDiffEntry>();
        var diffRaw = await _client.GetRawAsync(
            orgUrl,
            $"{DiffSet}?$filter={encoded}&$select=ribbondiffid,diffid,difftype,ismanaged,rdx&$orderby=diffid",
            ct: ct);
        foreach (var item in EnumerateValue(diffRaw))
        {
            var type = item.GetInt32OrZero("difftype");
            var entry = new RibbonDiffEntry(
                RibbonDiffId: item.TryGetGuid("ribbondiffid"),
                DiffId: item.GetStringOrEmpty("diffid"),
                DiffType: type,
                DiffTypeName: EnumName<RibbonDiffType>(type),
                IsManaged: IsTrue(item, "ismanaged"),
                Xml: item.GetStringOrNull("rdx"));

            SortDiffRow(entry, diffs, locLabels, otherDiffs);
        }

        var commands = new List<RibbonCommandEntry>();
        var cmdRaw = await _client.GetRawAsync(
            orgUrl,
            $"{CommandSet}?$filter={encoded}&$select=ribboncommandid,command,ismanaged,commanddefinition&$orderby=command",
            ct: ct);
        foreach (var item in EnumerateValue(cmdRaw))
        {
            commands.Add(new RibbonCommandEntry(
                RibbonCommandId: item.TryGetGuid("ribboncommandid"),
                CommandId: item.GetStringOrEmpty("command"),
                IsManaged: IsTrue(item, "ismanaged"),
                Xml: item.GetStringOrNull("commanddefinition")));
        }

        var rules = new List<RibbonRuleEntry>();
        var ruleRaw = await _client.GetRawAsync(
            orgUrl,
            $"{RuleSet}?$filter={encoded}&$select=ribbonruleid,ruleid,ruletype,ismanaged,ruledefinition&$orderby=ruleid",
            ct: ct);
        foreach (var item in EnumerateValue(ruleRaw))
        {
            rules.Add(new RibbonRuleEntry(
                RibbonRuleId: item.TryGetGuid("ribbonruleid"),
                RuleId: item.GetStringOrEmpty("ruleid"),
                RuleType: item.GetInt32OrZero("ruletype"),
                IsManaged: IsTrue(item, "ismanaged"),
                Xml: item.GetStringOrNull("ruledefinition")));
        }

        IReadOnlyList<string>? locations = null;
        if (includeLocations)
        {
            locations = await ListInsertLocationsAsync(orgUrl, tableLogicalName, ct);
        }

        return new RibbonInfo(
            TableLogicalName: tableLogicalName,
            CustomActionCount: diffs.Count,
            CommandDefinitionCount: commands.Count,
            RuleCount: rules.Count,
            CustomActions: diffs,
            CommandDefinitions: commands,
            Rules: rules,
            RibbonDiffXml: AssembleRibbonDiffXml(diffs, commands, rules, locLabels),
            InsertLocations: locations,
            Source: "ribbondiff / ribboncommand / ribbonrule rows (the stored difference, not the " +
                    "compiled ribbon). RetrieveEntityRibbon would return the merged out-of-the-box + " +
                    "managed + unmanaged ribbon instead.",
            LocLabels: locLabels,
            OtherDiffs: otherDiffs);
    }

    /// <summary>
    /// Put one <c>ribbondiff</c> row into the bucket its section calls for.
    /// <para>
    /// The element name of the stored <c>rdx</c> wins over <c>difftype</c>: the column is what the
    /// platform recorded when the row was written, the element is what the importer will actually
    /// parse. A <c>&lt;LocLabel&gt;</c> in <c>&lt;CustomActions&gt;</c> fails the import with
    /// "Missing Location Attribute" no matter what the column says.
    /// </para>
    /// </summary>
    private static void SortDiffRow(
        RibbonDiffEntry entry,
        List<RibbonDiffEntry> customActions,
        List<RibbonDiffEntry> locLabels,
        List<RibbonDiffEntry> otherDiffs)
    {
        var elementName = RootElementName(entry.Xml);

        if (string.Equals(elementName, "LocLabel", StringComparison.Ordinal)
            || (elementName is null && entry.DiffType == (int)RibbonDiffType.LocalizedLabel))
        {
            locLabels.Add(entry);
            return;
        }

        if (IsCustomActionsChild(elementName)
            || (elementName is null && entry.DiffType == (int)RibbonDiffType.Standard))
        {
            customActions.Add(entry);
            return;
        }

        otherDiffs.Add(entry);
    }

    /// <summary>
    /// Element names that legitimately live inside <c>&lt;CustomActions&gt;</c>. <c>HideCustomAction</c>
    /// belongs there just as much as <c>CustomAction</c> does — on a grown table it is usually the
    /// majority of the section (invoice: 11 of 17), and dropping one un-hides an out-of-the-box button.
    /// </summary>
    private static bool IsCustomActionsChild(string? elementName) =>
        elementName is "CustomAction" or "HideCustomAction";

    /// <summary>Local name of the stored node's root element, or <c>null</c> if it does not parse.</summary>
    private static string? RootElementName(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml))
        {
            return null;
        }

        try
        {
            return XElement.Parse(xml).Name.LocalName;
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
    }

    /// <summary>
    /// Enumerate the <c>Location</c> strings a <c>&lt;CustomAction&gt;</c> may target on this table,
    /// derived from the <b>compiled</b> ribbon (<c>RetrieveEntityRibbon</c>). Passing a location that is
    /// not in this list is one of the silent failures: the import succeeds, the publish succeeds, and no
    /// button appears.
    /// </summary>
    public async Task<IReadOnlyList<string>> ListInsertLocationsAsync(
        string orgUrl,
        string tableLogicalName,
        CancellationToken ct = default)
    {
        var xml = await RetrieveCompiledRibbonAsync(orgUrl, tableLogicalName, ct);

        var ids = Regex
            .Matches(xml, "<(?:Tab|Group)\\s[^>]*Id=\"(Mscrm\\.[^\"]+)\"", RegexOptions.CultureInvariant)
            .Select(m => m.Groups[1].Value)
            .Where(id => id.Contains($".{tableLogicalName}.", StringComparison.Ordinal))
            // Only groups (tab id + at least one more segment) can host controls.
            .Where(id => id.Split('.').Length >= 5)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .Select(id => $"{id}.Controls._children")
            .ToList();

        return ids;
    }

    /// <summary>
    /// Call <c>RetrieveEntityRibbon</c> and return the decompressed <c>RibbonXml.xml</c>.
    /// <para>
    /// Two traps: it is an OData <b>function</b> (GET with inline parameters — a POST answers 404), and
    /// the payload is a zip whose entries include <c>[Content_Types].xml</c>.
    /// </para>
    /// </summary>
    public async Task<string> RetrieveCompiledRibbonAsync(
        string orgUrl,
        string tableLogicalName,
        CancellationToken ct = default)
    {
        var url = "api/data/v9.2/RetrieveEntityRibbon(EntityName=@p1,RibbonLocationFilter=@p2)" +
                  $"?@p1='{Uri.EscapeDataString(tableLogicalName)}'" +
                  "&@p2=Microsoft.Dynamics.CRM.RibbonLocationFilters'All'";

        var raw = await _client.GetRawAsync(orgUrl, url, ct: ct);
        using var doc = JsonDocument.Parse(raw);
        var base64 = doc.RootElement.GetStringOrNull("CompressedEntityXml")
                     ?? throw new InvalidOperationException(
                         $"RetrieveEntityRibbon returned no CompressedEntityXml for '{tableLogicalName}'.");

        using var zip = new ZipArchive(new MemoryStream(Convert.FromBase64String(base64)), ZipArchiveMode.Read);
        var entry = zip.Entries.FirstOrDefault(e => e.Name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)
                                                    && !e.Name.Contains("Content_Types", StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException("RetrieveEntityRibbon zip contained no RibbonXml.xml.");

        using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
        return await reader.ReadToEndAsync(ct);
    }

    // ---------------------------------------------------------------------------------------------
    // Add
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Add a JavaScript-backed button to a table's classic ribbon through a solution round-trip:
    /// create a throwaway unmanaged solution, add the table <b>without subcomponents</b> (the export is
    /// then a ~3 KB table shell plus the RibbonDiffXml scaffold), swap in the new
    /// <c>&lt;RibbonDiffXml&gt;</c>, re-zip, import, publish, and finally verify that the
    /// <c>ribbondiff</c> row really exists.
    /// </summary>
    /// <remarks>
    /// The verification step is not decoration. The import job reports
    /// <c>&lt;entityRibbon … result="success"&gt;</c> even when nothing was written, so "the import
    /// succeeded" is not evidence that the button exists.
    /// </remarks>
    public async Task<RibbonAddButtonResult> AddButtonAsync(
        string orgUrl,
        string tableLogicalName,
        string buttonId,
        string location,
        string label,
        string webResourceName,
        string functionName,
        IReadOnlyList<RibbonParameter> parameters,
        int sequence = 41,
        string? imageWebResource = null,
        string? modernImage = null,
        string? enableRule = null,
        string? tooltipTitle = null,
        string? tooltipDescription = null,
        string? templateAlias = "o1",
        string? solutionUniqueName = null,
        string? publisherUniqueName = null,
        bool validateLocation = true,
        bool stripEntityInfo = true,
        bool publish = true,
        int importTimeoutSeconds = 900,
        int? languageCode = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(buttonId))
        {
            throw new ArgumentException("buttonId is required.", nameof(buttonId));
        }

        if (label.Contains("$LocLabels:", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "Pass the caption itself, not a $LocLabels: reference. This tool writes the LocLabel " +
                "node for you and points the button at it — a reference passed in here would end up " +
                "nested inside another one.",
                nameof(label));
        }

        if (validateLocation)
        {
            var known = await ListInsertLocationsAsync(orgUrl, tableLogicalName, ct);
            if (!known.Contains(location, StringComparer.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Location '{location}' does not exist in the compiled ribbon of '{tableLogicalName}'. " +
                    "Importing it would succeed silently and render nothing. Known insert points: " +
                    string.Join(", ", known.Take(40)) +
                    (known.Count > 40 ? $" … (+{known.Count - 40} more)" : string.Empty) +
                    ". Pass validateLocation=false to override.");
            }
        }

        var commandId = $"{buttonId}.Command";
        var customActionId = $"{buttonId}.CustomAction";

        // Read the current diff and carry it along. A non-empty <CustomActions> replaces the entire
        // collection, so sending only the new button would delete every other one on this table.
        var existing = await GetAsync(orgUrl, tableLogicalName, includeManaged: false, ct: ct);
        var preserved = existing.CustomActions
            .Where(c => !string.Equals(c.DiffId, customActionId, StringComparison.OrdinalIgnoreCase))
            .Select(c => c.DiffId)
            .ToList();

        var (captionLanguage, languageSource) = languageCode is { } explicitCode
            ? (explicitCode, "the languageCode argument")
            : await ResolveCaptionLanguageAsync(existing, orgUrl, ct);

        var ribbonDiffXml = BuildRibbonDiffXml(
            buttonId: buttonId,
            customActionId: customActionId,
            commandId: commandId,
            location: location,
            label: label,
            webResourceName: webResourceName,
            functionName: functionName,
            parameters: parameters,
            sequence: sequence,
            imageWebResource: imageWebResource,
            modernImage: modernImage,
            enableRule: enableRule,
            tooltipTitle: tooltipTitle,
            tooltipDescription: tooltipDescription,
            templateAlias: templateAlias,
            preserve: existing,
            languageCode: captionLanguage);

        var reuseSolution = !string.IsNullOrWhiteSpace(solutionUniqueName);
        var tempSolution = solutionUniqueName
                           ?? $"dvmcpribbon{Guid.NewGuid().ToString("N")[..8]}";

        if (!reuseSolution)
        {
            var publisher = publisherUniqueName
                            ?? await ResolvePublisherAsync(orgUrl, tableLogicalName, ct);
            await _solutions.CreateAsync(
                orgUrl, tempSolution, "DV MCP ribbon round-trip (temporary)", publisher, "1.0.0.0", ct);
        }

        var solutionDeleted = false;
        try
        {
            var metadataId = await GetTableMetadataIdAsync(orgUrl, tableLogicalName, ct);

            // DoNotIncludeSubcomponents keeps the export at the table shell + RibbonDiffXml scaffold.
            await _client.ExecuteActionAsync(orgUrl, "AddSolutionComponent", new
            {
                ComponentId = metadataId,
                ComponentType = EntityComponentType,
                SolutionUniqueName = tempSolution,
                AddRequiredComponents = false,
                DoNotIncludeSubcomponents = true
            }, ct);

            var export = await _solutions.ExportAsync(orgUrl, tempSolution, managed: false, ct: ct);
            if (string.IsNullOrEmpty(export.Base64Content))
            {
                throw new InvalidOperationException("ExportSolution returned no zip content.");
            }

            var patched = ReplaceRibbonDiffXml(
                Convert.FromBase64String(export.Base64Content), tableLogicalName, ribbonDiffXml, stripEntityInfo);

            var import = await _solutions.ImportAsync(
                orgUrl,
                zipBase64: Convert.ToBase64String(patched),
                overwriteUnmanaged: true,
                timeoutSeconds: importTimeoutSeconds,
                ct: ct);

            if (!import.Success)
            {
                throw new InvalidOperationException(
                    $"Solution import failed: {import.ErrorMessage} " +
                    (import.ComponentErrors.Count > 0
                        ? "Component errors: " + string.Join(" | ", import.ComponentErrors)
                        : string.Empty));
            }

            if (publish)
            {
                await PublishWithRetryAsync(orgUrl, tableLogicalName, ct);
            }

            // The ribbondiff row only materialises after the publish — verify against it, never against
            // the import result. The import job happily reports
            // <entityRibbon … result="success"> for a diff that was never written.
            var verified = publish && await DiffExistsAsync(orgUrl, tableLogicalName, customActionId, ct);

            // Collateral check: confirm the round-trip did not take the other buttons with it.
            var lost = new List<string>();
            if (publish)
            {
                foreach (var diffId in preserved)
                {
                    if (!await DiffExistsAsync(orgUrl, tableLogicalName, diffId, ct))
                    {
                        lost.Add(diffId);
                    }
                }
            }

            if (!reuseSolution)
            {
                // Deleting the temporary solution does NOT undo the imported ribbon diff — the diff now
                // lives in the Active layer. Use RemoveButtonAsync for that.
                solutionDeleted = await TryDeleteSolutionAsync(orgUrl, tempSolution, ct);
            }

            return new RibbonAddButtonResult(
                Success: verified || !publish,
                TableLogicalName: tableLogicalName,
                ButtonId: buttonId,
                CustomActionId: customActionId,
                CommandId: commandId,
                Location: location,
                RibbonDiffXml: ribbonDiffXml,
                TemporarySolutionUniqueName: tempSolution,
                TemporarySolutionDeleted: solutionDeleted,
                Published: publish,
                Verified: verified,
                ImportComponentErrors: import.ComponentErrors,
                Warning: BuildAddWarning(
                    publish, verified, customActionId, tableLogicalName, lost,
                    captionLanguage, languageSource),
                PreservedCustomActionIds: preserved,
                LostCustomActionIds: lost,
                CaptionLanguageCode: captionLanguage,
                CaptionLanguageSource: languageSource);
        }
        catch
        {
            if (!reuseSolution && !solutionDeleted)
            {
                await TryDeleteSolutionAsync(orgUrl, tempSolution, ct);
            }

            throw;
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Remove
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Remove a classic ribbon button by deleting its stored diff rows.
    /// <para>
    /// A solution import cannot do this: unmanaged import merges node-by-node, so an empty
    /// <c>&lt;CustomActions /&gt;</c> leaves the existing entry untouched even after
    /// <c>PublishAllXml</c>. Deleting the <c>ribbondiff</c> row does work — verified end-to-end against a
    /// live org: after the delete plus a publish of the table, the <c>&lt;CustomAction&gt;</c> and its
    /// <c>ribbontabtocommandmap</c> rows are gone from the compiled ribbon and the button stops
    /// rendering.
    /// </para>
    /// <para>
    /// One residue: the <c>&lt;CommandDefinition&gt;</c> can linger in the compiled ribbon even after its
    /// <c>ribboncommand</c> row is deleted and the org is fully published. It is inert — nothing places
    /// it on a tab any more — and it is reported as a warning rather than treated as a failure.
    /// </para>
    /// </summary>
    public async Task<RibbonRemoveButtonResult> RemoveButtonAsync(
        string orgUrl,
        string tableLogicalName,
        string buttonId,
        bool removeCommandDefinition = true,
        bool publish = true,
        CancellationToken ct = default)
    {
        var info = await GetAsync(orgUrl, tableLogicalName, includeManaged: true, ct: ct);

        // The label rows count as part of the button. Leaving them behind is not cosmetic: an orphaned
        // <LocLabel> stays in the table's diff and breaks the next ribbon_add_button on it.
        var candidates = info.CustomActions.Concat(info.LocLabels ?? []).ToList();

        var matches = candidates.Where(d => BelongsToButton(d, buttonId)).ToList();
        if (matches.Count == 0)
        {
            throw new InvalidOperationException(
                $"No ribbon diff of '{tableLogicalName}' matches '{buttonId}'. " +
                (candidates.Count == 0
                    ? "This table has no stored ribbon customisation at all."
                    : "Stored diff ids: " + string.Join(", ", candidates.Select(d => d.DiffId))));
        }

        var managed = matches.Where(m => m.IsManaged).ToList();
        if (managed.Count > 0)
        {
            throw new InvalidOperationException(
                "Refusing to delete managed ribbon diff rows (" +
                string.Join(", ", managed.Select(m => m.DiffId)) +
                "). A managed CustomAction can only be suppressed, by importing a solution that carries a " +
                "<HideCustomAction> for its location — deleting the row would put the org in a state the " +
                "owning solution cannot repair.");
        }

        var deletedDiffs = new List<string>();
        foreach (var match in matches)
        {
            await _client.DeleteAsync(orgUrl, $"{DiffSet}({match.RibbonDiffId})", ct);
            deletedDiffs.Add(match.DiffId);
        }

        // Only drop a CommandDefinition once no surviving CustomAction still points at it.
        var deletedCommands = new List<string>();
        var deletedRules = new List<string>();
        if (removeCommandDefinition)
        {
            var survivingXml = string.Concat(
                info.CustomActions.Except(matches).Select(d => d.Xml ?? string.Empty));

            foreach (var cmd in info.CommandDefinitions.Where(c => !c.IsManaged))
            {
                var referencedByRemoved = matches.Any(
                    m => (m.Xml ?? string.Empty).Contains($"\"{cmd.CommandId}\"", StringComparison.Ordinal));
                if (!referencedByRemoved)
                {
                    continue;
                }

                if (survivingXml.Contains($"\"{cmd.CommandId}\"", StringComparison.Ordinal))
                {
                    continue;
                }

                await _client.DeleteAsync(orgUrl, $"{CommandSet}({cmd.RibbonCommandId})", ct);
                deletedCommands.Add(cmd.CommandId);
            }

            // Rules the button declared for itself, once — several commands may have matched above.
            foreach (var rule in info.Rules.Where(
                         r => !r.IsManaged && r.RuleId.StartsWith(buttonId, StringComparison.Ordinal)))
            {
                if (deletedRules.Contains(rule.RuleId))
                {
                    continue;
                }

                await _client.DeleteAsync(orgUrl, $"{RuleSet}({rule.RibbonRuleId})", ct);
                deletedRules.Add(rule.RuleId);
            }
        }

        if (publish)
        {
            await PublishWithRetryAsync(orgUrl, tableLogicalName, ct);
        }

        var stillThere = new List<string>();
        foreach (var diffId in deletedDiffs)
        {
            if (await DiffExistsAsync(orgUrl, tableLogicalName, diffId, ct))
            {
                stillThere.Add(diffId);
            }
        }

        _logger.LogInformation(
            "Removed {DiffCount} ribbon diff row(s) and {CmdCount} command definition(s) from {Table}.",
            deletedDiffs.Count, deletedCommands.Count, tableLogicalName);

        return new RibbonRemoveButtonResult(
            Success: stillThere.Count == 0,
            TableLogicalName: tableLogicalName,
            ButtonId: buttonId,
            DeletedDiffIds: deletedDiffs,
            DeletedCommandIds: deletedCommands,
            DeletedRuleIds: deletedRules,
            Published: publish,
            Verified: stillThere.Count == 0,
            Warning: stillThere.Count > 0
                ? "Still present after the delete: " + string.Join(", ", stillThere)
                : deletedCommands.Count > 0
                    ? "The <CommandDefinition> may linger in the compiled ribbon returned by " +
                      "RetrieveEntityRibbon even though its ribboncommand row is gone. It is inert — no " +
                      "CustomAction places it on a tab any more."
                    : null);
    }

    private static string? BuildAddWarning(
        bool publish,
        bool verified,
        string customActionId,
        string tableLogicalName,
        IReadOnlyList<string> lost,
        int captionLanguage,
        string captionLanguageSource)
    {
        if (!publish)
        {
            return "publish=false — the button is stored but neither compiled nor verifiable yet. " +
                   $"Run publish_customizations for '{tableLogicalName}'.";
        }

        var parts = new List<string>();

        if (captionLanguageSource.StartsWith("a fallback", StringComparison.Ordinal))
        {
            parts.Add(
                $"The caption was written under language {captionLanguage} because nothing on this org " +
                "would say which language it uses. A 1033 caption does still render in a non-1033 org " +
                "(measured on invoice/1031), so this is a mismatch rather than a failure — but if your " +
                "users read something else, pass languageCode explicitly.");
        }

        if (!verified)
        {
            parts.Add(
                $"The import reported success but no ribbondiff row exists for '{customActionId}'. " +
                "Check the Location and the web resource reference — both fail silently.");
        }

        if (lost.Count > 0)
        {
            parts.Add(
                "These existing CustomActions did not survive the round-trip and have to be recreated: " +
                string.Join(", ", lost) +
                ". A non-empty <CustomActions> replaces the whole collection, so anything the import " +
                "omits is deleted.");
        }

        return parts.Count == 0 ? null : string.Join(" ", parts);
    }

    /// <summary>
    /// Publish the table, retrying the two failures that a freshly imported solution reliably produces.
    /// <para>
    /// <b>Concurrency.</b> A publish issued straight after <c>ImportSolutionAsync</c> reports terminal
    /// frequently comes back with HTTP 429 / <c>0x80071151 — "Cannot start the requested operation
    /// [Publish] because there is another [Import] running at this moment"</c>. The async operation is
    /// done; the solution installation is not. Since the round-trip imports and publishes back to back,
    /// this is the normal case rather than an edge case.
    /// </para>
    /// <para>
    /// <b>Transient server errors.</b> Publishing a table right after an import also throws the
    /// occasional HTTP 500 <c>0x80044150 — "Sql error: Generic SQL error … Sql Number: 10054"</c>
    /// (a reset connection). Retrying succeeds; failing the whole round-trip over it would leave a
    /// half-applied ribbon behind.
    /// </para>
    /// </summary>
    private async Task PublishWithRetryAsync(
        string orgUrl,
        string tableLogicalName,
        CancellationToken ct)
    {
        const int maxAttempts = 8;

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await _publish.PublishAsync(orgUrl, entities: new[] { tableLogicalName }, ct: ct);
                return;
            }
            catch (HttpRequestException ex) when (attempt < maxAttempts && IsTransientPublishFailure(ex))
            {
                var delay = TimeSpan.FromSeconds(Math.Min(60, 5 * Math.Pow(2, attempt - 1)));
                _logger.LogInformation(
                    "Publish of {Table} failed transiently (attempt {Attempt}/{Max}); retrying in {Delay}s. {Message}",
                    tableLogicalName, attempt, maxAttempts, delay.TotalSeconds, ex.Message);
                await Task.Delay(delay, ct);
            }
        }
    }

    /// <summary>
    /// True for the publish failures that are worth retrying: another solution operation still running,
    /// and the transient SQL/gateway errors Dataverse emits under that same load.
    /// </summary>
    public static bool IsTransientPublishFailure(HttpRequestException ex)
    {
        if (ex.StatusCode is System.Net.HttpStatusCode.TooManyRequests
            or System.Net.HttpStatusCode.ServiceUnavailable
            or System.Net.HttpStatusCode.GatewayTimeout)
        {
            return true;
        }

        return ex.Message.Contains("0x80071151", StringComparison.Ordinal)
               || ex.Message.Contains("because there is another", StringComparison.OrdinalIgnoreCase)
               // 0x80044150 — "Sql error: Generic SQL error", typically Sql Number 10054 (reset connection).
               || ex.Message.Contains("0x80044150", StringComparison.Ordinal)
               || ex.Message.Contains("Generic SQL error", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Whether a stored diff row belongs to <paramref name="buttonId"/>.
    /// <para>
    /// Label rows are matched by prefix rather than by a fixed suffix list: a <c>&lt;LocLabel&gt;</c> id
    /// is always <c>&lt;controlId&gt;.&lt;attribute&gt;</c> (<c>.LabelText</c>, <c>.Alt</c>,
    /// <c>.ToolTipTitle</c>, …), and the set of attributes a designer may localize is open-ended.
    /// </para>
    /// </summary>
    public static bool BelongsToButton(RibbonDiffEntry entry, string buttonId) =>
        string.Equals(entry.DiffId, buttonId, StringComparison.OrdinalIgnoreCase)
        || string.Equals(entry.DiffId, $"{buttonId}.CustomAction", StringComparison.OrdinalIgnoreCase)
        || (entry.Xml ?? string.Empty).Contains($"Id=\"{buttonId}\"", StringComparison.OrdinalIgnoreCase)
        || (IsLocLabel(entry)
            && entry.DiffId.StartsWith($"{buttonId}.", StringComparison.OrdinalIgnoreCase));

    private static bool IsLocLabel(RibbonDiffEntry entry) =>
        entry.DiffType == (int)RibbonDiffType.LocalizedLabel
        || string.Equals(RootElementName(entry.Xml), "LocLabel", StringComparison.Ordinal);

    /// <summary>
    /// The language to write a new caption in, and where that answer came from.
    /// <para>
    /// The table's own buttons are the best source there is: whatever language <b>they</b> are captioned
    /// in is the language this org's users read. That also happens to be the rule this document preaches
    /// for verifying a button — compare against a reference on the same table.
    /// </para>
    /// <para>
    /// A 1033 caption does render in a 1031 org — measured on <c>invoice</c>, caption and icon both — so
    /// a mismatch is untidy rather than fatal. What is fatal is not knowing: the chosen code and its
    /// source are reported back with the result instead of being swallowed.
    /// </para>
    /// </summary>
    public async Task<(int LanguageCode, string Source)> ResolveCaptionLanguageAsync(
        RibbonInfo existing,
        string orgUrl,
        CancellationToken ct = default)
    {
        var fromTable = LanguageCodeOfExistingCaptions(existing);
        if (fromTable is { } tableCode)
        {
            return (tableCode, $"the LocLabels already on '{existing.TableLogicalName}'");
        }

        try
        {
            var raw = await _client.GetRawAsync(
                orgUrl, "organizations?$select=languagecode&$top=1", ct: ct);
            foreach (var item in EnumerateValue(raw))
            {
                var code = item.GetInt32OrZero("languagecode");
                if (code > 0)
                {
                    return (code, "the org's base language");
                }
            }

            _logger.LogWarning("The organization row carried no languagecode; falling back to 1033.");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Could not read the org's base language; falling back to 1033.");
        }

        return (1033, "a fallback — neither the table nor the organization row answered");
    }

    /// <summary>The language code most of a table's existing captions use, or <c>null</c> if it has none.</summary>
    public static int? LanguageCodeOfExistingCaptions(RibbonInfo info) =>
        MostCommonLanguageCode(info.LocLabels);

    private static int? MostCommonLanguageCode(IReadOnlyList<RibbonDiffEntry>? locLabels)
    {
        var codes = (locLabels ?? [])
            .Select(l => TryParseElement(l.Xml))
            .Where(el => el is not null)
            .SelectMany(el => el!.Descendants("Title"))
            .Select(t => t.Attribute("languagecode")?.Value)
            .Where(v => int.TryParse(v, out var parsed) && parsed > 0)
            .Select(int.Parse!)
            .ToList();

        if (codes.Count == 0)
        {
            return null;
        }

        return codes.GroupBy(c => c).OrderByDescending(g => g.Count()).First().Key;
    }

    private static XElement? TryParseElement(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml))
        {
            return null;
        }

        try
        {
            return XElement.Parse(xml);
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
    }

    private async Task<bool> DiffExistsAsync(
        string orgUrl,
        string tableLogicalName,
        string diffId,
        CancellationToken ct)
    {
        var filter = $"entity eq '{tableLogicalName.Replace("'", "''")}' and diffid eq '{diffId.Replace("'", "''")}'";
        var raw = await _client.GetRawAsync(
            orgUrl, $"{DiffSet}?$filter={Uri.EscapeDataString(filter)}&$select=ribbondiffid&$top=1", ct: ct);
        return EnumerateValue(raw).Any();
    }

    // ---------------------------------------------------------------------------------------------
    // XML / zip plumbing (static so it is unit-testable without a live org)
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Build the <c>&lt;RibbonDiffXml&gt;</c> document for a single JavaScript button.
    /// <para>
    /// Captions are written the way the Ribbon Workbench writes them: one <c>&lt;LocLabel&gt;</c> node per
    /// string, referenced from the button as <c>$LocLabels:&lt;id&gt;</c>. A literal <c>LabelText</c> is
    /// stored and reported back correctly by both <c>ribbon_get</c> and <c>RetrieveEntityRibbon</c>, and
    /// the Unified Interface still does not draw the button — verified on <c>invoice</c>, where the same
    /// button appeared the moment its caption moved into a LocLabel.
    /// </para>
    /// </summary>
    public static string BuildRibbonDiffXml(
        string buttonId,
        string customActionId,
        string commandId,
        string location,
        string label,
        string webResourceName,
        string functionName,
        IReadOnlyList<RibbonParameter> parameters,
        int sequence = 41,
        string? imageWebResource = null,
        string? modernImage = null,
        string? enableRule = null,
        string? tooltipTitle = null,
        string? tooltipDescription = null,
        string? templateAlias = "o1",
        RibbonInfo? preserve = null,
        int languageCode = 1033)
    {
        var (labels, labelReferences) = BuildLocLabels(
            buttonId, label, tooltipTitle ?? label, tooltipDescription ?? label, languageCode);

        var button = new XElement("Button",
            new XAttribute("Id", buttonId),
            new XAttribute("Command", commandId),
            new XAttribute("LabelText", labelReferences.LabelText),
            // Alt is the accessible name and points at the caption, as on every hand-built button.
            new XAttribute("Alt", labelReferences.LabelText),
            new XAttribute("ToolTipTitle", labelReferences.ToolTipTitle),
            new XAttribute("ToolTipDescription", labelReferences.ToolTipDescription));

        if (!string.IsNullOrWhiteSpace(templateAlias))
        {
            button.Add(new XAttribute("TemplateAlias", templateAlias));
        }

        if (!string.IsNullOrWhiteSpace(imageWebResource))
        {
            var reference = imageWebResource.StartsWith("$webresource:", StringComparison.OrdinalIgnoreCase)
                ? imageWebResource
                : $"$webresource:{imageWebResource}";
            button.Add(new XAttribute("Image16by16", reference));
            button.Add(new XAttribute("Image32by32", reference));
        }

        if (!string.IsNullOrWhiteSpace(modernImage))
        {
            button.Add(new XAttribute("ModernImage", modernImage));
        }

        var (enableRuleElement, enableRuleReference) = BuildEnableRule(buttonId, enableRule);

        var jsFunction = new XElement("JavaScriptFunction",
            new XAttribute("FunctionName", functionName),
            new XAttribute("Library", webResourceName.StartsWith("$webresource:", StringComparison.OrdinalIgnoreCase)
                ? webResourceName
                : $"$webresource:{webResourceName}"));

        foreach (var p in parameters)
        {
            jsFunction.Add(new XElement(p.ElementName, new XAttribute("Value", p.Value)));
        }

        var enableRulesInCommand = new XElement("EnableRules");
        if (enableRuleReference is not null)
        {
            enableRulesInCommand.Add(new XElement("EnableRule", new XAttribute("Id", enableRuleReference)));
        }

        var ruleDefinitionEnableRules = new XElement("EnableRules");
        if (enableRuleElement is not null)
        {
            ruleDefinitionEnableRules.Add(enableRuleElement);
        }

        var newCustomAction = new XElement("CustomAction",
            new XAttribute("Id", customActionId),
            new XAttribute("Location", location),
            new XAttribute("Sequence", sequence),
            new XElement("CommandUIDefinition", button));

        var newCommandDefinition = new XElement("CommandDefinition",
            new XAttribute("Id", commandId),
            enableRulesInCommand,
            new XElement("DisplayRules"),
            new XElement("Actions", jsFunction));

        // Everything the table already has, minus anything this button is about to redefine. Omitting
        // an existing node from a non-empty section deletes it.
        var (existingActions, existingCommands, existingRules, existingLocLabels) = ExplodePreserved(preserve);

        var customActions = existingActions
            .Where(e => !IdEquals(e, customActionId))
            .Append(newCustomAction)
            .ToList();

        var commandDefinitions = existingCommands
            .Where(e => !IdEquals(e, commandId))
            .Append(newCommandDefinition)
            .ToList();

        var rules = existingRules.ToList();
        if (enableRuleElement is not null)
        {
            rules.RemoveAll(e => IdEquals(e, enableRuleReference!));
            rules.Add(enableRuleElement);
        }

        // The labels of every other button have to be re-sent too: a non-empty <LocLabels> replaces the
        // whole collection, exactly as <CustomActions> does.
        var locLabels = existingLocLabels
            .Where(e => !labels.Any(l => IdEquals(e, l.Attribute("Id")!.Value)))
            .Concat(labels)
            .ToList();

        return ComposeRibbonDiffXml(customActions, commandDefinitions, rules, locLabels).ToString();
    }

    /// <summary>
    /// Build the <c>&lt;LocLabel&gt;</c> nodes for a button's captions and the <c>$LocLabels:</c>
    /// references that point at them.
    /// <para>
    /// The reference carries no trailing semicolon — that is what every working button on a live org
    /// looks like, and what the Ribbon Workbench writes.
    /// </para>
    /// </summary>
    public static (List<XElement> Labels, (string LabelText, string ToolTipTitle, string ToolTipDescription) References)
        BuildLocLabels(
            string buttonId,
            string label,
            string tooltipTitle,
            string tooltipDescription,
            int languageCode)
    {
        XElement Label(string suffix, string text) =>
            new("LocLabel",
                new XAttribute("Id", $"{buttonId}.{suffix}"),
                new XElement("Titles",
                    new XElement("Title",
                        new XAttribute("languagecode", languageCode),
                        new XAttribute("description", text))));

        var labels = new List<XElement>
        {
            Label("LabelText", label),
            Label("ToolTipTitle", tooltipTitle),
            Label("ToolTipDescription", tooltipDescription)
        };

        var references = (
            LabelText: $"$LocLabels:{buttonId}.LabelText",
            ToolTipTitle: $"$LocLabels:{buttonId}.ToolTipTitle",
            ToolTipDescription: $"$LocLabels:{buttonId}.ToolTipDescription");

        return (labels, references);
    }

    /// <summary>
    /// Assemble a <c>&lt;RibbonDiffXml&gt;</c> document from node elements, putting each rule into the
    /// section its element name calls for.
    /// <para>
    /// Sections are emitted even when empty, because an empty section is what tells Dataverse "leave
    /// this collection alone" — the same property that makes an empty <c>&lt;CustomActions /&gt;</c>
    /// unable to delete anything.
    /// </para>
    /// </summary>
    public static XElement ComposeRibbonDiffXml(
        IEnumerable<XElement> customActions,
        IEnumerable<XElement> commandDefinitions,
        IEnumerable<XElement> rules,
        IEnumerable<XElement>? locLabels = null)
    {
        var ruleList = rules.ToList();

        XElement Bucket(string sectionName, string elementName) =>
            new(sectionName, ruleList.Where(r => r.Name.LocalName == elementName).Cast<object>().ToArray());

        return new XElement("RibbonDiffXml",
            new XElement("CustomActions", customActions.Cast<object>().ToArray()),
            new XElement("Templates",
                new XElement("RibbonTemplates", new XAttribute("Id", "Mscrm.Templates"), string.Empty)),
            new XElement("CommandDefinitions", commandDefinitions.Cast<object>().ToArray()),
            new XElement("RuleDefinitions",
                Bucket("TabDisplayRules", "TabDisplayRule"),
                Bucket("DisplayRules", "DisplayRule"),
                Bucket("EnableRules", "EnableRule")),
            new XElement("LocLabels", (locLabels ?? []).Cast<object>().ToArray()));
    }

    /// <summary>
    /// Turn the stored rows of a <see cref="RibbonInfo"/> back into XML elements, dropping the managed
    /// ones (they belong to their owning solution and must not be re-sent from an unmanaged layer) and
    /// anything unparseable.
    /// </summary>
    private static (List<XElement> Actions, List<XElement> Commands, List<XElement> Rules, List<XElement> LocLabels)
        ExplodePreserved(RibbonInfo? preserve)
    {
        var actions = new List<XElement>();
        var commands = new List<XElement>();
        var rules = new List<XElement>();
        var locLabels = new List<XElement>();

        if (preserve is null)
        {
            return (actions, commands, rules, locLabels);
        }

        // Second line of defence behind the sorting in GetAsync: the importer demands a Location
        // attribute of every child of <CustomActions>, and a <LocLabel> has none.
        foreach (var entry in preserve.CustomActions.Where(e => !e.IsManaged))
        {
            if (TryParse(entry.Xml) is { } el && IsCustomActionsChild(el.Name.LocalName))
            {
                actions.Add(el);
            }
        }

        foreach (var entry in (preserve.LocLabels ?? []).Where(e => !e.IsManaged))
        {
            if (TryParse(entry.Xml) is { } el)
            {
                locLabels.Add(el);
            }
        }

        foreach (var entry in preserve.CommandDefinitions.Where(e => !e.IsManaged))
        {
            if (TryParse(entry.Xml) is { } el)
            {
                commands.Add(el);
            }
        }

        foreach (var entry in preserve.Rules.Where(e => !e.IsManaged))
        {
            if (TryParse(entry.Xml) is { } el)
            {
                rules.Add(el);
            }
        }

        return (actions, commands, rules, locLabels);

        static XElement? TryParse(string? xml)
        {
            if (string.IsNullOrWhiteSpace(xml))
            {
                return null;
            }

            try
            {
                return XElement.Parse(xml);
            }
            catch (System.Xml.XmlException)
            {
                return null;
            }
        }
    }

    private static bool IdEquals(XElement element, string id) =>
        string.Equals(element.Attribute("Id")?.Value, id, StringComparison.OrdinalIgnoreCase);

    // ModernImage="$webresource:….svg" used to be rejected here, on the theory that it made the button
    // vanish. It does not: on invoice, six buttons carry exactly that and all of them render. The
    // original disappearance had a different cause — an empty caption, which gives the modern command
    // bar nothing to draw.

    /// <summary>
    /// Translate the <c>enableRule</c> argument into the <c>&lt;EnableRule&gt;</c> element to declare and
    /// the id to reference from the command.
    /// <para>
    /// Shortcuts: <c>OneSelected</c> (exactly one row), <c>AtLeastOneSelected</c>,
    /// <c>SelectionCountRule:min-max</c>. Anything starting with <c>&lt;</c> is taken as a literal
    /// <c>&lt;EnableRule&gt;</c> fragment. This is the classic-ribbon equivalent of a modern command's
    /// Power Fx visibility formula — and, unlike that one, it needs no component library and no app.
    /// </para>
    /// </summary>
    public static (XElement? Element, string? ReferenceId) BuildEnableRule(string buttonId, string? spec)
    {
        if (string.IsNullOrWhiteSpace(spec))
        {
            return (null, null);
        }

        var trimmed = spec.Trim();

        if (trimmed.StartsWith("<", StringComparison.Ordinal))
        {
            var element = XElement.Parse(trimmed);
            if (element.Name.LocalName != "EnableRule")
            {
                throw new ArgumentException(
                    "A literal enableRule fragment must be a single <EnableRule Id=\"…\">…</EnableRule> element.",
                    nameof(spec));
            }

            var id = element.Attribute("Id")?.Value
                     ?? throw new ArgumentException("The <EnableRule> fragment needs an Id attribute.", nameof(spec));
            return (element, id);
        }

        int minimum;
        int? maximum;

        if (trimmed.Equals("OneSelected", StringComparison.OrdinalIgnoreCase))
        {
            minimum = 1;
            maximum = 1;
        }
        else if (trimmed.Equals("AtLeastOneSelected", StringComparison.OrdinalIgnoreCase))
        {
            minimum = 1;
            maximum = null;
        }
        else if (trimmed.StartsWith("SelectionCountRule:", StringComparison.OrdinalIgnoreCase))
        {
            var range = trimmed["SelectionCountRule:".Length..].Split('-', StringSplitOptions.TrimEntries);
            if (range.Length == 0 || !int.TryParse(range[0], out minimum))
            {
                throw new ArgumentException(
                    "SelectionCountRule expects 'SelectionCountRule:<min>' or 'SelectionCountRule:<min>-<max>'.",
                    nameof(spec));
            }

            maximum = range.Length > 1 && int.TryParse(range[1], out var max) ? max : null;
        }
        else
        {
            throw new ArgumentException(
                $"Unknown enableRule '{spec}'. Use 'OneSelected', 'AtLeastOneSelected', " +
                "'SelectionCountRule:<min>[-<max>]', or a literal <EnableRule …> fragment.",
                nameof(spec));
        }

        var ruleId = $"{buttonId}.EnableRule";
        var selectionCount = new XElement("SelectionCountRule",
            new XAttribute("AppliesTo", "SelectedEntity"),
            new XAttribute("Minimum", minimum));
        if (maximum.HasValue)
        {
            selectionCount.Add(new XAttribute("Maximum", maximum.Value));
        }

        selectionCount.Add(new XAttribute("Default", "false"));

        return (new XElement("EnableRule", new XAttribute("Id", ruleId), selectionCount), ruleId);
    }

    /// <summary>
    /// Parse the <c>parameters</c> argument of <c>ribbon_add_button</c>. Classic ribbons name their
    /// parameters instead of numbering them, so the accepted entries are the <c>CrmParameter</c> names
    /// (<c>PrimaryControl</c>, <c>SelectedControlSelectedItemIds</c>, …) plus the literal forms
    /// <c>String:abc</c>, <c>Bool:true</c> and <c>Int:5</c>.
    /// </summary>
    public static IReadOnlyList<RibbonParameter> ParseParameterSpec(string? spec)
    {
        if (string.IsNullOrWhiteSpace(spec))
        {
            return Array.Empty<RibbonParameter>();
        }

        var result = new List<RibbonParameter>();
        foreach (var raw in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = raw.IndexOf(':');
            if (separator < 0)
            {
                result.Add(new RibbonParameter("CrmParameter", raw));
                continue;
            }

            var kind = raw[..separator].Trim();
            var value = raw[(separator + 1)..].Trim();

            var element = kind.ToLowerInvariant() switch
            {
                "string" => "StringParameter",
                "bool" or "boolean" => "BoolParameter",
                "int" or "integer" => "IntParameter",
                "decimal" => "DecimalParameter",
                "crm" or "crmparameter" => "CrmParameter",
                _ => throw new ArgumentException(
                    $"Unknown parameter kind '{kind}' in '{raw}'. Use a bare CrmParameter name, or " +
                    "String:/Bool:/Int:/Decimal: for literals.")
            };

            result.Add(new RibbonParameter(element, value));
        }

        return result;
    }

    /// <summary>
    /// Swap the <c>&lt;RibbonDiffXml&gt;</c> of one table inside an exported solution zip and repack it.
    /// <para>
    /// Done with <see cref="ZipArchive"/> deliberately. PowerShell's <c>Compress-Archive</c> treats the
    /// square brackets of <c>[Content_Types].xml</c> as a wildcard and drops the file without a word;
    /// the resulting archive is not a valid solution.
    /// </para>
    /// All entries stay at the archive root, which is where <c>customizations.xml</c>,
    /// <c>solution.xml</c> and <c>[Content_Types].xml</c> have to be.
    /// </summary>
    public static byte[] ReplaceRibbonDiffXml(
        byte[] solutionZip,
        string tableLogicalName,
        string ribbonDiffXml,
        bool stripEntityInfo = true)
    {
        using var source = new ZipArchive(new MemoryStream(solutionZip), ZipArchiveMode.Read);

        var customizationsEntry = source.Entries.FirstOrDefault(
                                      e => e.FullName.Equals("customizations.xml", StringComparison.OrdinalIgnoreCase))
                                  ?? throw new InvalidOperationException(
                                      "The exported solution contains no customizations.xml at the archive root.");

        string customizations;
        using (var reader = new StreamReader(customizationsEntry.Open(), Encoding.UTF8))
        {
            customizations = reader.ReadToEnd();
        }

        var patched = PatchCustomizationsXml(customizations, tableLogicalName, ribbonDiffXml, stripEntityInfo);

        var output = new MemoryStream();
        using (var target = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var entry in source.Entries)
            {
                var copy = target.CreateEntry(entry.FullName, CompressionLevel.Optimal);
                using var destination = copy.Open();

                if (entry.FullName.Equals("customizations.xml", StringComparison.OrdinalIgnoreCase))
                {
                    var bytes = new UTF8Encoding(false).GetBytes(patched);
                    destination.Write(bytes, 0, bytes.Length);
                }
                else
                {
                    using var input = entry.Open();
                    input.CopyTo(destination);
                }
            }
        }

        return output.ToArray();
    }

    /// <summary>
    /// Replace the <c>&lt;RibbonDiffXml&gt;</c> element of one <c>&lt;Entity&gt;</c> and drop its
    /// <c>&lt;EntityInfo&gt;</c> block.
    /// <para>
    /// Removing <c>&lt;EntityInfo&gt;</c> is not an optimisation, it is a correctness fix: the export
    /// carries the table's complete property block (ownership, auditing, duplicate detection, mobile
    /// visibility, …), and leaving it in means every ribbon import writes all of those back. An import
    /// carrying only <c>&lt;Name&gt;</c> plus <c>&lt;RibbonDiffXml&gt;</c> works and changes nothing else
    /// — verified against a live org.
    /// </para>
    /// </summary>
    public static string PatchCustomizationsXml(
        string customizationsXml,
        string tableLogicalName,
        string ribbonDiffXml,
        bool stripEntityInfo = true)
    {
        var doc = XDocument.Parse(customizationsXml);

        var entity = doc.Descendants("Entity")
                         .FirstOrDefault(e => string.Equals(
                             e.Element("Name")?.Value, tableLogicalName, StringComparison.OrdinalIgnoreCase))
                     ?? throw new InvalidOperationException(
                         $"The exported customizations.xml contains no <Entity> for '{tableLogicalName}'.");

        if (stripEntityInfo)
        {
            entity.Element("EntityInfo")?.Remove();
        }

        var replacement = XElement.Parse(ribbonDiffXml);
        var existing = entity.Element("RibbonDiffXml");
        if (existing is null)
        {
            entity.Add(replacement);
        }
        else
        {
            existing.ReplaceWith(replacement);
        }

        return doc.ToString(SaveOptions.None);
    }

    /// <summary>Reassemble a readable RibbonDiffXml document out of the stored rows.</summary>
    public static string AssembleRibbonDiffXml(
        IReadOnlyList<RibbonDiffEntry> diffs,
        IReadOnlyList<RibbonCommandEntry> commands,
        IReadOnlyList<RibbonRuleEntry> rules,
        IReadOnlyList<RibbonDiffEntry>? locLabels = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<RibbonDiffXml>");

        sb.AppendLine("  <CustomActions>");
        foreach (var diff in diffs.Where(d => !string.IsNullOrWhiteSpace(d.Xml)))
        {
            sb.Append("    ").AppendLine(diff.Xml!.Trim());
        }

        sb.AppendLine("  </CustomActions>");

        sb.AppendLine("  <CommandDefinitions>");
        foreach (var command in commands.Where(c => !string.IsNullOrWhiteSpace(c.Xml)))
        {
            sb.Append("    ").AppendLine(command.Xml!.Trim());
        }

        sb.AppendLine("  </CommandDefinitions>");

        sb.AppendLine("  <RuleDefinitions>");
        foreach (var rule in rules.Where(r => !string.IsNullOrWhiteSpace(r.Xml)))
        {
            sb.Append("    ").AppendLine(rule.Xml!.Trim());
        }

        sb.AppendLine("  </RuleDefinitions>");

        sb.AppendLine("  <LocLabels>");
        foreach (var locLabel in (locLabels ?? []).Where(l => !string.IsNullOrWhiteSpace(l.Xml)))
        {
            sb.Append("    ").AppendLine(locLabel.Xml!.Trim());
        }

        sb.AppendLine("  </LocLabels>");
        sb.Append("</RibbonDiffXml>");

        return sb.ToString();
    }

    // ---------------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------------

    public async Task<Guid> GetTableMetadataIdAsync(
        string orgUrl,
        string tableLogicalName,
        CancellationToken ct = default)
    {
        var url = $"api/data/v9.2/EntityDefinitions(LogicalName='{tableLogicalName}')?$select=MetadataId";
        var raw = await _client.GetRawAsync(orgUrl, url, ct: ct);
        var id = JsonDocument.Parse(raw).RootElement.TryGetGuid("MetadataId");
        if (id == Guid.Empty)
        {
            throw new InvalidOperationException($"Could not resolve the MetadataId of table '{tableLogicalName}'.");
        }

        return id;
    }

    private async Task<string> ResolvePublisherAsync(
        string orgUrl,
        string tableLogicalName,
        CancellationToken ct)
    {
        var underscore = tableLogicalName.IndexOf('_');
        if (underscore <= 0)
        {
            throw new InvalidOperationException(
                $"Cannot infer a publisher for the out-of-the-box table '{tableLogicalName}'. " +
                "Pass publisherUniqueName explicitly.");
        }

        var prefix = tableLogicalName[..underscore];
        var filter = $"customizationprefix eq '{prefix.Replace("'", "''")}'";
        var raw = await _client.GetRawAsync(
            orgUrl,
            $"api/data/v9.2/publishers?$filter={Uri.EscapeDataString(filter)}&$select=uniquename&$top=1",
            ct: ct);

        foreach (var item in EnumerateValue(raw))
        {
            var name = item.GetStringOrNull("uniquename");
            if (!string.IsNullOrWhiteSpace(name))
            {
                return name!;
            }
        }

        throw new InvalidOperationException(
            $"No publisher with customization prefix '{prefix}' exists. Pass publisherUniqueName explicitly.");
    }

    private async Task<bool> TryDeleteSolutionAsync(string orgUrl, string uniqueName, CancellationToken ct)
    {
        try
        {
            var filter = $"uniquename eq '{uniqueName.Replace("'", "''")}'";
            var raw = await _client.GetRawAsync(
                orgUrl,
                $"api/data/v9.2/solutions?$filter={Uri.EscapeDataString(filter)}&$select=solutionid&$top=1",
                ct: ct);

            foreach (var item in EnumerateValue(raw))
            {
                var id = item.TryGetGuid("solutionid");
                if (id != Guid.Empty)
                {
                    await _client.DeleteAsync(orgUrl, $"api/data/v9.2/solutions({id})", ct);
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not delete the temporary solution '{Solution}'.", uniqueName);
        }

        return false;
    }

    private static IEnumerable<JsonElement> EnumerateValue(string raw)
    {
        using var doc = JsonDocument.Parse(raw);
        if (!doc.RootElement.TryGetProperty("value", out var items) || items.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var item in items.EnumerateArray())
        {
            yield return item.Clone();
        }
    }

    private static bool IsTrue(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.True;

    private static string EnumName<T>(int value) where T : struct, Enum =>
        Enum.IsDefined(typeof(T), value) ? ((T)(object)value).ToString()! : $"Value{value}";
}
