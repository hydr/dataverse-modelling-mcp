namespace Dataverse.Core.Clients;

using System.Text.Json;

/// <summary>One request a dry run would have sent.</summary>
public sealed record PlannedRequest(
    string Method,
    string Url,
    IReadOnlyDictionary<string, string> Headers,
    JsonElement? Body);

/// <summary>
/// Dry-run scope for <see cref="DataverseHttpClient"/>. While a scope is open on the current async
/// flow, every request that would change the environment (POST, PATCH, PUT, DELETE) is recorded
/// instead of sent and answered with an empty "204 No Content". GET requests still go out, so a
/// dry-run update shows the real merged definition it would write.
/// </summary>
/// <remarks>
/// A record created inside a dry run has no id, so anything that refers to it afterwards shows
/// <see cref="Guid.Empty"/>. POSTs to Dataverse functions are always reads in the Web API (they
/// use GET), so recording every POST does not hide a read.
/// </remarks>
public static class DryRun
{
    private static readonly AsyncLocal<List<PlannedRequest>?> Current = new();

    public static bool IsActive => Current.Value is not null;

    /// <summary>Opens a dry-run scope; dispose it to close. The list fills as requests are recorded.</summary>
    public static Scope Begin()
    {
        var requests = new List<PlannedRequest>();
        Current.Value = requests;
        return new Scope(requests);
    }

    internal static void Record(PlannedRequest request) => Current.Value?.Add(request);

    public sealed class Scope : IDisposable
    {
        internal Scope(List<PlannedRequest> requests) => Requests = requests;

        public IReadOnlyList<PlannedRequest> Requests { get; }

        public void Dispose() => Current.Value = null;
    }
}
