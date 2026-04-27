namespace Dataverse.Core.Json;

using System.Text.Json;

/// <summary>
/// Convenience extension methods for reading JSON element properties without null checks at every call site.
/// </summary>
internal static class JsonElementExtensions
{
    public static Guid TryGetGuid(this JsonElement el, string prop)
    {
        if (el.ValueKind != JsonValueKind.Object) return Guid.Empty;
        if (el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String)
            if (Guid.TryParse(v.GetString(), out var g)) return g;
        return Guid.Empty;
    }

    public static string GetStringOrEmpty(this JsonElement el, string prop)
    {
        if (el.ValueKind != JsonValueKind.Object) return string.Empty;
        if (el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String)
            return v.GetString() ?? string.Empty;
        return string.Empty;
    }

    public static string? GetStringOrNull(this JsonElement el, string prop)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        if (el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String)
            return v.GetString();
        return null;
    }

    public static int GetInt32OrZero(this JsonElement el, string prop)
    {
        if (el.ValueKind != JsonValueKind.Object) return 0;
        if (el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number)
            return v.GetInt32();
        return 0;
    }

    public static DateTime? GetDateTimeOrNull(this JsonElement el, string prop)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        if (el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String)
            if (DateTime.TryParse(v.GetString(), out var dt)) return dt;
        return null;
    }
}
