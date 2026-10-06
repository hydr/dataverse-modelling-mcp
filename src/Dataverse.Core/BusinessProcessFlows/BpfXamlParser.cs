namespace Dataverse.Core.BusinessProcessFlows;

using System.Xml.Linq;
using Dataverse.Core.Workflows;

/// <param name="FullyUnderstood">
/// True when every element was mapped. When false, writing <see cref="Definition"/> back would drop
/// what <see cref="Unrecognised"/> lists.
/// </param>
public sealed record BpfParseResult(
    BpfDefinition Definition,
    bool FullyUnderstood,
    IReadOnlyList<string> Unrecognised,
    IReadOnlyList<string> Notes);

/// <summary>Reads business-process-flow XAML back into a <see cref="BpfDefinition"/>.</summary>
/// <remarks>
/// <para>
/// Two shapes exist. The current designer writes one <c>EntityComposite</c> per stage and links
/// stages with <c>NextStageId</c>; cross-table transitions sit in a
/// <c>StageRelationshipCollectionComposite</c>. The system processes shipped with Dynamics are older:
/// one <c>EntityComposite</c> per table holding several stages in path order, and the relationship to
/// the next table on the composite itself. Both are read into the same model; writing it back
/// produces the current shape.
/// </para>
/// <para>Element names are matched by local name, independent of namespace prefixes.</para>
/// </remarks>
public static class BpfXamlParser
{
    public static BpfParseResult Parse(string? xaml, string primaryEntity)
    {
        var unrecognised = new List<string>();
        var notes = new List<string>();

        if (string.IsNullOrWhiteSpace(xaml))
            return new BpfParseResult(new BpfDefinition { PrimaryEntity = primaryEntity }, true, unrecognised,
                ["The process has no XAML."]);

        XDocument doc;
        try
        {
            doc = XDocument.Parse(xaml);
        }
        catch (System.Xml.XmlException ex)
        {
            return new BpfParseResult(new BpfDefinition { PrimaryEntity = primaryEntity }, false,
                [$"XAML is not well-formed: {ex.Message}"], notes);
        }

        var workflow = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Workflow");
        if (workflow is null)
            return new BpfParseResult(new BpfDefinition { PrimaryEntity = primaryEntity }, false,
                ["No <mxswa:Workflow> root element found."], notes);

        var stages = new List<BpfStage>();
        var storedNext = new List<string?>();
        var relationships = new List<(string Name, string? Attribute, string Source, string Target)>();

        // Legacy shape: the relationship hangs on the composite of the table it leaves.
        var legacyLinks = new List<(int LastStageIndex, string Name, string? Attribute)>();
        var hasExplicitPath = false;
        int? language = null;

        foreach (var element in workflow.Elements())
        {
            var aqn = Attr(element, "AssemblyQualifiedName") ?? string.Empty;

            if (aqn.Contains(".StageRelationshipCollectionComposite"))
            {
                foreach (var rel in element.Descendants().Where(e => e.Name.LocalName == "StageRelationship"))
                    relationships.Add((Attr(rel, "RelationshipName") ?? string.Empty,
                        NullIfEmpty(Attr(rel, "AttributeName")),
                        Attr(rel, "SourceStageId") ?? string.Empty,
                        Attr(rel, "TargetStageId") ?? string.Empty));
                continue;
            }

            if (!aqn.Contains(".EntityComposite"))
            {
                unrecognised.Add($"Unhandled element <{element.Name.LocalName}> {Attr(element, "DisplayName")}");
                continue;
            }

            var entity = EntityOf(element) ?? primaryEntity;

            foreach (var child in ActivitiesOf(element))
            {
                var childAqn = Attr(child, "AssemblyQualifiedName") ?? string.Empty;

                if (childAqn.Contains(".StageComposite"))
                {
                    var (stage, nextId, explicitPath) = ParseStage(child, entity, unrecognised, ref language);
                    hasExplicitPath |= explicitPath;
                    stages.Add(stage);
                    storedNext.Add(nextId);
                }
                else if (childAqn.Contains(".PageComposite"))
                {
                    unrecognised.Add(
                        $"Page '{Attr(child, "DisplayName")}' — pages belong to task flows (business process "
                        + "type 1), which this server does not author.");
                }
                else
                {
                    unrecognised.Add($"Unhandled element in table '{entity}': {Attr(child, "DisplayName") ?? child.Name.LocalName}");
                }
            }

            var legacyRelationship = PropertyString(element, "RelationshipName");
            if (!string.IsNullOrEmpty(legacyRelationship) && stages.Count > 0)
                legacyLinks.Add((stages.Count - 1, legacyRelationship, NullIfEmpty(PropertyString(element, "AttributeName"))));

            if (string.Equals(PropertyString(element, "IsClosedLoop"), "True", StringComparison.OrdinalIgnoreCase))
                unrecognised.Add($"Table '{entity}' is marked as a closed loop, which this server does not author.");
        }

        // The legacy shape has no NextStageId: the path is the document order.
        if (!hasExplicitPath && stages.Count > 1)
            notes.Add("The process uses the older format (path in document order). Writing it back stores "
                      + "the current designer's format; ids and behaviour stay the same.");

        stages = ApplyPath(stages, storedNext, hasExplicitPath);

        // Relationships become part of the stage they lead into.
        foreach (var (name, attribute, source, target) in relationships)
            stages = AttachRelationship(stages, name, attribute, source, target, unrecognised);

        foreach (var (lastIndex, name, attribute) in legacyLinks)
        {
            if (lastIndex + 1 < stages.Count)
                stages = AttachRelationship(stages, name, attribute,
                    stages[lastIndex].StageId!, stages[lastIndex + 1].StageId!, unrecognised);
        }

        // Inherited tables are left implicit so the definition reads like a hand-written one.
        var simplified = new List<BpfStage>();
        string? previous = primaryEntity;
        foreach (var stage in stages)
        {
            simplified.Add(string.Equals(stage.Entity, previous, StringComparison.OrdinalIgnoreCase)
                ? stage with { Entity = null }
                : stage);
            previous = stage.Entity;
        }

        var definition = new BpfDefinition
        {
            PrimaryEntity = primaryEntity,
            Stages = simplified,
            LanguageCode = language
        };

        return new BpfParseResult(definition, unrecognised.Count == 0, unrecognised, notes);
    }

