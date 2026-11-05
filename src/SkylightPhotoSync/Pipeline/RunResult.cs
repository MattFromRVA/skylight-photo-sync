namespace SkylightPhotoSync.Pipeline;

/// <summary>What one <see cref="SyncPipeline.RunAsync"/> did.</summary>
/// <param name="Found">Candidates new to this run: listed by the source and neither sent to every recipient nor permanently skipped. Includes items the run then skipped or deferred past <c>max_items_per_run</c>.</param>
/// <param name="Sent">Item deliveries accepted by the SMTP server, counted once per item per recipient. In a dry run, the deliveries that would have happened.</param>
/// <param name="Skipped">Distinct items recorded as skipped during this run.</param>
/// <param name="Batches">Emails accepted by the SMTP server (one per batch per recipient). In a dry run, the emails that would have gone out.</param>
/// <param name="DryRun">True when nothing was sent or marked because <c>--dry-run</c> was given.</param>
/// <param name="Error">
/// <see langword="null"/> on success; otherwise <c>needs_login</c> (the run never touched the source),
/// <c>auth_expired</c> (the source rejected the credentials during the run) or <c>send_failed: …</c>.
/// </param>
public sealed record RunResult(int Found, int Sent, int Skipped, int Batches, bool DryRun, string? Error);
