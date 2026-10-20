namespace Dataverse.Tests.Services;

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Dataverse.Core.Models;
using Dataverse.Core.Services;
using NUnit.Framework;
using static Dataverse.Core.Services.RibbonDeliveryService;

/// <summary>
/// The decision rules of <see cref="RibbonDeliveryService"/>. The shapes of the stored rows, the compiled
/// ribbon and the client metadata below follow what a live environment returns; only names are synthetic.
/// </summary>
[TestFixture]
public sealed class RibbonDeliveryServiceTests
{
    private const string Table = "sample_widget";
    private const string CommandId = "sample.sample_widget.Send.Command";
    private static readonly Guid App = Guid.Parse("11111111-1111-1111-1111-111111110001");
    private static readonly Guid Form = Guid.Parse("11111111-1111-1111-1111-111111110002");

    private static string Command(string function, string library = "$webresource:sample_widget_actions.js") => $"""
        <CommandDefinition Id="{CommandId}">
          <EnableRules />
          <DisplayRules />
          <Actions>
            <JavaScriptFunction FunctionName="{function}" Library="{library}">
              <CrmParameter Value="PrimaryControl" />
            </JavaScriptFunction>
          </Actions>
        </CommandDefinition>
        """;

    private static StoredNode Layer(string solution, string function) =>
        new(StoredNode.CommandKind, CommandId, solution, solution == UnmanagedSolutionName, Command(function));

    private static (Guid, string?, Guid, DeliveredRibbon) Delivered(string function) =>
        (App, "Contoso Sales", Form, new DeliveredRibbon(
            "Form", 42, new Dictionary<string, RibbonActionSignature>
            {
                [CommandId] = new([$"js:{function}@$webresource:sample_widget_actions.js"])
            }));

    private static RibbonActionSignature Compiled(string function) =>
        SignatureFromXml(XElement.Parse(Command(function)));

    // ---------------------------------------------------------------------------------------------
    // Signatures
    // ---------------------------------------------------------------------------------------------

    [Test]
    public void SignatureFromXml_ReadsFunctionLibraryAndUrlActionsInOrder()
    {
        var xml = """
            <CommandDefinition Id="c">
              <Actions>
                <JavaScriptFunction FunctionName="Sample.run" Library="$webresource:a.js" />
                <Url Address="https://contoso.example/help" />
              </Actions>
            </CommandDefinition>
            """;

        Assert.That(SignatureFromXml(xml)!.Actions,
            Is.EqualTo(new[] { "js:Sample.run@$webresource:a.js", "url:https://contoso.example/help" }));
    }

    [Test]
    public void SignatureFromXml_IsNullForSomethingThatIsNoCommandDefinition() =>
        Assert.That(SignatureFromXml("<LocLabel Id=\"x\" />"), Is.Null);

    [Test]
    public void SignatureFromDelivered_ReadsTheClientShape()
    {
        using var doc = JsonDocument.Parse("""
            {
              "Id": "sample.sample_widget.Send.Command",
              "DisplayRules": ["Mscrm.OutlookRenderTypeWeb"],
              "Actions": [{
                "ActionType": 3,
                "Attributes": {
                  "__type": "JavaScriptFunctionAttributes:#Microsoft.Crm.Application.SharedObjects.Ribbon",
                  "FunctionName": "Sample.Widget.onSend",
                  "Library": "$webresource:sample_widget_actions.js"
                },
                "Parameters": [{ "Name": "CrmParameter", "ParameterType": 5 }]
              }],
              "SolutionUniqueName": "Active"
            }
            """);

        Assert.That(SignatureFromDelivered(doc.RootElement).Actions,
            Is.EqualTo(new[] { "js:Sample.Widget.onSend@$webresource:sample_widget_actions.js" }));
    }

    [Test]
    public void Signatures_FromXmlAndFromTheClient_CompareEqual() =>
        Assert.That(Compiled("Sample.Widget.onSend").SameAs(Delivered("Sample.Widget.onSend").Item4.Commands[CommandId]), Is.True);

    // ---------------------------------------------------------------------------------------------
    // Client metadata
    // ---------------------------------------------------------------------------------------------

