using SkylightPhotoSync.Config;

namespace SkylightPhotoSync.Tests.TestSupport;

/// <summary>An in-memory <see cref="IKeychain"/>; set <see cref="FailWith"/> to make every call throw.</summary>
public sealed class FakeKeychain : IKeychain
{
    public Dictionary<string, string> Items { get; } = new(StringComparer.Ordinal);

    /// <summary>When set, every call throws a <see cref="KeychainException"/> with this message.</summary>
    public string? FailWith { get; set; }

    public Task<string?> FindAsync(string service, CancellationToken ct)
    {
        ThrowIfFailing();
        return Task.FromResult(Items.TryGetValue(service, out var value) ? value : null);
    }

    public Task SetAsync(string service, string value, CancellationToken ct)
    {
        ThrowIfFailing();
        Items[service] = value;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string service, CancellationToken ct)
    {
        ThrowIfFailing();
        Items.Remove(service);
        return Task.CompletedTask;
    }

    private void ThrowIfFailing()
    {
        if (FailWith is not null)
        {
            throw new KeychainException(FailWith);
        }
    }
}
