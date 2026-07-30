namespace Dataverse.Core.Workflows;

using Dataverse.Core.Models;

/// <summary>
/// Parameter metadata of the code activities a definition refers to, keyed by
/// AssemblyQualifiedName.
/// </summary>
/// <remarks>
/// <para>
/// A <c>customActivity</c> step names its arguments but not their types, and the types are what the
/// XAML has to declare: the output variable, its <c>OutArgument</c> and every <c>InArgument</c> carry
/// the parameter's type, not <c>x:String</c>. Getting this wrong is what produces
/// <c>InvalidPropertyBag</c> on activation. The types live in
/// <c>plugintype.customworkflowactivityinfo</c>, so they have to be fetched from the environment —
/// <see cref="Services.WorkflowService.GetActivityCatalogAsync"/> does that once per write and hands
/// the result to <see cref="WorkflowXamlBuilder"/> and <see cref="WorkflowDefinitionValidator"/>.
/// </para>
/// <para>
/// An <see cref="Empty"/> catalog is valid: builder and validator then fall back to the caller's
/// <c>dataType</c> and skip the parameter checks, which keeps them usable without a live connection.
/// </para>
/// </remarks>
public sealed class WorkflowActivityCatalog
{
    private readonly Dictionary<string, IReadOnlyList<WorkflowActivityParameter>> _byActivity;

    public WorkflowActivityCatalog(
        IEnumerable<KeyValuePair<string, IReadOnlyList<WorkflowActivityParameter>>> activities)
    {
        _byActivity = new Dictionary<string, IReadOnlyList<WorkflowActivityParameter>>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var (key, parameters) in activities)
            if (!string.IsNullOrWhiteSpace(key))
                _byActivity[TypePart(key)] = parameters;
    }

    /// <summary>A catalog that knows nothing — every lookup misses.</summary>
    public static WorkflowActivityCatalog Empty { get; } = new([]);

    /// <summary>True when this activity's parameters are known and checks can be applied.</summary>
    public bool Knows(string? assemblyQualifiedName) =>
        Parameters(assemblyQualifiedName) is not null;

    /// <summary>Parameters of an activity, or null when it is not in the catalog.</summary>
    /// <remarks>
    /// Matched on the type part of the AssemblyQualifiedName, so a definition whose version or
    /// culture differs from the installed assembly still resolves — the version mismatch is a
    /// separate concern from the parameter types.
    /// </remarks>
    public IReadOnlyList<WorkflowActivityParameter>? Parameters(string? assemblyQualifiedName)
    {
        if (string.IsNullOrWhiteSpace(assemblyQualifiedName))
            return null;

        return _byActivity.TryGetValue(TypePart(assemblyQualifiedName), out var parameters)
            ? parameters
            : null;
    }

    /// <summary>
    /// A single parameter by its <c>DependencyPropertyName</c>, or null when the activity is unknown
    /// or has no such parameter.
    /// </summary>
    public WorkflowActivityParameter? Parameter(
        string? assemblyQualifiedName, string dependencyPropertyName, string? direction = null)
    {
        var parameters = Parameters(assemblyQualifiedName);
        if (parameters is null)
            return null;

        return parameters.FirstOrDefault(p =>
            string.Equals(p.DependencyPropertyName, dependencyPropertyName, StringComparison.OrdinalIgnoreCase)
            && (direction is null || string.Equals(p.Direction, direction, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>"Namespace.Type" out of "Namespace.Type, Assembly, Version=…".</summary>
    internal static string TypePart(string assemblyQualifiedName) =>
        assemblyQualifiedName.Split(',')[0].Trim();
}