    [Test]
    public void ParseDeliveredRibbons_UnwrapsTheMetadataStringAndKeepsOnlyTheTable()
    {
        var inner = new
        {
            MetadataVersion = 100,
            Ribbons = new object[]
            {
                new
                {
                    VersionNumber = 90, EntityLogicalName = "sample_widgetline", Context = "SubGridStandard",
                    Commands = new Dictionary<string, object> { ["sample_widgetline"] = new Dictionary<string, object>() }
                },
                new
                {
                    VersionNumber = 95, EntityLogicalName = Table, Context = "Form",
                    Commands = new Dictionary<string, object>
                    {
                        [Table] = new Dictionary<string, object>
                        {
                            [CommandId] = new
                            {
                                Id = CommandId,
                                Actions = new[]
                                {
                                    new { ActionType = 3, Attributes = new { FunctionName = "Sample.Widget.onSend", Library = "$webresource:w.js" } }
                                },
                                SolutionUniqueName = "Active"
                            }
                        }
                    }
                }
            }
        };

        // The response carries the metadata as a JSON string, not as an object.
        var response = JsonSerializer.Serialize(new { Metadata = JsonSerializer.Serialize(inner) });

        var ribbons = ParseDeliveredRibbons(response, Table);

        Assert.Multiple(() =>
        {
            Assert.That(ribbons, Has.Count.EqualTo(1));
            Assert.That(ribbons[0].Context, Is.EqualTo("Form"));
            Assert.That(ribbons[0].VersionNumber, Is.EqualTo(95));
            Assert.That(ribbons[0].Commands[CommandId].Actions, Is.EqualTo(new[] { "js:Sample.Widget.onSend@$webresource:w.js" }));
        });
    }

    [Test]
    public void ParseDeliveredRibbons_ToleratesAnAnswerWithoutRibbons() =>
        Assert.That(ParseDeliveredRibbons("""{"Metadata":"{ \"FormSelectors\":[] }"}""", Table), Is.Empty);

    [Test]
    public void BuildFormMetadataQuery_IsABareObjectScopedToFormAndApp()
    {
        using var doc = JsonDocument.Parse(BuildFormMetadataQuery(Table, Form, App));
        var root = doc.RootElement;

        Assert.Multiple(() =>
        {
            Assert.That(root.ValueKind, Is.EqualTo(JsonValueKind.Object));
            Assert.That(root.GetProperty("MetadataType").GetString(), Is.EqualTo("form"));
            Assert.That(root.GetProperty("MetadataId").GetGuid(), Is.EqualTo(Form));
            Assert.That(root.GetProperty("AppId").GetGuid(), Is.EqualTo(App));
            Assert.That(root.GetProperty("DependencyDepth").GetString(), Is.EqualTo("OnDemandWithoutContext"));
        });
    }

    // ---------------------------------------------------------------------------------------------
    // Verdicts
    // ---------------------------------------------------------------------------------------------

    [Test]
    public void Diagnose_UnmanagedLayerThatDiffersFromEveryManagedCopy_FailsAtStored()
    {
        var layers = new[]
        {
            Layer("ContosoSales", "Sample.Widget.onSend"),
            Layer("ContosoForms", "Sample.Widget.onSend"),
            Layer(UnmanagedSolutionName, "Legacy.sendWidget"),
        };

        var d = Diagnose(CommandId, layers, Compiled("Legacy.sendWidget"), [Delivered("Legacy.sendWidget")]);

        Assert.Multiple(() =>
        {
            Assert.That(d.Verdict, Is.EqualTo("stored"));
            Assert.That(d.UnmanagedOverridesManaged, Is.True);
            Assert.That(d.Repair, Does.Contain("ribbon_sync_unmanaged_layer").And.Contain("ContosoSales"));
        });
    }

    [Test]
    public void Diagnose_UnmanagedLayerEqualToAManagedCopy_IsNotAStoredFailure()
    {
        var layers = new[]
        {
            Layer("ContosoSales", "Sample.Widget.onSend"),
            Layer(UnmanagedSolutionName, "Sample.Widget.onSend"),
        };

        var d = Diagnose(CommandId, layers, Compiled("Sample.Widget.onSend"), [Delivered("Sample.Widget.onSend")]);

        Assert.That(d.Verdict, Is.EqualTo("ok"));
    }

