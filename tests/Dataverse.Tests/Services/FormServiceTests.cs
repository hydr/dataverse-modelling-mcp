namespace Dataverse.Tests.Services;

using System.Xml.Linq;
using Dataverse.Core.Services;
using NUnit.Framework;

/// <summary>
/// Covers reading FormXML as a structure and assembling it correctly.
/// </summary>
/// <remarks>
/// The assertions about code components are the important ones. Every detail here was verified
/// against forms the Maker portal generated, because getting one wrong produces an error a long way
/// from its cause — a missing form factor is rejected by the designer, the manifest name instead of
/// the stored name fails with <c>0x80160007</c>, and a control description left behind keeps
/// rendering a component the form no longer references.
/// </remarks>
[TestFixture]
public sealed class FormServiceTests
{
    /// <summary>
    /// Shaped after a real form: a code component sits in a cell as a generic control with a
    /// <c>uniqueid</c>, and the component itself is named in a separate description, once per form
    /// factor, with a static parameter.
    /// </summary>
    private const string FormXmlWithCustomControl = """
        <form>
          <tabs>
            <tab verticallayout="true" id="{091448fb-7861-4b91-b39e-6346e58c8c5a}" name="general">
              <labels><label description="Allgemein" languagecode="1031" /></labels>
              <columns>
                <column width="100%">
                  <sections>
                    <section showlabel="false" id="{5d51218b-95a6-4516-8e98-5a82c0f116df}" name="main">
                      <labels><label description="Hauptbereich" languagecode="1031" /></labels>
                      <rows>
                        <row>
                          <cell id="{483ab192-107a-4e04-b329-d767a9f44f35}">
                            <labels><label description="Name" languagecode="1031" /></labels>
                            <control id="xv_name" classid="{4273EDBD-AC1D-40d3-9FB2-095C621B552D}" datafieldname="xv_name" />
                          </cell>
                        </row>
                        <row>
                          <cell id="{2eec0dee-f02c-40eb-aa7a-9465aef35ae4}" rowspan="8" colspan="2">
                            <labels><label description="Dokumente" languagecode="1031" /></labels>
                            <control id="viewer" classid="{F9A8A302-114E-466A-B582-6771B2AE0D92}" isunbound="true" uniqueid="{c65ef1a3-ebe5-4aa9-8501-51748a9d3121}" />
                          </cell>
                        </row>
                      </rows>
                    </section>
                  </sections>
                </column>
              </columns>
            </tab>
          </tabs>
          <controlDescriptions>
            <controlDescription forControl="{c65ef1a3-ebe5-4aa9-8501-51748a9d3121}">
              <customControl name="xv_Crossvertise.SharePointDocumentViewer" formFactor="0">
                <parameters><authMode static="true" type="Enum">auto</authMode></parameters>
              </customControl>
              <customControl name="xv_Crossvertise.SharePointDocumentViewer" formFactor="1" />
              <customControl name="xv_Crossvertise.SharePointDocumentViewer" formFactor="2" />
            </controlDescription>
          </controlDescriptions>
        </form>
        """;

    // ---------------------------------------------------------------- read

    [Test]
    public void ParsesTheTreeDownToControls()
    {
        var (tabs, _) = FormService.ParseFormXml(FormXmlWithCustomControl);

        Assert.That(tabs, Has.Count.EqualTo(1));
        var section = tabs[0].Columns[0].Sections[0];

        Assert.Multiple(() =>
        {
            Assert.That(tabs[0].Label, Is.EqualTo("Allgemein"));
            Assert.That(tabs[0].Name, Is.EqualTo("general"));
            Assert.That(section.Label, Is.EqualTo("Hauptbereich"));
            Assert.That(section.Cells, Has.Count.EqualTo(2));
            Assert.That(section.Cells[0].Controls[0].DataFieldName, Is.EqualTo("xv_name"));
        });
    }

