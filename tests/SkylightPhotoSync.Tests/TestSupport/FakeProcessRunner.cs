using SkylightPhotoSync.Infrastructure;

namespace SkylightPhotoSync.Tests.TestSupport;

/// <summary>
/// An <see cref="IProcessRunner"/> that records every call and returns scripted results
/// in the order they were enqueued. Runs out of script → throws <see cref="InvalidOperationException"/>.
/// </summary>
public sealed class FakeProcessRunner : IProcessRunner
{
    private readonly Queue<ProcessResult> _scripted = new();
    private readonly List<ProcessCall> _calls = [];

    public IReadOnlyList<ProcessCall> Calls => _calls;

    public FakeProcessRunner Enqueue(ProcessResult result)
    {
        _scripted.Enqueue(result);
        return this;
    }

    public FakeProcessRunner Enqueue(int exitCode, string stdOut = "", string stdErr = "")
        => Enqueue(new ProcessResult(exitCode, stdOut, stdErr));

    public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> args, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _calls.Add(new ProcessCall(fileName, [.. args]));
        if (!_scripted.TryDequeue(out var result))
        {
            throw new InvalidOperationException(
                $"No scripted result queued for '{fileName} {string.Join(' ', args)}'.");
        }

        return Task.FromResult(result);
    }
}

public sealed record ProcessCall(string FileName, IReadOnlyList<string> Args);