    [Test]
    public void Diagnose_CompiledRibbonBehindTheStoredRows_FailsAtCompiled()
    {
        var layers = new[] { Layer(UnmanagedSolutionName, "Sample.Widget.onSend") };

        var d = Diagnose(CommandId, layers, Compiled("Legacy.sendWidget"), []);

        Assert.Multiple(() =>
        {
            Assert.That(d.Verdict, Is.EqualTo("compiled"));
            Assert.That(d.Repair, Does.Contain("publish"));
        });
    }

    /// <summary>
    /// The production case: storage and compiled ribbon are right, the client still runs the old code.
    /// </summary>
    [Test]
    public void Diagnose_ClientStillGettingTheOldCommand_FailsAtDelivered()
    {
        var layers = new[]
        {
            Layer("ContosoSales", "Sample.Widget.onSend"),
            Layer(UnmanagedSolutionName, "Sample.Widget.onSend"),
        };

        var d = Diagnose(CommandId, layers, Compiled("Sample.Widget.onSend"), [Delivered("Legacy.sendWidget")]);

        Assert.Multiple(() =>
        {
            Assert.That(d.Verdict, Is.EqualTo("delivered"));
            Assert.That(d.Repair, Does.Contain("ribbon_refresh_client_metadata"));
            Assert.That(d.Delivered.Single().MatchesCompiled, Is.False);
            Assert.That(d.Delivered.Single().RibbonVersionNumber, Is.EqualTo(42));
        });
    }

    [Test]
    public void Diagnose_CommandNoButtonUses_IsReportedAsUnused()
    {
        var d = Diagnose(CommandId, [Layer(UnmanagedSolutionName, "Sample.Widget.onSend")], compiledSignature: null, []);

        Assert.That(d.Verdict, Is.EqualTo("unused"));
    }

    // ---------------------------------------------------------------------------------------------
    // Sync plan
    // ---------------------------------------------------------------------------------------------

    private static StoredNode Node(string kind, string id, string solution, string xml) =>
        new(kind, id, solution, solution == UnmanagedSolutionName, xml);

    private static StoredRibbon TypicalTarget() => new(
    [
        // The managed copy the deployment shipped …
        Node(StoredNode.CommandKind, CommandId, "ContosoSales", Command("Sample.Widget.onSend")),
        Node(StoredNode.CustomActionKind, "sample.sample_widget.Send.CustomAction", "ContosoSales",
            """<CustomAction Id="sample.sample_widget.Send.CustomAction" Location="Mscrm.Form.sample_widget.MainTab.Save.Controls._children" Sequence="17" />"""),
        Node(StoredNode.LocLabelKind, "sample.sample_widget.Send.LabelText", "ContosoSales",
            """<LocLabel Id="sample.sample_widget.Send.LabelText"><Titles><Title description="Send" languagecode="1033" /></Titles></LocLabel>"""),

        // … and the stale unmanaged layer above it.
        Node(StoredNode.CommandKind, CommandId, UnmanagedSolutionName, Command("Legacy.sendWidget")),
        Node(StoredNode.CustomActionKind, "sample.sample_widget.Send.CustomAction", UnmanagedSolutionName,
            """<CustomAction Sequence="17" Location="Mscrm.Form.sample_widget.MainTab.Save.Controls._children" Id="sample.sample_widget.Send.CustomAction" />"""),
        Node(StoredNode.RuleKind, "sample.sample_widget.Local.EnableRule", UnmanagedSolutionName,
            """<EnableRule Id="sample.sample_widget.Local.EnableRule"><FormStateRule State="Existing" /></EnableRule>"""),
    ]);

    [Test]
    public void BuildSyncPlan_ClassifiesEveryNode()
    {
        var (_, changes) = BuildSyncPlan(TypicalTarget(), "ContosoSales", dropUnmanagedOnly: false);

        string Action(string id) => changes.Single(c => c.Id == id).Action;

        Assert.Multiple(() =>
        {
            Assert.That(Action(CommandId), Is.EqualTo("update"));
            Assert.That(Action("sample.sample_widget.Send.CustomAction"), Is.EqualTo("unchanged"),
                "attribute order alone does not make two nodes different");
            Assert.That(Action("sample.sample_widget.Send.LabelText"), Is.EqualTo("add"));
            Assert.That(Action("sample.sample_widget.Local.EnableRule"), Is.EqualTo("keep"));
        });
    }

