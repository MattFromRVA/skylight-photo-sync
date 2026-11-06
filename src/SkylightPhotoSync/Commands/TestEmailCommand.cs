using System.CommandLine;
using System.Globalization;
using MimeKit;
using Serilog.Core;
using SkiaSharp;
using SkylightPhotoSync.Config;
using SkylightPhotoSync.Mail;

namespace SkylightPhotoSync.Commands;

/// <summary>
/// <c>test-email</c>: sends one generated 800×600 JPEG to every <c>email.to</c> address, so the user
/// can approve the sender on the frame. <c>--size-probe</c> instead sends five noise JPEGs of growing
/// size, to find the largest attachment the frame accepts.
/// </summary>
public static class TestEmailCommand
{
    public const string ProbeHint = "Check which arrived on the frame";

    internal static readonly int[] ProbeSizesMb = [5, 10, 15, 20, 24];

    private const int _testWidth = 800;
    private const int _testHeight = 600;
    private const int _jpegQuality = 90;
    private const int _probeAttempts = 6;
    private const double _probeTolerance = 0.1;

    public static Command Create()
    {
        var sizeProbe = new Option<bool>("--size-probe")
        {
            Description = "Send five noise JPEGs of about 5, 10, 15, 20 and 24 MB instead of the test image.",
        };

        var command = new Command("test-email", "Send a generated test image to every configured recipient.");
        command.Options.Add(sizeProbe);

        command.SetAction((parseResult, ct) => ExecuteAsync(
            parseResult.GetValue(sizeProbe),
            parseResult.InvocationConfiguration.Output,
            parseResult.InvocationConfiguration.Error,
            ct));

        return command;
    }

    /// <summary>
    /// A JPEG of random pixels whose encoded size is within about 10% of <paramref name="targetBytes"/>.
    /// Noise barely compresses, so the size scales with the pixel count; a few rounds of resizing hit the target.
    /// </summary>
    internal static byte[] NoiseJpeg(int targetBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(targetBytes);
        var random = new Random(Seed: 20261105);
        var pixels = Math.Max(4096L, targetBytes / 2);
        var bytes = Array.Empty<byte>();
        for (var attempt = 0; attempt < _probeAttempts; attempt++)
        {
            var side = (int)Math.Sqrt(pixels);
            bytes = EncodeNoise(side, (int)(pixels / side), random);
            if (Math.Abs(bytes.Length - targetBytes) <= targetBytes * _probeTolerance)
            {
                break;
            }

            pixels = Math.Max(4096L, (long)(pixels * ((double)targetBytes / bytes.Length)));
        }

        return bytes;
    }

    /// <summary>The 800×600 test image: a gradient with a few circles, so it is recognisable on the frame.</summary>
    internal static byte[] TestJpeg()
    {
        var info = new SKImageInfo(_testWidth, _testHeight, SKColorType.Rgba8888, SKAlphaType.Opaque);
        using var surface = SKSurface.Create(info)
            ?? throw new InvalidOperationException("Cannot allocate the test image surface.");
        var canvas = surface.Canvas;
        using (var shader = SKShader.CreateLinearGradient(
            new SKPoint(0, 0),
            new SKPoint(_testWidth, _testHeight),
            [new SKColor(0x1E, 0x3A, 0x5F), new SKColor(0xF4, 0xA2, 0x61)],
            SKShaderTileMode.Clamp))
        using (var background = new SKPaint { Shader = shader })
        {
            canvas.DrawRect(0, 0, _testWidth, _testHeight, background);
        }

        using (var circle = new SKPaint { Color = SKColors.White, IsAntialias = true })
        {
            canvas.DrawCircle(_testWidth * 0.3f, _testHeight * 0.5f, 90, circle);
            circle.Color = new SKColor(0xE7, 0x6F, 0x51);
            canvas.DrawCircle(_testWidth * 0.6f, _testHeight * 0.4f, 60, circle);
            circle.Color = new SKColor(0x2A, 0x9D, 0x8F);
            canvas.DrawCircle(_testWidth * 0.75f, _testHeight * 0.7f, 40, circle);
        }

        return Encode(surface);
    }

