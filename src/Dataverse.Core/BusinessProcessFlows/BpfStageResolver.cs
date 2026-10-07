namespace Dataverse.Core.BusinessProcessFlows;

/// <summary>A stage with everything the definition leaves implicit worked out.</summary>
/// <param name="Entity">
/// The stage's table — its own, or inherited from the stages that lead to it (see
/// <see cref="BpfStageResolver"/>).
/// </param>
/// <param name="NextStageId">Id of the following stage on the main path, or null at the end.</param>
/// <param name="Relationship">The cross-table transition into this stage, with its source resolved.</param>
/// <param name="Predecessors">
/// Positions of the stages that lead here — through their main path or one of their branches.
/// </param>
/// <param name="AmbiguousEntity">
/// True when the stage names no table and the stages leading to it are on different tables, so
/// there is nothing sensible to inherit.
/// </param>
public sealed record BpfResolvedStage(
    BpfStage Source,
    int Index,
    string StageId,
    string Entity,
    string? NextStageId,
    BpfResolvedRelationship? Relationship,
    IReadOnlyList<int> Predecessors,
    bool AmbiguousEntity = false);

/// <param name="FromStageId">Id of the source stage; empty while the definition has no ids yet.</param>
/// <param name="FromIndex">Position of the source stage in the definition.</param>
public sealed record BpfResolvedRelationship(string Name, string? Attribute, string FromStageId, int FromIndex);

public sealed record BpfResolvedDefinition(IReadOnlyList<BpfResolvedStage> Stages)
{
    /// <summary>Id of the stage a reference names; throws when it names none.</summary>
    public string IdOf(string reference) =>
        BpfStageResolver.Find(Stages.Select(s => s.Source).ToList(), reference)?.StageId
        ?? throw new InvalidOperationException($"No stage '{reference}'.");
}

/// <summary>
/// Works out stage ids, tables, the main path and relationship sources from a definition.
/// </summary>
/// <remarks>
/// <para>
/// Shared by builder, parser and validator, so that all three read a definition the same way: a stage
/// is named by its <c>key</c>, its id or its name, in that order of precedence, ignoring case.
/// </para>
/// <para>
/// A stage without <c>entity</c> continues on the table of the stages that <em>lead</em> to it — over
/// the main path or a branch — not on that of the stage listed above it. Listing order only decides
/// the default <c>next</c>. A stage nothing leads to falls back to the stage listed above it; one that
/// is reached from stages on different tables has to name its table.
/// </para>
/// </remarks>
public static class BpfStageResolver
{
    /// <summary>Marks the end of the path in <see cref="BpfStage.Next"/>.</summary>
    public const string End = "end";

    /// <param name="assignMissingIds">
    /// Give stages and steps without an id a new one, writing it into the definition. The builder
    /// does; the validator only reads.
    /// </param>
    public static BpfResolvedDefinition Resolve(BpfDefinition definition, bool assignMissingIds)
    {
        var stages = definition.Stages;

        if (assignMissingIds)
        {
            foreach (var stage in stages)
            {
                stage.StageId ??= Guid.NewGuid().ToString("D");
                foreach (var step in stage.Steps)
                    step.StepId ??= Guid.NewGuid().ToString("D");
            }
        }

        var predecessors = PredecessorsOf(stages);
        var (entities, ambiguous) = EntitiesOf(stages, definition.PrimaryEntity, predecessors);
        var nextIds = stages.Select((stage, i) => NextOf(stages, i)?.StageId).ToList();

        var resolved = new List<BpfResolvedStage>();
        for (var i = 0; i < stages.Count; i++)
        {
            var stage = stages[i];
            BpfResolvedRelationship? relationship = null;

            if (stage.Relationship is { } rel)
            {
                var fromIndex = !string.IsNullOrWhiteSpace(rel.FromStage)
                    ? stages.IndexOf(Find(stages, rel.FromStage!)!)
                    : DefaultSourceOf(i, entities, predecessors);

                if (fromIndex >= 0)
                    relationship = new BpfResolvedRelationship(
                        rel.Name, rel.Attribute, stages[fromIndex].StageId ?? string.Empty, fromIndex);
            }

            resolved.Add(new BpfResolvedStage(stage, i, stage.StageId ?? string.Empty, entities[i], nextIds[i],
                relationship, predecessors[i], ambiguous[i]));
        }

        return new BpfResolvedDefinition(resolved);
    }

    /// <summary>
    /// The table a stage would have without its own <c>entity</c>: that of the stages leading to it,
    /// or — when nothing leads there — of the stage listed above it. Null when the stages leading to
    /// it disagree. The parser uses this to leave out tables a reader can infer.
    /// </summary>
    public static string? InheritedEntity(BpfDefinition definition, int index)
    {
        if (index == 0)
            return definition.PrimaryEntity;

        var stages = definition.Stages;
        var predecessors = PredecessorsOf(stages);
        var (entities, _) = EntitiesOf(stages, definition.PrimaryEntity, predecessors);

        var leading = predecessors[index].Select(p => entities[p]).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return leading.Count switch
        {
            1 => leading[0],
            0 => entities[index - 1],
            _ => null
        };
    }

