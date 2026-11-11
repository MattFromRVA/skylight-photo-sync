using System.CommandLine;
using SkylightPhotoSync.State;

namespace SkylightPhotoSync.Commands;

/// <summary>
/// <c>reset --yes</c>: forget every sent and skipped item so the next run starts over.
/// <c>reset --skipped</c>: forget only the skipped items so they are tried again; nothing is resent,
/// so it needs no <c>--yes</c>, and <c>--yes</c> next to it does not widen it.
/// </summary>
public static class ResetCommand
{
    public static Command Create()
    {
        var yes = new Option<bool>("--yes")
        {
            Description = "Confirm: forget every sent item, so the next run resends everything.",
        };

        var skipped = new Option<bool>("--skipped")
        {
            Description = "Forget only the skipped items so the next run tries them again. Sent items are kept; --yes is not needed.",
        };

        var command = new Command(
            "reset",
            "Forget what has been sent so the next run starts over (requires --yes), or with --skipped only what was skipped.");
        command.Options.Add(yes);
        command.Options.Add(skipped);

        command.SetAction(parseResult =>
        {
            var stdout = parseResult.InvocationConfiguration.Output;
            var stderr = parseResult.InvocationConfiguration.Error;
            var skippedOnly = parseResult.GetValue(skipped);
            if (!skippedOnly && !parseResult.GetValue(yes))
            {
                stderr.WriteLine(
                    "reset forgets every sent item and the next run resends everything; pass --yes to confirm, "
                    + "or --skipped to forget only the skipped items.");
                return 1;
            }

            try
            {
                using var store = new StateStore(AppPaths.Resolve().StateDb);
                if (skippedOnly)
                {
                    var cleared = store.ResetSkipped();
                    stdout.WriteLine($"Cleared {cleared} skipped items; the next run tries them again. Sent items were kept.");
                    return 0;
                }

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