    // ---------------------------------------------------------------- stages and steps

    /// <returns>The stage, its stored next-stage id, and whether the stage stores one at all.</returns>
    private static (BpfStage Stage, string? NextId, bool ExplicitPath) ParseStage(
        XElement element, string entity, List<string> unrecognised, ref int? language)
    {
        var (_, displayDescription) = WorkflowXamlParser.SplitDisplayName(Attr(element, "DisplayName"));
        var stageId = NormaliseId(PropertyString(element, "StageId"));
        var label = FirstLabel(element, ref language);

        var nextElement = PropertyElementIncludingNull(element, "NextStageId");
        var explicitPath = nextElement is not null;
        var nextId = nextElement?.Name.LocalName == "String" ? NormaliseId(nextElement.Value) : null;

        var steps = new List<BpfStep>();
        BpfBranching? branching = null;

        foreach (var child in ActivitiesOf(element))
        {
            var aqn = Attr(child, "AssemblyQualifiedName") ?? string.Empty;

            if (aqn.Contains(".StepComposite"))
            {
                var step = ParseStep(child, unrecognised, ref language);
                if (step is not null)
                    steps.Add(step);
            }
            else if (aqn.Contains(".ConditionSequence"))
            {
                if (branching is not null)
                    unrecognised.Add($"Stage '{label}' has more than one condition; only the first is kept.");
                else
                    branching = ParseBranching(child, entity, label, unrecognised);
            }
            else
            {
                unrecognised.Add($"Unhandled element in stage '{label}': {Attr(child, "DisplayName") ?? child.Name.LocalName}");
            }
        }

        var stage = new BpfStage
        {
            StageId = stageId,
            Name = label ?? displayDescription ?? string.Empty,
            Entity = entity,
            Category = BpfStageCategory.FromNumber(PropertyString(element, "StageCategory")),
            Steps = steps,
            Branch = branching
        };

        return (stage, nextId, explicitPath);
    }

