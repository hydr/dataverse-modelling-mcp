namespace Dataverse.Server.Tools;

using System.ComponentModel;
using System.Text.Json;
using Dataverse.Core.Config;
using Dataverse.Core.Services;
using ModelContextProtocol.Server;

/// <summary>
/// Tools for classic ribbons (<c>RibbonDiffXml</c>). Long descriptions on purpose — nearly every failure
/// mode in this corner of Dataverse is silent, so the guidance has to travel with the tool.
/// See <c>docs/tools/buttons-classic-vs-modern.md</c>.
/// </summary>
[McpServerToolType]
public sealed class RibbonTools
{
    private const string ChoiceNote =
        "Classic ribbon vs modern command: a classic ribbon button is plain XML, lives in source control, " +
        "ships through a solution import, and can gate itself on the grid selection with an entity-bound " +
        "SelectionCountRule — no app involved. A modern command (command_* tools) is a row in the " +
        "appaction table created by clicking, and its Power Fx visibility needs a canvas component " +
        "library that only the Command Designer opened FROM AN APP can create, so it drags an app " +
        "dependency along. Microsoft's direction is modern commanding, but classic ribbons do still " +
        "render — verified on an org whose appactionmigration row msdyn_System has ismigrated=true, " +
        "where both kinds render side by side.";

