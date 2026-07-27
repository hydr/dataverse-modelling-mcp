namespace Dataverse.Tests.Services;

using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using Dataverse.Core.Models;
using Dataverse.Core.Services;
using NUnit.Framework;

[TestFixture]
public sealed class RibbonServiceTests
{
    private const string ExportedCustomizations = """
        <ImportExportXml>
          <Entities>
            <Entity>
              <Name LocalizedName="MCP Test" OriginalName="MCP Test">sample_mcptest</Name>
              <EntityInfo>
                <entity Name="sample_mcptest">
                  <IsAuditEnabled>0</IsAuditEnabled>
                  <OwnershipTypeMask>UserOwned</OwnershipTypeMask>
                </entity>
              </EntityInfo>
              <RibbonDiffXml>
                <CustomActions />
                <Templates>
                  <RibbonTemplates Id="Mscrm.Templates"></RibbonTemplates>
                </Templates>
                <CommandDefinitions />
                <RuleDefinitions>
                  <TabDisplayRules />
                  <DisplayRules />
                  <EnableRules />
                </RuleDefinitions>
                <LocLabels />
              </RibbonDiffXml>
            </Entity>
          </Entities>
        </ImportExportXml>
        """;

    // ---------------------------------------------------------------------------------------------
    // BuildRibbonDiffXml
    // ---------------------------------------------------------------------------------------------

    [Test]
    public void BuildRibbonDiffXml_ProducesTheFourSectionsAndWiresButtonToCommand()
    {
        var xml = RibbonService.BuildRibbonDiffXml(
            buttonId: "sample.sample_mcptest.Probe.Button",
            customActionId: "sample.sample_mcptest.Probe.CustomAction",
            commandId: "sample.sample_mcptest.Probe.Command",
            location: "Mscrm.HomepageGrid.sample_mcptest.MainTab.Management.Controls._children",
            label: "MCP Probe",
            webResourceName: "sample_mcp_ribbon_probe.js",
            functionName: "Sample.Probe.run",
            parameters: RibbonService.ParseParameterSpec("SelectedControlSelectedItemIds,SelectedControl"));

        var doc = XElement.Parse(xml);

        var customAction = doc.Descendants("CustomAction").Single();
        Assert.That(customAction.Attribute("Id")!.Value, Is.EqualTo("sample.sample_mcptest.Probe.CustomAction"));
        Assert.That(
            customAction.Attribute("Location")!.Value,
            Is.EqualTo("Mscrm.HomepageGrid.sample_mcptest.MainTab.Management.Controls._children"));

        var button = doc.Descendants("Button").Single();
        Assert.That(button.Attribute("Command")!.Value, Is.EqualTo("sample.sample_mcptest.Probe.Command"));
        // A literal caption — $LocLabels: references render as the raw token.
        Assert.That(button.Attribute("LabelText")!.Value, Is.EqualTo("MCP Probe"));
        Assert.That(button.Attribute("TemplateAlias")!.Value, Is.EqualTo("o1"));

        var js = doc.Descendants("JavaScriptFunction").Single();
        Assert.That(js.Attribute("Library")!.Value, Is.EqualTo("$webresource:sample_mcp_ribbon_probe.js"));
        Assert.That(
            js.Elements("CrmParameter").Select(p => p.Attribute("Value")!.Value),
            Is.EqualTo(new[] { "SelectedControlSelectedItemIds", "SelectedControl" }));

        Assert.That(doc.Element("Templates"), Is.Not.Null);
        Assert.That(doc.Element("RuleDefinitions"), Is.Not.Null);
        Assert.That(doc.Element("LocLabels"), Is.Not.Null);
    }

    [Test]
    public void BuildRibbonDiffXml_UsesImageWebResourceAsAPngPair()
    {
        var xml = RibbonService.BuildRibbonDiffXml(
            "b", "b.CustomAction", "b.Command", "loc", "L", "wr.js", "fn",
            Array.Empty<RibbonParameter>(),
            imageWebResource: "sample_icon16.png");

        var button = XElement.Parse(xml).Descendants("Button").Single();
        Assert.That(button.Attribute("Image16by16")!.Value, Is.EqualTo("$webresource:sample_icon16.png"));
        Assert.That(button.Attribute("Image32by32")!.Value, Is.EqualTo("$webresource:sample_icon16.png"));
        Assert.That(button.Attribute("ModernImage"), Is.Null);
    }

