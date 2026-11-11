using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using SkylightPhotoSync.Alerts;
using SkylightPhotoSync.State;

namespace SkylightPhotoSync.Skylight;

/// <summary>
/// Stops scheduled runs from trying a Skylight password that was already refused. After a
/// <see cref="SkylightSignInException"/> the failure is recorded and <see cref="ShouldAttempt"/> is
/// false until <c>[skylight]</c> email or password changes, or a sign-in succeeds (for example a
/// manual <c>skylight-sync prune --dry-run</c>). Repeating a bad password every hour could lock the
/// account, and a CAPTCHA never clears on its own.
/// </summary>
public sealed class SkylightSignInGate(StateStore store, string email, string password, Func<DateTimeOffset> now)
{
    private const string _version = "v1";
    private const int _iterations = 100_000;
    private const int _saltBytes = 16;
    private const int _hashBytes = 32;

    /// <summary>
    /// True when a sign-in may be tried: nothing is recorded, or the credentials differ from the ones
    /// that failed. In the second case the old failure is forgotten, so a new one alerts again.
    /// </summary>
    public bool ShouldAttempt()
    {
        if (store.SkylightSignInFailure() is not { } failure)
        {
            return true;
        }

        if (Matches(failure.Fingerprint))
        {
            return false;
        }

        Clear();
        return true;
    }

    /// <summary>Records the refusal, pausing later runs for these credentials.</summary>
    public void RecordFailure(string reason)
    {
        var salt = RandomNumberGenerator.GetBytes(_saltBytes);
        store.SetSkylightSignInFailure(reason, Fingerprint(salt), now());
    }

    /// <summary>A sign-in worked: forget any recorded failure. Returns true when there was one.</summary>
    public bool RecordSuccess()
    {
        if (store.SkylightSignInFailure() is null)
        {
            return false;
        }

        Clear();
        return true;
    }

    private void Clear()
    {
        store.ClearSkylightSignInFailure();
        store.ClearAlert(AlertKind.SkylightSignIn.ToString());
    }

    private bool Matches(string fingerprint)
    {
        var parts = fingerprint.Split(':');
        if (parts.Length != 3 || parts[0] != _version)
        {
            return false;
        }

        try
        {
            var expected = Encoding.ASCII.GetBytes(Fingerprint(Convert.FromBase64String(parts[1])));
            return CryptographicOperations.FixedTimeEquals(expected, Encoding.ASCII.GetBytes(fingerprint));
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>
    /// <c>v1:salt:hash</c>, a salted PBKDF2 hash of the email and password. state.db is not a secrets
    /// file, so the password itself must never be stored there; the slow salted hash only answers
    /// "are these the credentials that failed?".
    /// </summary>
    private string Fingerprint(byte[] salt)
    {
        var material = Encoding.UTF8.GetBytes(email.Trim().ToLowerInvariant() + "\n" + password);
        var hash = Rfc2898DeriveBytes.Pbkdf2(material, salt, _iterations, HashAlgorithmName.SHA256, _hashBytes);
        return string.Create(
            CultureInfo.InvariantCulture, $"{_version}:{Convert.ToBase64String(salt)}:{Convert.ToBase64String(hash)}");
    }
}