    private const string SilentFailureNote =
        "Failure modes here are silent — the API returns success and the button simply is not there. " +
        "The verified ones: (1) a Location that does not exist in the compiled ribbon; " +
        "(2) ModernImage=\"$webresource:….svg\", which removes the button entirely even though the SVG " +
        "web resource exists, is published and has content — use a PNG pair via imageWebResource " +
        "instead; (3) an unresolved $LocLabels: reference, which renders the raw token as the caption " +
        "(a button captioned \"LabelText\"); (4) forgetting to publish. Also: RetrieveEntityRibbon " +
        "proves storage, not rendering, and custom buttons only render in some apps — d365default shows " +
        "nothing where a custom app shows the button. When checking, read the whole command bar and " +
        "compare against a button you know works; do not grep for the label you expect, because a " +
        "mislabelled button is exactly what you would then miss.";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    [McpServerTool(Name = "ribbon_get")]
    [Description("Read a table's classic ribbon DIFFERENCE — the customisations this org added, not the " +
                 "whole ribbon. Source is the row-wise storage behind RibbonDiffXml: one ribbondiff row " +
                 "per <CustomAction> (diffid + rdx), one ribboncommand row per <CommandDefinition>, one " +
                 "ribbonrule row per rule. Deliberately NOT RetrieveEntityRibbon, which returns the " +
                 "compiled ribbon (~500 KB of out-of-the-box XML merged with every managed and unmanaged " +
                 "customisation) and therefore cannot tell you what this org changed. Also deliberately " +
                 "not a solution export: exporting a table added without subcomponents yields an EMPTY " +
                 "<RibbonDiffXml> scaffold, and exporting it with subcomponents drags in every form, view " +
                 "and column. Set includeLocations=true to additionally list the Location strings a " +
                 "CustomAction may target, read off the compiled ribbon. " + SilentFailureNote)]
    public static async Task<string> RibbonGet(
        RibbonService svc,
        ConfigProvider config,
        [Description("Logical name of the table, e.g. 'sample_purchaseorder'")] string tableLogicalName,
        [Description("Include diff nodes owned by managed solutions (default false)")] bool includeManaged = false,
        [Description("Also list the valid CustomAction Location strings from the compiled ribbon")] bool includeLocations = false,
        CancellationToken ct = default)
    {
        try
        {
            var env = config.GetActiveEnvironment();
            var result = await svc.GetAsync(env.OrgUrl, tableLogicalName, includeManaged, includeLocations, ct);
            return JsonSerializer.Serialize(result, JsonOptions);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message, details = ex.GetType().Name });
        }
    }

    [McpServerTool(Name = "ribbon_add_button")]
    [Description("Add a JavaScript-backed button to a table's classic ribbon. Ribbons cannot be written " +
                 "through the Web API, so this performs the solution round-trip: create a throwaway " +
                 "unmanaged solution, add the table WITHOUT subcomponents (AddSolutionComponent with " +
                 "DoNotIncludeSubcomponents=true, giving a ~3 KB table-shell export), strip the " +
                 "<EntityInfo> block, swap in the new <RibbonDiffXml>, re-zip with all three files at the " +
                 "archive root, import with overwriteUnmanaged, publish the table, then VERIFY that the " +
                 "ribbondiff row exists and finally delete the throwaway solution. " +
                 "Stripping <EntityInfo> matters: left in, every ribbon import writes the table's entire " +
                 "property block (ownership, auditing, duplicate detection, mobile visibility) back — an " +
                 "import carrying only <Name> plus <RibbonDiffXml> is verified to work and change nothing " +
                 "else. " +
                 "Section semantics matter and are counter-intuitive: an EMPTY <CustomActions /> means " +
                 "'not specified' and changes nothing, but a NON-EMPTY <CustomActions> REPLACES the whole " +
                 "collection — so an import mentioning only the new button silently deletes every other " +
                 "button on the table. This tool therefore reads the current diff first and re-sends all " +
                 "existing unmanaged nodes alongside the new one, replacing only the node with a matching " +
                 "id, and reports anything that went missing in lostCustomActionIds. Re-running with the " +
                 "same buttonId updates that button in place. " +
                 "Each node goes into the section its element name calls for. That matters on any table " +
                 "someone has touched with the Ribbon Workbench: its localized captions are stored as " +
                 "<LocLabel> nodes, and re-sending one inside <CustomActions> fails the import with " +
                 "'Missing Location Attribute … for CustomAction element with Id=….LabelText'. " +
                 "Labels are written as literal LabelText attributes; a $LocLabels: value is rejected. " +
                 SilentFailureNote + " " + ChoiceNote)]
    public static async Task<string> RibbonAddButton(
        RibbonService svc,
        ConfigProvider config,
        [Description("Logical name of the table, e.g. 'sample_purchaseorder'")] string tableLogicalName,
        [Description("Id of the button, e.g. 'sample.sample_purchaseorder.CorrectPrice.Button'. The CustomAction " +
                     "gets '<buttonId>.CustomAction' and the command '<buttonId>.Command'.")] string buttonId,
        [Description("Full CustomAction Location, e.g. " +
                     "'Mscrm.Form.sample_purchaseorder.MainTab.Save.Controls._children' or " +
                     "'Mscrm.HomepageGrid.sample_purchaseorder.MainTab.Management.Controls._children'. " +
                     "Validated against the compiled ribbon; run ribbon_get with includeLocations=true " +
                     "to see the choices.")] string location,
        [Description("Literal button caption. Not a $LocLabels: reference — those are unreliable and " +
                     "render the raw token as the caption.")] string label,
        [Description("Name of the JScript web resource holding the handler, e.g. " +
                     "'sample_purchaseorder_correct_price.js'")] string webResourceName,
        [Description("Fully qualified handler, e.g. 'Sample.PurchaseOrder.CorrectPrice.onFormButton'")] string functionName,
        [Description("Comma-separated parameter list. Classic ribbons NAME their parameters instead of " +
                     "numbering them: 'PrimaryControl' for a form button, " +
                     "'SelectedControlSelectedItemIds,SelectedControl' for a grid button. Literals use " +
                     "'String:abc', 'Bool:true', 'Int:5'.")] string parameters,
        [Description("Display order within the group (default 41)")] int sequence = 41,
        [Description("Name of a PNG web resource for Image16by16/Image32by32. PNG pairs are the verified " +
                     "reliable icon route for classic buttons.")] string? imageWebResource = null,
        [Description("ModernImage attribute. A '$webresource:' value is REJECTED — with an SVG web " +
                     "resource that exists, is published and has content, the button silently stops " +
                     "rendering entirely. Use imageWebResource instead.")] string? modernImage = null,
        [Description("Visibility/enablement rule: 'OneSelected' (exactly one row), 'AtLeastOneSelected', " +
                     "'SelectionCountRule:<min>[-<max>]', or a literal <EnableRule Id=\"…\">…</EnableRule> " +
                     "fragment. This is the entity-bound equivalent of a modern command's Power Fx " +
                     "visibility — and needs no component library and no app.")] string? enableRule = null,
        [Description("Tooltip title (defaults to the label)")] string? tooltipTitle = null,
        [Description("Tooltip description (defaults to the label)")] string? tooltipDescription = null,
        [Description("TemplateAlias for the button placement (default 'o1')")] string? templateAlias = "o1",
        [Description("Reuse an existing solution instead of creating and deleting a throwaway one")] string? solutionUniqueName = null,
        [Description("Publisher for the throwaway solution; defaults to the publisher owning the table's " +
                     "customization prefix")] string? publisherUniqueName = null,
        [Description("Verify the Location against the compiled ribbon first (default true). Turning this " +
                     "off re-enables the silent 'imported fine, renders nothing' failure.")] bool validateLocation = true,
        [Description("Publish the table afterwards (default true). Without it the button is stored but " +
                     "neither compiled nor verifiable.")] bool publish = true,
        CancellationToken ct = default)
    {
        try
        {
            var env = config.GetActiveEnvironment();
            var parsed = RibbonService.ParseParameterSpec(parameters);

            var result = await svc.AddButtonAsync(
                env.OrgUrl,
                tableLogicalName,
                buttonId,
                location,
                label,
                webResourceName,
                functionName,
                parsed,
                sequence,
                imageWebResource,
                modernImage,
                enableRule,
                tooltipTitle,
                tooltipDescription,
                templateAlias,
                solutionUniqueName,
                publisherUniqueName,
                validateLocation,
                stripEntityInfo: true,
                publish: publish,
                ct: ct);

            return JsonSerializer.Serialize(result, JsonOptions);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message, details = ex.GetType().Name });
        }
    }

    [McpServerTool(Name = "ribbon_remove_button")]
    [Description("Remove a classic ribbon button. IMPORTANT — this does NOT work by importing an empty " +
                 "<CustomActions />: an empty section reads as 'not specified', so the omitted entry " +
                 "survives, even after PublishAllXml, and deleting the solution that imported it changes " +
                 "nothing because the diff now lives in the Active layer. (A non-empty collection does " +
                 "replace the whole section, but that only removes something while at least one other " +
                 "button remains, and it takes any node you forget with it.) What DOES " +
                 "work is deleting the stored rows directly: the diff is kept decomposed under the " +
                 "table's ribboncustomization record as one ribbondiff row per <CustomAction> plus one " +
                 "ribboncommand row per <CommandDefinition>, and those child rows accept DELETE over the " +
                 "Web API (the parent ribboncustomization does not — 0x80040800). This tool deletes the " +
                 "matching rows, publishes the table, and re-queries to confirm. Verified end-to-end: " +
                 "before, the compiled ribbon carried the <CustomAction> and the button rendered; after, " +
                 "both the CustomAction and its ribbontabtocommandmap rows are gone. One residue: the " +
                 "<CommandDefinition> can linger in the compiled ribbon even after its row is deleted and " +
                 "the org is fully published — it is inert, since nothing places it on a tab, and it is " +
                 "reported as a warning. Managed diff rows are refused; those can only be suppressed with " +
                 "a <HideCustomAction> shipped in a solution. " +
                 "The button's <LocLabel> rows ('<buttonId>.LabelText', '.Alt', …) are deleted with it. " +
                 "Leaving them behind is not cosmetic: an orphaned label row stays in the table's diff " +
                 "and breaks the next ribbon_add_button on that table.")]
    public static async Task<string> RibbonRemoveButton(
        RibbonService svc,
        ConfigProvider config,
        [Description("Logical name of the table")] string tableLogicalName,
        [Description("The button id, the CustomAction id, or the '<buttonId>.CustomAction' form — all " +
                     "three are matched, as is any diff whose XML declares Id=\"<buttonId>\"")] string buttonId,
        [Description("Also delete the <CommandDefinition> when no surviving CustomAction references it " +
                     "(default true)")] bool removeCommandDefinition = true,
        [Description("Publish the table afterwards (default true)")] bool publish = true,
        CancellationToken ct = default)
    {
        try
        {
            var env = config.GetActiveEnvironment();
            var result = await svc.RemoveButtonAsync(
                env.OrgUrl, tableLogicalName, buttonId, removeCommandDefinition, publish, ct);
            return JsonSerializer.Serialize(result, JsonOptions);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message, details = ex.GetType().Name });
        }
    }
}