    private static async Task<int> ExecuteAsync(bool sizeProbe, TextWriter stdout, TextWriter stderr, CancellationToken ct)
    {
        var paths = AppPaths.Resolve();
        Logger log;
        try
        {
            log = Logging.Configure(paths);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            stderr.WriteLine($"test-email failed: cannot open the log file {paths.LogFile}: {ex.Message}");
            return 1;
        }

        using (log)
        {
            try
            {
                var cfg = ConfigLoader.LoadConfig(paths.ConfigFile);
                var mailer = CommandSupport.CreateMailer(cfg.Smtp, CommandSupport.LoadSecretsIfPresent(paths));
                if (sizeProbe)
                {
                    var accepted = await SendProbesAsync(cfg, mailer, log, stdout, stderr, ct);
                    stdout.WriteLine(ProbeHint);
                    return accepted > 0 ? 0 : 1;
                }

                await SendTestImageAsync(cfg, mailer, log, stdout, ct);
                return 0;
            }
            catch (Exception ex) when (ex is ConfigException or SendFailedException)
            {
                log.Error("test-email failed: {Message}", ex.Message);
                stderr.WriteLine($"test-email failed: {ex.Message}");
                return 1;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                stderr.WriteLine("test-email cancelled.");
                return 1;
            }
            catch (Exception ex)
            {
                log.Error(ex, "test-email failed");
                stderr.WriteLine($"test-email failed: {ex.GetType().Name}: {ex.Message}");
                return 1;
            }
        }
    }

    private static async Task SendTestImageAsync(AppConfig cfg, IMailer mailer, Logger log, TextWriter stdout, CancellationToken ct)
    {
        var jpeg = TestJpeg();
        var stamp = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        foreach (var recipient in cfg.Email.To)
        {
            var message = Build(
                cfg.Email.From,
                recipient,
                $"Skylight Photo Sync test ({stamp})",
                "Test image from skylight-sync. If it shows on the frame, the sender is approved.",
                "skylight-sync-test.jpg",
                jpeg);
            await mailer.SendAsync(message, ct);
            log.Information("test email sent to {Recipient} ({Bytes} bytes)", recipient, jpeg.Length);
            stdout.WriteLine($"Sent test image to {recipient}");
        }
    }

    /// <summary>
    /// Sends every probe size to every recipient. An SMTP rejection (expected for the larger sizes on
    /// relays with a 25 MB limit) is reported and the series continues. Returns how many sends were accepted.
    /// </summary>
    private static async Task<int> SendProbesAsync(
        AppConfig cfg, IMailer mailer, Logger log, TextWriter stdout, TextWriter stderr, CancellationToken ct)
    {
        var accepted = 0;
        foreach (var megabytes in ProbeSizesMb)
        {
            ct.ThrowIfCancellationRequested();
            var jpeg = NoiseJpeg(megabytes * 1_000_000);
            foreach (var recipient in cfg.Email.To)
            {
                var message = Build(
                    cfg.Email.From,
                    recipient,
                    $"Skylight Photo Sync size probe — {megabytes} MB",
                    $"Size probe: one {megabytes} MB JPEG ({jpeg.Length:N0} bytes).",
                    $"skylight-sync-probe-{megabytes}mb.jpg",
                    jpeg);
                try
                {
                    await mailer.SendAsync(message, ct);
                }
                catch (SendFailedException ex)
                {
                    log.Warning("size probe {Megabytes} MB to {Recipient} rejected by SMTP: {Message}", megabytes, recipient, ex.Message);
                    stderr.WriteLine($"{megabytes} MB probe to {recipient} rejected by SMTP: {ex.Message}");
                    continue;
                }

                accepted++;
                log.Information("size probe {Megabytes} MB sent to {Recipient} ({Bytes} bytes)", megabytes, recipient, jpeg.Length);
                stdout.WriteLine($"Sent {megabytes} MB probe ({jpeg.Length:N0} bytes) to {recipient}");
            }
        }

        return accepted;
    }

    private static MimeMessage Build(string from, string to, string subject, string text, string fileName, byte[] jpeg)
    {
        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(from));
        message.To.Add(MailboxAddress.Parse(to));
        message.Subject = subject;
        message.Body = new Multipart("mixed")
        {
            new TextPart("plain") { Text = text },
            new MimePart("image", "jpeg")
            {
                Content = new MimeContent(new MemoryStream(jpeg)),
                ContentDisposition = new ContentDisposition(ContentDisposition.Attachment),
                ContentTransferEncoding = ContentEncoding.Base64,
                FileName = fileName,
            },
        };
        return message;
    }

    private static byte[] EncodeNoise(int width, int height, Random random)
    {
        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        using var bitmap = new SKBitmap(info);
        random.NextBytes(bitmap.GetPixelSpan());
        using var image = SKImage.FromBitmap(bitmap)
            ?? throw new InvalidOperationException("Cannot create the noise image.");
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, _jpegQuality)
            ?? throw new InvalidOperationException("JPEG encoding of the noise image failed.");
        return data.ToArray();
    }

    private static byte[] Encode(SKSurface surface)
    {
        using var snapshot = surface.Snapshot();
        using var data = snapshot.Encode(SKEncodedImageFormat.Jpeg, _jpegQuality)
            ?? throw new InvalidOperationException("JPEG encoding of the test image failed.");
        return data.ToArray();
    }
}
