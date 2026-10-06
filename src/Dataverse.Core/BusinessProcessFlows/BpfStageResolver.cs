namespace Dataverse.Core.BusinessProcessFlows;

/// <summary>A stage with everything the definition leaves implicit worked out.</summary>
/// <param name="Entity">The stage's table — its own, or inherited from the stage before.</param>
/// <param name="NextStageId">Id of the following stage on the main path, or null at the end.</param>
/// <param name="Relationship">The cross-table transition into this stage, with its source resolved.</param>
public sealed record BpfResolvedStage(
    BpfStage Source,
    int Index,
    string StageId,
    string Entity,
    string? NextStageId,
    BpfResolvedRelationship? Relationship);

public sealed record BpfResolvedRelationship(string Name, string? Attribute, string FromStageId);

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
/// Shared by builder and validator, so that both read a reference the same way: a stage is named by
/// its <c>key</c>, its id or its name, in that order of precedence, ignoring case.
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

        // Tables first: a stage without one continues on the table of the stage before it.
        var entities = new List<string>();
        var current = definition.PrimaryEntity;
        foreach (var stage in stages)
        {
            current = string.IsNullOrWhiteSpace(stage.Entity) ? current : stage.Entity!;
            entities.Add(current);
        }

        var nextIds = stages.Select((stage, i) => NextOf(stages, i)?.StageId).ToList();

        var resolved = new List<BpfResolvedStage>();
        for (var i = 0; i < stages.Count; i++)
        {
            var stage = stages[i];
            BpfResolvedRelationship? relationship = null;

            if (stage.Relationship is { } rel)
            {
                var from = !string.IsNullOrWhiteSpace(rel.FromStage)
                    ? Find(stages, rel.FromStage!)
                    : PredecessorOf(stages, i);

                if (from?.StageId is not null)
                    relationship = new BpfResolvedRelationship(rel.Name, rel.Attribute, from.StageId);
            }

            resolved.Add(new BpfResolvedStage(stage, i, stage.StageId ?? string.Empty, entities[i], nextIds[i], relationship));
        }

        return new BpfResolvedDefinition(resolved);
    }

    /// <summary>The stage that follows <c>stages[index]</c> on the main path, or null at the end.</summary>
    public static BpfStage? NextOf(List<BpfStage> stages, int index)
    {
        var next = stages[index].Next;

        if (string.IsNullOrWhiteSpace(next))
            return index + 1 < stages.Count ? stages[index + 1] : null;

        return string.Equals(next, End, StringComparison.OrdinalIgnoreCase) ? null : Find(stages, next!);
    }

    /// <summary>
    /// Where a cross-table stage is entered from: the one stage whose main path or branch leads to it,
    /// or else the stage listed before it.
    /// </summary>
    private static BpfStage? PredecessorOf(List<BpfStage> stages, int index)
    {
        var target = stages[index];

        var leadingHere = stages
            .Where((s, i) => i != index
                             && (ReferenceEquals(NextOf(stages, i), target)
                                 || BranchTargets(s).Any(t => ReferenceEquals(Find(stages, t), target))))
            .ToList();

        if (leadingHere.Count == 1)
            return leadingHere[0];

        return index > 0 ? stages[index - 1] : null;
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
