namespace Dataverse.Tests.Services;

using System.IO.Compression;
using System.Text;
using Dataverse.Core.Models;
using Dataverse.Core.Services;
using NUnit.Framework;

/// <summary>
/// Guards the post-import check for code components (PCF).
/// </summary>
/// <remarks>
/// A solution import reports success — down to <c>result="success"</c> for the control in the import
/// job — and still leaves an existing code component on its old version, because it only applies one
/// whose <c>ControlManifest</c> version is higher than the stored one
/// (learn.microsoft.com/power-apps/developer/component-framework/issues-and-workarounds).
/// Diagnosing that from the outside costs an hour, so the warning has to name the actual cause.
/// </remarks>
[TestFixture]
public sealed class CustomControlManifestReaderTests
{
    private static byte[] BuildZip(params (string Path, string Content)[] entries)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, content) in entries)
            {
                var entry = archive.CreateEntry(path);
                using var stream = entry.Open();
                stream.Write(Encoding.UTF8.GetBytes(content));
            }
        }
        return buffer.ToArray();
    }

    private const string Manifest = """
        <?xml version="1.0" encoding="utf-8"?>
        <manifest>
          <control namespace="Contoso" constructor="DocumentViewer" version="1.0.0"
                   display-name-key="Viewer" description-key="Viewer_Desc" control-type="standard">
            <property name="boundField" of-type="SingleLine.Text" usage="bound" required="true" />
          </control>
        </manifest>
        """;

    [Test]
    public void Read_FindsTheControl_AndItsVersion()
    {
        var zip = BuildZip(
            ("solution.xml", "<ImportExportXml />"),
            ("Controls/sample_Contoso.DocumentViewer/ControlManifest.xml", Manifest));

        var manifests = CustomControlManifestReader.Read(zip);

        Assert.That(manifests, Has.Count.EqualTo(1));
        Assert.Multiple(() =>
        {
            Assert.That(manifests[0].Namespace, Is.EqualTo("Contoso"));
            Assert.That(manifests[0].Constructor, Is.EqualTo("DocumentViewer"));
            Assert.That(manifests[0].Version, Is.EqualTo("1.0.0"));

            // The stored customcontrol.name carries a publisher prefix that the manifest does not,
            // so the qualified name is only ever usable as a suffix.
            Assert.That(manifests[0].QualifiedName, Is.EqualTo("Contoso.DocumentViewer"));
        });
    }

    [Test]
    public void Read_AlsoAcceptsControlManifestInputXml()
    {
        var zip = BuildZip(("Controls/Foo/ControlManifest.Input.xml", Manifest));

        Assert.That(CustomControlManifestReader.Read(zip), Has.Count.EqualTo(1));
    }

    [Test]
    public void Read_ReturnsNothing_ForASolutionWithoutControls()
    {
        var zip = BuildZip(("solution.xml", "<ImportExportXml />"), ("customizations.xml", "<x />"));

        Assert.That(CustomControlManifestReader.Read(zip), Is.Empty);
    }

    /// <summary>
    /// The check is a diagnostic bolted onto a successful import — it must never turn a good import
    /// into a failure just because the payload was not a readable zip.
    /// </summary>
    [Test]
    public void Read_ReturnsNothing_ForGarbageInsteadOfThrowing()
    {
        Assert.That(CustomControlManifestReader.Read([1, 2, 3, 4]), Is.Empty);
    }

    [Test]
    public void Read_SkipsMalformedManifestXml()
    {
        var zip = BuildZip(
            ("Controls/Broken/ControlManifest.xml", "<manifest><control namespace="),
            ("Controls/Good/ControlManifest.xml", Manifest));

        Assert.That(CustomControlManifestReader.Read(zip), Has.Count.EqualTo(1));
    }

    [Test]
    public void Read_SkipsAControlWithoutNamespaceOrConstructor()
    {
        var zip = BuildZip(("Controls/X/ControlManifest.xml",
            "<manifest><control version=\"1.0.0\" /></manifest>"));

        Assert.That(CustomControlManifestReader.Read(zip), Is.Empty);
    }

    // ---------------------------------------------------------------- warnings

    private static CustomControlVersionCheck Check(string? manifestVersion, string? storedVersion)
    {
        bool? matches = manifestVersion is null || storedVersion is null
            ? null
            : manifestVersion == storedVersion;

        return new CustomControlVersionCheck(
            ManifestName: "Contoso.DocumentViewer",
            StoredName: "sample_Contoso.DocumentViewer",
            ManifestVersion: manifestVersion,
            StoredVersion: storedVersion,
            Matches: matches);
    }

    [Test]
    public void BuildWarnings_SaysNothing_WhenTheVersionsMatch()
    {
        Assert.That(CustomControlManifestReader.BuildWarnings([Check("1.0.0", "1.0.0")]), Is.Empty);
    }

    /// <summary>
    /// Unknown is not the same as wrong: a control that is not in the environment yet (first import)
    /// or a manifest without a version must not produce a warning.
    /// </summary>
    [TestCase(null, "1.0.0")]
    [TestCase("1.0.0", null)]
    [TestCase(null, null)]
    public void BuildWarnings_SaysNothing_WhenTheComparisonIsUndecidable(string? manifest, string? stored)
    {
        Assert.That(CustomControlManifestReader.BuildWarnings([Check(manifest, stored)]), Is.Empty);
    }

    /// <summary>
    /// The case that cost the reporting session an hour: manifests at 0.0.1 / 0.0.2 / 1.0.0 imported
    /// against a stored 1.1.1. Every import legitimately skipped the control and reported success.
    /// </summary>
    [Test]
    public void BuildWarnings_ExplainsTheVersionRule_WhenTheStoredVersionIsHigher()
    {
        var warnings = CustomControlManifestReader.BuildWarnings([Check("1.0.0", "1.1.1")]);

        Assert.That(warnings, Has.Count.EqualTo(1));
        Assert.Multiple(() =>
        {
            Assert.That(warnings[0], Does.Contain("was NOT updated"));
            Assert.That(warnings[0], Does.Contain("1.1.1"));
            Assert.That(warnings[0], Does.Contain("higher than the stored one"));
            Assert.That(warnings[0], Does.Contain("pac pcf push"));
        });
    }

    /// <summary>
    /// The other direction — the zip is ahead and the import still did not apply it — is a layering
    /// problem, not a version problem, so it must not be explained as one.
    /// </summary>
    [Test]
    public void BuildWarnings_PointsAtLayering_WhenTheStoredVersionIsBehind()
    {
        var warnings = CustomControlManifestReader.BuildWarnings([Check("2.0.0", "1.0.0")]);

        Assert.That(warnings, Has.Count.EqualTo(1));
        Assert.Multiple(() =>
        {
            Assert.That(warnings[0], Does.Contain("still stores version 1.0.0"));
            Assert.That(warnings[0], Does.Contain("solution_check_layers"));
            Assert.That(warnings[0], Does.Not.Contain("higher than the stored one"));
        });
    }

    [TestCase("1.0.0", "1.0.0", 0)]
    [TestCase("1.1.1", "1.0.0", 1)]
    [TestCase("1.0.0", "1.1.1", -1)]
    [TestCase("1.0.0", "1.0", 0)]
    [TestCase("1.0.1", "1.0", 1)]
    [TestCase("2", "1.9.9", 1)]
    [TestCase("1.0.10", "1.0.9", 1)]
    public void CompareVersions_OrdersNumerically_NotLexically(string left, string right, int expected)
    {
        Assert.That(Math.Sign(CustomControlManifestReader.CompareVersions(left, right)), Is.EqualTo(expected));
    }

    /// <summary>
    /// An unparseable version means the ordering is unknown, and unknown must not be reported as
    /// equal-or-greater by accident — 0 keeps the caller from claiming a direction.
    /// </summary>
    [TestCase("1.0.0-preview", "1.0.0")]
    [TestCase(null, "1.0.0")]
    [TestCase("", "1.0.0")]
    public void CompareVersions_ReturnsZero_WhenEitherSideCannotBeParsed(string? left, string? right)
    {
        Assert.That(CustomControlManifestReader.CompareVersions(left, right), Is.Zero);
    }
}
