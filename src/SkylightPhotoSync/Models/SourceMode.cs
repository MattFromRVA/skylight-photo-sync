namespace SkylightPhotoSync.Config;

// TEMPORARY: Task 2 defines this enum in Config/AppConfig.cs. This copy exists only so Task 3
// builds before Task 2 merges. Delete this file when both are on main.

/// <summary>Which Amazon Photos items a run considers: one named album, or everything.</summary>
public enum SourceMode
{
    Album,
    All,
}
