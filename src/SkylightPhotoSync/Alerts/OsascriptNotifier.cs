using SkylightPhotoSync.Infrastructure;

namespace SkylightPhotoSync.Alerts;

/// <summary>
/// Posts a macOS notification via <c>osascript -e 'display notification "body" with title "title"'</c>.
/// Best effort: a non-zero exit, a missing <c>osascript</c> or any other failure is swallowed,
/// because a notification must never break the sync run that raised it.
/// </summary>
public sealed class OsascriptNotifier(IProcessRunner runner) : INotifier
{
    public async Task NotifyAsync(string title, string body, CancellationToken ct)
    {
        var script = $"display notification \"{Escape(body)}\" with title \"{Escape(title)}\"";
        try
        {
            await runner.RunAsync("osascript", ["-e", script], ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // Best effort: the notification is a courtesy, not a guarantee.
        }
    }

    /// <summary>Escapes a value for use inside an AppleScript double-quoted string literal.</summary>
    internal static string Escape(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
}