    /// <summary>Raw classids say nothing to a reader; the common ones get a name.</summary>
    [Test]
    public void NamesTheKnownClassIds()
    {
        var (tabs, _) = FormService.ParseFormXml(FormXmlWithCustomControl);
        var cells = tabs[0].Columns[0].Sections[0].Cells;

        Assert.Multiple(() =>
        {
            Assert.That(cells[0].Controls[0].ClassName, Is.EqualTo("Text"));
            Assert.That(cells[1].Controls[0].ClassName, Is.EqualTo("CustomControl"));
        });
    }

    /// <summary>
    /// The point of the structured read: a cell only carries a generic classid and a uniqueid, so
    /// without joining the control descriptions you cannot tell which component renders there.
    /// </summary>
    [Test]
    public void JoinsTheControlDescription_SoACodeComponentIsNamedAtItsCell()
    {
        var (tabs, descriptions) = FormService.ParseFormXml(FormXmlWithCustomControl);
        var control = tabs[0].Columns[0].Sections[0].Cells[1].Controls[0];

        Assert.Multiple(() =>
        {
            Assert.That(control.IsCustomControl, Is.True);
            Assert.That(control.IsUnbound, Is.True);
            Assert.That(control.UniqueId, Is.EqualTo("{c65ef1a3-ebe5-4aa9-8501-51748a9d3121}"));
            Assert.That(control.CustomControlNames,
                Is.EquivalentTo(new[] { "xv_Crossvertise.SharePointDocumentViewer" }));

            Assert.That(descriptions, Has.Count.EqualTo(1));
            Assert.That(descriptions[0].CustomControls, Has.Count.EqualTo(3));
            Assert.That(descriptions[0].CustomControls[0].Parameters["authMode"], Is.EqualTo("auto"));
        });
    }

    [Test]
    public void KeepsCellSpans()
    {
        var (tabs, _) = FormService.ParseFormXml(FormXmlWithCustomControl);
        var cell = tabs[0].Columns[0].Sections[0].Cells[1];

        Assert.Multiple(() =>
        {
            Assert.That(cell.RowSpan, Is.EqualTo(8));
            Assert.That(cell.ColSpan, Is.EqualTo(2));
        });
    }

    /// <summary>A broken form should still be listable and readable down to its metadata.</summary>
    [TestCase("")]
    [TestCase("<form><tabs>")]
    public void ReturnsAnEmptyStructure_RatherThanThrowing_OnUnusableXml(string xml)
    {
        var (tabs, descriptions) = FormService.ParseFormXml(xml);

        Assert.Multiple(() =>
        {
            Assert.That(tabs, Is.Empty);
            Assert.That(descriptions, Is.Empty);
        });
    }

    [TestCase(2, "Main")]
    [TestCase(6, "QuickViewForm")]
    [TestCase(7, "QuickCreate")]
    [TestCase(8, "Dialog")]
    [TestCase(0, "Dashboard")]
    [TestCase(99, "Type99")]
    public void MapsTheFormTypeCode(int type, string expected) =>
        Assert.That(FormService.MapFormType(type), Is.EqualTo(expected));

    // ---------------------------------------------------------------- write

    /// <summary>
    /// A code component always gets the generic classid — its own name goes in the description, not
    /// in the cell.
    /// </summary>
    [Test]
    public void BuildCell_UsesTheGenericClassIdAndAUniqueId_ForACodeComponent()
    {
        var (cell, uniqueId) = FormService.BuildCell(
            new FormControlSpec(CustomControlName: "xv_Ns.Ctrl", Label: "Docs"), 1031);

        var control = cell.Elements().Single(e => e.Name.LocalName == "control");

        Assert.Multiple(() =>
        {
            Assert.That((string?)control.Attribute("classid"), Is.EqualTo(FormService.CustomControlClassId));
            Assert.That(uniqueId, Is.Not.Null);
            Assert.That((string?)control.Attribute("uniqueid"), Is.EqualTo(uniqueId));
            Assert.That((string?)control.Attribute("isunbound"), Is.EqualTo("true"),
                "no dataFieldName was given, so the control is unbound");
        });
    }

