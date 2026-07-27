namespace Dataverse.Core.Models;

/// <summary>
/// <c>appaction.location</c> — which command bar the modern command appears on.
/// Values verified against the <c>appaction_location</c> global choice on a live org.
/// </summary>
public enum CommandLocation
{
    Form = 0,
    MainGrid = 1,
    SubGrid = 2,
    AssociatedGrid = 3,
    QuickForm = 4,
    GlobalHeader = 5,
    Dashboard = 6
}

/// <summary>
/// <c>appaction.origin</c> (<c>appaction_origin</c> choice).
/// <para>
/// <b>Default (0)</b> is what the Command Designer writes for a hand-authored command and the only
/// value that produces a rendered button. <b>Migrated (1)</b> marks the modern mirror of a button
/// that still lives in classic RibbonDiffXml — those rows are bound to a real ribbon
/// <c>CommandDefinition</c>, so a hand-made row with Origin=Migrated has nothing to bind to and
/// stays invisible.
/// </para>
/// <para>
/// <b>Origin is effectively create-only:</b> a <c>PATCH</c> that sets it returns HTTP 200 and the
/// value silently stays unchanged. A command created with the wrong origin must be deleted
/// and recreated.
/// </para>
/// </summary>
public enum CommandOrigin
{
    Default = 0,
    Migrated = 1,
    EnhancedMigrated = 2
}

/// <summary>
/// <c>appaction.visibilitytype</c> (<c>appaction_visibilitytype</c> choice, values read off the live
/// option set): <c>0 = None</c>, <c>1 = Formula</c> (Power Fx), <c>2 = ClassicRules</c>.
/// <para>
/// <b>None is not "always visible" on a grid.</b> This is the single most common trap of the modern
/// command bar: a grid command with <c>visibilitytype = None</c> renders while nothing is selected and
/// <b>disappears the moment rows are ticked</b>, because the command bar switches into its selection
/// context and commands without a rule drop out of it. If the button has to survive a selection it needs
/// either <c>Formula</c> or <c>ClassicRules</c>.
/// </para>
/// <para>
/// <c>Formula</c> is stored across three fields — <c>visibilityformulacomponentlibraryid</c> (a lookup to
/// a <b>canvas component library</b>, <c>canvasapp</c> with <c>canvasapptype = 1</c>),
/// <c>visibilityformulacomponentname</c> and <c>visibilityformulafunctionname</c>. The library can only
/// be authored by the Command Designer <b>opened from an app</b>, which is precisely the app dependency
/// that makes the classic <c>SelectionCountRule</c> the better tool for an entity-bound button.
/// </para>
/// <para>
/// <c>ClassicRules</c> requires <c>appactionrule</c> rows associated through the
/// <c>appaction_appactionrule_classicrules</c> N:N relationship; without them there is nothing to
/// evaluate.
/// </para>
/// </summary>
public enum CommandVisibilityType
{
    None = 0,
    Formula = 1,
    ClassicRules = 2
}

/// <summary>
/// A canvas component library (<c>canvasapp</c> with <c>canvasapptype = 1</c>) — the thing a modern
/// command's Power Fx visibility formula lives in.
/// </summary>
public sealed record ComponentLibrarySummary(
    Guid CanvasAppId,
    string Name,
    string? DisplayName,
    bool IsManaged);

