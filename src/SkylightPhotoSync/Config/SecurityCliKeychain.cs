using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace SkylightPhotoSync.Config;

/// <summary>
/// <see cref="IKeychain"/> over the macOS <c>security</c> tool. A value is never put on a command
/// line, where other processes could see it: it is written to <c>security -i</c> on stdin, and read
/// back from <c>find-generic-password -g</c>'s stderr. Output from the command that carries a value
/// never goes into an error message. Every item uses the account <see cref="Account"/>.
/// </summary>
public sealed class SecurityCliKeychain : IKeychain
{
    public const string Account = "skylight-photo-sync";

    private const string _securityPath = "/usr/bin/security";

    /// <summary><c>errSecItemNotFound</c>, the exit code for a missing item.</summary>
    private const int _notFoundExitCode = 44;

    private readonly string? _keychainPath;

    /// <param name="keychainPath">A keychain file to use instead of the user's default (login) keychain; tests pass a throwaway one.</param>
    public SecurityCliKeychain(string? keychainPath = null)
    {
        _keychainPath = keychainPath;
    }

    /// <summary>True when the <c>security</c> tool exists on this machine (macOS only).</summary>
    public static bool IsSupported => OperatingSystem.IsMacOS() && File.Exists(_securityPath);

    public async Task<string?> FindAsync(string service, CancellationToken ct)
    {
        var args = new List<string> { "find-generic-password", "-a", Account, "-s", service, "-g" };
        AddKeychain(args);
        var result = await RunAsync(args, stdin: null, ct);
        if (result.ExitCode == _notFoundExitCode)
        {
            return null;
        }

        if (result.ExitCode != 0)
        {
            throw new KeychainException(
                $"could not read {service} from the Keychain (security exit {result.ExitCode}: {FirstLine(result.StdErr)})");
        }

        return ParsePassword(result.StdErr);
    }

    public async Task SetAsync(string service, string value, CancellationToken ct)
    {
        if (value.Contains('\n') || value.Contains('\r'))
        {
            throw new KeychainException($"{service} contains a line break and cannot be stored in the Keychain.");
        }

        var line = new StringBuilder("add-generic-password -U -a ")
            .Append(Quote(Account)).Append(" -s ").Append(Quote(service))
            .Append(" -w ").Append(Quote(value));
        if (_keychainPath is not null)
        {
            line.Append(' ').Append(Quote(_keychainPath));
        }

        line.Append('\n');

        // `security -i` exits 0 even when a command fails, so the write is checked by reading it back.
        var result = await RunAsync(["-i"], line.ToString(), ct);
        if (result.ExitCode != 0 || await FindAsync(service, ct) != value)
        {
            throw new KeychainException($"the Keychain did not store {service}; is the login keychain unlocked?");
        }
    }

    public async Task DeleteAsync(string service, CancellationToken ct)
    {
        var args = new List<string> { "delete-generic-password", "-a", Account, "-s", service };
        AddKeychain(args);
        var result = await RunAsync(args, stdin: null, ct);
        if (result.ExitCode != 0 && result.ExitCode != _notFoundExitCode)
        {
            throw new KeychainException(
                $"could not remove {service} from the Keychain (security exit {result.ExitCode}: {FirstLine(result.StdErr)})");
        }
    }

    /// <summary>
    /// The value in <c>find-generic-password -g</c> output: <c>password: "text"</c> for printable
    /// ASCII, otherwise <c>password: 0xHEX  "escaped"</c>, whose hex bytes are UTF-8.
    /// </summary>
    internal static string ParsePassword(string stderr)
    {
        const string prefix = "password: ";
        var line = stderr.Split('\n').FirstOrDefault(l => l.StartsWith(prefix, StringComparison.Ordinal));
        if (line is not null)
        {
            var rest = line[prefix.Length..].TrimEnd('\r');
            if (rest.StartsWith("0x", StringComparison.Ordinal))
            {
                var end = rest.IndexOf(' ');
                var hex = end < 0 ? rest[2..] : rest[2..end];
                try
                {
                    return Encoding.UTF8.GetString(Convert.FromHexString(hex));
                }
                catch (FormatException)
                {
                    // Falls through to the error below.
                }
            }
            else if (rest.Length >= 2 && rest[0] == '"' && rest[^1] == '"')
            {
                return rest[1..^1];
            }
            else if (rest.Length == 0)
            {
                return "";
            }
        }

        throw new KeychainException("could not understand the Keychain's answer from security.");
    }

    /// <summary>A double-quoted word for <c>security -i</c>, which reads <c>\"</c> and <c>\\</c> as escapes.</summary>
    internal static string Quote(string value) =>
        "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    private void AddKeychain(List<string> args)
    {
        if (_keychainPath is not null)
        {
            args.Add(_keychainPath);
        }
    }

    private static string FirstLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "no message";

    private static async Task<(int ExitCode, string StdErr)> RunAsync(IReadOnlyList<string> args, string? stdin, CancellationToken ct)
    {
        var startInfo = new ProcessStartInfo(_securityPath)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (Win32Exception ex)
        {
            throw new KeychainException($"could not run {_securityPath}: {ex.Message}", ex);
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        try
        {
            if (stdin is not null)
            {
                await process.StandardInput.WriteAsync(stdin.AsMemory(), ct);
            }

            process.StandardInput.Close();
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Already exited.
            }

            throw;
        }

        await stdoutTask;
        var stderr = await stderrTask;
        return (process.ExitCode, stderr);
    }
}