    [Test]
    public void BuildCell_BindsTheControl_WhenAColumnWasGiven()
    {
        var (cell, _) = FormService.BuildCell(
            new FormControlSpec(DataFieldName: "xv_documentviewer", CustomControlName: "xv_Ns.Ctrl"), 1031);

        var control = cell.Elements().Single(e => e.Name.LocalName == "control");

        Assert.Multiple(() =>
        {
            Assert.That((string?)control.Attribute("datafieldname"), Is.EqualTo("xv_documentviewer"));
            Assert.That(control.Attribute("isunbound"), Is.Null);
            Assert.That((string?)control.Attribute("id"), Is.EqualTo("xv_documentviewer"));
        });
    }

    [Test]
    public void BuildCell_UsesTheFormsOwnLanguage_ForTheLabel()
    {
        var (cell, _) = FormService.BuildCell(
            new FormControlSpec(CustomControlName: "xv_Ns.Ctrl", Label: "Dokumente"), 1031);

        var label = cell.Descendants().Single(e => e.Name.LocalName == "label");

        Assert.Multiple(() =>
        {
            Assert.That((string?)label.Attribute("description"), Is.EqualTo("Dokumente"));
            Assert.That((string?)label.Attribute("languagecode"), Is.EqualTo("1031"));
        });
    }

    [Test]
    public void BuildCell_Throws_WhenNeitherAColumnNorAComponentWasGiven()
    {
        Assert.Throws<ArgumentException>(() => FormService.BuildCell(new FormControlSpec(), 1033));
    }

    [Test]
    public void BuildCell_Throws_WhenAPlainFieldControlHasNoClassId()
    {
        Assert.Throws<ArgumentException>(() =>
            FormService.BuildCell(new FormControlSpec(DataFieldName: "xv_name"), 1033));
    }

    /// <summary>
    /// A description that omits a form factor makes the designer reject the whole form with
    /// "Custom control declaration for form factor(s) 0,1,2 is missing".
    /// </summary>
    [Test]
    public void AddControlDescription_DeclaresAllThreeFormFactors()
    {
        var doc = XDocument.Parse("<form><tabs /></form>");

        FormService.AddControlDescription(
            doc, "{11111111-1111-1111-1111-111111111111}",
            new FormControlSpec(CustomControlName: "xv_Ns.Ctrl"));

        var controls = doc.Descendants().Where(e => e.Name.LocalName == "customControl").ToList();

        Assert.That(controls, Has.Count.EqualTo(3));
        Assert.That(
            controls.Select(c => (string?)c.Attribute("formFactor")),
            Is.EqualTo(new[] { "0", "1", "2" }));
    }

    [Test]
    public void AddControlDescription_UsesTheStoredComponentName_Verbatim()
    {
        var doc = XDocument.Parse("<form><tabs /></form>");

        FormService.AddControlDescription(
            doc, "{1}", new FormControlSpec(CustomControlName: "xv_Crossvertise.SharePointDocumentViewer"));

        Assert.That(
            doc.Descendants().Where(e => e.Name.LocalName == "customControl")
                .Select(c => (string?)c.Attribute("name")).Distinct().Single(),
            Is.EqualTo("xv_Crossvertise.SharePointDocumentViewer"),
            "The prefixed name is what customcontrol.name holds; the manifest name fails with 0x80160007.");
    }

