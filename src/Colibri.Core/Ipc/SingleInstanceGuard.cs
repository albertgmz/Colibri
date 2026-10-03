namespace Colibri.Core.Ipc;

/// <summary>
/// Decides which Colibri process is the primary one for the current user: the first to own a named
/// mutex. The others hand their arguments to it over the local pipe and exit.
/// </summary>
/// <remarks>
/// A named mutex is used rather than "whoever creates the pipe first": on Linux and macOS .NET removes a
/// leftover socket file when a pipe server starts, so a second process could quietly take the pipe over.
/// Named mutexes work across processes of the same user on all three systems, and the operating system
/// releases one when its owner exits or crashes (the next owner then sees it as abandoned, which is fine).
/// The mutex is owned by the thread that acquired it, so dispose this object on that same thread.
/// </remarks>
public sealed class SingleInstanceGuard : IDisposable
{
    private readonly Mutex _mutex;

    private SingleInstanceGuard(Mutex mutex)
    {
        _mutex = mutex;
    }

    /// <summary>
    /// Returns a guard when this process became the primary instance, or null when another process still
    /// holds the mutex after <paramref name="wait"/> (zero: do not wait). Blocks the calling thread while waiting.
    /// </summary>
    public static SingleInstanceGuard? TryAcquire(string name, TimeSpan wait)
    {
        // CurrentUserOnly: another user cannot create or hold this name to block us. Not limited to the
        // session: on Unix every terminal is its own session, and the pipe name is per user, not per session.
        var mutex = new Mutex(false, name, new NamedWaitHandleOptions { CurrentUserOnly = true, CurrentSessionOnly = false });
        bool owned;
        try
        {
            owned = mutex.WaitOne(wait);
        }
        catch (AbandonedMutexException)
        {
            owned = true; // The previous primary crashed; we own the mutex now.
        }

        if (owned)
        {
            return new SingleInstanceGuard(mutex);
        }

        mutex.Dispose();
        return null;
    }

    public void Dispose()
    {
        _mutex.ReleaseMutex();
        _mutex.Dispose();
    }
}
