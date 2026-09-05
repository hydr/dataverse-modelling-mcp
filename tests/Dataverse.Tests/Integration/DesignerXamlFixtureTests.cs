namespace Dataverse.Tests.Integration;

using Dataverse.Tests.Infrastructure;
using NUnit.Framework;

/// <summary>
/// Refreshes the designer-authored XAML fixtures from the environment.
/// </summary>
/// <remarks>
/// Builder and parser tests otherwise only prove they agree with each other. These fixtures are real
/// designer output, so they are the only evidence that the parser reads what Dataverse itself writes.
/// Run this when a new construct needs covering; the files it writes are committed.
/// </remarks>
[TestFixture]
[Category("Integration")]
[Explicit("Maintenance task — rewrites committed fixtures from the environment.")]
public sealed class DesignerXamlFixtureTests : IntegrationTestBase
{
    /// <summary>
    /// The workflows kept as fixtures, chosen for the constructs they contain rather than for what
    /// they do. Add a line to cover a new construct.
    /// </summary>
    private static readonly (string File, string Id, string Why)[] Fixtures =
    [
        ("designer-custom-activity.xaml", "f0000002-0000-4000-8000-000000000002",
            "code activity configured by hand: fixed lookup plus a related record's owner"),
        ("payment-reminder.xaml", "f0000009-0000-4000-8000-000000000009",
            "if/else-if chain with six cases, concatenated e-mail body, created-record references"),
        ("lead-mail-owner.xaml", "f0000006-0000-4000-8000-000000000006",
            "lead assignment: e-mail built from createRecord plus EmailToTeam and Class.SendEmail"),
        ("lead-mail-project-participant.xaml", "f0000007-0000-4000-8000-000000000007",
            "the platform's own SendEmail step, with party lists"),
        ("dunning-1.xaml", "f0000003-0000-4000-8000-000000000003",
            "first dunning letter, sibling of the payment reminder"),
        ("dunning-2.xaml", "f0000005-0000-4000-8000-000000000005",
            "second dunning letter — a draft, not activated"),
        ("dunning-1-manual.xaml", "f0000004-0000-4000-8000-000000000004",
            "manual variant of the first dunning letter, activated but not named alongside the others"),
        ("assign-owner-from-lead.xaml", "f0000001-0000-4000-8000-000000000001",
            "assignRecord — changing the owner, on account, real-time"),
        ("quote-as-won.xaml", "f000000a-0000-4000-8000-00000000000a",
            "changeStatus via SetState, on quote"),
        ("marketing-list-master.xaml", "f0000008-0000-4000-8000-000000000008",
            "startChildWorkflow — a master workflow driving child processes")
    ];

    [Test]
    public async Task Fetch_All()
    {
        foreach (var (file, id, why) in Fixtures)
        {
            var detail = await WorkflowService.GetAsync(OrgUrl, Guid.Parse(id));
            Assert.That(detail?.Xaml, Is.Not.Null.And.Not.Empty, $"{file}: no XAML");

            await WriteFixtureAsync(file, detail!.Xaml!);
            TestContext.Out.WriteLine($"  {detail.Name} — {why}");
        }
    }

    /// <summary>Writes into the source tree, not the build output, so the file can be committed.</summary>
    private static async Task WriteFixtureAsync(string fileName, string xaml)
    {
        var path = Path.Combine(TestContext.CurrentContext.TestDirectory,
            "..", "..", "..", "Workflows", "Fixtures", fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, xaml);

        TestContext.Out.WriteLine($"wrote {fileName} ({xaml.Length} chars)");
    }
}
