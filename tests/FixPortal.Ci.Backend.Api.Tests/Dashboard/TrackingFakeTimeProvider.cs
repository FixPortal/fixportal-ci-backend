using Microsoft.Extensions.Time.Testing;

namespace FixPortal.Ci.Backend.Api.Tests.Dashboard;

/// <summary>
/// Shared helper for driving a <c>RepoEnrichmentWorker&lt;T&gt;</c>'s real
/// <c>ExecuteAsync</c> loop deterministically. A raw <see cref="FakeTimeProvider"/>
/// gives no signal for WHEN the worker has actually registered its jitter (or
/// retry) timer, so advancing it immediately after <c>StartAsync</c> races the
/// worker's own scheduling onto the thread pool — a real hazard on a contended CI
/// runner, not just a local flake. Hooking <see cref="CreateTimer"/> lets a test
/// await the exact moment the timer exists before advancing past it, replacing a
/// wall-clock poll budget with an event-driven wait. Originally private to
/// RepoEnrichmentWorkerTests; promoted here so ReviewSignalMergeTests can reuse it
/// rather than reimplementing the same hook.
/// </summary>
internal sealed class TrackingFakeTimeProvider : FakeTimeProvider
{
    public TaskCompletionSource InitialDelayScheduled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource RetryDelayScheduled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = base.CreateTimer(callback, state, dueTime, period);
        if (dueTime < TimeSpan.FromSeconds(15))
        {
            InitialDelayScheduled.TrySetResult();
        }
        else if (dueTime == TimeSpan.FromMinutes(5))
        {
            RetryDelayScheduled.TrySetResult();
        }

        return timer;
    }
}
