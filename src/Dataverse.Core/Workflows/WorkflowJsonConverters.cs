namespace Dataverse.Core.Workflows;

using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// Polymorphic JSON converters for <see cref="ArgumentValue"/> and <see cref="WorkflowStep"/> keyed
/// on a "kind" discriminator. Unlike System.Text.Json's built-in [JsonPolymorphic], these buffer the
/// object first, so the discriminator may appear at ANY property position — important because LLM- or
/// tool-produced JSON does not reliably put "kind" first (STJ otherwise throws
/// "must specify a type discriminator").
/// </summary>
public static class WorkflowJsonConverters
{
    public static void Register(JsonSerializerOptions options)
    {
        options.Converters.Add(new KindConverter<ArgumentValue>(new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase)
        {
            ["literal"] = typeof(LiteralValue),
            ["field"] = typeof(FieldValue),
        }));
        options.Converters.Add(new KindConverter<WorkflowStep>(new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase)
        {
            ["updateEntity"] = typeof(UpdateEntityStep),
            ["createEntity"] = typeof(CreateEntityStep),
            ["customActivity"] = typeof(CustomActivityStep),
            ["condition"] = typeof(ConditionStep),
            ["unknown"] = typeof(UnknownStep),
        }));
    }

    private sealed class KindConverter<TBase>(IReadOnlyDictionary<string, Type> map) : JsonConverter<TBase>
    {
        public override TBase? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using var doc = JsonDocument.ParseValue(ref reader);
            var root = doc.RootElement;
            if (!root.TryGetProperty("kind", out var kindEl) || kindEl.ValueKind != JsonValueKind.String)
                throw new JsonException($"Missing 'kind' discriminator for {typeof(TBase).Name}. Expected one of: {string.Join(", ", map.Keys)}.");

            var kind = kindEl.GetString()!;
            if (!map.TryGetValue(kind, out var concrete))
                throw new JsonException($"Unknown {typeof(TBase).Name} kind '{kind}'. Expected one of: {string.Join(", ", map.Keys)}.");

            // Deserialize the concrete (sealed) type. The concrete type is not TBase, so this converter
            // is not re-entered for it; nested polymorphic members are handled on their own.
            return (TBase?)root.Deserialize(concrete, options);
        }

        public override void Write(Utf8JsonWriter writer, TBase value, JsonSerializerOptions options)
        {
            // Serialize the runtime type and inject the discriminator so output round-trips.
            var type = value!.GetType();
            var kind = map.FirstOrDefault(kv => kv.Value == type).Key;
            using var doc = JsonSerializer.SerializeToDocument(value, type, options);
            writer.WriteStartObject();
            if (kind is not null)
                writer.WriteString("kind", kind);
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (string.Equals(prop.Name, "kind", StringComparison.OrdinalIgnoreCase))
                    continue;
                prop.WriteTo(writer);
            }
            writer.WriteEndObject();
        }
    }
}
