using System.IO.Pipes;

namespace Colibri.Core.Ipc;

/// <summary>Sends one <see cref="IpcRequest"/> to the running Colibri and returns its answer.</summary>
public static class LocalPipeClient
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Connects to <paramref name="pipeName"/>, sends <paramref name="request"/> and reads the response,
    /// all within <paramref name="timeout"/>. Connecting keeps retrying until the timeout, so a server that
    /// is still starting is found.
    /// </summary>
    /// <exception cref="TimeoutException">No server answered in time.</exception>
    /// <exception cref="IOException">The connection broke or the response was not valid.</exception>
    public static async Task<IpcResponse> SendAsync(string pipeName, IpcRequest request, TimeSpan timeout, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        try
        {
            // CurrentUserOnly: only connects to a pipe created by the same user.
            await using var pipe = new NamedPipeClientStream(
                ".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(limit.Token);

            await IpcProtocol.WriteLineAsync(pipe, IpcProtocol.SerializeRequest(request), limit.Token);
            var (line, tooLong) = await IpcProtocol.ReadLineAsync(pipe, IpcProtocol.MaxLineBytes, limit.Token);
            if (line is null || tooLong)
            {
                throw new IOException("The server closed the connection without a valid response.");
            }

            return IpcProtocol.ParseResponse(line);
        }
        catch (InvalidDataException ex)
        {
            throw new IOException(ex.Message, ex);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"No answer on the local pipe '{pipeName}' within {timeout.TotalSeconds:0} s.");
        }
    }
}