    [Test]
    public void BuildRibbonDiffXml_RejectsAWebResourceModernImage()
    {
        // Verified against a live org: with an existing, published SVG web resource the button silently
        // stops rendering altogether. Same failure shape as an invalid fonticon on a modern command.
        var ex = Assert.Throws<ArgumentException>(() => RibbonService.BuildRibbonDiffXml(
            "b", "b.CustomAction", "b.Command", "loc", "L", "wr.js", "fn",
            Array.Empty<RibbonParameter>(),
            modernImage: "$webresource:sample_ResolveEaErDifference.svg"));

        Assert.That(ex!.Message, Does.Contain("silently"));
        Assert.That(ex.Message, Does.Contain("imageWebResource"));
    }

    [Test]
    public void BuildRibbonDiffXml_DeclaresAndReferencesTheEnableRule()
    {
        var xml = RibbonService.BuildRibbonDiffXml(
            "b", "b.CustomAction", "b.Command", "loc", "L", "wr.js", "fn",
            Array.Empty<RibbonParameter>(),
            enableRule: "OneSelected");

        var doc = XElement.Parse(xml);

        var declared = doc.Element("RuleDefinitions")!.Element("EnableRules")!.Element("EnableRule")!;
        Assert.That(declared.Attribute("Id")!.Value, Is.EqualTo("b.EnableRule"));

        var selectionCount = declared.Element("SelectionCountRule")!;
        Assert.That(selectionCount.Attribute("Minimum")!.Value, Is.EqualTo("1"));
        Assert.That(selectionCount.Attribute("Maximum")!.Value, Is.EqualTo("1"));
        Assert.That(selectionCount.Attribute("AppliesTo")!.Value, Is.EqualTo("SelectedEntity"));

        var referenced = doc.Element("CommandDefinitions")!
            .Element("CommandDefinition")!
            .Element("EnableRules")!
            .Element("EnableRule")!;
        Assert.That(referenced.Attribute("Id")!.Value, Is.EqualTo("b.EnableRule"));
    }

    // ---------------------------------------------------------------------------------------------
    // Preservation — a non-empty <CustomActions> replaces the whole collection
    // ---------------------------------------------------------------------------------------------

    [Test]
    public void BuildRibbonDiffXml_CarriesExistingNodesAlongsideTheNewButton()
    {
        // Verified the hard way: importing a diff that mentioned only the second button deleted the
        // first one. An empty <CustomActions /> is a no-op, but a non-empty one is a full replacement.
        var xml = RibbonService.BuildRibbonDiffXml(
            "b2", "b2.CustomAction", "b2.Command", "loc2", "L2", "wr.js", "fn2",
            Array.Empty<RibbonParameter>(),
            preserve: ExistingRibbon("b1"));

        var doc = XElement.Parse(xml);

        Assert.That(
            doc.Element("CustomActions")!.Elements("CustomAction").Select(e => e.Attribute("Id")!.Value),
            Is.EquivalentTo(new[] { "b1.CustomAction", "b2.CustomAction" }));
        Assert.That(
            doc.Element("CommandDefinitions")!.Elements("CommandDefinition").Select(e => e.Attribute("Id")!.Value),
            Is.EquivalentTo(new[] { "b1.Command", "b2.Command" }));
        Assert.That(
            doc.Element("RuleDefinitions")!.Element("EnableRules")!.Elements().Select(e => e.Attribute("Id")!.Value),
            Is.EquivalentTo(new[] { "b1.EnableRule" }));
    }

