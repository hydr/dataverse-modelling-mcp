namespace Dataverse.Core.Services;

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Dataverse.Core.Clients;
using Dataverse.Core.Json;
using Dataverse.Core.Models;
using Microsoft.Extensions.Logging;

/// <summary>
/// Whether a classic ribbon change actually reaches the user's browser — and the repairs for when it
/// does not.
/// <para>
/// A ribbon lives in three places, and each can be wrong while the others are right:
/// </para>
/// <list type="number">
/// <item><b>Stored</b> — one <c>ribboncommand</c> / <c>ribbondiff</c> / <c>ribbonrule</c> row per node
/// <i>per solution layer</i>, keyed by <c>solutionid</c>. The row whose solution is <c>Active</c> is the
/// unmanaged layer, and it always wins. An unmanaged layer left over on an environment that receives
/// managed solutions silently overrides every later deployment.</item>
/// <item><b>Compiled</b> — <c>RetrieveEntityRibbon</c>, the out-of-the-box ribbon with every layer
/// applied. It follows the stored rows after a publish of the table.</item>
/// <item><b>Delivered</b> — <c>GetClientMetadata</c>, what an app's client actually receives. It is
/// cached on the server per table and rebuilt when the <b>table's metadata version</b> changes. A
/// ribbon-only import plus publish does not always change it, so the client can keep receiving the old
/// command after the database and the compiled ribbon are already correct.</item>
/// </list>
/// <para>
/// None of this is visible in a successful import or publish. All of it was observed on a production
/// environment, where it took three separate repairs before a changed button reached the users.
/// </para>
/// </summary>
public sealed class RibbonDeliveryService
{
    /// <summary>Unique name of the solution that holds the unmanaged layer.</summary>
    public const string UnmanagedSolutionName = "Active";

    private readonly DataverseHttpClient _client;
    private readonly RibbonService _ribbons;
    private readonly SolutionService _solutions;
    private readonly ILogger<RibbonDeliveryService> _logger;

    public RibbonDeliveryService(
        DataverseHttpClient client,
        RibbonService ribbons,
        SolutionService solutions,
        ILogger<RibbonDeliveryService> logger)
    {
        _client = client;
        _ribbons = ribbons;
        _solutions = solutions;
        _logger = logger;
    }

    // ---------------------------------------------------------------------------------------------
    // Diagnose
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Compare a table's ribbon commands across stored layers, the compiled ribbon and — optionally —
    /// what each app's client receives, and name the first level that is wrong.
    /// </summary>
    /// <param name="commandIds">Commands to check; all stored commands of the table when empty.</param>
    /// <param name="appModuleId">One app to check delivery for; every app containing the table when null.</param>
    /// <param name="checkDelivered">
    /// Read the client metadata. One call per app, typically 5–15 MB each, so it can be switched off
    /// when only the stored and compiled levels matter.
    /// </param>
    public async Task<RibbonDiagnosisResult> DiagnoseAsync(
        string orgUrl,
        string tableLogicalName,
        IReadOnlyList<string>? commandIds = null,
        Guid? appModuleId = null,
        bool checkDelivered = true,
        CancellationToken ct = default)
    {
        var notes = new List<string>();
        var stored = await ReadStoredAsync(orgUrl, tableLogicalName, ct);

        var wanted = commandIds is { Count: > 0 }
            ? commandIds.ToList()
            : stored.Commands.Select(c => c.Id).Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal).ToList();

        if (wanted.Count == 0)
        {
            notes.Add("The table has no stored ribbon commands — nothing to diagnose.");
        }

        var compiled = XDocument.Parse(await _ribbons.RetrieveCompiledRibbonAsync(orgUrl, tableLogicalName, ct));

        var delivered = new List<(Guid AppId, string? AppName, Guid FormId, DeliveredRibbon Ribbon)>();
        if (checkDelivered && wanted.Count > 0)
        {
            delivered = await ReadDeliveredAsync(orgUrl, tableLogicalName, appModuleId, notes, ct);
        }

        var diagnoses = new List<RibbonCommandDiagnosis>();
        foreach (var commandId in wanted)
        {
            var layers = stored.Commands
                .Where(c => string.Equals(c.Id, commandId, StringComparison.Ordinal))
                .ToList();

            var compiledCommand = FindCompiledCommand(compiled, commandId);
            var compiledSignature = compiledCommand is null ? null : SignatureFromXml(compiledCommand);

            diagnoses.Add(Diagnose(commandId, layers, compiledSignature, delivered));
        }

