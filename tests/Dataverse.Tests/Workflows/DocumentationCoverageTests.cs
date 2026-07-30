namespace Dataverse.Tests.Workflows;

using System.Text.RegularExpressions;
using NUnit.Framework;

/// <summary>
/// Checks that the documentation kept up with the code.
/// </summary>
/// <remarks>
/// The skill and the format reference are the only way an agent learns this format — a validation code
/// or a model field that exists but is undocumented is invisible in practice, and that has happened
/// repeatedly: fixes went in, the docs lagged behind. These tests fail the build instead of relying on
/// anyone remembering.
/// </remarks>
[TestFixture]
public sealed class DocumentationCoverageTests
{
    private static string RepoPath(params string[] parts)
    {
        var root = Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", "..", "..");
        return Path.GetFullPath(Path.Combine([root, .. parts]));
    }

    private static string Read(params string[] parts)
    {
        var path = RepoPath(parts);
        Assert.That(File.Exists(path), Is.True, $"missing file: {path}");
        return File.ReadAllText(path);
    }

    /// <summary>
    /// Codes the skill mentions, expanding ranges written as `WF070`–`WF076` into every code between.
    /// </summary>
    private static HashSet<string> DocumentedCodes(string skill)
    {
        var documented = new HashSet<string>(StringComparer.Ordinal);

        foreach (var m in Regex.Matches(skill, @"WF(\d{3})`?\s*[–-]\s*`?WF(\d{3})").Cast<Match>())
            for (var i = int.Parse(m.Groups[1].Value); i <= int.Parse(m.Groups[2].Value); i++)
                documented.Add($"WF{i:D3}");

        foreach (var m in Regex.Matches(skill, @"WF\d{3}").Cast<Match>())
            documented.Add(m.Value);

        return documented;
    }

    [Test]
    public void EveryValidationCode_IsMentionedInTheSkill()
    {
        var sources = new[]
        {
            Read("src", "Dataverse.Core", "Workflows", "WorkflowDefinitionValidator.cs"),
            Read("src", "Dataverse.Core", "Services", "WorkflowAuthoringService.cs")
        };

        var used = sources
            .SelectMany(s => Regex.Matches(s, @"""(WF\d{3})""").Cast<Match>())
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

        var documented = DocumentedCodes(Read("skills", "classic-workflows", "SKILL.md"));
        var missing = used.Except(documented).OrderBy(c => c).ToList();

        Assert.That(missing, Is.Empty,
            "These validation codes exist in the code but are not in the skill's issue-code table. "
            + "An undocumented code is one an agent cannot act on: " + string.Join(", ", missing));
    }

    [Test]
    public void EveryModelField_IsMentionedInTheSkill()
    {
        var model = Read("src", "Dataverse.Core", "Workflows", "WorkflowDefinition.cs");

        // Public properties of the definition model, as a caller writes them in JSON (camelCase).
        var fields = Regex.Matches(model, @"public\s+[\w<>?,\[\]\s]+?\s(\w+)\s*\{\s*get")
            .Cast<Match>()
            .Select(m => m.Groups[1].Value)
            .Select(n => char.ToLowerInvariant(n[0]) + n[1..])
            .Distinct(StringComparer.Ordinal)
            .Where(n => n != "stepId" && n != "branchId")   // assigned by the builder, not written
            .ToList();

        var skill = Read("skills", "classic-workflows", "SKILL.md");
        var missing = fields
            .Where(f => !Regex.IsMatch(skill, $@"\b{Regex.Escape(f)}\b", RegexOptions.IgnoreCase))
            .OrderBy(f => f)
            .ToList();

        Assert.That(missing, Is.Empty,
            "These fields of the definition model are not documented in the skill, so nobody can know "
            + "they exist: " + string.Join(", ", missing));
    }

    [Test]
    public void EveryWorkflowTool_IsMentionedInTheSkillAndTheToolDoc()
    {
        var tools = Regex.Matches(
                Read("src", "Dataverse.Server", "Tools", "WorkflowTools.cs"),
                @"McpServerTool\(Name = ""(workflow_\w+)""\)")
            .Cast<Match>()
            .Select(m => m.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Assert.That(tools, Is.Not.Empty, "no tools found — has the file moved?");

        var skill = Read("skills", "classic-workflows", "SKILL.md");
        var toolDoc = Read("docs", "tools", "workflows.md");

        Assert.Multiple(() =>
        {
            Assert.That(tools.Where(t => !skill.Contains(t, StringComparison.Ordinal)), Is.Empty,
                "tools missing from the skill's tool map");
            Assert.That(tools.Where(t => !toolDoc.Contains(t, StringComparison.Ordinal)), Is.Empty,
                "tools missing from docs/tools/workflows.md");
        });
    }
}
