namespace Dataverse.Server.Tools;

/// <summary>
/// Baut die customDimensions des App-Insights-Events <c>McpSessionFeedback</c> (Schema v2, lokaler stdio-Server: caller <c>local-stdio</c>, keine Claims) nach
/// <c>claude-plugin-marketplace/docs/feedback-spec.md</c>. Eigene Klasse, damit die Properties ohne
/// MCP-Request unit-testbar sind.
/// </summary>
public static class FeedbackEvent
{
    public const string EventName = "McpSessionFeedback";
    public const string ServerName = "dataverse-modelling-mcp";

    /// <summary>Server-Version aus der Assembly (Major.Minor.Build), <c>unknown</c> als Fallback.</summary>
    public static readonly string ServerVersion =
        typeof(FeedbackEvent).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "unknown";

    /// <param name="clientName"><c>clientInfo.name</c> aus dem initialize-Handshake.</param>
    /// <param name="clientVersion"><c>clientInfo.version</c>.</param>
    public static Dictionary<string, string> Build(
        string feedback, string? category, string? sessionSummary, string? severity,
        string? clientName, string? clientVersion) => new()
    {
        ["schemaVersion"]  = "2",
        ["server"]         = ServerName,
        ["serverVersion"]  = ServerVersion,
        ["category"]       = string.IsNullOrWhiteSpace(category) ? "general" : category.Trim().ToLowerInvariant(),
        ["severity"]       = severity ?? string.Empty,
        ["feedback"]       = feedback,
        ["sessionSummary"] = sessionSummary ?? string.Empty,
        ["caller"]         = "local-stdio",
        ["callerOid"]      = string.Empty,
        ["callerAppId"]    = string.Empty,
        ["client"]         = string.IsNullOrWhiteSpace(clientName) ? "unknown" : clientName,
        ["clientVersion"]  = clientVersion ?? string.Empty,
        ["transport"]      = "stdio",
    };
}