    private static BpfStep? ParseStep(XElement element, List<string> unrecognised, ref int? language)
    {
        var label = FirstLabel(element, ref language);
        var stepId = NormaliseId(PropertyString(element, "ProcessStepId"));
        var required = string.Equals(PropertyString(element, "IsProcessRequired"), "True", StringComparison.OrdinalIgnoreCase);

        var control = element.Descendants().FirstOrDefault(e => e.Name.LocalName == "Control");
        if (control is null)
        {
            unrecognised.Add($"Step '{label}' holds no data field; this kind of step is not supported yet.");
            return null;
        }

        var field = Attr(control, "DataFieldName") ?? AttrElement(control, "DataFieldName");
        if (string.IsNullOrEmpty(field))
        {
            unrecognised.Add($"Step '{label}' shows control '{Attr(control, "ControlId")}' without a field "
                             + "(a link control of a system process); it is not supported.");
            return null;
        }

        return new BpfStep
        {
            Kind = BpfStepKind.Field,
            StepId = stepId,
            Label = label,
            Attribute = field,
            Required = required,
            ClassId = Attr(control, "ClassId")?.ToUpperInvariant(),
            Parameters = NullIfEmpty(Attr(control, "Parameters")),
            SystemControl = string.Equals(Attr(control, "IsSystemControl"), "True", StringComparison.OrdinalIgnoreCase)
        };
    }

    private static BpfBranching ParseBranching(
        XElement element, string entity, string? stageLabel, List<string> unrecognised)
    {
        var branching = new BpfBranching();
        var branches = new List<BpfBranch>();
        string? otherwise = null;

        foreach (var @case in WorkflowXamlParser.ReadConditionCases(element, entity))
        {
            var target = @case.Then?.Descendants().FirstOrDefault(e => e.Name.LocalName == "SetNextStage");
            var targetId = NormaliseId(Attr(target, "StageId"));

            if (targetId is null)
            {
                unrecognised.Add($"A branch of stage '{stageLabel}' does not lead to a stage.");
                continue;
            }

            if (@case.IsDefault)
            {
                otherwise = targetId;
                continue;
            }

            branches.Add(new BpfBranch
            {
                // The stage's table is implied; repeating it in every comparison is noise.
                Conditions = @case.Conditions.Select(c => WithoutEntity(c, entity)).ToList(),
                LogicalOperator = @case.LogicalOperator,
                Next = targetId,
                Description = @case.Description
            });
        }

        return branching with { Branches = branches, Else = otherwise };
    }

    private static WorkflowCondition WithoutEntity(WorkflowCondition condition, string entity) =>
        condition.IsGroup
            ? condition with { Conditions = condition.Conditions!.Select(c => WithoutEntity(c, entity)).ToList() }
            : string.Equals(condition.Entity, entity, StringComparison.OrdinalIgnoreCase)
                ? condition with { Entity = null }
                : condition;

    // ---------------------------------------------------------------- path and relationships

    /// <summary>
    /// Turns the stored next-stage ids into the model's <c>next</c>: left out where it is simply the
    /// following stage, <c>"end"</c> where the path stops early, the id otherwise.
    /// </summary>
    private static List<BpfStage> ApplyPath(List<BpfStage> stages, List<string?> storedNext, bool explicitPath)
    {
        if (!explicitPath)
            return stages;

        var result = new List<BpfStage>();
        for (var i = 0; i < stages.Count; i++)
        {
            var stored = storedNext[i];
            var following = i + 1 < stages.Count ? stages[i + 1].StageId : null;

            string? next;
            if (stored is null)
                next = following is null ? null : BpfStageResolver.End;
            else if (string.Equals(stored, following, StringComparison.OrdinalIgnoreCase))
                next = null;
            else
                next = stored;

            result.Add(stages[i] with { Next = next });
        }

        return result;
    }