    /// <summary>
    /// A fixed configuration value carries <c>static="true"</c>; a property bound to a column must
    /// not, or the control renders empty.
    /// </summary>
    [Test]
    public void AddControlDescription_MarksStaticParameters_ButNotBoundOnes()
    {
        var doc = XDocument.Parse("<form><tabs /></form>");

        FormService.AddControlDescription(doc, "{1}", new FormControlSpec(
            CustomControlName: "xv_Ns.Ctrl",
            Parameters: new Dictionary<string, FormControlParameter>
            {
                ["authMode"] = new("auto", Static: true, Type: "Enum"),
                ["boundField"] = new("xv_documentviewer", Static: false)
            }));

        var parameters = doc.Descendants().First(e => e.Name.LocalName == "parameters");
        var authMode = parameters.Elements().Single(e => e.Name.LocalName == "authMode");
        var boundField = parameters.Elements().Single(e => e.Name.LocalName == "boundField");

        Assert.Multiple(() =>
        {
            Assert.That((string?)authMode.Attribute("static"), Is.EqualTo("true"));
            Assert.That((string?)authMode.Attribute("type"), Is.EqualTo("Enum"));
            Assert.That(authMode.Value, Is.EqualTo("auto"));

            Assert.That(boundField.Attribute("static"), Is.Null);
            Assert.That(boundField.Value, Is.EqualTo("xv_documentviewer"));
        });
    }

    /// <summary>
    /// A description whose control is gone points at nothing. Leaving it behind is how a removed
    /// component keeps turning up.
    /// </summary>
    [Test]
    public void RemoveOrphanedControlDescriptions_DropsOnlyTheDanglingOnes()
    {
        var doc = XDocument.Parse(FormXmlWithCustomControl);
        FormService.AddControlDescription(doc, "{dead-beef}", new FormControlSpec(CustomControlName: "xv_Gone"));

        FormService.RemoveOrphanedControlDescriptions(doc);

        var remaining = doc.Descendants()
            .Where(e => e.Name.LocalName == "controlDescription")
            .Select(e => (string?)e.Attribute("forControl"))
            .ToList();

        Assert.That(remaining, Is.EquivalentTo(new[] { "{c65ef1a3-ebe5-4aa9-8501-51748a9d3121}" }));
    }

    /// <summary>
    /// A tab whose id, name and label all differ should be findable by any of them — a caller has
    /// whichever one they happened to read.
    /// </summary>
    [TestCase("{091448fb-7861-4b91-b39e-6346e58c8c5a}")]
    [TestCase("091448fb-7861-4b91-b39e-6346e58c8c5a")]
    [TestCase("general")]
    [TestCase("Allgemein")]
    [TestCase("ALLGEMEIN")]
    public void FindsATab_ByIdNameOrLabel(string key)
    {
        var doc = XDocument.Parse(FormXmlWithCustomControl);

        Assert.That(FormService.FindTabForTest(doc, key), Is.Not.Null);
    }

    [Test]
    public void DoesNotFindATab_ThatIsNotThere()
    {
        var doc = XDocument.Parse(FormXmlWithCustomControl);

        Assert.That(FormService.FindTabForTest(doc, "nosuchtab"), Is.Null);
    }

    // ---------------------------------------------------------------- spec parsing

    [Test]
    public void ParseSpec_AcceptsABareStringAsAStaticParameter()
    {
        var spec = FormControlSpec.Parse("""
            { "customControlName": "xv_Ns.Ctrl", "parameters": { "authMode": "auto" } }
            """);

        Assert.Multiple(() =>
        {
            Assert.That(spec.Parameters!["authMode"].Value, Is.EqualTo("auto"));
            Assert.That(spec.Parameters!["authMode"].Static, Is.True,
                "A bare value is a fixed configuration value, which is the common case.");
        });
    }

    [Test]
    public void ParseSpec_ReadsTheFullParameterForm()
    {
        var spec = FormControlSpec.Parse("""
            {
              "customControlName": "xv_Ns.Ctrl",
              "dataFieldName": "xv_col",
              "label": "Docs",
              "rowSpan": 8,
              "colSpan": 2,
              "parameters": { "boundField": { "value": "xv_col", "static": false } }
            }
            """);

        Assert.Multiple(() =>
        {
            Assert.That(spec.DataFieldName, Is.EqualTo("xv_col"));
            Assert.That(spec.Label, Is.EqualTo("Docs"));
            Assert.That(spec.RowSpan, Is.EqualTo(8));
            Assert.That(spec.ColSpan, Is.EqualTo(2));
            Assert.That(spec.Parameters!["boundField"].Static, Is.False);
        });
    }
}
