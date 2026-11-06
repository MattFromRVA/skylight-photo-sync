using System.CommandLine;
using SkylightPhotoSync.State;

namespace SkylightPhotoSync.Commands;

/// <summary><c>reset --yes</c>: forget every sent and skipped item so the next run starts over.</summary>
public static class ResetCommand
{
    public static Command Create()
    {
        var yes = new Option<bool>("--yes")
        {
            Description = "Confirm: forget every sent item, so the next run resends everything.",
        };

        var command = new Command("reset", "Forget what has been sent so the next run starts over. Requires --yes.");
        command.Options.Add(yes);

        command.SetAction(parseResult =>
        {
            var stdout = parseResult.InvocationConfiguration.Output;
            var stderr = parseResult.InvocationConfiguration.Error;
            if (!parseResult.GetValue(yes))
            {
                stderr.WriteLine("reset forgets every sent item and the next run resends everything; pass --yes to confirm.");
                return 1;
            }

            try
            {
                using var store = new StateStore(AppPaths.Resolve().StateDb);
                var removed = store.ResetSent();
                stdout.WriteLine($"Forgot {removed} sent items; the next run starts over.");
                return 0;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
            {
                stderr.WriteLine($"reset failed: {ex.Message}");
                return 1;
            }
        });

        return command;
    }
}
