namespace Dataverse.Server.Tools;

/// <summary>
/// Builds the customDimensions of the Application Insights event <c>McpSessionFeedback</c> (schema v2;
/// local stdio server: caller <c>local-stdio</c>, no claims) as specified in
/// <c>claude-plugin-marketplace/docs/feedback-spec.md</c>. A class of its own so that the properties
/// can be unit-tested without an MCP request.
/// </summary>
public static class FeedbackEvent
{
    public const string EventName = "McpSessionFeedback";
    public const string ServerName = "dataverse-modelling-mcp";

    /// <summary>Server version from the assembly (Major.Minor.Build), <c>unknown</c> as a fallback.</summary>
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
