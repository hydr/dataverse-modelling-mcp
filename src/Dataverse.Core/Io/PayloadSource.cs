namespace Dataverse.Core.Io;

/// <summary>
/// Resolves a tool payload that may arrive inline or as a path to a local file.
/// </summary>
/// <remarks>
/// Some payloads are too large to pass inline in practice. A workflow definition whose e-mail body
/// carries a base64-encoded signature image runs to ~28.000 characters; the XAML behind it to
/// ~180.000. An agent passing that inline has to reproduce every character, and characters do flip in
/// the process. The failure is silent: the JSON stays valid, validation passes, and only the payload
/// it encodes — the image — is corrupt. Reading from disk removes the copy, and with it the class of
/// error. The counterpart is <c>backupFile</c> on workflow_set_definition, which keeps the previous
/// XAML out of the response for the same reason.
/// </remarks>
public static class PayloadSource
{
    /// <summary>Largest file accepted. Turns a wrong path into an error instead of an OOM.</summary>
    public const long MaxBytes = 32L * 1024 * 1024;

    /// <summary>The outcome of resolving a payload: exactly one of the two is set.</summary>
    public sealed record Result(string? Content, string? Error)
    {
        public static Result Ok(string content) => new(content, null);

        public static Result Fail(string error) => new(null, error);
    }

    /// <summary>
    /// Returns the payload from <paramref name="inline"/> or from the file at <paramref name="path"/>.
    /// Exactly one of the two must be given; the parameter names are passed in so the error message
    /// names the arguments the caller actually sees.
    /// </summary>
    public static async Task<Result> ResolveAsync(
        string? inline,
        string? path,
        string inlineName,
        string pathName,
        CancellationToken ct = default)
    {
        var hasInline = !string.IsNullOrWhiteSpace(inline);
        var hasPath = !string.IsNullOrWhiteSpace(path);

        if (hasInline && hasPath)
        {
            return Result.Fail(
                $"Pass either {inlineName} or {pathName}, not both — it is ambiguous which one wins.");
        }

        if (!hasInline && !hasPath)
        {
            return Result.Fail(
                $"Pass either {inlineName} (inline) or {pathName} (a path to a local file). "
                + $"Prefer {pathName} for large payloads: passing them inline means copying every "
                + "character, and a single flipped character is not caught by validation.");
        }

        if (hasInline)
        {
            return Result.Ok(inline!);
        }

        var fullPath = Path.GetFullPath(path!);

        if (!File.Exists(fullPath))
        {
            return Result.Fail(
                $"File not found: {fullPath}. {pathName} is resolved against the server's working "
                + "directory, so pass an absolute path.");
        }

        var size = new FileInfo(fullPath).Length;
        if (size == 0)
        {
            return Result.Fail($"File is empty: {fullPath}");
        }

        if (size > MaxBytes)
        {
            return Result.Fail(
                $"File is {size:N0} bytes, above the {MaxBytes:N0} byte limit: {fullPath}");
        }

        var content = await File.ReadAllTextAsync(fullPath, ct);

        // A UTF-8 BOM survives ReadAllTextAsync as U+FEFF and makes the JSON parser reject the
        // document at position 0 — an error that reads as if the file itself were malformed.
        return Result.Ok(content.TrimStart('﻿'));
    }

    /// <summary>
    /// Writes <paramref name="content"/> to <paramref name="path"/>, creating the directory, and
    /// returns the full path. Used to keep a large response payload out of the response.
    /// </summary>
    public static async Task<string> WriteAsync(string path, string content, CancellationToken ct = default)
    {
        var fullPath = Path.GetFullPath(path);
        var dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        await File.WriteAllTextAsync(fullPath, content, ct);
        return fullPath;
    }
}
