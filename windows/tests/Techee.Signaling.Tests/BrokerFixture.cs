using System.Diagnostics;
using System.Net.Sockets;
using Xunit;

namespace Techee.Signaling.Tests;

/// <summary>
/// Runs the real Node signaling broker for the duration of a test class.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately the actual <c>server/src/server.js</c> rather than a C# stand-in.
/// A mock broker would be written from the same understanding as the client, so the
/// two would agree on a misreading of the protocol and the tests would pass while a
/// real device could not connect. The whole point of W2 is proving Windows works
/// against the broker Android already talks to.
/// </para>
/// <para>
/// Skips rather than fails when Node is unavailable, so the rest of the suite still
/// runs on a machine without it.
/// </para>
/// </remarks>
public sealed class BrokerFixture : IAsyncLifetime
{
    private Process? _process;

    public int Port { get; private set; }
    public Uri Url => new($"ws://127.0.0.1:{Port}");

    /// <summary>Null when the broker started; otherwise why it did not.</summary>
    public string? SkipReason { get; private set; }

    public Task InitializeAsync()
    {
        var repo = FindRepoRoot();
        if (repo is null)
        {
            SkipReason = "could not locate the repository root";
            return Task.CompletedTask;
        }

        var serverJs = Path.Combine(repo, "server", "src", "server.js");
        if (!File.Exists(serverJs))
        {
            SkipReason = $"{serverJs} not found";
            return Task.CompletedTask;
        }

        Port = FreePort();

        var psi = new ProcessStartInfo("node", $"\"{serverJs}\"")
        {
            WorkingDirectory = Path.Combine(repo, "server"),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.Environment["PORT"] = Port.ToString();
        // Short enough that an expiry test does not stall, long enough that a
        // loopback handshake plus a TPM signature never races it.
        psi.Environment["REGISTER_CHALLENGE_TTL_MS"] = "5000";

        try
        {
            _process = Process.Start(psi);
        }
        catch (Exception e)
        {
            SkipReason = $"could not start node: {e.Message}";
            return Task.CompletedTask;
        }

        if (_process is null)
        {
            SkipReason = "node did not start";
            return Task.CompletedTask;
        }

        // Drain the pipes; a full buffer would deadlock the child.
        _process.OutputDataReceived += (_, _) => { };
        _process.ErrorDataReceived += (_, _) => { };
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

        if (!WaitForPort(Port, TimeSpan.FromSeconds(15)))
        {
            SkipReason = $"broker did not open port {Port}";
        }

        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        try
        {
            if (_process is { HasExited: false })
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(5000);
            }
        }
        catch (InvalidOperationException)
        {
            // Already gone.
        }

        _process?.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>Polls rather than sleeping a fixed interval, so a fast start is not penalised.</summary>
    private static bool WaitForPort(int port, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var probe = new TcpClient();
                probe.Connect("127.0.0.1", port);
                return true;
            }
            catch (SocketException)
            {
                Thread.Sleep(100);
            }
        }
        return false;
    }

    /// <summary>
    /// Binds port 0 to have the OS pick a free port.
    /// </summary>
    /// <remarks>
    /// A fixed port would collide with a developer's running broker, or with the Node
    /// smoke test, and produce a confusing intermittent failure.
    /// </remarks>
    private static int FreePort()
    {
        var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static string? FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "server", "src"))
                && Directory.Exists(Path.Combine(dir.FullName, "protocol", "fixtures")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        return null;
    }
}

[CollectionDefinition("broker")]
public sealed class BrokerCollection : ICollectionFixture<BrokerFixture>;
