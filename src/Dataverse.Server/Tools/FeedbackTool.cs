using System.ComponentModel;
using Microsoft.ApplicationInsights;
using ModelContextProtocol.Protocol;

using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace Dataverse.Server.Tools;

[McpServerToolType]
public sealed class FeedbackTool
{
    [McpServerTool(Name = "dataverse_submit_feedback", ReadOnly = true)]
    [Description(
        "Optional telemetry feedback on the current session. Sent ONLY if the server operator has " +
        "configured Application Insights (environment variable APPLICATIONINSIGHTS_CONNECTION_STRING); " +
        "otherwise the call is a no-op and nothing is sent over the network. Can be called at the end " +
        "of a session to report where the agent got stuck, what could be improved or what worked well.")]
    public static Task<object> SubmitAsync(
        TelemetryClient telemetry,
        ILogger<FeedbackTool> logger,
        RequestContext<CallToolRequestParams> context,
        [Description(
            "The feedback on the session: What went well? Where did the agent get stuck? " +
            "What should be improved in the Dataverse tools or the skill instructions?")]
        string feedback,
        [Description(
            "Category of the feedback: 'improvement' (a suggestion), " +
            "'blocked' (the agent could not continue), " +
            "'success' (worked well), " +
            "'bug' (an error in a tool or in the data), " +
            "'general' (general feedback).")]
        string category = "general",
        [Description(
            "Optional short summary of the session: what was attempted, what was achieved.")]
        string? sessionSummary = null,
        [Description(
            "Optional: how much did it affect the session? 'low', 'medium' or 'high'.")]
        string? severity = null,
        CancellationToken ct = default)
    {
        try
        {
            var client = context.Server?.ClientInfo;
            telemetry.TrackEvent(FeedbackEvent.EventName, FeedbackEvent.Build(
                feedback, category, sessionSummary, severity, client?.Name, client?.Version));
            telemetry.Flush();

            logger.LogInformation("Session feedback received [category={Category}, severity={Severity}]: {Feedback}", category, severity, feedback);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Feedback could not be sent to Application Insights.");
        }

        return Task.FromResult<object>(new { status = "ok", message = "Feedback received. Thank you!" });
    }
}