    [Test]
    public void BuildRibbonDiffXml_ReplacesTheNodesOfAButtonWithTheSameId()
    {
        var xml = RibbonService.BuildRibbonDiffXml(
            "b1", "b1.CustomAction", "b1.Command", "newloc", "New label", "wr.js", "fn",
            Array.Empty<RibbonParameter>(),
            preserve: ExistingRibbon("b1"));

        var actions = XElement.Parse(xml).Element("CustomActions")!.Elements("CustomAction").ToList();

        Assert.That(actions, Has.Count.EqualTo(1));
        Assert.That(actions[0].Attribute("Location")!.Value, Is.EqualTo("newloc"));
    }

    [Test]
    public void BuildRibbonDiffXml_DoesNotReSendManagedNodes()
    {
        // Managed nodes belong to their owning solution; re-sending them from an unmanaged layer would
        // fork them into the Active layer.
        var managed = new RibbonInfo(
            "sample_mcptest", 1, 0, 0,
            new[]
            {
                new RibbonDiffEntry(Guid.NewGuid(), "sys.CustomAction", 0, "Standard", true,
                    "<CustomAction Id=\"sys.CustomAction\" Location=\"loc\" />")
            },
            Array.Empty<RibbonCommandEntry>(),
            Array.Empty<RibbonRuleEntry>(),
            "<RibbonDiffXml />", null, "test");

        var xml = RibbonService.BuildRibbonDiffXml(
            "b", "b.CustomAction", "b.Command", "loc", "L", "wr.js", "fn",
            Array.Empty<RibbonParameter>(),
            preserve: managed);

        Assert.That(
            XElement.Parse(xml).Element("CustomActions")!.Elements("CustomAction").Select(e => e.Attribute("Id")!.Value),
            Is.EqualTo(new[] { "b.CustomAction" }));
    }

    [Test]
    public void ComposeRibbonDiffXml_SortsRulesIntoTheirSections()
    {
        var doc = RibbonService.ComposeRibbonDiffXml(
            Array.Empty<XElement>(),
            Array.Empty<XElement>(),
            new[]
            {
                XElement.Parse("<EnableRule Id=\"e\" />"),
                XElement.Parse("<DisplayRule Id=\"d\" />"),
                XElement.Parse("<TabDisplayRule Id=\"t\" />")
            });

        var rules = doc.Element("RuleDefinitions")!;
        Assert.Multiple(() =>
        {
            Assert.That(rules.Element("EnableRules")!.Elements().Single().Attribute("Id")!.Value, Is.EqualTo("e"));
            Assert.That(rules.Element("DisplayRules")!.Elements().Single().Attribute("Id")!.Value, Is.EqualTo("d"));
            Assert.That(rules.Element("TabDisplayRules")!.Elements().Single().Attribute("Id")!.Value, Is.EqualTo("t"));
        });
    }

    [Test]
    public void ComposeRibbonDiffXml_EmitsEmptySectionsRatherThanOmittingThem()
    {
        // An empty section is how you say "leave this collection alone" — which is also why an empty
        // <CustomActions /> cannot delete anything.
        var doc = RibbonService.ComposeRibbonDiffXml(
            Array.Empty<XElement>(), Array.Empty<XElement>(), Array.Empty<XElement>());

        Assert.Multiple(() =>
        {
            Assert.That(doc.Element("CustomActions"), Is.Not.Null);
            Assert.That(doc.Element("CommandDefinitions"), Is.Not.Null);
            Assert.That(doc.Element("Templates"), Is.Not.Null);
            Assert.That(doc.Element("LocLabels"), Is.Not.Null);
        });
    }

    private static RibbonInfo ExistingRibbon(string buttonId) => new(
        "sample_mcptest", 1, 1, 1,
        new[]
        {
            new RibbonDiffEntry(Guid.NewGuid(), $"{buttonId}.CustomAction", 0, "Standard", false,
                $"<CustomAction Id=\"{buttonId}.CustomAction\" Location=\"oldloc\" Sequence=\"41\">" +
                $"<CommandUIDefinition><Button Id=\"{buttonId}\" Command=\"{buttonId}.Command\" " +
                "LabelText=\"Old\" /></CommandUIDefinition></CustomAction>")
        },
        new[]
        {
            new RibbonCommandEntry(Guid.NewGuid(), $"{buttonId}.Command", false,
                $"<CommandDefinition Id=\"{buttonId}.Command\"><EnableRules /><DisplayRules />" +
                "<Actions /></CommandDefinition>")
        },
        new[]
        {
            new RibbonRuleEntry(Guid.NewGuid(), $"{buttonId}.EnableRule", 1, false,
                $"<EnableRule Id=\"{buttonId}.EnableRule\"><SelectionCountRule Minimum=\"1\" /></EnableRule>")
        },
        "<RibbonDiffXml />", null, "test");