/// <summary>
/// The <c>fonticon</c> values that are known to render.
/// <para>
/// An unknown value does not error and does not fall back to a default — the command simply never
/// appears on the command bar, with nothing logged anywhere. The only visible symptom is the Command
/// Designer flagging "Icon is required" in red. <c>$clientsvg:Money</c>, for instance, looks entirely
/// plausible and silently kills the button.
/// </para>
/// <para>
/// This list is the set observed across the 729 icon-bearing <c>appaction</c> rows of a live org. It is
/// a floor, not a ceiling: <see cref="CommandService.ValidateFontIconAsync"/> unions it with whatever the
/// target environment actually uses, so an org with more icons stays usable.
/// </para>
/// </summary>
public static class CommandFontIcons
{
    /// <summary>Fluent icons referenced as <c>$clientsvg:&lt;Name&gt;</c>.</summary>
    public static readonly IReadOnlyList<string> ClientSvg = new[]
    {
        "$clientsvg:Accept",
        "$clientsvg:Add",
        "$clientsvg:Archive",
        "$clientsvg:Calendar",
        "$clientsvg:CreateMajor",
        "$clientsvg:CreateMinor",
        "$clientsvg:Delete",
        "$clientsvg:Edit",
        "$clientsvg:EditMail",
        "$clientsvg:FollowUser",
        "$clientsvg:ImportToExcel",
        "$clientsvg:MailLink",
        "$clientsvg:MergeCase",
        "$clientsvg:OpenEnrollment",
        "$clientsvg:Org",
        "$clientsvg:PageBlock",
        "$clientsvg:PageCompleted",
        "$clientsvg:Phone",
        "$clientsvg:Pin",
        "$clientsvg:Refresh",
        "$clientsvg:RelatedKnowledgeArticle",
        "$clientsvg:RevertToDraft",
        "$clientsvg:RoutingRule",
        "$clientsvg:Save",
        "$clientsvg:SaveAndClose",
        "$clientsvg:Share",
        "$clientsvg:TranslationNew",
        "$clientsvg:UpdateRestore"
    };

    /// <summary>Legacy bare icon names that migrated system commands still carry.</summary>
    public static readonly IReadOnlyList<string> Legacy = new[]
    {
        "Cancel",
        "Close",
        "Connection",
        "DeleteBulk",
        "EditDefaultFilter",
        "FormDesign",
        "NewMeeting",
        "No",
        "OpenDelve",
        "OpenEmail",
        "OpenRecord",
        "PublishKnowledgeArticle",
        "QueueItemRelease",
        "QueueItemRemove",
        "Report",
        "Resolve",
        "RestoreArticle",
        "SendSelected",
        "SetRegarding",
        "SharePointAddDocumentLocation",
        "SharePointCheckinDocument",
        "SharePointCheckoutDocument",
        "SharePointDeleteDocument",
        "SharePointDiscardCheckoutDocument",
        "SharePointEditDocument",
        "SharePointEditDocumentProperties",
        "SharePointOpenLocation",
        "SharePointUploadDocument",
        "TableGroup",
        "ViewHierarchy",
        "Yes"
    };

    /// <summary>Every statically known-good value.</summary>
    public static IReadOnlyList<string> All { get; } = ClientSvg.Concat(Legacy).ToArray();

    public static bool IsKnown(string fontIcon) =>
        All.Contains(fontIcon, StringComparer.Ordinal);
}

/// <summary>
/// <c>appaction.onclickeventtype</c> (<c>appaction_onclickeventtype</c> choice).
/// </summary>
public enum CommandOnClickEventType
{
    None = 0,
    Formula = 1,
    JavaScript = 2
}

/// <summary>
/// <c>appaction.type</c> (<c>appaction_type</c> choice) — the button shape.
/// </summary>
public enum CommandButtonType
{
    StandardButton = 0,
    DropdownButton = 1,
    SplitButton = 2,
    Group = 3
}

/// <summary>
/// <c>appaction.context</c> (<c>appaction_context</c> choice). <c>Entity</c> commands additionally
/// need <c>contextvalue</c> (the logical name) and the <c>ContextEntity</c> lookup (the MetadataId).
/// </summary>
public enum CommandContext
{
    All = 0,
    Entity = 1
}

