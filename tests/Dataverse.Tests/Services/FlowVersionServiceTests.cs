namespace Dataverse.Tests.Services;

using System;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dataverse.Core.Services;
using NUnit.Framework;

[TestFixture]
public sealed class FlowVersionServiceTests
{
    [Test]
    public void ValidateAndNormalizeClientData_ReturnsCompactJson_ForValidInput()
    {
        var input = "{\n  \"properties\": {\n    \"definition\": { \"actions\": {} }\n  },\n  \"schemaVersion\": \"1.0.0.0\"\n}";

        var result = FlowVersionService.ValidateAndNormalizeClientData(input);

        // Well-formed and re-parseable, whitespace stripped.
        Assert.That(() => JsonNode.Parse(result), Throws.Nothing);
        Assert.That(result, Does.Not.Contain("\n"));
        var node = JsonNode.Parse(result)!;
        Assert.That(node["schemaVersion"]!.GetValue<string>(), Is.EqualTo("1.0.0.0"));
    }

    [Test]
    public void ValidateAndNormalizeClientData_Throws_ForTrailingPadBrace()
    {
        // The historical "append a trailing brace" workaround now surfaces as a clear error
        // instead of being silently accepted / producing a cryptic Dataverse 400.
        var padded = "{\"properties\":{},\"schemaVersion\":\"1.0.0.0\"}}";

        Assert.That(
            () => FlowVersionService.ValidateAndNormalizeClientData(padded),
            Throws.TypeOf<ArgumentException>());
    }

    [Test]
    public void ValidateAndNormalizeClientData_Throws_ForTruncatedJson()
    {
        // Simulates a dropped final character (the exact symptom that produced
        // "Unexpected end when deserializing object … position N-1" from Dataverse).
        var truncated = "{\"properties\":{},\"schemaVersion\":\"1.0.0.0\"";

        Assert.That(
            () => FlowVersionService.ValidateAndNormalizeClientData(truncated),
            Throws.TypeOf<ArgumentException>());
    }

    [TestCase("")]
    [TestCase("   ")]
    [TestCase(null)]
    public void ValidateAndNormalizeClientData_Throws_ForEmptyInput(string? input)
    {
        Assert.That(
            () => FlowVersionService.ValidateAndNormalizeClientData(input!),
            Throws.TypeOf<ArgumentException>());
    }
}