    private static List<BpfStage> AttachRelationship(
        List<BpfStage> stages, string name, string? attribute, string source, string target,
        List<string> unrecognised)
    {
        var sourceId = NormaliseId(source);
        var targetId = NormaliseId(target);
        var index = stages.FindIndex(s => string.Equals(s.StageId, targetId, StringComparison.OrdinalIgnoreCase));

        if (index < 0)
        {
            unrecognised.Add($"Relationship '{name}' leads to stage {target}, which does not exist.");
            return stages;
        }

        if (stages[index].Relationship is not null)
        {
            unrecognised.Add($"Stage '{stages[index].Name}' is entered through more than one relationship; "
                             + $"only '{stages[index].Relationship!.Name}' is kept.");
            return stages;
        }

        // The source is only worth stating when the default would pick another one.
        var defaultSource = index > 0 ? stages[index - 1].StageId : null;
        var fromStage = string.Equals(defaultSource, sourceId, StringComparison.OrdinalIgnoreCase) ? null : sourceId;

        var copy = stages.ToList();
        copy[index] = copy[index] with
        {
            Relationship = new BpfRelationship { Name = name, Attribute = attribute, FromStage = fromStage }
        };
        return copy;
    }

    // ---------------------------------------------------------------- XML helpers

    private static string? EntityOf(XElement entityComposite)
    {
        // "EntityStep3: lead" — the table is the description part of the DisplayName.
        var (_, description) = WorkflowXamlParser.SplitDisplayName(Attr(entityComposite, "DisplayName"));
        return string.IsNullOrWhiteSpace(description) ? null : description.Trim();
    }

    private static string? FirstLabel(XElement element, ref int? language)
    {
        var labels = PropertyElement(element, "StepLabels");
        var label = labels?.Elements().FirstOrDefault(e => e.Name.LocalName == "StepLabel");
        if (label is null)
            return null;

        if (language is null && int.TryParse(Attr(label, "LanguageCode"), out var code))
            language = code;

        return Attr(label, "Description");
    }

    private static IEnumerable<XElement> ActivitiesOf(XElement activityReference) =>
        PropertyElement(activityReference, "Activities")?.Elements() ?? [];

    private static XElement? PropertyElement(XElement activityReference, string key) =>
        PropertyElementIncludingNull(activityReference, key) is { } e && e.Name.LocalName != "Null" ? e : null;

    private static XElement? PropertyElementIncludingNull(XElement activityReference, string key) =>
        activityReference.Elements()
            .FirstOrDefault(e => e.Name.LocalName == "ActivityReference.Properties")
            ?.Elements()
            .FirstOrDefault(e => e.Attributes().Any(a => a.Name.LocalName == "Key" && a.Value == key));

    private static string? PropertyString(XElement activityReference, string key) =>
        PropertyElement(activityReference, key)?.Value;

    private static string? Attr(XElement? element, string name) =>
        element?.Attributes().FirstOrDefault(a => a.Name.LocalName == name)?.Value;

    /// <summary>A property written as a child element (<c>&lt;mcwb:Control.DataFieldName&gt;</c>).</summary>
    private static string? AttrElement(XElement element, string name) =>
        element.Elements().FirstOrDefault(e => e.Name.LocalName.EndsWith("." + name, StringComparison.Ordinal))
            ?.Descendants().Select(d => Attr(d, "Value")).FirstOrDefault(v => !string.IsNullOrEmpty(v));

    private static string? NormaliseId(string? id) =>
        string.IsNullOrWhiteSpace(id) ? null : id.Trim().Trim('{', '}').ToLowerInvariant();

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;
}
