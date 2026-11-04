using SkylightPhotoSync.Infrastructure;

namespace SkylightPhotoSync.Tests.TestSupport;

/// <summary>
/// An <see cref="IProcessRunner"/> that records every call and returns scripted results
/// in the order they were enqueued. Runs out of script → returns <see cref="Default"/>.
/// </summary>
public sealed class FakeProcessRunner : IProcessRunner
{
    private readonly Queue<ProcessResult> _scripted = new();
    private readonly List<ProcessCall> _calls = [];

    /// <summary>Result returned when no scripted result is queued. Defaults to exit 0 with empty output.</summary>
    public ProcessResult Default { get; set; } = new(0, string.Empty, string.Empty);

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
        var result = _scripted.Count > 0 ? _scripted.Dequeue() : Default;
        return Task.FromResult(result);
    }
}

public sealed record ProcessCall(string FileName, IReadOnlyList<string> Args);
