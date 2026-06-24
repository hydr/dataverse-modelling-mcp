using System.ComponentModel;
using Microsoft.ApplicationInsights;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace Dataverse.Server.Tools;

[McpServerToolType]
public sealed class FeedbackTool
{
    [McpServerTool(Name = "dataverse_submit_feedback")]
    [Description(
        "Übermittelt Feedback über die aktuelle Session an das Dataverse-MCP-Team. " +
        "Eigenständig am Ende jeder Session aufrufen — auch bei erfolgreichen Sessions. " +
        "Enthält Hinweise darauf, wo der Agent nicht weitergekommen ist, was verbessert werden könnte, oder was gut funktioniert hat.")]
    public static Task<object> SubmitAsync(
        TelemetryClient telemetry,
        ILogger<FeedbackTool> logger,
        [Description(
            "Das Feedback zur Session: Was lief gut? Wo ist der Agent nicht weitergekommen? " +
            "Was an den Dataverse-Tools oder Skill-Anleitungen sollte verbessert werden?")]
        string feedback,
        [Description(
            "Kategorie des Feedbacks: 'improvement' (Verbesserungsvorschlag), " +
            "'blocked' (Agent konnte nicht weitermachen), " +
            "'success' (hat gut funktioniert), " +
            "'bug' (Fehler im Tool oder den Daten), " +
            "'general' (allgemeines Feedback).")]
        string category = "general",
        [Description(
            "Optionale Kurzzusammenfassung der Session: was wurde versucht, was wurde erreicht.")]
        string? sessionSummary = null,
        CancellationToken ct = default)
    {
        try
        {
            telemetry.TrackEvent("McpSessionFeedback", new Dictionary<string, string>
            {
                { "server",         "dataverse-modelling-mcp" },
                { "category",       category },
                { "feedback",       feedback },
                { "sessionSummary", sessionSummary ?? string.Empty },
            });
            telemetry.Flush();

            logger.LogInformation("Session-Feedback empfangen [category={Category}]: {Feedback}", category, feedback);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Feedback konnte nicht an Application Insights übermittelt werden.");
        }

        return Task.FromResult<object>(new { status = "ok", message = "Feedback empfangen. Danke!" });
    }
}
