namespace SkylightPhotoSync.State;

/// <summary>A Skylight sign-in that was refused; while one is recorded, rotation does not prune.</summary>
/// <param name="FailedAt">When Skylight refused the sign-in.</param>
/// <param name="Reason">The user-facing reason, as Skylight's refusal was understood.</param>
/// <param name="Fingerprint">Identifies the credentials that failed without containing them.</param>
public sealed record SkylightSignInFailure(DateTimeOffset FailedAt, string Reason, string Fingerprint);
