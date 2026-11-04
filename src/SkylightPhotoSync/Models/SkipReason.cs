namespace SkylightPhotoSync.Models;

/// <summary>Why the pipeline left an item out of a run.</summary>
public enum SkipReason
{
    /// <summary>Too large to email even after processing.</summary>
    Oversize,

    /// <summary>A video while <c>source.include_videos</c> is off.</summary>
    VideoDisabled,

    /// <summary>A media type Skylight does not accept.</summary>
    UnsupportedType,

    /// <summary>The source could not deliver the bytes.</summary>
    DownloadFailed,

    /// <summary>Normalizing the file (decode, orient, resize) failed.</summary>
    ProcessingFailed,
}