    /// <summary>The stage that follows <c>stages[index]</c> on the main path, or null at the end.</summary>
    public static BpfStage? NextOf(List<BpfStage> stages, int index)
    {
        var next = stages[index].Next;

        if (string.IsNullOrWhiteSpace(next))
            return index + 1 < stages.Count ? stages[index + 1] : null;

        return string.Equals(next, End, StringComparison.OrdinalIgnoreCase) ? null : Find(stages, next!);
    }

    /// <summary>Every stage a stage can lead to: its next stage and its branch targets, distinct.</summary>
    public static IReadOnlyList<int> SuccessorsOf(List<BpfStage> stages, int index)
    {
        var result = new List<int>();
        if (NextOf(stages, index) is { } next)
            result.Add(stages.IndexOf(next));

        foreach (var target in BranchTargets(stages[index]))
            if (Find(stages, target) is { } t && !result.Contains(stages.IndexOf(t)))
                result.Add(stages.IndexOf(t));

        return result;
    }

    /// <summary>
    /// Where a cross-table stage is entered from when its relationship does not say: the one stage
    /// leading here from another table, else the one stage leading here at all, else the stage listed
    /// above it.
    /// </summary>
    private static int DefaultSourceOf(int index, IReadOnlyList<string> entities, IReadOnlyList<IReadOnlyList<int>> predecessors)
    {
        var leading = predecessors[index];
        var crossing = leading.Where(p => !string.Equals(entities[p], entities[index], StringComparison.OrdinalIgnoreCase)).ToList();

        if (crossing.Count >= 1)
            return crossing[0];
        if (leading.Count == 1)
            return leading[0];
        return index - 1;
    }

    private static IReadOnlyList<IReadOnlyList<int>> PredecessorsOf(List<BpfStage> stages)
    {
        var result = stages.Select(_ => new List<int>()).ToList();
        for (var i = 0; i < stages.Count; i++)
            foreach (var successor in SuccessorsOf(stages, i))
                if (successor != i && !result[successor].Contains(i))
                    result[successor].Add(i);

        return result;
    }

    private static (string[] Entities, bool[] Ambiguous) EntitiesOf(
        List<BpfStage> stages, string primaryEntity, IReadOnlyList<IReadOnlyList<int>> predecessors)
    {
        var entities = new string?[stages.Count];
        var ambiguous = new bool[stages.Count];
        var visiting = new bool[stages.Count];

        string? EntityOf(int i)
        {
            if (entities[i] is not null)
                return entities[i];
            if (!string.IsNullOrWhiteSpace(stages[i].Entity))
                return entities[i] = stages[i].Entity!;
            if (i == 0)
                return entities[i] = primaryEntity;
            if (visiting[i])
                return null;   // a loop; resolved from another entry point

            visiting[i] = true;
            var leading = predecessors[i].Select(EntityOf).OfType<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            visiting[i] = false;

            if (leading.Count > 1)
                ambiguous[i] = true;

            return entities[i] = leading.Count == 1 ? leading[0] : EntityOf(i - 1) ?? primaryEntity;
        }

        for (var i = 0; i < stages.Count; i++)
            EntityOf(i);

        return (entities.Select(e => e ?? primaryEntity).ToArray(), ambiguous);
    }

    /// <summary>Every stage reference in a stage's branching.</summary>
    public static IEnumerable<string> BranchTargets(BpfStage stage)
    {
        if (stage.Branch is null)
            yield break;

        foreach (var branch in stage.Branch.Branches)
            if (!string.IsNullOrWhiteSpace(branch.Next))
                yield return branch.Next;

        if (!string.IsNullOrWhiteSpace(stage.Branch.Else))
            yield return stage.Branch.Else!;
    }

    /// <summary>The stage a reference names: by key, then id, then name — or null.</summary>
    public static BpfStage? Find(List<BpfStage> stages, string reference)
    {
        if (string.IsNullOrWhiteSpace(reference))
            return null;

        var trimmed = reference.Trim().Trim('{', '}');

        return stages.FirstOrDefault(s => string.Equals(s.Key, reference, StringComparison.OrdinalIgnoreCase))
               ?? stages.FirstOrDefault(s => s.StageId is not null
                                             && string.Equals(s.StageId.Trim('{', '}'), trimmed, StringComparison.OrdinalIgnoreCase))
               ?? stages.FirstOrDefault(s => string.Equals(s.Name, reference, StringComparison.OrdinalIgnoreCase));
    }
}
