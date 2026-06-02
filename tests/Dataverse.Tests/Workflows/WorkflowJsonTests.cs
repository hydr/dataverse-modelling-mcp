namespace Dataverse.Tests.Workflows;

using System.Text.Json;
using System.Text.Json.Serialization;
using Dataverse.Core.Workflows;
using NUnit.Framework;

[TestFixture]
public sealed class WorkflowJsonTests
{
    private static JsonSerializerOptions Options()
    {
        var o = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() }
        };
        WorkflowJsonConverters.Register(o);
        return o;
    }

    [Test]
    public void Deserialize_DiscriminatorNotFirst_Succeeds()
    {
        // 'kind' deliberately placed last — the built-in [JsonPolymorphic] would fail here.
        const string json = """
        {
          "primaryEntity": "account",
          "mode": "Realtime",
          "scope": "Organization",
          "trigger": { "onUpdate": true, "updateAttributes": ["telephone1"], "stage": "PostOperation" },
          "steps": [
            { "entityName": "account",
              "assignments": [ { "attribute": "description", "type": "String", "value": { "type": "String", "value": "hi", "kind": "literal" } } ],
              "kind": "updateEntity" }
          ]
        }
        """;

        var def = JsonSerializer.Deserialize<WorkflowDefinition>(json, Options());

        Assert.That(def, Is.Not.Null);
        Assert.That(def!.PrimaryEntity, Is.EqualTo("account"));
        Assert.That(def.Mode, Is.EqualTo(WorkflowMode.Realtime));
        var step = (UpdateEntityStep)def.Steps.Single();
        Assert.That(step.EntityName, Is.EqualTo("account"));
        var assignment = step.Assignments.Single();
        Assert.That(assignment.Value, Is.TypeOf<LiteralValue>());
        Assert.That(((LiteralValue)assignment.Value).Value, Is.EqualTo("hi"));
    }

    [Test]
    public void Deserialize_CustomActivityWithFieldAndLiteral_Succeeds()
    {
        const string json = """
        {
          "primaryEntity": "opportunity",
          "mode": "Background",
          "scope": "Organization",
          "trigger": { "onDemand": true },
          "steps": [
            { "kind": "customActivity",
              "assemblyQualifiedName": "Some.Activity, Some.Asm",
              "label": "X",
              "inputArguments": [
                { "name": "A", "type": "String", "value": { "kind": "literal", "type": "String", "value": "lit" } },
                { "name": "B", "type": "EntityReference", "value": { "kind": "field", "attribute": "ownerid", "type": "EntityReference" } }
              ],
              "outputArguments": [ { "name": "Out", "type": "String" } ] }
          ]
        }
        """;

        var def = JsonSerializer.Deserialize<WorkflowDefinition>(json, Options());

        var step = (CustomActivityStep)def!.Steps.Single();
        Assert.That(step.InputArguments, Has.Count.EqualTo(2));
        Assert.That(step.InputArguments[0].Value, Is.TypeOf<LiteralValue>());
        Assert.That(step.InputArguments[1].Value, Is.TypeOf<FieldValue>());
        Assert.That(step.OutputArguments!.Single().Name, Is.EqualTo("Out"));
    }

    [Test]
    public void Deserialize_UnknownKind_Throws()
    {
        const string json = """
        { "primaryEntity": "account", "mode": "Realtime", "scope": "Organization",
          "trigger": {}, "steps": [ { "kind": "frobnicate" } ] }
        """;
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<WorkflowDefinition>(json, Options()));
    }
}
