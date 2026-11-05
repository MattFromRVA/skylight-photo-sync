namespace SkylightPhotoSync.Alerts;

/// <summary>Shows a local notification to the person running the sync. Never throws.</summary>
public interface INotifier
{
    Task NotifyAsync(string title, string body, CancellationToken ct);
}
