using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using EKA.Application.Abstractions.Ingestion;
using EKA.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace EKA.IntegrationTests.Persistence;

/// <summary>
/// The ClamAV adapter (Step 14) against a scripted clamd: a real TCP listener that speaks the
/// INSTREAM protocol, so the framing is tested byte for byte without a 1 GB signature database.
/// The real daemon is exercised by the deployment drill (docs/steps/step-14).
/// </summary>
public sealed class ClamAvScannerTests
{
    /// <summary>Accepts one connection, records the streamed bytes, answers <paramref name="reply"/>.</summary>
    private sealed class FakeClamd : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly Task _serving;

        public FakeClamd(string? reply)
        {
            _listener.Start();
            _serving = ServeAsync(reply);
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public string Command { get; private set; } = string.Empty;

        public byte[] Received { get; private set; } = [];

        private async Task ServeAsync(string? reply)
        {
            using var client = await _listener.AcceptTcpClientAsync();
            var stream = client.GetStream();

            var command = new byte[10];
            await stream.ReadExactlyAsync(command);
            Command = Encoding.ASCII.GetString(command);

            var body = new MemoryStream();
            var length = new byte[4];
            while (true)
            {
                await stream.ReadExactlyAsync(length);
                var size = BinaryPrimitives.ReadUInt32BigEndian(length);
                if (size == 0)
                {
                    break;
                }

                var chunk = new byte[size];
                await stream.ReadExactlyAsync(chunk);
                body.Write(chunk);
            }

            Received = body.ToArray();

            if (reply is not null)
            {
                await stream.WriteAsync(Encoding.ASCII.GetBytes(reply + "\0"));
            }
        }

        public async ValueTask DisposeAsync()
        {
            _listener.Stop();
            try
            {
                await _serving;
            }
            catch (Exception exception) when (exception is SocketException or IOException or ObjectDisposedException)
            {
            }
        }
    }

    /// <summary>The adapter as the worker gets it: through AddInfrastructure and configuration.</summary>
    private static IMalwareScanner Scanner(int port, string scanner = "ClamAv")
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                // Never opened: these tests resolve the scanner only.
                [$"ConnectionStrings:{DependencyInjection.ConnectionStringName}"] = "Server=unused;Database=unused",
                ["MalwareScanning:Scanner"] = scanner,
                ["MalwareScanning:Host"] = "127.0.0.1",
                ["MalwareScanning:Port"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["MalwareScanning:Timeout"] = "00:00:05",
            })
            .Build();

        return new ServiceCollection()
            .AddLogging()
            .AddInfrastructure(configuration)
            .BuildServiceProvider()
            .GetRequiredService<IMalwareScanner>();
    }

    [Fact]
    public async Task A_clean_file_is_streamed_whole_and_reported_clean()
    {
        await using var clamd = new FakeClamd("stream: OK");
        var content = Enumerable.Range(0, 200_000).Select(i => (byte)i).ToArray();

        var verdict = await Scanner(clamd.Port).ScanAsync(new MemoryStream(content), "big.bin");

        verdict.IsInfected.ShouldBeFalse();
        clamd.Command.ShouldBe("zINSTREAM\0");
        clamd.Received.ShouldBe(content);
    }

    [Fact]
    public async Task A_signature_match_is_a_verdict_of_infected_with_its_name()
    {
        await using var clamd = new FakeClamd("stream: Eicar-Test-Signature FOUND");

        var verdict = await Scanner(clamd.Port).ScanAsync(new MemoryStream("x"u8.ToArray()), "eicar.txt");

        verdict.IsInfected.ShouldBeTrue();
        verdict.Threat.ShouldBe("Eicar-Test-Signature");
    }

    [Fact]
    public async Task An_error_reply_throws_so_the_job_retries_rather_than_reading_it_as_clean()
    {
        await using var clamd = new FakeClamd("INSTREAM size limit exceeded. ERROR");

        await Should.ThrowAsync<InvalidOperationException>(
            () => Scanner(clamd.Port).ScanAsync(new MemoryStream("x"u8.ToArray()), "big.pdf"));
    }

    [Fact]
    public async Task A_connection_closed_without_a_reply_throws()
    {
        await using var clamd = new FakeClamd(reply: null);

        await Should.ThrowAsync<InvalidOperationException>(
            () => Scanner(clamd.Port).ScanAsync(new MemoryStream("x"u8.ToArray()), "a.txt"));
    }

    [Fact]
    public async Task An_unreachable_scanner_throws()
    {
        // A port that was just free: nothing listens there.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        await Should.ThrowAsync<SocketException>(
            () => Scanner(port).ScanAsync(new MemoryStream("x"u8.ToArray()), "a.txt"));
    }

    [Fact]
    public async Task Without_configuration_no_scanner_runs_and_everything_is_clean()
    {
        var verdict = await Scanner(port: 1, scanner: "None").ScanAsync(new MemoryStream("x"u8.ToArray()), "a.txt");

        verdict.IsInfected.ShouldBeFalse();
    }
}
