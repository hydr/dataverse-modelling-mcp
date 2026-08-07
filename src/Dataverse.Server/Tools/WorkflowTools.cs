namespace Dataverse.Server.Tools;

using System.ComponentModel;
using System.Text.Json;
using Dataverse.Core.Config;
using Dataverse.Core.Io;
using Dataverse.Core.Services;
using Dataverse.Core.Workflows;
using ModelContextProtocol.Server;

[McpServerToolType]
public sealed class WorkflowTools
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    [McpServerTool(Name = "workflow_list")]
    [Description("List Classic Workflows in the configured Dataverse environment.")]
    public static async Task<string> WorkflowList(
        WorkflowService svc,
        ConfigProvider config,
        [Description("Optional OData filter (e.g. \"name eq 'MyWorkflow'\")")] string? filter = null,
        [Description("Maximum number of results to return (default 50)")] int top = 50,
        CancellationToken ct = default)
    {
        try
        {
            var env = config.GetActiveEnvironment();
            var result = await svc.ListAsync(env.OrgUrl, filter, top, ct);
            return JsonSerializer.Serialize(result, JsonOptions);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message, details = ex.GetType().Name });
        }
    }

    [McpServerTool(Name = "workflow_get")]
    [Description("Get the full definition of a Classic Workflow, including its XAML.")]
    public static async Task<string> WorkflowGet(
        WorkflowService svc,
        ConfigProvider config,
        [Description("The workflow GUID")] string workflowId,
        CancellationToken ct = default)
    {
        try
        {
            if (!Guid.TryParse(workflowId, out var id))
                return JsonSerializer.Serialize(new { error = "Invalid workflowId GUID format." });

            var env = config.GetActiveEnvironment();
            var result = await svc.GetAsync(env.OrgUrl, id, ct);
            return result is null
                ? JsonSerializer.Serialize(new { error = "Workflow not found." })
                : JsonSerializer.Serialize(result, JsonOptions);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message, details = ex.GetType().Name });
        }
    }

    [McpServerTool(Name = "workflow_export_xaml")]
    [Description("Export the raw XAML definition of a Classic Workflow, as-is. " +
                 "Use for inspection, backup or diffing. Always call this before changing a workflow " +
                 "so you have a restore point (write it back with workflow_restore_xaml). " +
                 "To understand a workflow prefer workflow_explain; to change one prefer " +
                 "workflow_set_definition, which generates conventional XAML for you.")]
    public static async Task<string> WorkflowExportXaml(
        WorkflowService svc,
        ConfigProvider config,
        [Description("The workflow GUID")] string workflowId,
        CancellationToken ct = default)
    {
        try
        {
            if (!Guid.TryParse(workflowId, out var id))
                return JsonSerializer.Serialize(new { error = "Invalid workflowId GUID format." });

            var env = config.GetActiveEnvironment();
            var xaml = await svc.GetXamlAsync(env.OrgUrl, id, ct);
            return xaml is null
                ? JsonSerializer.Serialize(new { error = "Workflow not found or has no XAML." })
                : xaml;
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message, details = ex.GetType().Name });
        }
    }

    [McpServerTool(Name = "workflow_create")]
    [Description("Create a new Classic Workflow (draft) for a given primary entity and return its id. " +
                 "The workflow starts with an empty but valid XAML skeleton; add logic with " +
                 "workflow_set_definition, then activate with workflow_set_state. " +
                 "Triggers (isoncreate, updatestage, ...) are set with workflow_update.")]
    public static async Task<string> WorkflowCreate(
        WorkflowService svc,
        ConfigProvider config,
        [Description("Display name of the workflow")] string name,
        [Description("Logical name of the primary entity (e.g. 'account')")] string primaryEntity,
        [Description("Optional description")] string? description = null,
        [Description("true = real-time (synchronous) workflow, false = background (default)")] bool isRealtime = false,
        CancellationToken ct = default)
    {
        try
        {
            var env = config.GetActiveEnvironment();
            var id = await svc.CreateAsync(env.OrgUrl, name, primaryEntity, description, isRealtime, ct);
            return JsonSerializer.Serialize(new { workflowId = id, name, primaryEntity, isRealtime });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message, details = ex.GetType().Name });
        }
    }

    [McpServerTool(Name = "workflow_update")]
    [Description("Update metadata properties of a Classic Workflow via OData PATCH. " +
                 "Updatable fields: name, description, " +
                 "primaryentity, scope (1=User,2=BU,3=ParentChildBU,4=Org), " +
                 "mode (0=Background,1=Realtime), runas (0=Owner,1=CallingUser), " +
                 "ondemand, isoncreate, isonupdate, isondelete, triggerattribute, " +
                 "createstage/updatestage/deletestage (20=Pre,40=Post), " +
                 "logcontent (0=None,1=Details,2=All), asyncautodelete, rank, istransacted, subprocess. " +
                 "For workflow LOGIC use workflow_set_definition instead of writing 'xaml' here — it " +
                 "generates XAML that follows the designer's naming conventions and validates it first. " +
                 "Writing 'xaml' directly is possible but only sensible to restore a backup " +
                 "(workflow_restore_xaml). Note: the workflow must be a draft for any change.")]
    public static async Task<string> WorkflowUpdate(
        WorkflowService svc,
        ConfigProvider config,
        [Description("The workflow GUID")] string workflowId,
        [Description("JSON object with properties to update (e.g. {\"logcontent\": 2, \"ondemand\": true})")] string propertiesJson,
        CancellationToken ct = default)
    {
        try
        {
            if (!Guid.TryParse(workflowId, out var id))
                return JsonSerializer.Serialize(new { error = "Invalid workflowId GUID format." });

            var props = JsonSerializer.Deserialize<Dictionary<string, object?>>(propertiesJson)
                        ?? throw new ArgumentException("propertiesJson could not be parsed.");

            var env = config.GetActiveEnvironment();

            // Changing the mode changes what the XAML may contain: a real-time process must not carry
            // persistence points. Existing logic does not adapt on its own.
            string? warning = null;
            if (props.Keys.Any(k => string.Equals(k, "mode", StringComparison.OrdinalIgnoreCase)))
            {
                var before = await svc.GetAsync(env.OrgUrl, id, ct);
                if (before?.Xaml is { Length: > 0 } xaml && xaml.Contains("<Persist", StringComparison.Ordinal))
                    warning = "The workflow already has logic containing persistence points (<Persist />), "
                        + "which a real-time process must not have. If you switched it to real-time, write "
                        + "the logic again with workflow_set_definition — otherwise activation fails with "
                        + "0x80040216.";
            }

            await svc.UpdateAsync(env.OrgUrl, id, props, ct);
            return JsonSerializer.Serialize(new { success = true, workflowId, warning });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message, details = ex.GetType().Name });
        }
    }

    [McpServerTool(Name = "workflow_set_state")]
    [Description("Activate or deactivate a Classic Workflow.")]
    public static async Task<string> WorkflowSetState(
        WorkflowService svc,
        ConfigProvider config,
        [Description("The workflow GUID")] string workflowId,
        [Description("true to activate, false to deactivate")] bool activate,
        CancellationToken ct = default)
    {
        try
        {
            if (!Guid.TryParse(workflowId, out var id))
                return JsonSerializer.Serialize(new { error = "Invalid workflowId GUID format." });

            var env = config.GetActiveEnvironment();
            await svc.SetStateAsync(env.OrgUrl, id, activate, ct);
            return JsonSerializer.Serialize(new { success = true, workflowId, activate });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message, details = ex.GetType().Name });
        }
    }

    [McpServerTool(Name = "workflow_delete")]
    [Description("Delete one or more Classic Workflows. Irreversible — export the XAML first if the " +
                 "logic might be needed again (workflow_export_xaml). " +
                 "Activating a workflow makes Dataverse store a second row (type=2, the activation " +
                 "copy) that neither deactivating nor deleting the definition removes; this tool " +
                 "deletes those copies along with the definition, which is how an environment fills " +
                 "up with leftovers otherwise. " +
                 "An activated workflow is refused unless deactivateFirst=true, so a running process " +
                 "cannot be removed by a typo.")]
    public static async Task<string> WorkflowDelete(
        WorkflowService svc,
        ConfigProvider config,
        [Description("Workflow GUID, or several separated by commas. Leave empty when using namePrefix.")]
        string workflowIds = "",
        [Description("Deactivate an activated workflow before deleting it (default false)")]
        bool deactivateFirst = false,
        [Description("Instead of ids: every workflow whose name starts with this, e.g. \"ZZ \". " +
                     "Lists the matches WITHOUT deleting unless confirmPrefix=true.")]
        string? namePrefix = null,
        [Description("Required to actually delete a namePrefix match — the safety catch for bulk deletes")]
        bool confirmPrefix = false,
        CancellationToken ct = default)
    {
        try
        {
            var env0 = config.GetActiveEnvironment();
            var ids = new List<Guid>();
            var malformed = new List<string>();

            if (!string.IsNullOrWhiteSpace(namePrefix))
            {
                var matches = await svc.FindByNamePrefixAsync(env0.OrgUrl, namePrefix!, ct);

                if (!confirmPrefix)
                    return JsonSerializer.Serialize(new
                    {
                        dryRun = true,
                        namePrefix,
                        matchCount = matches.Count,
                        matches = matches.Select(m => new { m.WorkflowId, m.Name, m.Type, m.IsActivated }),
                        note = "Nothing was deleted. Re-run with confirmPrefix=true to delete these."
                    }, JsonOptions);

                ids.AddRange(matches.Select(m => m.WorkflowId));
            }

            foreach (var raw in workflowIds.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                if (Guid.TryParse(raw, out var id))
                    ids.Add(id);
                else
                    malformed.Add(raw);
            }

            if (malformed.Count > 0)
                return JsonSerializer.Serialize(new
                {
                    error = $"Not a GUID: {string.Join(", ", malformed)}",
                    fix = "Pass workflow ids as GUIDs, separated by commas. Nothing was deleted."
                });

            if (ids.Count == 0)
                return JsonSerializer.Serialize(new { error = "No workflow id given." });

            var env = config.GetActiveEnvironment();
            var deleted = new List<object>();
            var skipped = new List<object>();

            foreach (var id in ids)
            {
                var detail = await svc.GetAsync(env.OrgUrl, id, ct);
                if (detail is null)
                {
                    skipped.Add(new { workflowId = id, reason = "not found" });
                    continue;
                }

                if (detail.StateCode == 1)
                {
                    if (!deactivateFirst)
                    {
                        skipped.Add(new
                        {
                            workflowId = id,
                            name = detail.Name,
                            reason = "activated — pass deactivateFirst=true to deactivate and delete it"
                        });
                        continue;
                    }

                    await svc.SetStateAsync(env.OrgUrl, id, activate: false, ct);
                }

                await svc.DeleteAsync(env.OrgUrl, id, ct);
                deleted.Add(new { workflowId = id, name = detail.Name });
            }

            return JsonSerializer.Serialize(new
            {
                success = skipped.Count == 0,
                deletedCount = deleted.Count,
                deleted,
                skipped = skipped.Count == 0 ? null : skipped
            }, JsonOptions);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message, details = ex.GetType().Name });
        }
    }

    [McpServerTool(Name = "workflow_assign")]
    [Description("Assign a Classic Workflow to a different owner (user or team).")]
    public static async Task<string> WorkflowAssign(
        WorkflowService svc,
        ConfigProvider config,
        [Description("The workflow GUID")] string workflowId,
        [Description("Owner type: 'user' or 'team'")] string ownerType,
        [Description("Owner GUID")] string ownerId,
        CancellationToken ct = default)
    {
        try
        {
            if (!Guid.TryParse(workflowId, out var wfId))
                return JsonSerializer.Serialize(new { error = "Invalid workflowId GUID format." });
            if (!Guid.TryParse(ownerId, out var owId))
                return JsonSerializer.Serialize(new { error = "Invalid ownerId GUID format." });

            var env = config.GetActiveEnvironment();
            await svc.AssignAsync(env.OrgUrl, wfId, ownerType, owId, ct);
            return JsonSerializer.Serialize(new { success = true, workflowId, ownerType, ownerId });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message, details = ex.GetType().Name });
        }
    }

    [McpServerTool(Name = "workflow_list_activities")]
    [Description("List all Custom Workflow Activities (code activities) registered in Dataverse, including their AssemblyQualifiedName for use in XAML.")]
    public static async Task<string> WorkflowListActivities(
        WorkflowService svc,
        ConfigProvider config,
        [Description("Optional name filter (substring match)")] string? nameFilter = null,
        CancellationToken ct = default)
    {
        try
        {
            var env = config.GetActiveEnvironment();
            var result = await svc.ListActivitiesAsync(env.OrgUrl, nameFilter, ct);
            return JsonSerializer.Serialize(result, JsonOptions);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message, details = ex.GetType().Name });
        }
    }

    [McpServerTool(Name = "workflow_get_activity_parameters")]
    [Description("Get the Input and Output parameters of a Custom Workflow Activity, needed to wire up arguments in XAML.")]
    public static async Task<string> WorkflowGetActivityParameters(
        WorkflowService svc,
        ConfigProvider config,
        [Description("The PluginType GUID (from workflow_list_activities)")] string pluginTypeId,
        CancellationToken ct = default)
    {
        try
        {
            if (!Guid.TryParse(pluginTypeId, out var id))
                return JsonSerializer.Serialize(new { error = "Invalid pluginTypeId GUID format." });

            var env = config.GetActiveEnvironment();
            var result = await svc.GetActivityParametersAsync(env.OrgUrl, id, ct);
            return result is null
                ? JsonSerializer.Serialize(new { error = "Plugin type not found." })
                : JsonSerializer.Serialize(result, JsonOptions);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message, details = ex.GetType().Name });
        }
    }

    [McpServerTool(Name = "workflow_explain")]
    [Description("Explain a Classic Workflow in readable form: triggers, execution settings and the " +
                 "full step tree with conditions and field assignments. Start here when asked what a " +
                 "workflow does. If the report ends with a 'Not understood' section, the workflow uses " +
                 "constructs this server cannot model — do not rewrite it with workflow_set_definition.")]
    public static async Task<string> WorkflowExplain(
        WorkflowAuthoringService authoring,
        ConfigProvider config,
        [Description("The workflow GUID")] string workflowId,
        CancellationToken ct = default)
    {
        try
        {
            if (!Guid.TryParse(workflowId, out var id))
                return JsonSerializer.Serialize(new { error = "Invalid workflowId GUID format." });

            var env = config.GetActiveEnvironment();
            var (detail, parsed) = await authoring.GetDefinitionAsync(env.OrgUrl, id, ct);
            return WorkflowExplainer.Explain(detail, parsed);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message, details = ex.GetType().Name });
        }
    }

    [McpServerTool(Name = "workflow_get_definition")]
    [Description("Read a Classic Workflow's logic as an editable JSON definition (the same shape " +
                 "workflow_set_definition accepts). Use this to modify an existing workflow: read, " +
                 "change the JSON, write it back. IMPORTANT: only write it back when " +
                 "'fullyUnderstood' is true — otherwise parts of the original would be lost.")]
    public static async Task<string> WorkflowGetDefinition(
        WorkflowAuthoringService authoring,
        ConfigProvider config,
        [Description("The workflow GUID")] string workflowId,
        CancellationToken ct = default)
    {
        try
        {
            if (!Guid.TryParse(workflowId, out var id))
                return JsonSerializer.Serialize(new { error = "Invalid workflowId GUID format." });

            var env = config.GetActiveEnvironment();
            var (detail, parsed) = await authoring.GetDefinitionAsync(env.OrgUrl, id, ct);

            return JsonSerializer.Serialize(new
            {
                workflowId = id,
                name = detail.Name,
                stateCode = detail.StateCode,
                isDraft = detail.StateCode != 1,
                fullyUnderstood = parsed.FullyUnderstood,
                unrecognised = parsed.Unrecognised,
                notes = parsed.Notes,
                definition = parsed.Definition
            }, JsonOptions);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message, details = ex.GetType().Name });
        }
    }

    [McpServerTool(Name = "workflow_validate_definition")]
    [Description("Check a workflow definition without writing anything. Validates structure, " +
                 "completeness, value expressions and — against live metadata — that every table and " +
                 "attribute exists. Returns issues with a code, a JSON path, the problem and the fix. " +
                 "Use this while composing a definition; workflow_set_definition runs the same checks " +
                 "and refuses to write when any error remains. " +
                 "Pass the definition inline as definitionJson, or as definitionFile — a path to a " +
                 "local .json file, which is the better choice for anything large.")]
    public static async Task<string> WorkflowValidateDefinition(
        WorkflowAuthoringService authoring,
        ConfigProvider config,
        [Description("The workflow definition as JSON (see the classic-workflows skill for the shape)")] string? definitionJson = null,
        [Description("Path to a local file holding the definition JSON — use this instead of definitionJson for large definitions")] string? definitionFile = null,
        CancellationToken ct = default)
    {
        try
        {
            var payload = await PayloadSource.ResolveAsync(
                definitionJson, definitionFile, nameof(definitionJson), nameof(definitionFile), ct);
            if (payload.Error is not null)
                return JsonSerializer.Serialize(new { canSave = false, error = payload.Error }, JsonOptions);

            var definition = ParseDefinition(payload.Content!, out var parseError);
            if (definition is null)
                return JsonSerializer.Serialize(new { canSave = false, issues = new[] { parseError } }, JsonOptions);

            var env = config.GetActiveEnvironment();
            var result = await authoring.ValidateAsync(env.OrgUrl, definition, ct);

            return JsonSerializer.Serialize(new
            {
                canSave = result.CanSave,
                errorCount = result.ErrorCount,
                warningCount = result.WarningCount,
                issues = result.Issues
            }, JsonOptions);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message, details = ex.GetType().Name });
        }
    }

    [McpServerTool(Name = "workflow_set_definition")]
    [Description("Write a workflow's logic from a JSON definition. This is the supported way to author " +
                 "Classic Workflow logic: step ids, DisplayNames and variables are generated so the " +
                 "Dataverse designer can still render the result. " +
                 "Nothing is written unless validation passes (structure, value expressions, and table/" +
                 "attribute existence) and the generated XAML passes a self-check. " +
                 "The workflow must be a draft; pass reactivate=true to deactivate, write and " +
                 "re-activate in one call. The response contains the previous XAML as 'backup'. " +
                 "Pass dryRun=true first on a workflow you did not author: it validates and reports " +
                 "what would change, in 'diff', without writing anything. " +
                 "Pass the definition inline as definitionJson, or as definitionFile — a path to a " +
                 "local .json file. Prefer definitionFile: a definition with an embedded image runs to " +
                 "tens of thousands of characters, and copying it inline flips characters silently, " +
                 "which no validation catches. backupFile writes the previous XAML to disk instead of " +
                 "into the response, which keeps the response small enough to read.")]
    public static async Task<string> WorkflowSetDefinition(
        WorkflowAuthoringService authoring,
        ConfigProvider config,
        [Description("The workflow GUID")] string workflowId,
        [Description("The workflow definition as JSON (see the classic-workflows skill for the shape)")] string? definitionJson = null,
        [Description("Path to a local file holding the definition JSON — use this instead of definitionJson for large definitions")] string? definitionFile = null,
        [Description("If the workflow is active: deactivate, write, re-activate (default false = refuse)")] bool reactivate = false,
        [Description("Validate and report what would change, without writing (default false)")] bool dryRun = false,
        [Description("Path to write the previous XAML to; the response then carries 'backupFile' instead of the XAML itself")] string? backupFile = null,
        CancellationToken ct = default)
    {
        try
        {
            if (!Guid.TryParse(workflowId, out var id))
                return JsonSerializer.Serialize(new { error = "Invalid workflowId GUID format." });

            var payload = await PayloadSource.ResolveAsync(
                definitionJson, definitionFile, nameof(definitionJson), nameof(definitionFile), ct);
            if (payload.Error is not null)
                return JsonSerializer.Serialize(new { applied = false, error = payload.Error }, JsonOptions);

            var definition = ParseDefinition(payload.Content!, out var parseError);
            if (definition is null)
                return JsonSerializer.Serialize(new { applied = false, issues = new[] { parseError } }, JsonOptions);

            var env = config.GetActiveEnvironment();
            var result = await authoring.SetDefinitionAsync(env.OrgUrl, id, definition, reactivate, dryRun, ct);

            // The backup is the whole previous XAML — six figures of characters for a real workflow.
            // Written to disk it stays a usable restore point without swamping the response.
            var backupPath = backupFile is null || result.Backup is null
                ? null
                : await PayloadSource.WriteAsync(backupFile, result.Backup, ct);

            return JsonSerializer.Serialize(new
            {
                applied = result.Applied,
                dryRun = dryRun ? true : (bool?)null,
                diff = result.Diff,
                workflowId = result.WorkflowId,
                message = result.Message,
                stepIds = result.StepIds,
                canSave = result.Validation.CanSave,
                errorCount = result.Validation.ErrorCount,
                warningCount = result.Validation.WarningCount,
                issues = result.Validation.Issues,
                // Keep either of these to undo the change with workflow_restore_xaml.
                backupFile = backupPath,
                backup = backupPath is null ? result.Backup : null
            }, JsonOptions);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message, details = ex.GetType().Name });
        }
    }

    [McpServerTool(Name = "workflow_diagnose_activation")]
    [Description("Find out WHICH PART of a definition Dataverse refuses to activate. " +
                 "Activation errors say almost nothing on their own (0x80040216 is literally " +
                 "'an unexpected error occurred'), so this writes subsets of the definition into " +
                 "throwaway workflows and activates each, until the culprit is isolated — first per " +
                 "step, then per case of a condition. The throwaway workflows are deleted again. " +
                 "Use this instead of guessing when workflow_set_state fails after a clean write.")]
    public static async Task<string> WorkflowDiagnoseActivation(
        WorkflowActivationDiagnoser diagnoser,
        ConfigProvider config,
        [Description("Logical name of the primary entity")] string primaryEntity,
        [Description("The workflow definition as JSON — the one that will not activate")] string? definitionJson = null,
        [Description("Path to a local file holding the definition JSON — use this instead of definitionJson for large definitions")] string? definitionFile = null,
        [Description("true if the workflow is real-time (default false = background)")] bool isRealtime = false,
        CancellationToken ct = default)
    {
        try
        {
            var payload = await PayloadSource.ResolveAsync(
                definitionJson, definitionFile, nameof(definitionJson), nameof(definitionFile), ct);
            if (payload.Error is not null)
                return JsonSerializer.Serialize(new { error = payload.Error }, JsonOptions);

            var definition = ParseDefinition(payload.Content!, out var parseError);
            if (definition is null)
                return JsonSerializer.Serialize(new { issues = new[] { parseError } }, JsonOptions);

            if (definition.Steps.Count == 0)
                return JsonSerializer.Serialize(new { error = "The definition has no steps to bisect." });

            var env = config.GetActiveEnvironment();
            var result = await diagnoser.DiagnoseAsync(
                env.OrgUrl, definition, primaryEntity, isRealtime, ct);

            return JsonSerializer.Serialize(new
            {
                activated = result.Activated,
                error = result.Error,
                culprit = result.Culprit,
                attempts = result.Attempts
            }, JsonOptions);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message, details = ex.GetType().Name });
        }
    }

    [McpServerTool(Name = "workflow_restore_xaml")]
    [Description("Write a previously exported XAML back verbatim, to undo a change. " +
                 "Takes the string from workflow_export_xaml or the 'backup' field of " +
                 "workflow_set_definition. The workflow must be a draft. " +
                 "Prefer xamlFile — a path to the file the backup was written to. A workflow's XAML " +
                 "runs to six figures of characters, and 'verbatim' is not something that survives " +
                 "being copied inline.")]
    public static async Task<string> WorkflowRestoreXaml(
        WorkflowAuthoringService authoring,
        ConfigProvider config,
        [Description("The workflow GUID")] string workflowId,
        [Description("The XAML to restore")] string? xaml = null,
        [Description("Path to a local file holding the XAML — use this instead of xaml, which is large by nature")] string? xamlFile = null,
        CancellationToken ct = default)
    {
        try
        {
            if (!Guid.TryParse(workflowId, out var id))
                return JsonSerializer.Serialize(new { error = "Invalid workflowId GUID format." });

            var payload = await PayloadSource.ResolveAsync(
                xaml, xamlFile, nameof(xaml), nameof(xamlFile), ct);
            if (payload.Error is not null)
                return JsonSerializer.Serialize(new { error = payload.Error }, JsonOptions);

            var env = config.GetActiveEnvironment();
            await authoring.RestoreXamlAsync(env.OrgUrl, id, payload.Content!, ct);
            return JsonSerializer.Serialize(new { success = true, workflowId });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message, details = ex.GetType().Name });
        }
    }

    /// <summary>Deserialises a definition and turns failures into an actionable issue.</summary>
    private static WorkflowDefinition? ParseDefinition(string json, out object error)
    {
        error = new
        {
            severity = "error",
            code = "WF000",
            path = "$",
            problem = "definitionJson could not be parsed as JSON.",
            fix = "Pass a JSON object like " +
                  "{\"primaryEntity\":\"lead\",\"steps\":[{\"kind\":\"updateRecord\"," +
                  "\"attributes\":[{\"attribute\":\"subject\",\"value\":{\"kind\":\"literal\",\"literal\":\"x\"}}]}]}"
        };

        try
        {
            var definition = JsonSerializer.Deserialize<WorkflowDefinition>(json, DefinitionJsonOptions);
            if (definition is null)
                return null;
            error = null!;
            return definition;
        }
        catch (JsonException ex)
        {
            error = new
            {
                severity = "error",
                code = "WF000",
                path = "$",
                problem = "definitionJson is not valid JSON: " + ex.Message,
                fix = "Fix the JSON syntax. Field names are camelCase: kind, conditions, then, else, " +
                      "attributes, value, literal, fields, fallback, stepOutput."
            };
            return null;
        }
    }

    private static readonly JsonSerializerOptions DefinitionJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    [McpServerTool(Name = "workflow_validate")]
    [Description("Quick health check of a stored Classic Workflow (has XAML, has a primary entity, " +
                 "is activated). For checking a definition you are about to write, use " +
                 "workflow_validate_definition instead.")]
    public static async Task<string> WorkflowValidate(
        WorkflowService svc,
        ConfigProvider config,
        [Description("The workflow GUID")] string workflowId,
        CancellationToken ct = default)
    {
        try
        {
            if (!Guid.TryParse(workflowId, out var id))
                return JsonSerializer.Serialize(new { error = "Invalid workflowId GUID format." });

            var env = config.GetActiveEnvironment();
            var report = await svc.ValidateAsync(env.OrgUrl, id, ct);
            return JsonSerializer.Serialize(report, JsonOptions);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message, details = ex.GetType().Name });
        }
    }
}
