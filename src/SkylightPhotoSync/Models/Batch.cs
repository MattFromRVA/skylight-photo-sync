namespace SkylightPhotoSync.Models;

/// <summary>A set of processed files that go out in one email.</summary>
/// <param name="Id">Batch identifier recorded with each sent item.</param>
/// <param name="Items">The source items in the batch, oldest-first.</param>
/// <param name="Files">Paths of the processed files to attach, parallel to <paramref name="Items"/>.</param>
/// <param name="TotalBytes">Sum of the attachment sizes.</param>
public sealed record Batch(
    string Id,
    IReadOnlyList<Item> Items,
    IReadOnlyList<string> Files,
    long TotalBytes);
