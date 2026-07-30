namespace Dataverse.Tests.Workflows;

using Dataverse.Core.Workflows;
using NUnit.Framework;

/// <summary>
/// Reads every designer-authored fixture and reports what the parser makes of it.
/// </summary>
/// <remarks>
/// Ten real workflows from contoso-dev, chosen for different constructs rather than different purposes. Each
/// one that arrived here brought at least one format defect to light that no hand-written test case had
/// found — so the survey is both a regression net and the place where the next gap shows up.
/// </remarks>
[TestFixture]
public sealed class FixtureSurveyTests
{
    private static string FixtureDirectory => Path.Combine(
        TestContext.CurrentContext.TestDirectory, "Workflows", "Fixtures");

    private static IEnumerable<string> Fixtures =>
        Directory.EnumerateFiles(FixtureDirectory, "*.xaml").OrderBy(f => f, StringComparer.Ordinal);

    /// <summary>Prints one line per fixture: understood, steps, kinds, and what was missed.</summary>
    [Test]
    public void Survey()
    {
        var unreadable = new List<string>();

        foreach (var path in Fixtures)
        {
            var name = Path.GetFileNameWithoutExtension(path);
            var parsed = WorkflowXamlParser.Parse(File.ReadAllText(path), GuessEntity(name));

            var kinds = parsed.Definition.Steps
                .SelectMany(Flatten)
                .GroupBy(s => s.Kind, StringComparer.Ordinal)
                .OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => g.Count() == 1 ? g.Key : $"{g.Key}x{g.Count()}");

            TestContext.Out.WriteLine(
                $"{(parsed.FullyUnderstood ? "OK  " : "GAP ")} {name,-34} {string.Join(" ", kinds)}");

            foreach (var item in parsed.Unrecognised.Distinct())
                TestContext.Out.WriteLine($"       ! {Shorten(item)}");

            if (!parsed.FullyUnderstood)
                unreadable.Add(name);
        }

        TestContext.Out.WriteLine($"\n{Fixtures.Count()} fixtures, {unreadable.Count} with gaps");

        // Not an assertion on zero: a gap is a finding to report, and some constructs (performAction)
        // are known to be read-only. The point is that no gap goes unnoticed.
        Assert.That(Fixtures.Any(), Is.True, "no fixtures found — has the directory moved?");
    }

    /// <summary>
    /// Every fixture must be rebuildable: the reading goes back through the builder and the generated
    /// XAML passes its own self-check. That is the offline half of "can this workflow be edited" —
    /// the online half is proven by PaymentReminderRebuildTests, which activates a rebuild.
    /// </summary>
    [Test]
    public void EveryFixture_CanBeRebuilt()
    {
        foreach (var path in Fixtures)
        {
            var name = Path.GetFileNameWithoutExtension(path);
            var entity = GuessEntity(name);
            var parsed = WorkflowXamlParser.Parse(File.ReadAllText(path), entity);

            if (!parsed.FullyUnderstood)
            {
                TestContext.Out.WriteLine($"SKIP {name} — not fully understood, must not be rewritten");
                continue;
            }

            var definition = parsed.Definition with { PrimaryEntity = entity };

            // The catalog is empty here: no network in a unit test. Argument types then fall back to
            // the caller's dataType, which is exactly what the parser recovered — good enough to prove
            // the structure rebuilds.
            var rebuilt = WorkflowXamlBuilder.Build(definition);
            var check = WorkflowDefinitionValidator.ValidateGeneratedXaml(rebuilt.Xaml);

            Assert.That(check.CanSave, Is.True,
                $"{name}: the rebuilt XAML fails its self-check — "
                + string.Join("; ", check.Issues.Select(i => $"{i.Code} {i.Problem}")));

            TestContext.Out.WriteLine(
                $"REBUILT {name,-34} {rebuilt.StepIds.Count} step ids, {rebuilt.Xaml.Length} chars");
        }
    }

    /// <summary>Every fixture must at least parse into steps; silence would hide a broken reading.</summary>
    [Test]
    public void EveryFixture_YieldsSteps()
    {
        foreach (var path in Fixtures)
        {
            var name = Path.GetFileNameWithoutExtension(path);
            var parsed = WorkflowXamlParser.Parse(File.ReadAllText(path), GuessEntity(name));

            Assert.That(parsed.Definition.Steps, Is.Not.Empty, $"{name}: no steps recognised at all");
        }
    }

    private static string GuessEntity(string fixture) => fixture switch
    {
        "payment-reminder" or "dunning-1" or "dunning-2" or "dunning-1-manual" => "invoice",
        "lead-mail-owner" or "lead-mail-project-participant" => "lead",
        "assign-owner-from-lead" => "account",
        "quote-as-won" => "quote",
        "marketing-list-master" => "list",
        _ => "salesorder"
    };

    private static IEnumerable<WorkflowStep> Flatten(WorkflowStep step)
    {
        yield return step;
        foreach (var child in (step.Then ?? []).Concat(step.Else ?? []).Concat(step.Children ?? [])
                     .Concat((step.Branches ?? []).SelectMany(b => b.Steps ?? [])))
            foreach (var nested in Flatten(child))
                yield return nested;
    }

    private static string Shorten(string text) => text.Length <= 130 ? text : text[..130] + " …";
}