    /// <summary>
    /// A non-empty section replaces the whole unmanaged collection, so an unmanaged-only node that is not
    /// re-sent is deleted. Keeping it is the default.
    /// </summary>
    [Test]
    public void BuildSyncPlan_PutsEveryNodeInItsSectionAndKeepsUnmanagedOnlyNodes()
    {
        var (xml, _) = BuildSyncPlan(TypicalTarget(), "ContosoSales", dropUnmanagedOnly: false);
        var doc = XElement.Parse(xml);

        Assert.Multiple(() =>
        {
            Assert.That(doc.Element("CommandDefinitions")!.Elements("CommandDefinition").Single()
                .Descendants("JavaScriptFunction").Single().Attribute("FunctionName")!.Value, Is.EqualTo("Sample.Widget.onSend"),
                "the source copy wins");
            Assert.That(doc.Element("CustomActions")!.Elements("CustomAction").Count(), Is.EqualTo(1));
            Assert.That(doc.Element("LocLabels")!.Elements("LocLabel").Count(), Is.EqualTo(1));
            Assert.That(doc.Element("RuleDefinitions")!.Element("EnableRules")!.Elements("EnableRule").Count(), Is.EqualTo(1));
        });
    }

    [Test]
    public void BuildSyncPlan_CanDropUnmanagedOnlyNodes()
    {
        var (xml, changes) = BuildSyncPlan(TypicalTarget(), "ContosoSales", dropUnmanagedOnly: true);

        Assert.Multiple(() =>
        {
            Assert.That(changes.Single(c => c.Id == "sample.sample_widget.Local.EnableRule").Action, Is.EqualTo("drop"));
            Assert.That(XElement.Parse(xml).Descendants("EnableRule"), Is.Empty);
        });
    }

    // ---------------------------------------------------------------------------------------------
    // Refresh
    // ---------------------------------------------------------------------------------------------

    [TestCase("Something that has been billed.", "Something that has been billed")]
    [TestCase("Something that has been billed", "Something that has been billed.")]
    public void ToggleTrailingPeriod_RoundTrips(string before, string after)
    {
        Assert.Multiple(() =>
        {
            Assert.That(ToggleTrailingPeriod(before), Is.EqualTo(after));
            Assert.That(ToggleTrailingPeriod(ToggleTrailingPeriod(before)), Is.EqualTo(before));
        });
    }

    [Test]
    public void PickDescriptionLabel_TakesTheFirstNonEmptyLabel()
    {
        var definition = JsonNode.Parse("""
            {"Description":{"LocalizedLabels":[{"Label":"","LanguageCode":1031},{"Label":"A widget.","LanguageCode":1033}]}}
            """)!.AsObject();

        var (label, text) = PickDescriptionLabel(definition, allowAdding: false);

        Assert.Multiple(() =>
        {
            Assert.That(text, Is.EqualTo("A widget."));
            Assert.That(label["LanguageCode"]!.GetValue<int>(), Is.EqualTo(1033));
        });
    }

    [Test]
    public void PickDescriptionLabel_AddsALabelOnlyWhenTheRestoreRemovesTheLayer()
    {
        var withoutLabel = """{"DisplayName":{"LocalizedLabels":[{"Label":"Widget","LanguageCode":1031}]},"Description":{"LocalizedLabels":[]}}""";

        var (added, text) = PickDescriptionLabel(JsonNode.Parse(withoutLabel)!.AsObject(), allowAdding: true);

        Assert.Multiple(() =>
        {
            Assert.That(text, Is.Empty);
            Assert.That(added["LanguageCode"]!.GetValue<int>(), Is.EqualTo(1031), "the language of the display name");
            Assert.Throws<InvalidOperationException>(
                () => PickDescriptionLabel(JsonNode.Parse(withoutLabel)!.AsObject(), allowAdding: false));
        });
    }
}
