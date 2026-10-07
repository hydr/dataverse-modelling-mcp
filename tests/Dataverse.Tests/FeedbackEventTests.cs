using Dataverse.Server.Tools;
using NUnit.Framework;

namespace Dataverse.Tests;

[TestFixture]
public class FeedbackEventTests
{
    [Test]
    public void Build_FillsAllV2Fields()
    {
        var p = FeedbackEvent.Build("txt", " Bug ", "sum", "medium", "claude-code", "2.1.0");

        Assert.Multiple(() =>
        {
            Assert.That(p["schemaVersion"], Is.EqualTo("2"));
            Assert.That(p["server"], Is.EqualTo("dataverse-modelling-mcp"));
            Assert.That(p["serverVersion"], Is.Not.Empty);
            Assert.That(p["category"], Is.EqualTo("bug"));
            Assert.That(p["severity"], Is.EqualTo("medium"));
            Assert.That(p["feedback"], Is.EqualTo("txt"));
            Assert.That(p["sessionSummary"], Is.EqualTo("sum"));
            Assert.That(p["caller"], Is.EqualTo("local-stdio"));
            Assert.That(p["callerOid"], Is.Empty);
            Assert.That(p["callerAppId"], Is.Empty);
            Assert.That(p["client"], Is.EqualTo("claude-code"));
            Assert.That(p["clientVersion"], Is.EqualTo("2.1.0"));
            Assert.That(p["transport"], Is.EqualTo("stdio"));
        });
    }

    [TestCase(null)]
    [TestCase("")]
    public void Build_EmptyCategory_BecomesGeneral(string? category) =>
        Assert.That(FeedbackEvent.Build("f", category, null, null, null, null)["category"], Is.EqualTo("general"));

    [Test]
    public void Build_NoClientInfo_IsUnknown()
    {
        var p = FeedbackEvent.Build("f", null, null, null, null, null);
        Assert.Multiple(() =>
        {
            Assert.That(p["client"], Is.EqualTo("unknown"));
            Assert.That(p["severity"], Is.Empty);
            Assert.That(p["clientVersion"], Is.Empty);
        });
    }
}
