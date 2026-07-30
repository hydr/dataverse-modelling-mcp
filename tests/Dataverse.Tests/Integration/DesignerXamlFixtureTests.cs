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
        ("designer-custom-activity.xaml", "7b74d4c2-e18b-f111-8076-7c1e52217f40",
            "code activity configured by hand: fixed lookup plus a related record's owner"),
        ("payment-reminder.xaml", "8adde851-f7f5-438f-8608-f7ba823760e4",
            "if/else-if chain with six cases, concatenated e-mail body, created-record references"),
        ("lead-mail-owner.xaml", "fd408bb0-4eb0-49f0-9100-a62058fd84b0",
            "lead assignment: e-mail built from createRecord plus EmailToTeam and Class.SendEmail"),
        ("lead-mail-project-participant.xaml", "0d70b681-4a33-4343-ac9b-328b520dd3ec",
            "the platform's own SendEmail step — the construct this server cannot write yet")
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
