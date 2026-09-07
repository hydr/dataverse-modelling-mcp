namespace Dataverse.Core.Models;

/// <summary>
/// What stands in the way of deleting one component, as reported by
/// <c>RetrieveDependenciesForDelete</c>.
/// </summary>
/// <param name="CanDelete">
/// True when nothing depends on the component. This is the whole point of asking: a plain "no
/// dependencies" is an answer you can act on, and the raw function gives you an empty collection
/// that is easy to misread as a failed call.
/// </param>
/// <param name="Dependents">
/// The components that would break. Empty when <paramref name="CanDelete"/> is true.
/// </param>
public sealed record ComponentDependencyReport(
    Guid ComponentId,
    int ComponentType,
    string ComponentTypeName,
    string? ComponentName,
    bool CanDelete,
    int DependentCount,
    string Summary,
    IReadOnlyList<ComponentDependency> Dependents);

/// <summary>
/// One thing that depends on the component being examined.
/// </summary>
/// <param name="ParentId">
/// The dependent's own parent, where it has one — for a column the owning table's
/// <c>MetadataId</c>. Null when the platform reported an empty parent.
/// </param>
/// <param name="DependencyType">
/// The platform's <c>dependencytype</c> code. Left as a number: the values are not documented as a
/// named choice, so naming them would be a guess.
/// </param>
public sealed record ComponentDependency(
    Guid ComponentId,
    int ComponentType,
    string ComponentTypeName,
    string? Name,
    Guid? ParentId,
    string? ParentName,
    int DependencyType);

/// <summary>
/// Which solutions carry a table, and what each of them holds explicitly.
/// </summary>
/// <remarks>
/// The recurring question is "which solution actually carries this change?", and the answer hangs
/// entirely on <c>rootcomponentbehavior</c> per solution — the same table routinely sits in several
/// solutions with different behaviours.
/// </remarks>
public sealed record EntitySolutionMap(
    string LogicalName,
    Guid MetadataId,
    int SolutionCount,
    string Summary,
    IReadOnlyList<EntitySolutionMembership> Solutions);

/// <summary>
/// A table's membership in one solution.
/// </summary>
/// <param name="RootComponentBehavior">
/// 0 = include subcomponents (forms and columns travel with the table and get no membership row of
/// their own), 1 = do not include subcomponents, 2 = include as shell only.
/// </param>
/// <param name="Subcomponents">
/// The subcomponents this solution holds explicitly, found through their
/// <c>rootsolutioncomponentid</c> back-reference to the table's own membership row. Always empty for
/// behaviour 0, where subcomponents are covered by the table instead.
/// </param>
public sealed record EntitySolutionMembership(
    string SolutionUniqueName,
    Guid SolutionId,
    bool IsManaged,
    int? RootComponentBehavior,
    string? RootComponentBehaviorName,
    Guid SolutionComponentId,
    int SubcomponentCount,
    IReadOnlyList<SolutionComponent> Subcomponents);

/// <summary>
/// What uninstalling a solution would mean, and — unless it was a dry run — what it did.
/// </summary>
/// <remarks>
/// Deleting a solution row is the uninstall: for a managed solution it takes its components with it.
/// That is not something to do on a hunch, so the default is a dry run that reports the blockers and
/// changes nothing.
/// </remarks>
/// <param name="Blockers">
/// Root components that other things depend on. Empty does not prove the uninstall is safe — only
/// root components are checked, and only up to <paramref name="ComponentsChecked"/> of them.
/// </param>
public sealed record SolutionUninstallReport(
    string UniqueName,
    Guid SolutionId,
    bool IsManaged,
    bool DryRun,
    bool Uninstalled,
    int ComponentCount,
    int RootComponentCount,
    int ComponentsChecked,
    bool CheckTruncated,
    string Summary,
    IReadOnlyList<ComponentDependencyReport> Blockers);
