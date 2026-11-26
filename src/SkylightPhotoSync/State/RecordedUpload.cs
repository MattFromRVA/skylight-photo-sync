namespace SkylightPhotoSync.State;

/// <summary>One photo this tool uploaded to a frame through the Skylight API and has not deleted since.</summary>
/// <param name="MessageId">The Skylight photo id the upload returned.</param>
/// <param name="ItemId">The source item that was uploaded; for a <c>test-upload</c> image, a made-up id starting with <see cref="TestItemPrefix"/>.</param>
public sealed record RecordedUpload(string MessageId, string ItemId, DateTimeOffset UploadedAt)
{
    /// <summary>Starts the item id recorded for a <c>test-upload</c> image, which is no item of any source.</summary>
    public const string TestItemPrefix = "skylight-sync:test-upload:";

    /// <summary>True for a <c>test-upload</c> image: rotation removes it before any real photo.</summary>
    public bool IsTest => ItemId.StartsWith(TestItemPrefix, StringComparison.Ordinal);
}
