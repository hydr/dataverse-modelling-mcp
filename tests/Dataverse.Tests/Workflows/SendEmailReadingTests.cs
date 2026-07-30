namespace Dataverse.Tests.Workflows;

using Dataverse.Core.Workflows;
using NUnit.Framework;

/// <summary>
/// Reads "Lead : Mail Project Participant" — an activated workflow that uses the platform's own
/// SendEmail step, triggered when a lead's project participant changes.
/// </summary>
[TestFixture]
public sealed class SendEmailReadingTests
{
    private static string Xaml()
    {
        var path = Path.Combine(TestContext.CurrentContext.TestDirectory,
            "Workflows", "Fixtures", "lead-mail-project-participant.xaml");
        Assert.That(File.Exists(path), Is.True, $"fixture missing: {path}");
        return File.ReadAllText(path);
    }

    [Test]
    public void Parse_SendEmailStep_ReadsRecipientsAndBody()
    {
        var parsed = WorkflowXamlParser.Parse(Xaml(), "lead");
        var send = parsed.Definition.Steps
            .SelectMany(Flatten)
            .Single(s => s.Kind == WorkflowStepKind.SendEmail);

        Assert.That(send.Entity, Is.EqualTo("email"));
        var byName = send.Attributes!.ToDictionary(a => a.Attribute, a => a.Value);

        // Recipients are party lists, not plain references.
        Assert.That(byName["from"].DataType, Is.EqualTo("PartyList"));
        Assert.That(byName["from"].Literal, Does.StartWith("systemuser:"));
        Assert.That(byName["to"].DataType, Is.EqualTo("PartyList"));
        Assert.That(byName["to"].Fields, Is.EqualTo(new[] { "lead.dc_projectparticipant1" }));

        Assert.That(byName["subject"].Literal, Is.EqualTo("Leadzuordnung Projektbeteiligter"));
        Assert.That(byName["description"].Kind, Is.EqualTo(WorkflowValueKind.Concat));
        Assert.That(byName["regardingobjectid"].Fields, Is.EqualTo(new[] { "lead.leadid" }));
    }

    private static IEnumerable<WorkflowStep> Flatten(WorkflowStep step)
    {
        yield return step;
        foreach (var child in (step.Then ?? []).Concat(step.Else ?? []).Concat(step.Children ?? [])
                     .Concat((step.Branches ?? []).SelectMany(b => b.Steps ?? [])))
            foreach (var nested in Flatten(child))
                yield return nested;
    }
}
