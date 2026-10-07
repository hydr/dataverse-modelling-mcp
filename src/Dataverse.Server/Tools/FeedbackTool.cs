using System.ComponentModel;
using Microsoft.ApplicationInsights;
using ModelContextProtocol.Protocol;

using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace Dataverse.Server.Tools;

[McpServerToolType]
public sealed class FeedbackTool
{
    [McpServerTool(Name = "dataverse_submit_feedback")]
    [Description(
        "Optionales Telemetrie-Feedback zur aktuellen Session. Wird NUR übertragen, wenn der " +
        "Server-Betreiber Application Insights konfiguriert hat (Umgebungsvariable " +
        "APPLICATIONINSIGHTS_CONNECTION_STRING); andernfalls ist der Aufruf ein No-op und es wird " +
        "nichts über das Netz gesendet. Kann am Ende einer Session aufgerufen werden, um Hinweise zu " +
        "geben, wo der Agent nicht weiterkam, was verbessert werden könnte oder was gut funktioniert hat.")]
    public static Task<object> SubmitAsync(
        TelemetryClient telemetry,
        ILogger<FeedbackTool> logger,
        RequestContext<CallToolRequestParams> context,
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
        [Description(
            "Optional: wie stark hat es die Session beeinträchtigt? 'low', 'medium' oder 'high'.")]
        string? severity = null,
        CancellationToken ct = default)
    {
        try
        {
            var client = context.Server?.ClientInfo;
            telemetry.TrackEvent(FeedbackEvent.EventName, FeedbackEvent.Build(
                feedback, category, sessionSummary, severity, client?.Name, client?.Version));
            telemetry.Flush();

            logger.LogInformation("Session-Feedback empfangen [category={Category}, severity={Severity}]: {Feedback}", category, severity, feedback);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Feedback konnte nicht an Application Insights übermittelt werden.");
        }

        return Task.FromResult<object>(new { status = "ok", message = "Feedback empfangen. Danke!" });
    }
}
