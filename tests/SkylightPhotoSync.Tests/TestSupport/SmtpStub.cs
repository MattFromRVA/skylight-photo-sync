using System.Buffers;
using System.Net;
using System.Net.Sockets;
using MimeKit;
using SmtpServer;
using SmtpServer.ComponentModel;
using SmtpServer.Protocol;
using SmtpServer.Storage;

namespace SkylightPhotoSync.Tests.TestSupport;

/// <summary>
/// An in-process SMTP server on 127.0.0.1 with an ephemeral port, no authentication and no TLS.
/// Every message it accepts is parsed into <see cref="Received"/>.
/// </summary>
public sealed class SmtpStub : IAsyncDisposable
{
    private static readonly TimeSpan _readyTimeout = TimeSpan.FromSeconds(10);

    private readonly SmtpServer.SmtpServer _server;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _run;

    private SmtpStub(int port, int maxMessageSize)
    {
        Port = port;
        var options = new SmtpServerOptionsBuilder()
            .ServerName("localhost")
            .MaxMessageSize(maxMessageSize)
            .Endpoint(e => e.Endpoint(new IPEndPoint(IPAddress.Loopback, port)).IsSecure(false))
            .Build();
        var services = new ServiceProvider();
        services.Add(new CapturingStore(Received));
        _server = new SmtpServer.SmtpServer(options, services);
        _run = _server.StartAsync(_cts.Token);
    }

    /// <summary>The port the stub listens on.</summary>
    public int Port { get; }

    /// <summary>Messages accepted so far, in arrival order.</summary>
    public List<MimeMessage> Received { get; } = [];

    /// <summary>
    /// Starts a stub and returns once it accepts connections. Messages larger than
    /// <paramref name="maxMessageSize"/> bytes (0: no limit) are rejected.
    /// </summary>
    public static async Task<SmtpStub> StartAsync(int maxMessageSize = 0)
    {
        var stub = new SmtpStub(FreePort(), maxMessageSize);
        await stub.WaitUntilReadyAsync();
        return stub;
    }

    /// <summary>Returns a loopback port that nothing is listening on right now.</summary>
    public static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _server.Shutdown();
        await _cts.CancelAsync();
        try
        {
            await _run;
        }
        catch (OperationCanceledException)
        {
            // Expected: the listener loop ends by cancellation.
        }

        _cts.Dispose();
    }

    private async Task WaitUntilReadyAsync()
    {
        var deadline = DateTime.UtcNow + _readyTimeout;
        while (true)
        {
            try
            {
                using var probe = new TcpClient();
                await probe.ConnectAsync(IPAddress.Loopback, Port);
                return;
            }
            catch (SocketException) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(20);
            }
        }
    }

    private sealed class CapturingStore(List<MimeMessage> received) : MessageStore
    {
        public override async Task<SmtpResponse> SaveAsync(
            ISessionContext context,
            IMessageTransaction transaction,
            ReadOnlySequence<byte> buffer,
            CancellationToken cancellationToken)
        {
            using var stream = new MemoryStream();
            foreach (var segment in buffer)
            {
                stream.Write(segment.Span);
            }

            stream.Position = 0;
            var message = await MimeMessage.LoadAsync(stream, cancellationToken);
            received.Add(message);
            return SmtpResponse.Ok;
        }
    }
}
