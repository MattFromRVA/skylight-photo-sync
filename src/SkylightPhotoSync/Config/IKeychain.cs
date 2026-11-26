namespace SkylightPhotoSync.Config;

/// <summary>
/// Generic-password items in the macOS Keychain, one item per <paramref name="service"/> name.
/// The real one is <see cref="SecurityCliKeychain"/>; tests use a fake so CI never runs <c>security</c>.
/// </summary>
public interface IKeychain
{
    /// <summary>The item's value, or null when there is no such item.</summary>
    Task<string?> FindAsync(string service, CancellationToken ct);

    /// <summary>Adds the item, or replaces its value when it already exists.</summary>
    Task SetAsync(string service, string value, CancellationToken ct);

    /// <summary>Removes the item; nothing happens when there is none.</summary>
    Task DeleteAsync(string service, CancellationToken ct);
}

/// <summary>The Keychain could not be used. The message never contains a secret value.</summary>
public sealed class KeychainException : Exception
{
    public KeychainException(string message)
        : base(message)
    {
    }

    public KeychainException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
