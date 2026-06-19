using System.Collections.Concurrent;
using FixPortal.Ci.Backend.Api.Dashboard.Configuration;
using FixPortal.Ci.Backend.Api.Integrations.GitHub;
using Microsoft.Extensions.Options;
using NodaTime;

namespace FixPortal.Ci.Backend.Api.Dashboard.Services;

/// <summary>
/// Shared, time-bounded cache over the GitHub inventory reads that several
/// services need every cycle — the org repo list and each repo's workflow list.
/// Without it the 60s board refresh and the (independently-timed) enrichment
/// workers each re-list repos, and the job-lane workers re-list the workflows
/// the board already fetched, multiplying calls against the rate budget.
///
/// Entries live for one board-refresh cycle (<c>RefreshSeconds</c>): the board is
/// the freshest consumer, so anything it sees within a cycle is fresh enough for
/// the slower workers. Pull-through with per-key single-flight — the first caller
/// fetches, concurrent callers for the same key wait and reuse the result, and
/// callers for different repos never block each other (so the board keeps its
/// parallel per-repo concurrency). A failed fetch is not cached and propagates,
/// leaving the prior entry in place; every call site already degrades to
/// last-known-good on transport/rate-limit faults.
/// </summary>
public sealed class GitHubInventoryCache(
    GitHubOrgClient client,
    IClock clock,
    IOptions<DashboardOptions> options)
{
    private sealed record Entry<T>(Instant FetchedAt, T Value);

    private readonly SemaphoreSlim _reposGate = new(1, 1);
    private volatile Entry<IReadOnlyList<GitHubRepoDto>>? _repos;

    private sealed class WorkflowSlot
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        private volatile Entry<IReadOnlyList<GitHubWorkflowDto>>? _entry;
        public Entry<IReadOnlyList<GitHubWorkflowDto>>? Entry
        {
            get => _entry;
            set => _entry = value;
        }
    }

    private readonly ConcurrentDictionary<string, WorkflowSlot> _workflows = new(StringComparer.OrdinalIgnoreCase);

    private Duration Ttl => Duration.FromSeconds(options.Value.RefreshSeconds);

    /// <summary>The org repo list, cached for one refresh cycle and shared by every caller.</summary>
    public Task<IReadOnlyList<GitHubRepoDto>> GetRepositoriesAsync(CancellationToken ct) =>
        GetOrFetchAsync(_reposGate, () => _repos, e => _repos = e, () => client.ListRepositoriesAsync(ct), ct);

    /// <summary>One repo's workflow list, cached per repo so the board and the job-lane workers share a fetch.</summary>
    public Task<IReadOnlyList<GitHubWorkflowDto>> GetWorkflowsAsync(string repo, CancellationToken ct)
    {
        var slot = _workflows.GetOrAdd(repo, _ => new WorkflowSlot());
        return GetOrFetchAsync(slot.Gate, () => slot.Entry, e => slot.Entry = e,
            () => client.ListWorkflowsAsync(repo, ct), ct);
    }

    private async Task<T> GetOrFetchAsync<T>(
        SemaphoreSlim gate, Func<Entry<T>?> read, Action<Entry<T>> write, Func<Task<T>> fetch, CancellationToken ct)
    {
        if (TryFresh(read(), out var cached))
        {
            return cached;
        }

        await gate.WaitAsync(ct);
        try
        {
            // Re-check under the gate: another caller may have refreshed while we waited.
            if (TryFresh(read(), out cached))
            {
                return cached;
            }

            var value = await fetch();
            write(new Entry<T>(clock.GetCurrentInstant(), value));
            return value;
        }
        finally
        {
            _ = gate.Release();
        }
    }

    private bool TryFresh<T>(Entry<T>? entry, out T value)
    {
        if (entry is not null && clock.GetCurrentInstant() - entry.FetchedAt < Ttl)
        {
            value = entry.Value;
            return true;
        }
        value = default!;
        return false;
    }
}
