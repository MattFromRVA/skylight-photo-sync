namespace SkylightPhotoSync.Pipeline;

/// <summary>What one <see cref="SyncPipeline.RunAsync"/> did.</summary>
/// <param name="Found">Candidates new to this run: listed by the source and neither sent to every recipient nor permanently skipped. Includes items the run then skipped or deferred past <c>max_items_per_run</c>.</param>
/// <param name="Sent">Item deliveries accepted by the SMTP server, counted once per item per recipient. In a dry run, the deliveries that would have happened.</param>
/// <param name="Skipped">Distinct items recorded as skipped during this run.</param>
/// <param name="Batches">Emails accepted by the SMTP server (one per batch per recipient). In a dry run, the emails that would have gone out.</param>
/// <param name="DryRun">True when nothing was sent or marked because <c>--dry-run</c> was given.</param>
/// <param name="Error">
/// <see langword="null"/> on success; otherwise <c>needs_login</c> (the run never touched the source),
/// <c>auth_expired</c> (the source rejected the credentials during the run), <c>send_failed: …</c>,
/// <c>prune_failed: …</c> (everything was sent, but rotation could not clean up the frame) or
/// <c>skylight_sign_in_failed: …</c> (everything was sent, but Skylight refused the sign-in; pruning is now paused).
/// </param>
/// <param name="Recycled">With <c>[rotation]</c>: already-sent items sent again because nothing new was left. Included in <see cref="Sent"/>.</param>
/// <param name="Pruned">With <c>[rotation]</c>: photos deleted from the frame; in a dry run, the photos that would have been.</param>
/// <param name="PrunePaused">With <c>[rotation]</c>: pruning was left out because an earlier Skylight sign-in was refused and the credentials have not changed since.</param>
public sealed record RunResult(
    int Found, int Sent, int Skipped, int Batches, bool DryRun, string? Error, int Recycled = 0, int Pruned = 0, bool PrunePaused = false);
