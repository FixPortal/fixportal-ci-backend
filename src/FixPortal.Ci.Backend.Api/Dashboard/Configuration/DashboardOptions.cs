using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace FixPortal.Ci.Backend.Api.Dashboard.Configuration;

// Properties are populated by Microsoft.Extensions.Configuration binding.
// ReSharper disable AutoPropertyCanBeMadeGetOnly.Global

public sealed class DashboardOptions
{
    public required string SnapshotPath { get; init; }
    public required int RefreshSeconds { get; init; }
    public bool ExcludeArchived { get; init; } = true;
    public IReadOnlyList<string> IncludeRepositories { get; init; } = [];
    public IReadOnlyList<string> ExcludeRepositories { get; init; } = [];
    public IReadOnlyList<string> IncludeTopics { get; init; } = [];
    public IReadOnlyList<string> ExcludeTopics { get; init; } = [];
    public bool IncludeReusable { get; init; }
    public bool IncludeCodeQl { get; init; } = true;
    public int RunHistoryPageSize { get; init; } = 30;
    public bool MetricsEnabled { get; init; } = true;
    public int MetricsRefreshSeconds { get; init; } = 43_200; // 12h
    public string MetricsWorkDirectory { get; init; } = ""; // "" -> temp subdir
    public bool MergedPrEnabled { get; init; } = true;
    public int MergedPrRefreshSeconds { get; init; } = 300;

    /// <summary>
    /// Identity of the repository-filter configuration, used to decide whether a persisted
    /// snapshot may be restored. A snapshot carries whatever repository set was live when it
    /// was written, and restore republishes it ahead of the first refresh — including to the
    /// anonymous public projection, and with no time bound while GitHub is unreachable.
    /// <para>Re-applying the filters at restore instead is not possible:
    /// <c>RepositorySnapshot</c> stores no topics, so the topic gates would have nothing to
    /// evaluate against.</para>
    /// <para>Lists are sorted before hashing — reordering an include list is not a semantic
    /// change and must not cost the trend history a discard.</para>
    /// </summary>
    public string FilterFingerprint()
    {
        // Length-prefixed, not delimiter-joined. A separator character can appear INSIDE a
        // pattern -- nothing validates against that -- and two different filter sets then
        // encode identically: {"a<sep>b"} and {"a", "b"} are indistinguishable once joined.
        // A collision here lets a snapshot written under different filters pass the
        // provenance check and restore the repository set this fingerprint exists to keep
        // off the board. Prefixing each value with its length removes the ambiguity without
        // constraining what a pattern may contain.
        var canonical = new StringBuilder();
        // Each UTF-16 code unit as fixed-width hex, so the canonical string is pure ASCII
        // and its UTF-8 encoding is lossless. Appending the raw value was not:
        // Encoding.UTF8.GetBytes substitutes U+FFFD for an ISOLATED surrogate, and nothing
        // validates patterns against one, so "\uD800" and "\uD801" hashed identically --
        // another route into the collision this encoding exists to rule out.
        void Append(string value)
        {
            _ = canonical.Append(value.Length).Append(':');
            foreach (var unit in value)
            {
                _ = canonical.Append(((int)unit).ToString("x4", CultureInfo.InvariantCulture));
            }

            _ = canonical.Append(';');
        }
        void AppendAll(IReadOnlyList<string> patterns)
        {
            _ = canonical.Append(patterns.Count).Append('|');
            foreach (var pattern in patterns.Order(StringComparer.Ordinal))
            {
                Append(pattern);
            }
        }
        Append(ExcludeArchived ? "archived:excluded" : "archived:included");
        AppendAll(IncludeRepositories);
        AppendAll(ExcludeRepositories);
        AppendAll(IncludeTopics);
        AppendAll(ExcludeTopics);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
    }

    // Compiled fallback lanes, used when no Dashboard:JobLanes are configured. These
    // are deliberately NOT the default of the bound JobLanes property below: the
    // configuration binder APPENDS bound collection items to a pre-populated list
    // rather than replacing it, so keeping defaults there would leave every
    // configured lane shadowed behind the compiled default of the same Key
    // (FirstOrDefault(Key) returns the default first). Consume GetEffectiveJobLanes().
    public static readonly IReadOnlyList<JobLaneOptions> DefaultJobLanes =
    [
        new()
        {
            Key = "deploys",
            Label = "Deploys",
            Patterns = ["deploy"],
        },
        new()
        {
            Key = "packages",
            Label = "Packages",
            Patterns = ["publish", "package", "docker", "image", "release", "ghcr"],
        },
    ];

    // Bound from Dashboard:JobLanes. Empty by default (see DefaultJobLanes) so the
    // binder replaces rather than appends. Consumers must use GetEffectiveJobLanes().
    public IReadOnlyList<JobLaneOptions> JobLanes { get; init; } = [];

    // The lanes actually in force: the configured lanes when any are bound, otherwise
    // the compiled defaults. De-duplicated by Key (last occurrence wins) so a repeated
    // key resolves to the last configured entry rather than the first.
    public IReadOnlyList<JobLaneOptions> GetEffectiveJobLanes() =>
        JobLanes.Count == 0
            ? DefaultJobLanes
            : JobLanes.GroupBy(l => l.Key, StringComparer.OrdinalIgnoreCase).Select(g => g.Last()).ToList();
}
