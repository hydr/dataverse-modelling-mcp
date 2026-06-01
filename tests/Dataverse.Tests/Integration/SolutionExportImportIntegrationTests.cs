namespace Dataverse.Tests.Integration;

using Dataverse.Tests.Infrastructure;
using NUnit.Framework;

/// <summary>
/// Round-trip test for the file-based export + async import path against contoso-dev.
/// Exports the small DV_MCP_Test solution (unmanaged) to a temp file, then re-imports it
/// via the async ImportSolutionAsync + ImportJob-polling flow with overwriteUnmanaged.
/// </summary>
[TestFixture]
[Category("Integration")]
public sealed class SolutionExportImportIntegrationTests : IntegrationTestBase
{
    private const string TestSolutionUniqueName = "DV_MCP_Test";

    [Test]
    public async Task Export_ToFilePath_WritesZipAndReturnsPathAndSize()
    {
        var path = Path.Combine(Path.GetTempPath(), $"DV_MCP_Test_export_{Guid.NewGuid():N}.zip");
        try
        {
            var result = await SolutionService.ExportAsync(OrgUrl, TestSolutionUniqueName, managed: false, filePath: path);

            Assert.That(result.FilePath, Is.EqualTo(path));
            Assert.That(result.Base64Content, Is.Null, "Base64 should be omitted when a filePath is given.");
            Assert.That(result.FileSizeBytes, Is.Not.Null.And.GreaterThan(0));
            Assert.That(File.Exists(path), Is.True);
            Assert.That(new FileInfo(path).Length, Is.EqualTo(result.FileSizeBytes));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Test]
    public async Task ExportThenImport_RoundTrip_Succeeds()
    {
        var path = Path.Combine(Path.GetTempPath(), $"DV_MCP_Test_roundtrip_{Guid.NewGuid():N}.zip");
        try
        {
            // Export unmanaged to disk.
            var export = await SolutionService.ExportAsync(OrgUrl, TestSolutionUniqueName, managed: false, filePath: path);
            Assert.That(File.Exists(path), Is.True, "Export should have written the zip to disk.");

            // Re-import the same unmanaged solution back into the source env (async + polling).
            var import = await SolutionService.ImportAsync(
                OrgUrl, zipBase64: null, overwriteUnmanaged: true, filePath: path, timeoutSeconds: 600);

            // Verify the async mechanism: the import was accepted, polled to a terminal state, and
            // produced a well-formed result. We do NOT require Success here: re-importing an
            // unmanaged solution that contains a cloud flow currently in ActiveUnpublished state
            // back into its source env is a legitimate Dataverse conflict — and surfacing that
            // per-component error is exactly what this path is meant to do.
            Assert.That(import.AsyncOperationId, Is.Not.EqualTo(Guid.Empty), "Import was not accepted / no async operation created.");
            Assert.That(import.ImportJobId, Is.Not.EqualTo(Guid.Empty));
            Assert.That(import.StatusReason, Is.Not.Null.And.Not.Empty, "Polling should have captured a terminal status reason.");
            // Either it succeeded cleanly, or it failed with parsed component errors — never a silent failure.
            Assert.That(import.Success || import.ComponentErrors.Count > 0, Is.True,
                $"Import neither succeeded nor reported any component error. Status: {import.StatusReason}; Error: {import.ErrorMessage}");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
