namespace Dataverse.Core.Services;

using System.IO.Compression;
using System.Xml.Linq;
using Dataverse.Core.Models;

/// <summary>A code component as its <c>ControlManifest.xml</c> inside a solution zip declares it.</summary>
public sealed record PcfControlManifest(string Namespace, string Constructor, string? Version)
{
    /// <summary>
    /// The manifest name, without the publisher prefix. The stored <c>customcontrol.name</c>
    /// prepends that prefix — the manifest's <c>Crossvertise.SharePointDocumentViewer</c> is
    /// stored as <c>xv_Crossvertise.SharePointDocumentViewer</c> — so a lookup has to match on
    /// this as a suffix.
    /// </summary>
    public string QualifiedName => $"{Namespace}.{Constructor}";
}

/// <summary>
/// Reads the code-component (PCF) manifests out of a solution zip and turns a version comparison
/// against the environment into an explanation.
/// </summary>
/// <remarks>
/// Separate from <see cref="SolutionService"/> because none of it needs the HTTP client: it is pure
/// zip and version arithmetic, and it is worth testing on its own.
/// </remarks>
public static class CustomControlManifestReader
{
    /// <summary>
    /// Read the code-component manifests out of a solution zip. Best-effort: a zip without controls,
    /// an unreadable archive or malformed manifest XML simply yields nothing to check.
    /// </summary>
    public static IReadOnlyList<PcfControlManifest> Read(byte[] zipBytes)
    {
        var manifests = new List<PcfControlManifest>();
        try
        {
            using var stream = new MemoryStream(zipBytes, writable: false);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

            foreach (var entry in archive.Entries)
            {
                if (!entry.Name.Equals("ControlManifest.xml", StringComparison.OrdinalIgnoreCase)
                    && !entry.Name.Equals("ControlManifest.Input.xml", StringComparison.OrdinalIgnoreCase))
                    continue;

                using var entryStream = entry.Open();
                XDocument xdoc;
                try
                {
                    xdoc = XDocument.Load(entryStream);
                }
                catch (System.Xml.XmlException)
                {
                    continue;
                }

                foreach (var control in xdoc.Descendants().Where(e => e.Name.LocalName == "control"))
                {
                    var ns = (string?)control.Attribute("namespace");
                    var ctor = (string?)control.Attribute("constructor");
                    if (string.IsNullOrWhiteSpace(ns) || string.IsNullOrWhiteSpace(ctor))
                        continue;

                    manifests.Add(new PcfControlManifest(ns!, ctor!, (string?)control.Attribute("version")));
                }
            }
        }
        catch (Exception)
        {
            // Not a readable zip — nothing to verify. Never let the check fail the import report.
        }

        return manifests;
    }


    /// <summary>
    /// Turn confirmed version mismatches into an explanation the caller can act on.
    /// </summary>
    /// <remarks>
    /// A solution import only applies a code component when the manifest version is <i>higher</i>
    /// than the stored one: "Every update in the component needs a component version bump to be
    /// reflected on the Microsoft Dataverse server"
    /// (learn.microsoft.com/power-apps/developer/component-framework/issues-and-workarounds). The
    /// import job still marks the component <c>result="success"</c>, so the old code keeps running
    /// with nothing in the result to say so — which is exactly what this warning is for.
    /// <c>pac pcf push</c> appears to work in the same situation because it deliberately "bypasses
    /// the code component versioning requirements".
    /// </remarks>
    public static IReadOnlyList<string> BuildWarnings(
        IReadOnlyList<CustomControlVersionCheck> checks)
    {
        var warnings = new List<string>();

        foreach (var check in checks.Where(c => c.Matches == false))
        {
            var name = check.StoredName ?? check.ManifestName;
            var storedIsNewer = CompareVersions(check.StoredVersion, check.ManifestVersion) > 0;

            warnings.Add(storedIsNewer
                ? $"Custom control '{name}' was NOT updated: the zip declares version "
                  + $"{check.ManifestVersion}, but the environment already stores {check.StoredVersion}. "
                  + "A solution import only applies a code component when the manifest version is "
                  + "higher than the stored one, and it reports success either way. Raise the version "
                  + $"in ControlManifest.xml above {check.StoredVersion} and import again, or push the "
                  + "control directly with `pac pcf push --publisher-prefix <prefix>`, which bypasses "
                  + "the version check."
                : $"Custom control '{name}' still stores version {check.StoredVersion} although the "
                  + $"zip declares {check.ManifestVersion} and the import reported success. The old "
                  + "code is still running. Check for an unmanaged active layer on the control "
                  + "(solution_check_layers, componentType 66) and re-import with "
                  + "overwriteUnmanaged=true, or push it with `pac pcf push --publisher-prefix "
                  + "<prefix>`.");
        }

        return warnings;
    }

    /// <summary>
    /// Compare two dotted version strings numerically, padding the shorter one with zeros.
    /// Returns 0 when either side cannot be parsed — an unknown ordering must not be reported as one.
    /// </summary>
    public static int CompareVersions(string? left, string? right)
    {
        var l = ParseVersionParts(left);
        var r = ParseVersionParts(right);
        if (l is null || r is null)
            return 0;

        for (var i = 0; i < Math.Max(l.Length, r.Length); i++)
        {
            var lv = i < l.Length ? l[i] : 0;
            var rv = i < r.Length ? r[i] : 0;
            if (lv != rv)
                return lv.CompareTo(rv);
        }

        return 0;
    }

    private static int[]? ParseVersionParts(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
            return null;

        var parts = version.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return null;

        var numbers = new int[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], out numbers[i]))
                return null;
        }

        return numbers;
    }
}
