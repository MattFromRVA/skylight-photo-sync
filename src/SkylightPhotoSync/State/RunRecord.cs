using SkylightPhotoSync.Config;

namespace SkylightPhotoSync.State;

/// <summary>One row of the <c>runs</c> table: a sync run, finished or still in progress.</summary>
/// <param name="Id">Row id returned by <see cref="StateStore.StartRun"/>.</param>
/// <param name="StartedAt">When the run started (UTC).</param>
/// <param name="FinishedAt">When the run finished (UTC), or <see langword="null"/> while it is in progress.</param>
/// <param name="Mode">The source mode the run used.</param>
/// <param name="Found">Candidate items the source reported.</param>
/// <param name="Sent">Items marked sent during the run.</param>
/// <param name="Skipped">Items recorded as skipped during the run.</param>
/// <param name="Error">Short error label when the run failed; <see langword="null"/> on success or while in progress.</param>
public sealed record RunRecord(
    long Id,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    SourceMode Mode,
    int Found,
    int Sent,
    int Skipped,
    string? Error);