    // ---------------------------------------------------------------------------------------------
    // Enable rules
    // ---------------------------------------------------------------------------------------------

    [Test]
    public void BuildEnableRule_ReturnsNothingForNoSpec()
    {
        var (element, reference) = RibbonService.BuildEnableRule("b", null);
        Assert.That(element, Is.Null);
        Assert.That(reference, Is.Null);
    }

    [Test]
    public void BuildEnableRule_AtLeastOneSelectedOmitsTheMaximum()
    {
        var (element, _) = RibbonService.BuildEnableRule("b", "AtLeastOneSelected");
        var rule = element!.Element("SelectionCountRule")!;
        Assert.That(rule.Attribute("Minimum")!.Value, Is.EqualTo("1"));
        Assert.That(rule.Attribute("Maximum"), Is.Null);
    }

    [Test]
    public void BuildEnableRule_ParsesAnExplicitRange()
    {
        var (element, _) = RibbonService.BuildEnableRule("b", "SelectionCountRule:2-5");
        var rule = element!.Element("SelectionCountRule")!;
        Assert.That(rule.Attribute("Minimum")!.Value, Is.EqualTo("2"));
        Assert.That(rule.Attribute("Maximum")!.Value, Is.EqualTo("5"));
    }

    [Test]
    public void BuildEnableRule_AcceptsALiteralFragmentAndTakesItsId()
    {
        var (element, reference) = RibbonService.BuildEnableRule(
            "b",
            "<EnableRule Id=\"my.Rule\"><CustomRule FunctionName=\"f\" Library=\"$webresource:a.js\" /></EnableRule>");

        Assert.That(reference, Is.EqualTo("my.Rule"));
        Assert.That(element!.Element("CustomRule"), Is.Not.Null);
    }

    [Test]
    public void BuildEnableRule_ThrowsOnAnUnknownShortcut()
    {
        Assert.Throws<ArgumentException>(() => RibbonService.BuildEnableRule("b", "WheneverIFeelLikeIt"));
    }

    // ---------------------------------------------------------------------------------------------
    // Parameters
    // ---------------------------------------------------------------------------------------------

    [Test]
    public void ParseParameterSpec_TreatsBareNamesAsCrmParameters()
    {
        var result = RibbonService.ParseParameterSpec("PrimaryControl, SelectedControl");

        Assert.That(result.Select(p => p.ElementName), Is.All.EqualTo("CrmParameter"));
        Assert.That(result.Select(p => p.Value), Is.EqualTo(new[] { "PrimaryControl", "SelectedControl" }));
    }

    [Test]
    public void ParseParameterSpec_MapsLiteralPrefixesToTheirElements()
    {
        var result = RibbonService.ParseParameterSpec("String:abc, Bool:true, Int:5");

        Assert.That(
            result.Select(p => p.ElementName),
            Is.EqualTo(new[] { "StringParameter", "BoolParameter", "IntParameter" }));
        Assert.That(result[0].Value, Is.EqualTo("abc"));
    }

    [Test]
    public void ParseParameterSpec_ThrowsOnAnUnknownKind()
    {
        Assert.Throws<ArgumentException>(() => RibbonService.ParseParameterSpec("Guid:abc"));
    }

    [Test]
    public void ParseParameterSpec_ReturnsEmptyForBlank()
    {
        Assert.That(RibbonService.ParseParameterSpec(null), Is.Empty);
        Assert.That(RibbonService.ParseParameterSpec("  "), Is.Empty);
    }

    // ---------------------------------------------------------------------------------------------
    // customizations.xml patching
    // ---------------------------------------------------------------------------------------------

