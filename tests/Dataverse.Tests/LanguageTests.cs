namespace Dataverse.Tests;

using System.Text.RegularExpressions;
using NUnit.Framework;

/// <summary>
/// Fails the build when German text shows up in the repository, which is kept in English throughout.
/// </summary>
/// <remarks>
/// <para>
/// A heuristic, line by line: a line counts as German when it contains an umlaut or ß, or at least two
/// common German words that do not double as English ones. Inline code (<c>`…`</c>) is ignored. That
/// catches prose, comments and tool descriptions; it does not judge single German product terms.
/// </para>
/// <para>
/// German that is there on purpose — search keywords in a skill's description, labels the platform
/// returns in a German environment, test data reproducing real content — is listed in
/// <see cref="Allowed"/> with its reason. See the "Language" section of CLAUDE.md.
/// </para>
/// </remarks>
[TestFixture]
public sealed class LanguageTests
{
    private static string RepoRoot() =>
        Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", "..", ".."));

    private static readonly string[] ScannedRoots = ["docs", "src", "tests", "skills", "hooks", "scripts", ".claude-plugin", ".github"];

    private static readonly string[] ScannedExtensions = [".cs", ".md", ".json", ".ps1", ".sh", ".yml", ".csproj"];

    private static readonly string[] RootFiles = ["README.md", "CLAUDE.md", "CONTRIBUTING.md", "SECURITY.md", "install.ps1"];

    /// <summary>German words that are not also English words ("die", "mit", "also", "will" are left out).</summary>
    private static readonly Regex GermanWord = new(
        @"\b(und|nicht|wird|werden|wurde|oder|dass|eine|einen|einer|sich|wenn|aber|noch|beim|zum|zur|sind|kann|muss|auch|nur|bei|ist|der|das|den|dem|des|fuer|ueber|nach|wie|vom|ohne|sowie|bzw|weil|damit|hier|jetzt|immer|keine|kein)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex Umlaut = new("[äöüÄÖÜß]");

    private static readonly Regex InlineCode = new("`[^`]*`");

    /// <summary>
    /// Deliberate German, as (file, fragment of the line) with the reason. A line is exempt when it
    /// contains the fragment. Keep entries narrow — this is not a place to park new findings.
    /// </summary>
    private static readonly (string File, string Fragment, string Reason)[] Allowed =
    [
        // Search keywords: a skill is picked by its description, and requests come in German too.
        ("skills/business-process-flows/SKILL.md", "Geschäftsprozessfl", "search keyword in the skill description"),
        ("skills/modeling-patterns/SKILL.md", "description:", "search keywords in the skill description"),
        ("CLAUDE.md", "Geschäftsprozessfluss", "names the search-keyword exception"),

        // Labels Dataverse returns in a German environment — values to match, not prose.
        ("skills/solution-pipelines/SKILL.md", "Nicht gestartet", "platform status label"),
        ("skills/solution-pipelines/SKILL.md", "Wird ausgeführt", "platform status label"),
        ("skills/solution-pipelines/SKILL.md", "Überprüf", "platform status label"),
        ("skills/solution-pipelines/SKILL.md", "Bereitstellung", "platform UI label"),

        // Error texts as a German environment returns them, quoted verbatim so that they can be found.
        ("docs/classic-workflows-reference.md", "außerhalb der", "verbatim platform error text"),
        ("docs/classic-workflows-reference.md", "Dieser Workflow", "verbatim platform error text"),

        // Test data: umlaut handling and content from real designer output.
        ("tests/Dataverse.Tests/Services/PayloadSourceTests.cs", "Grüße aus München", "tests UTF-8 round trip"),
        ("tests/Dataverse.Tests/Services/PayloadSourceTests.cs", "Ümlaut", "tests UTF-8 round trip"),
        ("tests/Dataverse.Tests/Workflows/WorkflowXamlParserTests.cs", "Anruf wieder öffnen", "name of a real designer workflow in a fixture"),
        ("tests/Dataverse.Tests/Workflows/WorkflowXamlParserTests.cs", "Außendienst, Köln", "tests a literal with umlaut and comma"),

        // This file and the anonymisation guard state their rules.
        ("tests/Dataverse.Tests/LanguageTests.cs", "", "defines the word list"),
        ("README.md", "© ", "copyright line"),
    ];

    private static IEnumerable<string> FilesToScan(string root)
    {
        foreach (var relativeRoot in ScannedRoots)
        {
            var directory = Path.Combine(root, relativeRoot);
            if (!Directory.Exists(directory))
                continue;

            foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                if (path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                    || path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
                    continue;

                if (ScannedExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
                    yield return path;
            }
        }

        foreach (var file in RootFiles)
        {
            var path = Path.Combine(root, file);
            if (File.Exists(path))
                yield return path;
        }
    }

    /// <summary>Why a line reads as German, or null.</summary>
    public static string? GermanEvidence(string line)
    {
        var text = InlineCode.Replace(line, string.Empty);

        if (Umlaut.Match(text) is { Success: true } umlaut)
            return $"'{umlaut.Value}'";

        var words = GermanWord.Matches(text).Select(m => m.Value.ToLowerInvariant()).Distinct().ToList();
        return words.Count >= 2 ? string.Join(", ", words) : null;
    }

    [Test]
    public void RepositoryContent_IsEnglish()
    {
        var root = RepoRoot();
        var offenders = new List<string>();

        foreach (var path in FilesToScan(root))
        {
            var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            var lines = File.ReadAllLines(path);

            for (var i = 0; i < lines.Length; i++)
            {
                if (GermanEvidence(lines[i]) is not { } evidence)
                    continue;

                if (Allowed.Any(a => a.File == relative && lines[i].Contains(a.Fragment, StringComparison.Ordinal)))
                    continue;

                offenders.Add($"{relative}:{i + 1} ({evidence}): {lines[i].Trim()[..Math.Min(lines[i].Trim().Length, 120)]}");
            }
        }

        Assert.That(offenders, Is.Empty,
            "The repository is kept in English (see the \"Language\" section of CLAUDE.md). Translate these "
            + "lines, or — for deliberate German such as platform labels or test data — add a narrow entry "
            + "with its reason to LanguageTests.Allowed. Found:\n" + string.Join("\n", offenders));
    }

    [TestCase("Die Stage wird nicht geschrieben.", true)]
    [TestCase("Grüße", true)]
    [TestCase("The stage is not written.", false)]
    [TestCase("MIT License", false)]
    [TestCase("Use `der Wert ist` as a sample.", false)]
    public void GermanEvidence_SeparatesGermanFromEnglish(string line, bool german) =>
        Assert.That(GermanEvidence(line) is not null, Is.EqualTo(german));
}
