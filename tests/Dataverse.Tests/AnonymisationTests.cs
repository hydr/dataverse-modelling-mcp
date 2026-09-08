namespace Dataverse.Tests;

using System.Text.RegularExpressions;
using NUnit.Framework;

/// <summary>
/// Fails the build when an identifier from a real environment reaches the public repository.
/// </summary>
/// <remarks>
/// The repository is deliberately anonymised. Relying on anyone remembering that did not work: a
/// release once shipped dozens of a customer's publisher-prefixed table, column, solution and code
/// component names, picked up from live testing, and a published release cannot be taken back. So
/// the build says no instead.
/// <para>
/// The publisher's own identity is a different matter and stays — see <see cref="IdentityFiles"/>.
/// </para>
/// </remarks>
[TestFixture]
public sealed class AnonymisationTests
{
    private static string RepoRoot()
    {
        var root = Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", "..", "..");
        return Path.GetFullPath(root);
    }

    /// <summary>
    /// Substrings that must not appear. Kept as fragments so this file does not trip its own test —
    /// a literal would be a match in the scan below.
    /// </summary>
    private static readonly string[] ForbiddenFragments =
    [
        "xv" + "_",          // a real org's publisher prefix; use sample_ instead
        "cross" + "vertise", // as an artifact name; the publisher identity is allowlisted below
        "xv" + "dev",        // environment names
        "xv" + "staging"
    ];

    /// <summary>
    /// Files that legitimately name the publisher: licence, security contact, package metadata.
    /// </summary>
    private static readonly string[] IdentityFiles =
    [
        "LICENSE",
        "SECURITY.md",
        "README.md",
        "CONTRIBUTING.md",
        Path.Combine(".claude-plugin", "plugin.json"),
        Path.Combine("src", "Dataverse.Setup", "Dataverse.Setup.csproj")
    ];

    /// <summary>
    /// Exceptions with a reason. Anything here is a deliberate, reviewed decision — not a place to
    /// park new findings.
    /// </summary>
    private static readonly Dictionary<string, string> Allowed = new()
    {
        // Addresses a real environment and needs a real MetadataId as its fixture. Predates the
        // rule and is skipped rather than broken.
        [Path.Combine("tests", "Dataverse.Tests", "Integration", "SolutionIntegrationTests.cs")] =
            "integration fixture against a real environment",

        // States the rule and therefore has to name what it forbids.
        ["CLAUDE.md"] = "documents the rule itself",

        // This file.
        [Path.Combine("tests", "Dataverse.Tests", "AnonymisationTests.cs")] = "enforces the rule"
    };

    private static readonly string[] ScannedRoots = ["docs", "src", "tests", "skills", "hooks", "scripts"];

    private static readonly string[] ScannedExtensions = [".cs", ".md", ".json", ".ps1", ".sh", ".xml", ".yml"];

    private static IEnumerable<string> FilesToScan(string root)
    {
        foreach (var relativeRoot in ScannedRoots)
        {
            var directory = Path.Combine(root, relativeRoot);
            if (!Directory.Exists(directory))
                continue;

            foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                // Build output is generated, not authored.
                if (path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                    || path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
                    continue;

                if (ScannedExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
                    yield return path;
            }
        }

        foreach (var identityFile in new[] { "CLAUDE.md" })
        {
            var path = Path.Combine(root, identityFile);
            if (File.Exists(path))
                yield return path;
        }
    }

    [Test]
    public void NoIdentifierFromARealEnvironment_ReachesTheRepository()
    {
        var root = RepoRoot();
        var offenders = new List<string>();

        foreach (var path in FilesToScan(root))
        {
            var relative = Path.GetRelativePath(root, path);

            if (Allowed.ContainsKey(relative) || IdentityFiles.Contains(relative, StringComparer.OrdinalIgnoreCase))
                continue;

            var text = File.ReadAllText(path);
            foreach (var fragment in ForbiddenFragments)
            {
                if (!text.Contains(fragment, StringComparison.OrdinalIgnoreCase))
                    continue;

                var line = text[..text.IndexOf(fragment, StringComparison.OrdinalIgnoreCase)]
                    .Count(c => c == '\n') + 1;
                offenders.Add($"{relative}:{line} contains '{fragment}'");
            }
        }

        Assert.That(offenders, Is.Empty,
            "Identifiers from a real environment must not reach the public repository — use sample_* "
            + "for tables and columns, Contoso* for solutions and namespaces, and synthetic GUIDs. "
            + "See the \"Keine Firmen-interna im öffentlichen Repo\" section of CLAUDE.md. Found:\n"
            + string.Join("\n", offenders));
    }

    /// <summary>
    /// A personal name or address is a different class of problem from an internal identifier, and
    /// nobody should have to notice it in review.
    /// </summary>
    [Test]
    public void NoPersonalEmailAddress_ReachesTheRepository()
    {
        var root = RepoRoot();

        // A personal address looks like f.lastname@… — a role mailbox (it@, security@) does not.
        var personal = new Regex(@"\b[a-z]\.[a-z]{2,}@[a-z0-9.-]+\.[a-z]{2,}\b", RegexOptions.IgnoreCase);
        var offenders = new List<string>();

        foreach (var path in FilesToScan(root))
        {
            var relative = Path.GetRelativePath(root, path);
            if (relative.Equals(Path.Combine("tests", "Dataverse.Tests", "AnonymisationTests.cs"), StringComparison.OrdinalIgnoreCase))
                continue;

            foreach (var match in personal.Matches(File.ReadAllText(path)).Cast<Match>())
                offenders.Add($"{relative}: {match.Value}");
        }

        Assert.That(offenders, Is.Empty,
            "Personal email addresses must not reach the repository. Use a role mailbox instead. Found:\n"
            + string.Join("\n", offenders));
    }
}
