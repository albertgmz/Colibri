using System.IO.Pipes;
using Microsoft.Extensions.Logging;

namespace Colibri.Core.Ipc;

/// <summary>
/// Listens on a named pipe that only the current user can open, and answers one <see cref="IpcRequest"/>
/// per connection through a handler. Several connections are served at the same time.
/// </summary>
/// <remarks>
/// Invalid requests are answered with an error response without calling the handler. A connection that
/// does not finish within <see cref="ConnectionTimeout"/> is dropped.
/// </remarks>
public sealed class LocalPipeServer : IAsyncDisposable
{
    public static readonly TimeSpan ConnectionTimeout = TimeSpan.FromSeconds(10);

    private const string TooLargeError = "The request is too large.";

    private readonly string _pipeName;
    private readonly Func<IpcRequest, CancellationToken, Task<IpcResponse>> _handler;
    private readonly ILogger _logger;
    private readonly Func<NamedPipeServerStream> _createPipe;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _connectionsLock = new();
    private readonly HashSet<Task> _connections = [];
    private Task? _acceptLoop;

    public LocalPipeServer(string pipeName, Func<IpcRequest, CancellationToken, Task<IpcResponse>> handler, ILogger logger)
        : this(pipeName, handler, logger, () => new NamedPipeServerStream(
            pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly))
    {
    }

    // Test seam for checking listener ownership and creation-failure recovery.
    internal LocalPipeServer(string pipeName, Func<IpcRequest, CancellationToken, Task<IpcResponse>> handler,
        ILogger logger, Func<NamedPipeServerStream> createPipe)
    {
        _pipeName = pipeName;
        _handler = handler;
        _logger = logger;
        _createPipe = createPipe;
    }

    /// <summary>Starts accepting connections in the background.</summary>
    public void Start()
    {
        _acceptLoop ??= Task.Run(() => AcceptLoopAsync(_stop.Token));
    }

    /// <summary>Stops listening and waits for the open connections to end.</summary>
    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        if (_acceptLoop is not null)
        {
            await _acceptLoop;
        }

        Task[] open;
        lock (_connectionsLock)
        {
            open = _connections.ToArray();
        }

        await Task.WhenAll(open);
        _stop.Dispose();
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        NamedPipeServerStream? listener = null;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                listener ??= await CreateListenerAsync(ct);
                if (listener is null) return;
                try
                {
                    await listener.WaitForConnectionAsync(ct);
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex)
                {
                    // A failed connection (including a refused Unix peer) must not stop the server.
                    _logger.LogDebug(ex, "A local pipe connection failed before it started");
                    await listener.DisposeAsync();
                    listener = null;
                    continue;
                }

                var accepted = listener;
                listener = null;
                try
                {
                    // Unix streams share a listener that closes when its last stream is disposed.
                    // Retain its successor before the client handler can close the accepted stream.
                    // This successor is the next real accept instance on Windows as well.
                    listener = await CreateListenerAsync(ct);
                }
                catch
                {
                    await accepted.DisposeAsync();
                    throw;
                }
                if (listener is null)
                {
                    await accepted.DisposeAsync();
                    return;
                }
                Track(HandleConnectionAsync(accepted, ct));
            }
        }
        finally
        {
            if (listener is not null) await listener.DisposeAsync();
        }
    }

    private async Task<NamedPipeServerStream?> CreateListenerAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { return _createPipe(); }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not create the local pipe {PipeName}", _pipeName);
                if (!await DelayAsync(TimeSpan.FromSeconds(1), ct)) return null;
            }
        }
        return null;
    }

    private void Track(Task connection)
    {
        lock (_connectionsLock)
        {
            _connections.Add(connection);
        }

        connection.ContinueWith(
            finished =>
            {
                lock (_connectionsLock)
                {
                    _connections.Remove(finished);
                }
            },
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private async Task HandleConnectionAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        await using (pipe)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(ConnectionTimeout);
            var token = timeout.Token;
            try
            {
                var response = await ReadAndHandleAsync(pipe, token);
                if (response is null)
                {
                    return; // The client closed without sending anything.
                }

                await IpcProtocol.WriteLineAsync(pipe, IpcProtocol.SerializeResponse(response), token);

                // Wait for the client to read the answer and hang up, so closing our end cannot cut it off.
                // Not after a rejected oversized request: that client may still be writing.
                if (response.Error != TooLargeError)
                {
                    _ = await pipe.ReadAsync(new byte[1], token); // Returns 0 once the client has closed.
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogDebug("A local pipe connection timed out or the server stopped");
            }
            catch (IOException ex)
            {
                _logger.LogDebug(ex, "A local pipe connection broke");
            }
        }
    }

    private async Task<IpcResponse?> ReadAndHandleAsync(Stream pipe, CancellationToken ct)
    {
        string? line;
        try
        {
            (line, var tooLong) = await IpcProtocol.ReadLineAsync(pipe, IpcProtocol.MaxLineBytes, ct);
            if (tooLong)
            {
                return IpcResponse.Failure(TooLargeError);
            }
        }
        catch (InvalidDataException ex)
        {
            return IpcResponse.Failure(ex.Message);
        }

        if (line is null)
        {
            return null;
        }

        if (!IpcProtocol.TryParseRequest(line, out var request, out var error))
        {
            _logger.LogWarning("Rejected a local pipe request: {Error}", error);
            return IpcResponse.Failure(error);
        }

        try
        {
            return await _handler(request, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Handling a {Type} request failed", request.GetType().Name);
            return IpcResponse.Failure("The request could not be handled.");
        }
    }

    private static async Task<bool> DelayAsync(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delay, ct);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
