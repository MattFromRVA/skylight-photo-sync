namespace SkylightPhotoSync.Infrastructure;

/// <summary>
/// Runs an external process. Mac-only tools (<c>sips</c>, <c>osascript</c>, <c>launchctl</c>)
/// are invoked through this interface so tests can fake them.
/// </summary>
public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> args, CancellationToken ct = default);
}

public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr);