/// <summary>
/// Parameter kinds inside <c>appaction.onclickeventjavascriptparameters</c>, which is a JSON array of
/// <c>{"type":&lt;int&gt;,"value":&lt;string|null&gt;}</c>. The integers are the classic ribbon
/// <c>CrmParameter</c> / literal-parameter enum carried over by the ribbon-to-modern-command migration.
/// <para>
/// These values were derived empirically: the migrated <c>appaction</c> rows of a live org were aligned
/// position-by-position with the <c>&lt;CrmParameter&gt;</c> children of the matching
/// <c>CommandDefinition</c> in the ribbon XML returned by <c>RetrieveApplicationRibbon</c> and
/// <c>RetrieveEntityRibbon</c> (470 commands aligned, unanimous per position).
/// </para>
/// <para>
/// Only the members listed here are confirmed. Other integers occur in system commands but could not be
/// disambiguated — pass them as raw numbers if you ever need them.
/// </para>
/// </summary>
public enum CommandParameterType
{
    /// <summary>Object type code of the form's table.</summary>
    PrimaryEntityTypeCode = 1,

    /// <summary>Logical name of the form's table.</summary>
    PrimaryEntityTypeName = 2,

    /// <summary>Array with the id of the form's record.</summary>
    PrimaryItemIds = 3,

    /// <summary>The GUID of the record the form is showing.</summary>
    FirstPrimaryItemId = 4,

    /// <summary>The form context object — what most form handlers want.</summary>
    PrimaryControl = 5,

    /// <summary>Object type code of the grid's table.</summary>
    SelectedEntityTypeCode = 7,

    /// <summary>Logical name of the grid's table.</summary>
    SelectedEntityTypeName = 8,

    /// <summary>GUID of the first selected grid row.</summary>
    FirstSelectedItemId = 10,

    /// <summary>The grid control object — what grid handlers use to refresh.</summary>
    SelectedControl = 12,

    /// <summary>Literal boolean constant; put "true"/"false" in <c>value</c>.</summary>
    BoolParameter = 18,

    /// <summary>Literal integer constant; put the number in <c>value</c>.</summary>
    IntParameter = 20,

    /// <summary>Literal string constant; put the text in <c>value</c>.</summary>
    StringParameter = 21,

    /// <summary>Array of GUIDs of the selected grid rows.</summary>
    SelectedControlSelectedItemIds = 23,

    /// <summary>Array of entity references (id + logical name + name) of the selected grid rows.</summary>
    SelectedControlSelectedItemReferences = 24,

    /// <summary>Count of all rows in the grid.</summary>
    SelectedControlAllItemCount = 25
}

public sealed record CommandParameter(int Type, string? Value)
{
    public string TypeName => Enum.IsDefined(typeof(CommandParameterType), Type)
        ? ((CommandParameterType)Type).ToString()
        : $"Type{Type}";
}

public sealed record CommandSummary(
    Guid AppActionId,
    string Name,
    string? UniqueName,
    string? ButtonLabelText,
    int Location,
    string LocationName,
    int Origin,
    string OriginName,
    int VisibilityType,
    string VisibilityTypeName,
    string? OnClickEventJavaScriptFunctionName,
    bool Hidden,
    bool IsManaged);

public sealed record CommandDetail(
    Guid AppActionId,
    string Name,
    string? UniqueName,
    string? ButtonLabelText,
    string? ButtonTooltipTitle,
    string? ButtonTooltipDescription,
    int Context,
    string ContextName,
    string? ContextValue,
    Guid? ContextEntityMetadataId,
    int Location,
    string LocationName,
    int ButtonType,
    string ButtonTypeName,
    int Origin,
    string OriginName,
    int VisibilityType,
    string VisibilityTypeName,
    int OnClickEventType,
    string OnClickEventTypeName,
    Guid? OnClickEventJavaScriptWebResourceId,
    string? OnClickEventJavaScriptFunctionName,
    IReadOnlyList<CommandParameter> Parameters,
    string? FontIcon,
    Guid? IconWebResourceId,
    decimal? Sequence,
    bool Hidden,
    bool IsDisabled,
    Guid? AppModuleId,
    bool IsManaged,
    Guid? VisibilityFormulaComponentLibraryId = null,
    string? VisibilityFormulaComponentName = null,
    string? VisibilityFormulaFunctionName = null);
