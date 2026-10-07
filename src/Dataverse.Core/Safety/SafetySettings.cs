namespace Dataverse.Core.Safety;

using Dataverse.Core.Config;

/// <summary>
/// The two safety switches, resolved from config.json and environment variables. An environment
/// variable can only switch a protection on (read-only) or explicitly lift one (production writes);
/// a value it does not recognise as true leaves the config.json setting in place.
/// </summary>
public sealed record SafetySettings(bool ReadOnly, bool AllowProductionWrites)
{
    public const string ReadOnlyVariable = "DATAVERSE_READ_ONLY";
    public const string AllowProductionWritesVariable = "DATAVERSE_ALLOW_PRODUCTION_WRITES";

    public static SafetySettings Resolve(DataverseMcpConfig config, Func<string, string?> getVariable) =>
        new(
            ReadOnly: config.ReadOnly || IsTrue(getVariable(ReadOnlyVariable)),
            AllowProductionWrites: config.AllowProductionWrites || IsTrue(getVariable(AllowProductionWritesVariable)));

    private static bool IsTrue(string? value) =>
        value?.Trim().ToLowerInvariant() is "true" or "1" or "yes";
}
