namespace Colibri.Core.Services;

/// <summary>A browser offer is pending until confirmation and expires safely.</summary>
public sealed class CaptureSession : IDisposable
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _lifetime;
    private string _state = "pending";
    private string? _rejection;
    private Operation? _operation;
    private bool _cleanupUnsafe;
    private TaskCompletionSource _settled = CompletedSignal();
    public CaptureSession(TimeSpan lifetime)
    {
        Id = Guid.NewGuid().ToString("D");
        _lifetime = new CancellationTokenSource(lifetime);
        _lifetime.Token.Register(() => Reject("browser"));
    }
    public string Id { get; }
    public CancellationToken Token => _lifetime.Token;
    public string State { get { lock (_gate) return _state; } }
    /// <summary>Serializes transfer acquisition and delays browser release until rollback has finished.</summary>
    public Operation? TryBeginOperation()
    {
        lock (_gate)
        {
            if (_state != "pending" || _rejection is not null || _cleanupUnsafe || Token.IsCancellationRequested || _operation is not null)
                return null;
            _settled = new(TaskCreationOptions.RunContinuationsAsynchronously);
            return _operation = new Operation(this);
        }
    }
    public Task WaitForOperationAsync(CancellationToken ct)
    {
        lock (_gate) return _settled.Task.WaitAsync(ct);
    }
    public bool Accept()
    {
        lock (_gate)
        {
            if (_state != "pending" || _rejection is not null || _cleanupUnsafe || Token.IsCancellationRequested) return false;
            _state = "accepted";
            _lifetime.CancelAfter(Timeout.InfiniteTimeSpan);
            return true;
        }
    }
    public void Reject(string state = "rejected")
    {
        lock (_gate)
        {
            if (_state != "pending" || _rejection is not null) return;
            _rejection = state;
            if (_operation is null && !_cleanupUnsafe) _state = state;
        }
        _lifetime.Cancel();
    }
    public void Dispose() => _lifetime.Dispose();

    private static TaskCompletionSource CompletedSignal()
    {
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        signal.SetResult();
        return signal;
    }
    public sealed class Operation : IDisposable
    {
        private readonly CaptureSession _owner;
        private bool _unsafeCleanup;
        internal Operation(CaptureSession owner) => _owner = owner;
        /// <summary>Keep browser ownership pending when the engine cannot be confirmed stopped.</summary>
        public void CleanupFailed() => _unsafeCleanup = true;
        public void Dispose()
        {
            lock (_owner._gate)
            {
                if (_owner._operation != this) return;
                _owner._operation = null;
                _owner._cleanupUnsafe |= _unsafeCleanup;
                if (!_owner._cleanupUnsafe && _owner._state == "pending" && _owner._rejection is { } rejection)
                    _owner._state = rejection;
                _owner._settled.TrySetResult();
            }
        }
    }
}