        return new RibbonDiagnosisResult(
            OrgUrl: orgUrl,
            TableLogicalName: tableLogicalName,
            Commands: diagnoses,
            DeliveredChecked: checkDelivered,
            Notes: notes,
            Summary: Summarise(diagnoses, checkDelivered));
    }

    /// <summary>
    /// The verdict for one command. Pure, so the decision rules are testable without an environment.
    /// </summary>
    public static RibbonCommandDiagnosis Diagnose(
        string commandId,
        IReadOnlyList<StoredNode> layers,
        RibbonActionSignature? compiledSignature,
        IReadOnlyList<(Guid AppId, string? AppName, Guid FormId, DeliveredRibbon Ribbon)> delivered)
    {
        var unmanaged = layers.FirstOrDefault(l => l.IsUnmanagedLayer);
        var managed = layers.Where(l => !l.IsUnmanagedLayer).ToList();

        var overrides = unmanaged is not null
                        && managed.Count > 0
                        && managed.All(m => !m.Signature.SameAs(unmanaged.Signature));

        RibbonActionSignature? effective = unmanaged?.Signature;
        if (effective is null)
        {
            var distinct = managed.Select(m => m.Signature.ToString()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (distinct.Count == 1)
            {
                effective = managed[0].Signature;
            }
        }

        var compiledMatches = compiledSignature is not null
                              && (effective is null || compiledSignature.SameAs(effective));

        var deliveredEntries = delivered
            .Select(d =>
            {
                d.Ribbon.Commands.TryGetValue(commandId, out var signature);
                return new RibbonDeliveredCommand(
                    AppModuleId: d.AppId,
                    AppModuleName: d.AppName,
                    FormId: d.FormId,
                    Context: d.Ribbon.Context,
                    RibbonVersionNumber: d.Ribbon.VersionNumber,
                    Signature: signature?.ToString(),
                    MatchesCompiled: signature is not null && compiledSignature is not null && signature.SameAs(compiledSignature));
            })
            .Where(d => d.Signature is not null)
            .ToList();

        string verdict;
        string? repair;
        if (overrides)
        {
            verdict = "stored";
            repair = "The unmanaged layer differs from every managed copy, so no managed deployment can take " +
                     "effect. If a managed solution holds the intended version, run ribbon_sync_unmanaged_layer " +
                     "with sourceSolution set to it (managed layers here: " +
                     string.Join(", ", managed.Select(m => m.SolutionUniqueName).Distinct()) + ").";
        }
        else if (compiledSignature is null)
        {
            verdict = "unused";
            repair = "Not in the compiled ribbon — no button places this command. Nothing renders it, so " +
                     "there is nothing to deliver.";
        }
        else if (!compiledMatches)
        {
            verdict = "compiled";
            repair = "The compiled ribbon does not follow the stored rows yet: publish the table " +
                     "(publish_customizations).";
        }
        else if (deliveredEntries.Any(d => !d.MatchesCompiled))
        {
            verdict = "delivered";
            repair = "The client still receives an older command than the compiled ribbon. Run " +
                     "ribbon_refresh_client_metadata for the table — publishing the ribbon or the app does " +
                     "not rebuild it.";
        }
        else
        {
            verdict = "ok";
            repair = null;
        }

        return new RibbonCommandDiagnosis(
            CommandId: commandId,
            StoredLayers: layers
                .Select(l => new RibbonCommandLayer(l.SolutionUniqueName, !l.IsUnmanagedLayer, l.Signature.ToString()))
                .ToList(),
            UnmanagedOverridesManaged: overrides,
            CompiledSignature: compiledSignature?.ToString(),
            CompiledMatchesStored: compiledMatches,
            Delivered: deliveredEntries,
            Verdict: verdict,
            Repair: repair);
    }

    private static string Summarise(IReadOnlyList<RibbonCommandDiagnosis> diagnoses, bool deliveredChecked)
    {
        if (diagnoses.Count == 0)
        {
            return "Nothing to check.";
        }

        var failing = diagnoses.Where(d => d.Verdict is "stored" or "compiled" or "delivered").ToList();
        if (failing.Count == 0)
        {
            return deliveredChecked
                ? $"All {diagnoses.Count} command(s) are consistent from storage to the client."
                : $"All {diagnoses.Count} command(s) are consistent between storage and the compiled ribbon " +
                  "(delivery not checked).";
        }

        return string.Join(" ", failing
            .GroupBy(d => d.Verdict)
            .Select(g => $"{g.Count()} command(s) fail at the {g.Key} level ({string.Join(", ", g.Select(d => d.CommandId))})."));
    }

    // ---------------------------------------------------------------------------------------------
    // Scan
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// List every table whose ribbon has an unmanaged layer, and which of its commands override a
    /// managed copy. Meant as a pre- and post-deployment check: a managed deployment cannot win against
    /// any command listed in <see cref="RibbonUnmanagedLayerTable.CommandsOverridingManaged"/>.
    /// </summary>
    public async Task<RibbonUnmanagedLayerScanResult> ScanUnmanagedLayersAsync(string orgUrl, CancellationToken ct = default)
    {
        var solutions = await ReadSolutionsAsync(orgUrl, ct);
        var activeId = solutions.FirstOrDefault(s => s.Value == UnmanagedSolutionName).Key;
        if (activeId == Guid.Empty)
        {
            return new RibbonUnmanagedLayerScanResult(orgUrl, 0, [], "No 'Active' solution found — nothing to scan.");
        }

        var customizations = await _client.GetAllPagesAsync(
            orgUrl, $"api/data/v9.2/ribboncustomizations?$filter=solutionid eq {activeId}&$select=entity", ct: ct);

        var unmanagedCommands = await _client.GetAllPagesAsync(
            orgUrl, $"api/data/v9.2/ribboncommands?$filter=solutionid eq {activeId}&$select=command,commanddefinition,entity", ct: ct);

        var tables = new List<RibbonUnmanagedLayerTable>();
        foreach (var group in unmanagedCommands.GroupBy(c => c.GetStringOrNull("entity") ?? string.Empty))
        {
            var table = group.Key;
            var filter = string.IsNullOrEmpty(table) ? "entity eq null" : $"entity eq '{table.Replace("'", "''")}'";
            var all = await _client.GetAllPagesAsync(
                orgUrl, $"api/data/v9.2/ribboncommands?$filter={Uri.EscapeDataString(filter)}&$select=command,commanddefinition,solutionid", ct: ct);

            var overriding = new List<string>();
            var unmanagedOnly = new List<string>();
            foreach (var row in group)
            {
                var id = row.GetStringOrEmpty("command");
                var signature = SignatureFromXml(row.GetStringOrNull("commanddefinition")) ?? RibbonActionSignature.Empty;
                var managed = all
                    .Where(r => r.GetStringOrEmpty("command") == id && r.TryGetGuid("solutionid") != activeId)
                    .Select(r => SignatureFromXml(r.GetStringOrNull("commanddefinition")) ?? RibbonActionSignature.Empty)
                    .ToList();

                if (managed.Count == 0)
                {
                    unmanagedOnly.Add(id);
                }
                else if (managed.All(m => !m.SameAs(signature)))
                {
                    overriding.Add(id);
                }
            }

            tables.Add(new RibbonUnmanagedLayerTable(
                TableLogicalName: string.IsNullOrEmpty(table) ? "(application ribbon)" : table,
                UnmanagedCommandCount: group.Count(),
                CommandsOverridingManaged: overriding,
                UnmanagedOnlyCommands: unmanagedOnly));
        }

        tables = tables.OrderByDescending(t => t.CommandsOverridingManaged.Count).ThenBy(t => t.TableLogicalName).ToList();
        var blocking = tables.Where(t => t.CommandsOverridingManaged.Count > 0).ToList();

        var summary = blocking.Count == 0
            ? $"{tables.Count} table(s) carry unmanaged ribbon commands, none of them overrides a managed copy right now. " +
              "Each of them will override the next managed change to the same command."
            : $"{blocking.Count} table(s) have unmanaged commands that override their managed copies: " +
              string.Join(", ", blocking.Select(t => $"{t.TableLogicalName} ({string.Join(", ", t.CommandsOverridingManaged)})")) +
              ". A managed deployment cannot change these; see ribbon_sync_unmanaged_layer.";

        return new RibbonUnmanagedLayerScanResult(orgUrl, customizations.Count, tables, summary);
    }

    // ---------------------------------------------------------------------------------------------
    // Sync
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Overwrite the table's unmanaged ribbon layer with the copy a managed solution holds, so that the
    /// unmanaged layer stops overriding it.
    /// </summary>
    /// <remarks>
    /// <c>RemoveActiveCustomizations</c> refuses ribbons (<c>0x8004f059</c>), so overwriting is the only
    /// repair. Every node of <paramref name="sourceSolutionUniqueName"/> is written into the unmanaged
    /// layer. Nodes that exist <i>only</i> in the unmanaged layer are kept unless
    /// <paramref name="dropUnmanagedOnly"/> is set — a non-empty section replaces the whole collection,
    /// so leaving them out would delete them.
    /// </remarks>
    public async Task<RibbonSyncResult> SyncUnmanagedLayerAsync(
        string orgUrl,
        string tableLogicalName,
        string sourceSolutionUniqueName,
        bool dryRun = true,
        bool dropUnmanagedOnly = false,
        string? publisherUniqueName = null,
        CancellationToken ct = default)
    {
        if (string.Equals(sourceSolutionUniqueName, UnmanagedSolutionName, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("sourceSolution must be the solution holding the intended copy, not 'Active'.");
        }

        var stored = await ReadStoredAsync(orgUrl, tableLogicalName, ct);
        if (!stored.AllNodes.Any(n => string.Equals(n.SolutionUniqueName, sourceSolutionUniqueName, StringComparison.OrdinalIgnoreCase)))
        {
            var available = stored.AllNodes.Select(n => n.SolutionUniqueName).Distinct().OrderBy(n => n);
            throw new InvalidOperationException(
                $"Solution '{sourceSolutionUniqueName}' holds no ribbon node for '{tableLogicalName}'. " +
                "Solutions with ribbon nodes on this table: " + string.Join(", ", available));
        }

        var (ribbonDiffXml, changes) = BuildSyncPlan(stored, sourceSolutionUniqueName, dropUnmanagedOnly);

        if (dryRun)
        {
            return new RibbonSyncResult(
                OrgUrl: orgUrl,
                TableLogicalName: tableLogicalName,
                SourceSolutionUniqueName: sourceSolutionUniqueName,
                DryRun: true,
                Changes: changes,
                RibbonDiffXml: ribbonDiffXml,
                Imported: false,
                Verified: false,
                StillDifferent: [],
                TemporarySolutionUniqueName: null,
                Summary: DescribePlan(changes) + " Dry run — nothing was imported. Run again with dryRun=false to apply.");
        }

        var outcome = await _ribbons.ImportRibbonDiffXmlAsync(
            orgUrl, tableLogicalName, ribbonDiffXml, publisherUniqueName: publisherUniqueName, ct: ct);

        var after = await ReadStoredAsync(orgUrl, tableLogicalName, ct);
        var stillDifferent = after.AllNodes
            .Where(n => string.Equals(n.SolutionUniqueName, sourceSolutionUniqueName, StringComparison.OrdinalIgnoreCase))
            .Where(source =>
            {
                var unmanaged = after.AllNodes.FirstOrDefault(n => n.IsUnmanagedLayer && n.Kind == source.Kind && n.Id == source.Id);
                return unmanaged is null || !XmlEquals(unmanaged.Xml, source.Xml);
            })
            .Select(n => $"{n.Kind}:{n.Id}")
            .ToList();

        return new RibbonSyncResult(
            OrgUrl: orgUrl,
            TableLogicalName: tableLogicalName,
            SourceSolutionUniqueName: sourceSolutionUniqueName,
            DryRun: false,
            Changes: changes,
            RibbonDiffXml: ribbonDiffXml,
            Imported: true,
            Verified: stillDifferent.Count == 0,
            StillDifferent: stillDifferent,
            TemporarySolutionUniqueName: outcome.SolutionUniqueName,
            Summary: stillDifferent.Count == 0
                ? DescribePlan(changes) + " Imported and verified: the unmanaged layer now equals the source. " +
                  "Check delivery with ribbon_diagnose; if the client still gets the old commands, run " +
                  "ribbon_refresh_client_metadata."
                : "Imported, but these nodes still differ from the source: " + string.Join(", ", stillDifferent));
    }

    /// <summary>
    /// The RibbonDiffXml to import and the per-node plan. Pure, so the merge rules are testable.
    /// </summary>
    public static (string RibbonDiffXml, IReadOnlyList<RibbonSyncChange> Changes) BuildSyncPlan(
        StoredRibbon stored,
        string sourceSolutionUniqueName,
        bool dropUnmanagedOnly)
    {
        var source = stored.AllNodes
            .Where(n => string.Equals(n.SolutionUniqueName, sourceSolutionUniqueName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var unmanaged = stored.AllNodes.Where(n => n.IsUnmanagedLayer).ToList();

        var changes = new List<RibbonSyncChange>();
        var selected = new List<StoredNode>();

        foreach (var node in source)
        {
            var current = unmanaged.FirstOrDefault(u => u.Kind == node.Kind && u.Id == node.Id);
            changes.Add(new RibbonSyncChange(
                node.Kind,
                node.Id,
                current is null ? "add" : XmlEquals(current.Xml, node.Xml) ? "unchanged" : "update"));
            selected.Add(node);
        }

        foreach (var node in unmanaged.Where(u => !source.Any(s => s.Kind == u.Kind && s.Id == u.Id)))
        {
            changes.Add(new RibbonSyncChange(node.Kind, node.Id, dropUnmanagedOnly ? "drop" : "keep"));
            if (!dropUnmanagedOnly)
            {
                selected.Add(node);
            }
        }

        XElement[] Parse(string kind) => selected
            .Where(n => n.Kind == kind)
            .Select(n => TryParse(n.Xml))
            .OfType<XElement>()
            .ToArray();

        var xml = RibbonService.ComposeRibbonDiffXml(
            customActions: Parse(StoredNode.CustomActionKind),
            commandDefinitions: Parse(StoredNode.CommandKind),
            rules: Parse(StoredNode.RuleKind),
            locLabels: Parse(StoredNode.LocLabelKind));

        return (xml.ToString(), changes);
    }

    private static string DescribePlan(IReadOnlyList<RibbonSyncChange> changes)
    {
        string Count(string action) => $"{changes.Count(c => c.Action == action)} {action}";
        return $"Plan: {Count("add")}, {Count("update")}, {Count("unchanged")}, {Count("keep")} (unmanaged-only, kept), " +
               $"{Count("drop")} (unmanaged-only, dropped).";
    }

    // ---------------------------------------------------------------------------------------------
    // Refresh
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Force the server to rebuild the client ribbon metadata of a table by changing the table's metadata
    /// version, then put the table back exactly as it was.
    /// </summary>
    /// <remarks>
    /// The change is one character in one description label, published. Restoring depends on what was
    /// there before: a table without an unmanaged layer gets its new unmanaged layer removed again
    /// (<c>RemoveActiveCustomizations</c>); a table that already had one gets the original label written
    /// back, so the customizations already in that layer stay untouched. Either way the version moves a
    /// second time, which does no harm.
    /// </remarks>
    public async Task<RibbonRefreshResult> RefreshClientMetadataAsync(
        string orgUrl,
        string tableLogicalName,
        bool verify = true,
        CancellationToken ct = default)
    {
        var metadataId = await _ribbons.GetTableMetadataIdAsync(orgUrl, tableLogicalName, ct);
        var layers = await _solutions.CheckLayersAsync(orgUrl, metadataId, RibbonService.EntityComponentType, ct);
        var hadUnmanaged = layers.Layers.Any(l => string.Equals(l.SolutionName, UnmanagedSolutionName, StringComparison.OrdinalIgnoreCase));

        var definitionUrl = $"api/data/v9.2/EntityDefinitions({metadataId})";
        var raw = await _client.GetRawAsync(orgUrl, definitionUrl, ct: ct);
        var definition = JsonNode.Parse(raw)!.AsObject();

        var (labelNode, original) = PickDescriptionLabel(definition, allowAdding: !hadUnmanaged);
        labelNode["Label"] = ToggleTrailingPeriod(original);
        ClearUserLocalizedLabel(definition);

        var headers = new Dictionary<string, string> { ["MSCRM.MergeLabels"] = "true" };
        await _client.PutAsync(orgUrl, definitionUrl, definition, headers, ct);
        await _ribbons.PublishWithRetryAsync(orgUrl, tableLogicalName, ct);

        string restoredBy;
        if (hadUnmanaged)
        {
            labelNode["Label"] = original;
            await _client.PutAsync(orgUrl, definitionUrl, definition, headers, ct);
            restoredBy = "writing the original description back (the table already had an unmanaged layer, which stays as it was)";
        }
        else
        {
            await _solutions.RemoveActiveLayerAsync(orgUrl, metadataId, RibbonService.EntityComponentType, ct);
            restoredBy = "removing the unmanaged table layer the bump created (RemoveActiveCustomizations)";
        }

        await _ribbons.PublishWithRetryAsync(orgUrl, tableLogicalName, ct);

        var restored = true;
        RibbonDiagnosisResult? diagnosis = null;
        if (!DryRun.IsActive)
        {
            var check = JsonNode.Parse(await _client.GetRawAsync(orgUrl, definitionUrl + "?$select=Description", ct: ct))!.AsObject();
            var (_, now) = PickDescriptionLabel(check, allowAdding: true, languageCode: LanguageOf(labelNode));
            restored = string.Equals(now, original, StringComparison.Ordinal);

            if (verify)
            {
                try
                {
                    diagnosis = await DiagnoseAsync(orgUrl, tableLogicalName, checkDelivered: true, ct: ct);
                }
                catch (HttpRequestException ex)
                {
                    // The refresh itself succeeded; a failing check must not turn that into an error.
                    _logger.LogWarning(ex, "The diagnosis after the refresh of {Table} failed.", tableLogicalName);
                }
            }
        }

        return new RibbonRefreshResult(
            OrgUrl: orgUrl,
            TableLogicalName: tableLogicalName,
            HadUnmanagedTableLayer: hadUnmanaged,
            RestoredBy: restoredBy,
            DescriptionRestored: restored,
            DiagnosisAfter: diagnosis,
            Summary: (restored ? "The table's metadata version was bumped and the table restored. " : "WARNING: the description was not restored — check it. ") +
                     (diagnosis is null ? string.Empty : diagnosis.Summary));
    }

    /// <summary>Toggle a trailing period — the smallest visible change that round-trips exactly.</summary>
    public static string ToggleTrailingPeriod(string label) =>
        label.EndsWith('.') ? label[..^1] : label + ".";

    /// <summary>
    /// The description label to toggle: the first non-empty one, optionally of a given language. When
    /// there is none and adding is allowed (the restore will remove the whole unmanaged layer anyway),
    /// a new label in the language of the display name is added.
    /// </summary>
    public static (JsonObject Label, string Text) PickDescriptionLabel(JsonObject definition, bool allowAdding, int? languageCode = null)
    {
        var description = definition["Description"] as JsonObject;
        var labels = description?["LocalizedLabels"] as JsonArray;

        var match = labels?
            .OfType<JsonObject>()
            .FirstOrDefault(l => (languageCode is null || LanguageOf(l) == languageCode)
                                 && !string.IsNullOrEmpty(l["Label"]?.GetValue<string>()));
        if (match is not null)
        {
            return (match, match["Label"]!.GetValue<string>());
        }

        if (!allowAdding)
        {
            throw new InvalidOperationException(
                "The table has no description label to toggle, and it already has an unmanaged layer — " +
                "adding a label could not be undone without touching that layer. Add a description to the " +
                "table yourself, then run this again.");
        }

        var language = languageCode
                       ?? ((definition["DisplayName"] as JsonObject)?["LocalizedLabels"] as JsonArray)?
                          .OfType<JsonObject>().Select(LanguageOf).FirstOrDefault(l => l > 0)
                       ?? 1033;

        description ??= new JsonObject();
        definition["Description"] = description;
        labels ??= new JsonArray();
        description["LocalizedLabels"] = labels;

        var added = new JsonObject { ["Label"] = string.Empty, ["LanguageCode"] = language };
        labels.Add(added);
        return (added, string.Empty);
    }

    private static int LanguageOf(JsonObject label) =>
        label["LanguageCode"] is JsonValue v && v.TryGetValue<int>(out var code) ? code : 0;

    private static void ClearUserLocalizedLabel(JsonObject definition)
    {
        if (definition["Description"] is JsonObject description)
        {
            description["UserLocalizedLabel"] = null;
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Reading
    // ---------------------------------------------------------------------------------------------

    /// <summary>One stored ribbon node in one solution layer.</summary>
    public sealed record StoredNode(
        string Kind,
        string Id,
        string SolutionUniqueName,
        bool IsUnmanagedLayer,
        string? Xml)
    {
        public const string CustomActionKind = "customAction";
        public const string CommandKind = "command";
        public const string RuleKind = "rule";
        public const string LocLabelKind = "locLabel";

        public RibbonActionSignature Signature => SignatureFromXml(Xml) ?? RibbonActionSignature.Empty;
    }

    /// <summary>Every stored ribbon node of a table, across all solution layers.</summary>
    public sealed record StoredRibbon(IReadOnlyList<StoredNode> AllNodes)
    {
        public IEnumerable<StoredNode> Commands => AllNodes.Where(n => n.Kind == StoredNode.CommandKind);
    }

    /// <summary>A ribbon as one app's client receives it.</summary>
    public sealed record DeliveredRibbon(
        string Context,
        long? VersionNumber,
        IReadOnlyDictionary<string, RibbonActionSignature> Commands);

    private async Task<StoredRibbon> ReadStoredAsync(string orgUrl, string tableLogicalName, CancellationToken ct)
    {
        var solutions = await ReadSolutionsAsync(orgUrl, ct);
        var filter = Uri.EscapeDataString($"entity eq '{tableLogicalName.Replace("'", "''")}'");
        var nodes = new List<StoredNode>();

        string SolutionName(JsonElement row) =>
            solutions.TryGetValue(row.TryGetGuid("solutionid"), out var name) ? name : "(unknown)";

        foreach (var row in await _client.GetAllPagesAsync(
                     orgUrl, $"api/data/v9.2/ribboncommands?$filter={filter}&$select=command,commanddefinition,solutionid", ct: ct))
        {
            var solution = SolutionName(row);
            nodes.Add(new StoredNode(StoredNode.CommandKind, row.GetStringOrEmpty("command"), solution,
                solution == UnmanagedSolutionName, row.GetStringOrNull("commanddefinition")));
        }

        foreach (var row in await _client.GetAllPagesAsync(
                     orgUrl, $"api/data/v9.2/ribbondiffs?$filter={filter}&$select=diffid,difftype,rdx,solutionid", ct: ct))
        {
            var xml = row.GetStringOrNull("rdx");
            var element = TryParse(xml)?.Name.LocalName;
            var kind = element switch
            {
                "LocLabel" => StoredNode.LocLabelKind,
                "CustomAction" or "HideCustomAction" => StoredNode.CustomActionKind,
                null when row.GetInt32OrZero("difftype") == (int)RibbonDiffType.LocalizedLabel => StoredNode.LocLabelKind,
                null when row.GetInt32OrZero("difftype") == (int)RibbonDiffType.Standard => StoredNode.CustomActionKind,
                _ => null
            };

            if (kind is null)
            {
                continue; // Tab / LayoutTemplate diffs: not placed into sections by this code.
            }

            var solution = SolutionName(row);
            nodes.Add(new StoredNode(kind, row.GetStringOrEmpty("diffid"), solution, solution == UnmanagedSolutionName, xml));
        }

        foreach (var row in await _client.GetAllPagesAsync(
                     orgUrl, $"api/data/v9.2/ribbonrules?$filter={filter}&$select=ruleid,ruledefinition,solutionid", ct: ct))
        {
            var solution = SolutionName(row);
            nodes.Add(new StoredNode(StoredNode.RuleKind, row.GetStringOrEmpty("ruleid"), solution,
                solution == UnmanagedSolutionName, row.GetStringOrNull("ruledefinition")));
        }

        return new StoredRibbon(nodes);
    }

    private async Task<Dictionary<Guid, string>> ReadSolutionsAsync(string orgUrl, CancellationToken ct)
    {
        var rows = await _client.GetAllPagesAsync(orgUrl, "api/data/v9.2/solutions?$select=solutionid,uniquename", ct: ct);
        return rows
            .Where(r => r.TryGetGuid("solutionid") != Guid.Empty)
            .GroupBy(r => r.TryGetGuid("solutionid"))
            .ToDictionary(g => g.Key, g => g.First().GetStringOrEmpty("uniquename"));
    }

    /// <summary>
    /// Read the form ribbon of the table as each app's client receives it.
    /// </summary>
    /// <remarks>
    /// <c>GetClientMetadata</c> with <c>MetadataType = "ribbon"</c> ignores the table and returns every
    /// ribbon context of the app — over 300 MB on a grown org. The form query is scoped: for a main form
    /// that is part of the app it returns that form with its command bar, typically 5–15 MB. A form the
    /// app does not contain returns an almost empty answer, so the forms are tried in turn.
    /// </remarks>
    private async Task<List<(Guid AppId, string? AppName, Guid FormId, DeliveredRibbon Ribbon)>> ReadDeliveredAsync(
        string orgUrl,
        string tableLogicalName,
        Guid? appModuleId,
        List<string> notes,
        CancellationToken ct)
    {
        var result = new List<(Guid, string?, Guid, DeliveredRibbon)>();
        var metadataId = await _ribbons.GetTableMetadataIdAsync(orgUrl, tableLogicalName, ct);

        var apps = await AppsContainingTableAsync(orgUrl, metadataId, appModuleId, ct);
        if (apps.Count == 0)
        {
            notes.Add("No app contains this table, so no client receives its ribbon.");
            return result;
        }

        var mainForms = (await _client.GetAllPagesAsync(
                orgUrl,
                "api/data/v9.2/systemforms?$select=formid&$filter=" +
                Uri.EscapeDataString($"objecttypecode eq '{tableLogicalName.Replace("'", "''")}' and type eq 2"),
                ct: ct))
            .Select(r => r.TryGetGuid("formid"))
            .Where(id => id != Guid.Empty)
            .ToList();

        // Apps are read in parallel: each answer is several megabytes, and the per-app cache (#18 in
        // this repository's issues) means every app has to be checked on its own.
        using var gate = new SemaphoreSlim(3);
        var perApp = await Task.WhenAll(apps.Select(async app =>
        {
            await gate.WaitAsync(ct);
            try
            {
                return await ReadDeliveredForAppAsync(orgUrl, tableLogicalName, app.Id, app.Name, mainForms, ct);
            }
            finally
            {
                gate.Release();
            }
        }));

        foreach (var (app, entry) in apps.Zip(perApp))
        {
            if (entry.Found is { } found)
            {
                result.Add((app.Id, app.Name, found.FormId, found.Ribbon));
            }
            else
            {
                notes.Add($"App '{app.Name ?? app.Id.ToString()}': no main form of '{tableLogicalName}' returned a form ribbon, " +
                          "so delivery could not be read" + (entry.Error is null ? "." : $" ({entry.Error})."));
            }
        }

        return result;
    }

    /// <summary>
    /// Read one app's form ribbon. <c>GetClientMetadata</c> answers some app/form combinations with
    /// <c>400 0x80040216 "GetClientMetadata failed"</c> — observed right after a publish of the table.
    /// That must not fail the whole diagnosis: the next form is tried, and the error is reported.
    /// </summary>
    private async Task<((Guid FormId, DeliveredRibbon Ribbon)? Found, string? Error)> ReadDeliveredForAppAsync(
        string orgUrl,
        string tableLogicalName,
        Guid appId,
        string? appName,
        IReadOnlyList<Guid> mainForms,
        CancellationToken ct)
    {
        var appForms = (await _client.GetAllPagesAsync(
                orgUrl,
                $"api/data/v9.2/appmodulecomponents?$select=objectid&$filter=componenttype eq 60 and _appmoduleidunique_value eq {await AppUniqueIdAsync(orgUrl, appId, ct)}",
                ct: ct))
            .Select(r => r.TryGetGuid("objectid"))
            .ToHashSet();

        // An app without explicit form components shows all of the table's forms.
        var candidates = mainForms.Where(f => appForms.Count == 0 || appForms.Contains(f)).ToList();

        string? lastError = null;
        foreach (var formId in candidates)
        {
            string metadataJson;
            try
            {
                metadataJson = await GetClientMetadataAsync(orgUrl, tableLogicalName, formId, appId, ct);
            }
            catch (HttpRequestException ex)
            {
                lastError = ex.Message.Length > 200 ? ex.Message[..200] : ex.Message;
                _logger.LogWarning("GetClientMetadata for app {App}, form {Form} failed: {Message}", appName ?? appId.ToString(), formId, ex.Message);
                continue;
            }

            var ribbon = ParseDeliveredRibbons(metadataJson, tableLogicalName)
                .FirstOrDefault(r => string.Equals(r.Context, "Form", StringComparison.OrdinalIgnoreCase));
            if (ribbon is not null)
            {
                return ((formId, ribbon), null);
            }
        }

        _logger.LogInformation(
            "App {App}: none of {Count} candidate form(s) of {Table} returned a form ribbon.",
            appName ?? appId.ToString(), candidates.Count, tableLogicalName);
        return (null, lastError);
    }

    private async Task<List<(Guid Id, string? Name)>> AppsContainingTableAsync(
        string orgUrl, Guid tableMetadataId, Guid? onlyApp, CancellationToken ct)
    {
        var uniqueIds = (await _client.GetAllPagesAsync(
                orgUrl,
                $"api/data/v9.2/appmodulecomponents?$select=_appmoduleidunique_value&$filter=componenttype eq 1 and objectid eq {tableMetadataId}",
                ct: ct))
            .Select(r => r.TryGetGuid("_appmoduleidunique_value"))
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();

        var apps = new List<(Guid, string?)>();
        foreach (var uniqueId in uniqueIds)
        {
            foreach (var row in await _client.GetAllPagesAsync(
                         orgUrl, $"api/data/v9.2/appmodules?$select=appmoduleid,name&$filter=appmoduleidunique eq {uniqueId}", ct: ct))
            {
                var id = row.TryGetGuid("appmoduleid");
                if (id != Guid.Empty && (onlyApp is null || onlyApp == id))
                {
                    apps.Add((id, row.GetStringOrNull("name")));
                }
            }
        }

        if (onlyApp is { } single && apps.Count == 0)
        {
            apps.Add((single, null));
        }

        return apps;
    }

    private async Task<Guid> AppUniqueIdAsync(string orgUrl, Guid appModuleId, CancellationToken ct)
    {
        var raw = await _client.GetRawAsync(orgUrl, $"api/data/v9.2/appmodules({appModuleId})?$select=appmoduleidunique", ct: ct);
        using var doc = JsonDocument.Parse(raw);
        return doc.RootElement.TryGetGuid("appmoduleidunique");
    }

    private async Task<string> GetClientMetadataAsync(
        string orgUrl, string tableLogicalName, Guid formId, Guid appModuleId, CancellationToken ct)
    {
        var url = "api/data/v9.0/GetClientMetadata(ClientMetadataQuery=@q)?@q=" +
                  Uri.EscapeDataString(BuildFormMetadataQuery(tableLogicalName, formId, appModuleId));
        return await _client.GetRawAsync(orgUrl, url, ct: ct);
    }

    /// <summary>
    /// The <c>MetadataQuery</c> for one form. It is a complex type, passed as a bare JSON object — a quoted
    /// string fails with "Type verification failed. Expected type 'Microsoft.Dynamics.CRM.MetadataQuery'".
    /// </summary>
    public static string BuildFormMetadataQuery(string tableLogicalName, Guid formId, Guid appModuleId) =>
        JsonSerializer.Serialize(new
        {
            MetadataType = "form",
            EntityLogicalName = tableLogicalName,
            MetadataId = formId,
            MetadataNames = Array.Empty<string>(),
            GetDefault = false,
            DependencyDepth = "OnDemandWithoutContext",
            Exclude = Array.Empty<Guid>(),
            AppId = appModuleId
        });

    // ---------------------------------------------------------------------------------------------
    // Parsing — pure
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The ribbons for <paramref name="tableLogicalName"/> in a <c>GetClientMetadata</c> response. The
    /// response wraps the actual metadata as a JSON <b>string</b> in <c>Metadata</c>; inside,
    /// <c>Ribbons[]</c> carries one entry per context (<c>Form</c>, <c>HomePageGrid</c>, …) with its
    /// <c>VersionNumber</c> and <c>Commands.&lt;table&gt;.&lt;commandId&gt;</c>.
    /// </summary>
    public static IReadOnlyList<DeliveredRibbon> ParseDeliveredRibbons(string response, string tableLogicalName)
    {
        using var outer = JsonDocument.Parse(response);
        if (!outer.RootElement.TryGetProperty("Metadata", out var metadataProp) || metadataProp.ValueKind != JsonValueKind.String)
        {
            return [];
        }

        using var inner = JsonDocument.Parse(metadataProp.GetString()!);
        if (!inner.RootElement.TryGetProperty("Ribbons", out var ribbons) || ribbons.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new List<DeliveredRibbon>();
        foreach (var ribbon in ribbons.EnumerateArray())
        {
            if (!string.Equals(ribbon.GetStringOrNull("EntityLogicalName"), tableLogicalName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var commands = new Dictionary<string, RibbonActionSignature>(StringComparer.Ordinal);
            if (ribbon.TryGetProperty("Commands", out var byTable)
                && byTable.ValueKind == JsonValueKind.Object
                && byTable.TryGetProperty(tableLogicalName, out var tableCommands)
                && tableCommands.ValueKind == JsonValueKind.Object)
            {
                foreach (var command in tableCommands.EnumerateObject())
                {
                    commands[command.Name] = SignatureFromDelivered(command.Value);
                }
            }

            long? version = ribbon.TryGetProperty("VersionNumber", out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n)
                ? n
                : null;

            result.Add(new DeliveredRibbon(ribbon.GetStringOrNull("Context") ?? string.Empty, version, commands));
        }

        return result;
    }

    /// <summary>Signature of a delivered command (<c>Actions[].Attributes</c>).</summary>
    public static RibbonActionSignature SignatureFromDelivered(JsonElement command)
    {
        if (!command.TryGetProperty("Actions", out var actions) || actions.ValueKind != JsonValueKind.Array)
        {
            return RibbonActionSignature.Empty;
        }

        var list = new List<string>();
        foreach (var action in actions.EnumerateArray())
        {
            var attributes = action.TryGetProperty("Attributes", out var a) && a.ValueKind == JsonValueKind.Object ? a : default;
            if (attributes.ValueKind == JsonValueKind.Object && attributes.GetStringOrNull("FunctionName") is { } function)
            {
                list.Add($"js:{function}@{attributes.GetStringOrNull("Library")}");
            }
            else if (attributes.ValueKind == JsonValueKind.Object && attributes.GetStringOrNull("Address") is { } address)
            {
                list.Add($"url:{address}");
            }
            else
            {
                list.Add($"action:{action.GetInt32OrZero("ActionType")}");
            }
        }

        return new RibbonActionSignature(list);
    }

    /// <summary>Signature of a stored or compiled <c>&lt;CommandDefinition&gt;</c>; null when it does not parse.</summary>
    public static RibbonActionSignature? SignatureFromXml(string? xml) =>
        TryParse(xml) is { } element && element.Name.LocalName == "CommandDefinition" ? SignatureFromXml(element) : null;

    public static RibbonActionSignature SignatureFromXml(XElement commandDefinition)
    {
        var actions = commandDefinition.Elements().FirstOrDefault(e => e.Name.LocalName == "Actions");
        if (actions is null)
        {
            return RibbonActionSignature.Empty;
        }

        var list = new List<string>();
        foreach (var action in actions.Elements())
        {
            list.Add(action.Name.LocalName switch
            {
                "JavaScriptFunction" => $"js:{action.Attribute("FunctionName")?.Value}@{action.Attribute("Library")?.Value}",
                "Url" => $"url:{action.Attribute("Address")?.Value}",
                var other => $"action:{other}"
            });
        }

        return new RibbonActionSignature(list);
    }

    /// <summary>The <c>&lt;CommandDefinition&gt;</c> with this id in a compiled ribbon, or null.</summary>
    public static XElement? FindCompiledCommand(XDocument compiled, string commandId) =>
        compiled.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "CommandDefinition"
                                 && string.Equals(e.Attribute("Id")?.Value, commandId, StringComparison.Ordinal));

    private static bool XmlEquals(string? a, string? b)
    {
        var x = TryParse(a);
        var y = TryParse(b);
        return x is not null && y is not null ? XNode.DeepEquals(Normalise(x), Normalise(y)) : string.Equals(a, b, StringComparison.Ordinal);
    }

    /// <summary>Attribute order and insignificant whitespace do not make two nodes different.</summary>
    private static XElement Normalise(XElement element) =>
        new(element.Name,
            element.Attributes().OrderBy(a => a.Name.ToString(), StringComparer.Ordinal),
            element.Nodes().Select(n => n is XElement child ? Normalise(child) : n)
                .Where(n => n is not XText text || !string.IsNullOrWhiteSpace(text.Value)));

    private static XElement? TryParse(string? xml)
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
