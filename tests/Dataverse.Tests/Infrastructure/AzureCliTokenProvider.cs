namespace Dataverse.Tests.Infrastructure;

using System.Diagnostics;
using System.Text.Json;
using Dataverse.Core.Auth;

public sealed class AzureCliTokenProvider : ITokenProvider
{
    public async Task<string> GetTokenAsync(string scope, CancellationToken ct = default)
    {
        var resource = scope.Replace("/.default", string.Empty);

        // On Windows, az is az.cmd and must be invoked via cmd.exe
        ProcessStartInfo psi;
        if (OperatingSystem.IsWindows())
        {
            psi = new ProcessStartInfo("cmd.exe",
                $"/c az account get-access-token --resource \"{resource}\" --query \"accessToken\" -o tsv")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
        }
        else
        {
            psi = new ProcessStartInfo("az",
                $"account get-access-token --resource \"{resource}\" --query \"accessToken\" -o tsv")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
        }

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start az CLI process.");

        var output = await process.StandardOutput.ReadToEndAsync(ct);
        var error = await process.StandardError.ReadToEndAsync(ct);

        await process.WaitForExitAsync(ct);

        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"az account get-access-token failed (exit {process.ExitCode}): {error.Trim()}");

        var token = output.Trim();
        if (string.IsNullOrEmpty(token))
            throw new InvalidOperationException("az CLI returned an empty token. Run 'az login' first.");

        return token;
    }
}
