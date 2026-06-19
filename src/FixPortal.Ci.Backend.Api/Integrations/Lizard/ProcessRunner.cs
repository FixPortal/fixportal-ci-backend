using System.Diagnostics;
using System.Text;

namespace FixPortal.Ci.Backend.Api.Integrations.Lizard;

public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr);

/// <summary>
/// Runs an external process, capturing stdout/stderr, with a wall-clock timeout
/// layered on top of the caller's cancellation token. Arguments are passed via
/// <see cref="ProcessStartInfo.ArgumentList"/> so the caller never has to quote
/// or escape, and no shell is involved.
/// </summary>
public static class ProcessRunner
{
    private static readonly TimeSpan PostExitDrainTimeout = TimeSpan.FromSeconds(5);

    public static async Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken ct,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in arguments)
        {
            psi.ArgumentList.Add(a);
        }

        if (environment is not null)
        {
            foreach (var pair in environment)
            {
                psi.Environment[pair.Key] = pair.Value;
            }
        }

        using var process = new Process();
        process.StartInfo = psi;
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var stdoutClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stderrClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                stdoutClosed.TrySetResult();
                return;
            }

            stdout.AppendLine(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                stderrClosed.TrySetResult();
                return;
            }

            stderr.AppendLine(e.Data);
        };

        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start process '{fileName}'.");
        }
        // Close the child's stdin immediately so processes that read from it
        // (pwsh in non-interactive CI, git prompting for credentials) get EOF
        // and never block waiting on inherited input.
        process.StandardInput.Close();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await WaitForExitAndDrainAsync(
                process.WaitForExitAsync(),
                stdoutClosed.Task,
                stderrClosed.Task,
                timeout,
                ct);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }
        return new ProcessResult(process.ExitCode, stdout.ToString(), stderr.ToString());
    }

    internal static async Task WaitForExitAndDrainAsync(
        Task waitForExit,
        Task stdoutClosed,
        Task stderrClosed,
        TimeSpan timeout,
        CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        await waitForExit.WaitAsync(timeoutCts.Token);
        using var drainCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        drainCts.CancelAfter(PostExitDrainTimeout);
        await Task.WhenAll(stdoutClosed, stderrClosed).WaitAsync(drainCts.Token);
    }

    private static void TryKill(Process process)
    {
        try
        { if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch { /* best effort */ }
    }
}
