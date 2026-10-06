namespace Dataverse.Server.Tools;

using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dataverse.Core.BusinessProcessFlows;
using Dataverse.Core.Config;
using Dataverse.Core.Io;
using Dataverse.Core.Services;
using ModelContextProtocol.Server;

/// <summary>
/// Business process flows end to end: author the definition, manage the lifecycle, run instances.
/// See the business-process-flows skill for the definition shape.
/// </summary>
[McpServerToolType]
public sealed class BusinessProcessFlowTools
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        // Only nulls: a false canSave/applied/created must reach the caller.
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// For a definition handed back for editing: unset fields and false flags are noise there, and
    /// leaving them out keeps the JSON close to what a caller would write by hand.
    /// </summary>
    private static readonly JsonSerializerOptions DefinitionOutputOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault
    };

    private static readonly JsonSerializerOptions DefinitionJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private static string Error(Exception ex) =>
        JsonSerializer.Serialize(new { error = ex.Message, details = ex.GetType().Name });

    private static string Error(string message) => JsonSerializer.Serialize(new { error = message });

    // ---------------------------------------------------------------- read

    [McpServerTool(Name = "bpf_list")]
    [Description("List business process flows, optionally for one table, in their process order. " +
                 "uniqueName is also the logical name of the table holding the process's instances.")]
    public static async Task<string> BpfList(
        BusinessProcessFlowService svc,
        ConfigProvider config,
        [Description("Only processes on this table (logical name), e.g. 'opportunity'")] string? primaryEntity = null,
        [Description("Include task flows (business process type 1) as well (default false)")] bool includeTaskFlows = false,
        CancellationToken ct = default)
    {
        try
        {
            var env = config.GetActiveEnvironment();
            return JsonSerializer.Serialize(await svc.ListAsync(env.OrgUrl, primaryEntity, includeTaskFlows, ct), JsonOptions);
        }
        catch (Exception ex)
        {
            return Error(ex);
        }
    }

    [McpServerTool(Name = "bpf_get_definition")]
    [Description("Read a business process flow as an editable JSON definition — stages, steps, branches, " +
                 "relationships and triggered workflows, in the shape bpf_set_definition accepts. To change " +
                 "a process: read, edit, write back. Keep the stageId/stepId values: running instances " +
                 "point at them. Only write back when 'fullyUnderstood' is true, otherwise the parts listed " +
                 "in 'unrecognised' would be lost.")]
    public static async Task<string> BpfGetDefinition(
        BusinessProcessFlowService svc,
        ConfigProvider config,
        [Description("The process GUID (workflowid)")] string processId,
        CancellationToken ct = default)
    {
        try
        {
            if (!Guid.TryParse(processId, out var id))
                return Error("Invalid processId GUID format.");

            var env = config.GetActiveEnvironment();
            var (detail, parsed) = await svc.GetDefinitionAsync(env.OrgUrl, id, ct);

            return JsonSerializer.Serialize(new
            {
                processId = id,
                name = detail.Name,
                uniqueName = detail.UniqueName,
                description = detail.Description,
                isActivated = detail.StateCode == 1,
                processOrder = detail.ProcessOrder,
                isManaged = detail.IsManaged,
                type = detail.BusinessProcessType == 1 ? "task flow" : "business process flow",
                fullyUnderstood = parsed.FullyUnderstood,
                unrecognised = parsed.Unrecognised,
                notes = parsed.Notes,
                definition = JsonSerializer.SerializeToElement(parsed.Definition, DefinitionOutputOptions)
            }, JsonOptions);
        }
        catch (Exception ex)
        {
            return Error(ex);
        }
    }

    [McpServerTool(Name = "bpf_validate_definition")]
    [Description("Check a business-process-flow definition without writing anything: structure, paths and " +
                 "branches, the designer's own rules, and — against live metadata — tables, columns, " +
                 "relationships and the workflows/actions/flows it refers to. Returns issues with a code, " +
                 "a JSON path, the problem and the fix. Pass the definition inline as definitionJson or as " +
                 "definitionFile (a path to a local .json file).")]
    public static async Task<string> BpfValidateDefinition(
        BusinessProcessFlowService svc,
        ConfigProvider config,
        [Description("The definition as JSON (see the business-process-flows skill for the shape)")] string? definitionJson = null,
        [Description("Path to a local file holding the definition JSON — use this instead of definitionJson for large definitions")] string? definitionFile = null,
        CancellationToken ct = default)
    {
        try
        {
            var (definition, problem) = await ReadDefinitionAsync(definitionJson, definitionFile, ct);
            if (definition is null)
                return JsonSerializer.Serialize(new { canSave = false, issues = new[] { problem } }, JsonOptions);

            var env = config.GetActiveEnvironment();
            var (result, completed, _) = await svc.ValidateAsync(env.OrgUrl, definition, ct);

            return JsonSerializer.Serialize(new
            {
                canSave = result.CanSave,
                errorCount = result.ErrorCount,
                warningCount = result.WarningCount,
                issues = result.Issues,
                // Relationship attributes are filled in from metadata; show the caller what was assumed.
                relationships = completed.Stages.Where(s => s.Relationship is not null)
                    .Select(s => new { stage = s.Name, s.Relationship!.Name, s.Relationship.Attribute })
            }, JsonOptions);
        }
        catch (Exception ex)
        {
            return Error(ex);
        }
    }

    // ---------------------------------------------------------------- write

    [McpServerTool(Name = "bpf_create")]
    [Description("Create a business process flow from a definition — no designer needed. The definition is " +
                 "validated first; nothing is created while an error remains. Created as a draft unless " +
                 "activate=true. The FIRST activation creates the table that stores the instances (named " +
                 "after uniqueName) and takes about two minutes. Pass solutionUniqueName to create it in a " +
                 "solution; its publisher prefix is then used for a derived uniqueName.")]
    public static async Task<string> BpfCreate(
        BusinessProcessFlowService svc,
        ConfigProvider config,
        [Description("Display name of the process")] string name,
        [Description("The definition as JSON (see the business-process-flows skill)")] string? definitionJson = null,
        [Description("Path to a local file holding the definition JSON")] string? definitionFile = null,
        [Description("Unique name '<prefix>_<name>', e.g. 'sample_leadtoorder' — becomes the instance table's logical name. Derived from name when omitted.")] string? uniqueName = null,
        [Description("Optional description")] string? description = null,
        [Description("Unique name of the solution to create the process in")] string? solutionUniqueName = null,
        [Description("Activate right away (default false; the first activation takes about two minutes)")] bool activate = false,
        CancellationToken ct = default)
    {
        try
        {
            var (definition, problem) = await ReadDefinitionAsync(definitionJson, definitionFile, ct);
            if (definition is null)
                return JsonSerializer.Serialize(new { created = false, issues = new[] { problem } }, JsonOptions);

            var env = config.GetActiveEnvironment();
            var (result, unique) = await svc.CreateAsync(env.OrgUrl, name, definition, uniqueName, description,
                solutionUniqueName, activate, ct);

            return JsonSerializer.Serialize(new
            {
                created = result.Applied,
                processId = result.Applied ? result.ProcessId : (Guid?)null,
                uniqueName = unique,
                message = result.Message,
                stages = result.Stages,
                errorCount = result.Validation.ErrorCount,
                warningCount = result.Validation.WarningCount,
                issues = result.Validation.Issues
            }, JsonOptions);
        }
        catch (Exception ex)
        {
            return Error(ex);
        }
    }

    [McpServerTool(Name = "bpf_set_definition")]
    [Description("Rewrite a business process flow from a definition. Works on an ACTIVATED process — the " +
                 "platform updates stages and the instance table in place (a new table in the process gets " +
                 "its lookup column during the write). Stages and steps without an id are matched to the " +
                 "existing ones by name/table and field, so their ids — and the instances standing on " +
                 "them — survive. Nothing is written while validation reports an error. Pass dryRun=true " +
                 "first: it reports what would change in 'diff'. The previous XAML comes back as 'backup' " +
                 "(or in backupFile) for bpf_restore_xaml.")]
    public static async Task<string> BpfSetDefinition(
        BusinessProcessFlowService svc,
        ConfigProvider config,
        [Description("The process GUID")] string processId,
        [Description("The definition as JSON")] string? definitionJson = null,
        [Description("Path to a local file holding the definition JSON")] string? definitionFile = null,
        [Description("Validate and report what would change, without writing (default false)")] bool dryRun = false,
        [Description("Path to write the previous XAML to; the response then carries 'backupFile' instead of the XAML")] string? backupFile = null,
        CancellationToken ct = default)
    {
        try
        {
            if (!Guid.TryParse(processId, out var id))
                return Error("Invalid processId GUID format.");

            var (definition, problem) = await ReadDefinitionAsync(definitionJson, definitionFile, ct);
            if (definition is null)
                return JsonSerializer.Serialize(new { applied = false, issues = new[] { problem } }, JsonOptions);

            var env = config.GetActiveEnvironment();
            var result = await svc.SetDefinitionAsync(env.OrgUrl, id, definition, dryRun, ct);

            var backupPath = backupFile is null || result.Backup is null
                ? null
                : await PayloadSource.WriteAsync(backupFile, result.Backup, ct);

            return JsonSerializer.Serialize(new
            {
                applied = result.Applied,
                dryRun,
                diff = result.Diff,
                message = result.Message,
                stages = result.Stages,
                canSave = result.Validation.CanSave,
                errorCount = result.Validation.ErrorCount,
                warningCount = result.Validation.WarningCount,
                issues = result.Validation.Issues,
                backupFile = backupPath,
                backup = backupPath is null ? result.Backup : null
            }, JsonOptions);
        }
        catch (Exception ex)
        {
            return Error(ex);
        }
    }

    [McpServerTool(Name = "bpf_update")]
    [Description("Rename a business process flow or change its description. The unique name (and with it the " +
                 "instance table) cannot change.")]
    public static async Task<string> BpfUpdate(
        BusinessProcessFlowService svc,
        ConfigProvider config,
        [Description("The process GUID")] string processId,
        [Description("New display name")] string? name = null,
        [Description("New description")] string? description = null,
        CancellationToken ct = default)
    {
        try
        {
            if (!Guid.TryParse(processId, out var id))
                return Error("Invalid processId GUID format.");
            if (name is null && description is null)
                return Error("Pass name and/or description.");

            var env = config.GetActiveEnvironment();
            await svc.UpdatePropertiesAsync(env.OrgUrl, id, name, description, ct);
            return JsonSerializer.Serialize(new { success = true, processId = id });
        }
        catch (Exception ex)
        {
            return Error(ex);
        }
    }

    [McpServerTool(Name = "bpf_set_state")]
    [Description("Activate or deactivate a business process flow. The first activation creates the instance " +
                 "table and takes about two minutes; afterwards it is quick. Until users are granted access " +
                 "(bpf_grant_access), only System Administrator and System Customizer see the process.")]
    public static async Task<string> BpfSetState(
        BusinessProcessFlowService svc,
        ConfigProvider config,
        [Description("The process GUID")] string processId,
        [Description("true to activate, false to deactivate")] bool activate,
        CancellationToken ct = default)
    {
        try
        {
            if (!Guid.TryParse(processId, out var id))
                return Error("Invalid processId GUID format.");

            var env = config.GetActiveEnvironment();
            var started = DateTime.UtcNow;
            await svc.SetStateAsync(env.OrgUrl, id, activate, ct);
            return JsonSerializer.Serialize(new
            {
                success = true,
                processId = id,
                isActivated = activate,
                seconds = (int)(DateTime.UtcNow - started).TotalSeconds
            });
        }
        catch (Exception ex)
        {
            return Error(ex);
        }
    }

    [McpServerTool(Name = "bpf_delete")]
    [Description("Delete a business process flow. Irreversible: its instance table and every instance go with " +
                 "it (the table disappears asynchronously, shortly after). An activated process must be " +
                 "deactivated first — pass deactivateFirst=true. Export the XAML first if in doubt.")]
    public static async Task<string> BpfDelete(
        BusinessProcessFlowService svc,
        ConfigProvider config,
        [Description("The process GUID")] string processId,
        [Description("Deactivate an activated process before deleting it (default false)")] bool deactivateFirst = false,
        CancellationToken ct = default)
    {
        try
        {
            if (!Guid.TryParse(processId, out var id))
                return Error("Invalid processId GUID format.");

            var env = config.GetActiveEnvironment();
            await svc.DeleteAsync(env.OrgUrl, id, deactivateFirst, ct);
            return JsonSerializer.Serialize(new { success = true, deleted = id });
        }
        catch (Exception ex)
        {
            return Error(ex);
        }
    }

    [McpServerTool(Name = "bpf_set_order")]
    [Description("Set the order of a table's business process flows. A new record gets the first process " +
                 "(in this order) the user has access to. Listed processes come first, in the given order; " +
                 "the others keep their relative order after them.")]
    public static async Task<string> BpfSetOrder(
        BusinessProcessFlowService svc,
        ConfigProvider config,
        [Description("Logical name of the table, e.g. 'lead'")] string primaryEntity,
        [Description("Process GUIDs in the wanted order, comma-separated")] string processIds,
        CancellationToken ct = default)
    {
        try
        {
            var ids = new List<Guid>();
            foreach (var part in processIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!Guid.TryParse(part, out var g))
                    return Error($"'{part}' is not a GUID.");
                ids.Add(g);
            }

            var env = config.GetActiveEnvironment();
            return JsonSerializer.Serialize(await svc.SetOrderAsync(env.OrgUrl, primaryEntity, ids, ct), JsonOptions);
        }
        catch (Exception ex)
        {
            return Error(ex);
        }
    }

    [McpServerTool(Name = "bpf_grant_access")]
    [Description("Give security roles access to a business process flow — what the designer's 'Edit security " +
                 "roles' does. Access to a process is access to its instance table, so this grants the " +
                 "table's privileges at organisation level (or only Read with readOnly=true). The process " +
                 "must have been activated once, so the table exists.")]
    public static async Task<string> BpfGrantAccess(
        BusinessProcessFlowService svc,
        ConfigProvider config,
        [Description("The process GUID")] string processId,
        [Description("Role GUIDs, comma-separated (role_list shows them)")] string roleIds,
        [Description("Grant Read only, so users see the process but cannot move it (default false)")] bool readOnly = false,
        CancellationToken ct = default)
    {
        try
        {
            if (!Guid.TryParse(processId, out var id))
                return Error("Invalid processId GUID format.");

            var roles = new List<Guid>();
            foreach (var part in roleIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!Guid.TryParse(part, out var g))
                    return Error($"'{part}' is not a GUID.");
                roles.Add(g);
            }

            var env = config.GetActiveEnvironment();
            var granted = await svc.GrantAccessAsync(env.OrgUrl, id, roles, readOnly, ct);
            return JsonSerializer.Serialize(new { success = true, roles, privileges = granted }, JsonOptions);
        }
        catch (Exception ex)
        {
            return Error(ex);
        }
    }

    [McpServerTool(Name = "bpf_export_xaml")]
    [Description("Export the raw XAML of a business process flow, as-is — a restore point before a change. " +
                 "Pass file to write it to disk instead of returning it.")]
    public static async Task<string> BpfExportXaml(
        BusinessProcessFlowService svc,
        ConfigProvider config,
        [Description("The process GUID")] string processId,
        [Description("Path to write the XAML to (recommended — it is large)")] string? file = null,
        CancellationToken ct = default)
    {
        try
        {
            if (!Guid.TryParse(processId, out var id))
                return Error("Invalid processId GUID format.");

            var env = config.GetActiveEnvironment();
            var detail = await svc.GetAsync(env.OrgUrl, id, ct);
            if (detail is null)
                return Error($"Business process flow {id} not found.");

            if (file is not null)
                return JsonSerializer.Serialize(new { file = await PayloadSource.WriteAsync(file, detail.Xaml ?? string.Empty, ct) });

            return JsonSerializer.Serialize(new { processId = id, xaml = detail.Xaml });
        }
        catch (Exception ex)
        {
            return Error(ex);
        }
    }

    [McpServerTool(Name = "bpf_restore_xaml")]
    [Description("Write a previously exported XAML back verbatim, to undo a change (from bpf_export_xaml or " +
                 "the backup of bpf_set_definition). Works on an activated process. Prefer xamlFile.")]
    public static async Task<string> BpfRestoreXaml(
        BusinessProcessFlowService svc,
        ConfigProvider config,
        [Description("The process GUID")] string processId,
        [Description("The XAML to restore")] string? xaml = null,
        [Description("Path to a local file holding the XAML")] string? xamlFile = null,
        CancellationToken ct = default)
    {
        try
        {
            if (!Guid.TryParse(processId, out var id))
                return Error("Invalid processId GUID format.");

            var payload = await PayloadSource.ResolveAsync(xaml, xamlFile, nameof(xaml), nameof(xamlFile), ct);
            if (payload.Error is not null)
                return Error(payload.Error);

            var env = config.GetActiveEnvironment();
            await svc.RestoreXamlAsync(env.OrgUrl, id, payload.Content!, ct);
            return JsonSerializer.Serialize(new { success = true, processId = id });
        }
        catch (Exception ex)
        {
            return Error(ex);
        }
    }

    // ---------------------------------------------------------------- instances

    [McpServerTool(Name = "bpf_instance_list")]
    [Description("List the business-process-flow instances running over one record, across all processes — " +
                 "the most recently touched first, which is the one the form shows. Gives each instance's " +
                 "active stage and status.")]
    public static async Task<string> BpfInstanceList(
        BusinessProcessFlowService svc,
        ConfigProvider config,
        [Description("Logical name of the record's table, e.g. 'lead'")] string entity,
        [Description("The record GUID")] string recordId,
        CancellationToken ct = default)
    {
        try
        {
            if (!Guid.TryParse(recordId, out var id))
                return Error("Invalid recordId GUID format.");

            var env = config.GetActiveEnvironment();
            return JsonSerializer.Serialize(await svc.ListInstancesForRecordAsync(env.OrgUrl, entity, id, ct), JsonOptions);
        }
        catch (Exception ex)
        {
            return Error(ex);
        }
    }

    [McpServerTool(Name = "bpf_instance_start")]
    [Description("Start a business process flow on a record, or switch the record to it — creates an instance " +
                 "on the first stage (or on stageId, which must lie on the main path).")]
    public static async Task<string> BpfInstanceStart(
        BusinessProcessFlowService svc,
        ConfigProvider config,
        [Description("The process GUID")] string processId,
        [Description("The record GUID (on the process's primary table)")] string recordId,
        [Description("Stage to start on; the first stage when omitted")] string? stageId = null,
        CancellationToken ct = default)
    {
        try
        {
            if (!Guid.TryParse(processId, out var pid) || !Guid.TryParse(recordId, out var rid))
                return Error("processId and recordId must be GUIDs.");
            Guid? sid = null;
            if (stageId is not null)
            {
                if (!Guid.TryParse(stageId, out var parsed))
                    return Error("stageId must be a GUID.");
                sid = parsed;
            }

            var env = config.GetActiveEnvironment();
            var instance = await svc.StartInstanceAsync(env.OrgUrl, pid, rid, sid, ct);
            return JsonSerializer.Serialize(new { success = true, instanceId = instance });
        }
        catch (Exception ex)
        {
            return Error(ex);
        }
    }

    [McpServerTool(Name = "bpf_instance_move")]
    [Description("Move an instance to another stage: back to any stage it has passed, or forward to the stage " +
                 "that follows the active one (its next stage or a branch target) — one stage at a time, as " +
                 "in the form. The traversed path is kept consistent. Moving onto a stage of another table " +
                 "needs recordId: the record of that table the process continues on.")]
    public static async Task<string> BpfInstanceMove(
        BusinessProcessFlowService svc,
        ConfigProvider config,
        [Description("The process GUID")] string processId,
        [Description("The instance GUID (from bpf_instance_list)")] string instanceId,
        [Description("The target stage GUID")] string stageId,
        [Description("For a stage on another table: the record it works on")] string? recordId = null,
        CancellationToken ct = default)
    {
        try
        {
            if (!Guid.TryParse(processId, out var pid) || !Guid.TryParse(instanceId, out var iid) || !Guid.TryParse(stageId, out var sid))
                return Error("processId, instanceId and stageId must be GUIDs.");
            Guid? rid = null;
            if (recordId is not null)
            {
                if (!Guid.TryParse(recordId, out var parsed))
                    return Error("recordId must be a GUID.");
                rid = parsed;
            }

            var env = config.GetActiveEnvironment();
            var stage = await svc.MoveInstanceAsync(env.OrgUrl, pid, iid, sid, rid, ct);
            return JsonSerializer.Serialize(new { success = true, activeStage = stage });
        }
        catch (Exception ex)
        {
            return Error(ex);
        }
    }

    [McpServerTool(Name = "bpf_instance_set_status")]
    [Description("Finish, abandon or reactivate a business-process-flow instance. 'finished' is only possible " +
                 "on the last stage of the path.")]
    public static async Task<string> BpfInstanceSetStatus(
        BusinessProcessFlowService svc,
        ConfigProvider config,
        [Description("The process GUID")] string processId,
        [Description("The instance GUID")] string instanceId,
        [Description("'active', 'finished' or 'aborted'")] string status,
        CancellationToken ct = default)
    {
        try
        {
            if (!Guid.TryParse(processId, out var pid) || !Guid.TryParse(instanceId, out var iid))
                return Error("processId and instanceId must be GUIDs.");

            var env = config.GetActiveEnvironment();
            await svc.SetInstanceStatusAsync(env.OrgUrl, pid, iid, status, ct);
            return JsonSerializer.Serialize(new { success = true, instanceId = iid, status });
        }
        catch (Exception ex)
        {
            return Error(ex);
        }
    }

    // ---------------------------------------------------------------- helpers

    private static async Task<(BpfDefinition? Definition, object? Problem)> ReadDefinitionAsync(
        string? inline, string? file, CancellationToken ct)
    {
        var payload = await PayloadSource.ResolveAsync(inline, file, "definitionJson", "definitionFile", ct);
        if (payload.Error is not null)
            return (null, new { severity = "error", code = "BPF000", path = "$", problem = payload.Error, fix = "Pass the definition." });

        try
        {
            var definition = JsonSerializer.Deserialize<BpfDefinition>(payload.Content!, DefinitionJsonOptions);
            return definition is null
                ? (null, new { severity = "error", code = "BPF000", path = "$", problem = "The definition is empty.", fix = "Pass a JSON object." })
                : (definition, null);
        }
        catch (JsonException ex)
        {
            return (null, new
            {
                severity = "error",
                code = "BPF000",
                path = "$",
                problem = "The definition is not valid JSON: " + ex.Message,
                fix = "Fix the JSON. Shape: {\"primaryEntity\":\"lead\",\"stages\":[{\"name\":\"Qualify\"," +
                      "\"steps\":[{\"attribute\":\"subject\"}]}]}"
            });
        }
    }
}
