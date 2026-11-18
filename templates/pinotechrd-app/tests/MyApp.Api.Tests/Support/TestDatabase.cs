using System.Net;
using System.Net.Sockets;

namespace MyApp.Api.Tests.Support;

/// <summary>Connection strings of the hosts that must not reach a real database.</summary>
internal static class TestDatabase
{
    /// <summary>The password inside <see cref="PlaceholderConnectionString"/>, recognizable in any output.</summary>
    public const string PlaceholderPassword = "Placeholder-Pw-0c41";

    /// <summary>
    /// Syntactically valid, and it points to port 1 of the loopback address, where nothing listens. The hosts that do
    /// not test the database get it so they start; they never open it.
    /// </summary>
    public const string PlaceholderConnectionString =
        "Host=127.0.0.1;Port=1;Database=placeholder_db;Username=placeholder_user;Password=" + PlaceholderPassword;
}

/// <summary>
/// A TCP listener that accepts connections at the operating system level and never answers them, like a server that
/// does not respond. It also tells whether anything tried to connect, which no timing can tell reliably.
/// </summary>
internal sealed class SilentServer : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);

    public SilentServer() => _listener.Start();

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>Whether a client has connected (the connection waits unaccepted in the backlog).</summary>
    public bool HasConnectionAttempt => _listener.Pending();

    public string ConnectionString(string password) =>
        $"Host=127.0.0.1;Port={Port};Database=silent_db;Username=silent_user;Password={password}";

    public void Dispose() => _listener.Stop();
}

/// <summary>
/// A server that accepts connections, keeps them open and never answers, and counts how many it accepted: a
/// connection attempt that is still pending is visible here, which the kernel's backlog alone cannot show.
/// </summary>
internal sealed class CountingSilentServer : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly System.Collections.Concurrent.ConcurrentBag<TcpClient> _clients = [];
    private readonly CancellationTokenSource _stop = new();
    private int _accepted;

    public CountingSilentServer()
    {
        _listener.Start();
        _ = Task.Run(AcceptLoopAsync);
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public int Accepted => Volatile.Read(ref _accepted);

    public string ConnectionString(string password) =>
        $"Host=127.0.0.1;Port={Port};Database=silent_db;Username=silent_user;Password={password}";

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                _clients.Add(client);
                Interlocked.Increment(ref _accepted);
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException or SocketException)
        {
            // Stopped by Dispose.
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
        foreach (var client in _clients)
        {
            client.Dispose();
        }

        _stop.Dispose();
    }
}

/// <summary>
/// A TCP proxy between a test host and a server (the shared container) that can be frozen: while frozen it keeps
/// reading what either side sends and forwards nothing, so every connection, established or new, stalls without
/// the server being touched. Thawing delivers what was held.
/// </summary>
internal sealed class FreezableProxy : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly string _targetHost;
    private readonly int _targetPort;
    private readonly CancellationTokenSource _stop = new();
    private readonly System.Collections.Concurrent.ConcurrentBag<TcpClient> _clients = [];
    private TaskCompletionSource _gate = Open();
    private int _accepted;

    public FreezableProxy(string targetHost, int targetPort, bool frozen = false)
    {
        _targetHost = targetHost;
        _targetPort = targetPort;
        if (frozen)
        {
            Freeze();
        }

        _listener.Start();
        _ = Task.Run(AcceptLoopAsync);
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>How many client connections the proxy accepted.</summary>
    public int Accepted => Volatile.Read(ref _accepted);

    public void Freeze()
    {
        lock (_stop)
        {
            if (_gate.Task.IsCompleted)
            {
                _gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }
    }

    public void Thaw()
    {
        lock (_stop)
        {
            _gate.TrySetResult();
        }
    }

    private static TaskCompletionSource Open()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        gate.SetResult();
        return gate;
    }

    private Task Gate()
    {
        lock (_stop)
        {
            return _gate.Task;
        }
    }

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                _clients.Add(client);
                Interlocked.Increment(ref _accepted);
                _ = Task.Run(() => ServeAsync(client));
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException or SocketException)
        {
            // Stopped by Dispose.
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        try
        {
            var upstream = new TcpClient();
            _clients.Add(upstream);
            await upstream.ConnectAsync(_targetHost, _targetPort, _stop.Token);
            await Task.WhenAny(
                PumpAsync(client.GetStream(), upstream.GetStream()),
                PumpAsync(upstream.GetStream(), client.GetStream()));
        }
#pragma warning disable CA1031 // A broken pipe, a reset or a stop ends the pair; nothing else to do.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
        finally
        {
            client.Dispose();
        }
    }

    private async Task PumpAsync(Stream from, Stream to)
    {
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = await from.ReadAsync(buffer, _stop.Token);
            if (read == 0)
            {
                return;
            }

            await Gate().WaitAsync(_stop.Token);
            await to.WriteAsync(buffer.AsMemory(0, read), _stop.Token);
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        Thaw();
        _listener.Stop();
        foreach (var client in _clients)
        {
            client.Dispose();
        }

        _stop.Dispose();
    }
}
