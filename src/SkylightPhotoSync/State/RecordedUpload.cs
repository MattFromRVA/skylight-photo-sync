namespace SkylightPhotoSync.State;

/// <summary>One photo this tool uploaded to a frame through the Skylight API and has not deleted since.</summary>
/// <param name="MessageId">The Skylight photo id the upload returned.</param>
/// <param name="ItemId">The source item that was uploaded.</param>
public sealed record RecordedUpload(string MessageId, string ItemId, DateTimeOffset UploadedAt);