    [Test]
    public void PatchCustomizationsXml_ReplacesTheRibbonAndDropsEntityInfo()
    {
        var ribbon = RibbonService.BuildRibbonDiffXml(
            "b", "b.CustomAction", "b.Command",
            "Mscrm.HomepageGrid.sample_mcptest.MainTab.Management.Controls._children",
            "L", "wr.js", "fn", Array.Empty<RibbonParameter>());

        var patched = RibbonService.PatchCustomizationsXml(ExportedCustomizations, "sample_mcptest", ribbon);
        var entity = XDocument.Parse(patched).Descendants("Entity").Single();

        // Left in, EntityInfo makes every ribbon import rewrite the whole table property block.
        Assert.That(entity.Element("EntityInfo"), Is.Null);
        Assert.That(entity.Element("Name")!.Value, Is.EqualTo("sample_mcptest"));
        Assert.That(entity.Element("RibbonDiffXml")!.Descendants("CustomAction").Count(), Is.EqualTo(1));
    }

    [Test]
    public void PatchCustomizationsXml_CanKeepEntityInfoWhenAskedTo()
    {
        var patched = RibbonService.PatchCustomizationsXml(
            ExportedCustomizations, "sample_mcptest", "<RibbonDiffXml />", stripEntityInfo: false);

        Assert.That(XDocument.Parse(patched).Descendants("EntityInfo").Count(), Is.EqualTo(1));
    }

    [Test]
    public void PatchCustomizationsXml_ThrowsWhenTheTableIsNotInTheExport()
    {
        Assert.Throws<InvalidOperationException>(
            () => RibbonService.PatchCustomizationsXml(ExportedCustomizations, "account", "<RibbonDiffXml />"));
    }

    // ---------------------------------------------------------------------------------------------
    // Zip repacking
    // ---------------------------------------------------------------------------------------------

    [Test]
    public void ReplaceRibbonDiffXml_KeepsAllThreeRootEntriesIncludingContentTypes()
    {
        var original = BuildSolutionZip();

        var patched = RibbonService.ReplaceRibbonDiffXml(
            original, "sample_mcptest", "<RibbonDiffXml><CustomActions /></RibbonDiffXml>");

        using var zip = new ZipArchive(new MemoryStream(patched), ZipArchiveMode.Read);
        var names = zip.Entries.Select(e => e.FullName).ToList();

        // Compress-Archive silently drops [Content_Types].xml because the brackets read as a wildcard,
        // which produces an archive Dataverse will not accept as a solution.
        Assert.That(names, Does.Contain("[Content_Types].xml"));
        Assert.That(names, Does.Contain("customizations.xml"));
        Assert.That(names, Does.Contain("solution.xml"));
        // Everything has to sit at the archive root.
        Assert.That(names.All(n => !n.Contains('/')), Is.True);
    }

    [Test]
    public void ReplaceRibbonDiffXml_WritesThePatchedCustomizationsAndLeavesOthersUntouched()
    {
        var patched = RibbonService.ReplaceRibbonDiffXml(
            BuildSolutionZip(), "sample_mcptest", "<RibbonDiffXml><CustomActions><CustomAction Id=\"x\" /></CustomActions></RibbonDiffXml>");

        using var zip = new ZipArchive(new MemoryStream(patched), ZipArchiveMode.Read);

        using var customizations = new StreamReader(zip.GetEntry("customizations.xml")!.Open());
        var text = customizations.ReadToEnd();
        Assert.That(text, Does.Contain("<CustomAction Id=\"x\""));
        Assert.That(text, Does.Not.Contain("<EntityInfo>"));

        using var solution = new StreamReader(zip.GetEntry("solution.xml")!.Open());
        Assert.That(solution.ReadToEnd(), Is.EqualTo("<ImportExportXml />"));
    }

    [Test]
    public void ReplaceRibbonDiffXml_ThrowsWhenCustomizationsXmlIsMissing()
    {
        var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            zip.CreateEntry("solution.xml");
        }

        Assert.Throws<InvalidOperationException>(
            () => RibbonService.ReplaceRibbonDiffXml(stream.ToArray(), "sample_mcptest", "<RibbonDiffXml />"));
    }

    // ---------------------------------------------------------------------------------------------
    // Reassembly
    // ---------------------------------------------------------------------------------------------

    [Test]
    public void AssembleRibbonDiffXml_GroupsTheStoredRowsIntoOneDocument()
    {
        var xml = RibbonService.AssembleRibbonDiffXml(
            new[]
            {
                new RibbonDiffEntry(Guid.NewGuid(), "a.CustomAction", 0, "Standard", false,
                    "<CustomAction Id=\"a.CustomAction\" Location=\"loc\" />")
            },
            new[]
            {
                new RibbonCommandEntry(Guid.NewGuid(), "a.Command", false,
                    "<CommandDefinition Id=\"a.Command\" />")
            },
            new[]
            {
                new RibbonRuleEntry(Guid.NewGuid(), "a.EnableRule", 1, false,
                    "<EnableRule Id=\"a.EnableRule\" />")
            });

        var doc = XElement.Parse(xml);
        Assert.That(doc.Element("CustomActions")!.Elements("CustomAction").Count(), Is.EqualTo(1));
        Assert.That(doc.Element("CommandDefinitions")!.Elements("CommandDefinition").Count(), Is.EqualTo(1));
        Assert.That(doc.Element("RuleDefinitions")!.Elements("EnableRule").Count(), Is.EqualTo(1));
    }

    // ---------------------------------------------------------------------------------------------
    // Publish retry classification
    // ---------------------------------------------------------------------------------------------

    [TestCase("Dataverse API error 429: {\"error\":{\"code\":\"0x80071151\",\"message\":\"Cannot start the requested operation [Publish] because there is another [Import] running\"}}")]
    [TestCase("Dataverse API error 500: {\"error\":{\"code\":\"0x80044150\",\"message\":\" Sql error: Generic SQL error. Sql Number: 10054\"}}")]
    public void IsTransientPublishFailure_RetriesTheFailuresAFreshImportProduces(string message)
    {
        Assert.That(RibbonService.IsTransientPublishFailure(new HttpRequestException(message)), Is.True);
    }

    [Test]
    public void IsTransientPublishFailure_DoesNotRetryARealError()
    {
        var ex = new HttpRequestException(
            "Dataverse API error 400: {\"error\":{\"code\":\"0x80048d19\",\"message\":\"undeclared property\"}}",
            null,
            System.Net.HttpStatusCode.BadRequest);

        Assert.That(RibbonService.IsTransientPublishFailure(ex), Is.False);
    }

    [Test]
    public void IsTransientPublishFailure_RetriesOnThrottlingStatusCodes()
    {
        Assert.Multiple(() =>
        {
            Assert.That(
                RibbonService.IsTransientPublishFailure(
                    new HttpRequestException("busy", null, System.Net.HttpStatusCode.TooManyRequests)),
                Is.True);
            Assert.That(
                RibbonService.IsTransientPublishFailure(
                    new HttpRequestException("busy", null, System.Net.HttpStatusCode.ServiceUnavailable)),
                Is.True);
        });
    }

    [Test]
    public void RibbonDiffTypeEnum_MatchesTheLiveOptionSet()
    {
        Assert.Multiple(() =>
        {
            Assert.That((int)RibbonDiffType.Standard, Is.EqualTo(0));
            Assert.That((int)RibbonDiffType.Tab, Is.EqualTo(1));
            Assert.That((int)RibbonDiffType.LayoutTemplate, Is.EqualTo(2));
            Assert.That((int)RibbonDiffType.LocalizedLabel, Is.EqualTo(3));
        });
    }

    private static byte[] BuildSolutionZip()
    {
        var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            Write(zip, "[Content_Types].xml", "<Types />");
            Write(zip, "customizations.xml", ExportedCustomizations);
            Write(zip, "solution.xml", "<ImportExportXml />");
        }

        return stream.ToArray();

        static void Write(ZipArchive zip, string name, string content)
        {
            using var entryStream = zip.CreateEntry(name).Open();
            var bytes = new UTF8Encoding(false).GetBytes(content);
            entryStream.Write(bytes, 0, bytes.Length);
        }
    }
}
