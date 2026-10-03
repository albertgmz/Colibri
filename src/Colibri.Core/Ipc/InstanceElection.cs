using System.Diagnostics;

namespace Colibri.Core.Ipc;

/// <summary>What a starting Colibri process turned out to be.</summary>
public enum InstanceRole
{
    /// <summary>No other Colibri was running: this process is the primary instance.</summary>
    Primary,

    /// <summary>The previous Colibri held the mutex without answering, then exited: this process took over.</summary>
    PrimaryAfterWaiting,

    /// <summary>The running Colibri accepted this start's arguments; this process should exit.</summary>
    Secondary,

    /// <summary>Another Colibri holds the mutex but neither accepted the arguments nor exited in time; this process should exit.</summary>
    NoAnswer,
}

/// <summary>The outcome of <see cref="InstanceElection.Run"/>.</summary>
/// <param name="Role">What this process is.</param>
/// <param name="Guard">Held by a primary instance until it exits; null otherwise.</param>
public sealed record InstanceElectionResult(InstanceRole Role, SingleInstanceGuard? Guard);

/// <summary>Decides at startup whether this process runs Colibri or hands its arguments to the one already running.</summary>
public static class InstanceElection
{
    /// <summary>How long one wait for the mutex lasts before the running Colibri is asked again.</summary>
    public static readonly TimeSpan RetryInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Takes the single-instance mutex if it is free. Otherwise sends this process's request to the running
    /// Colibri with <paramref name="sendToPrimary"/> (null: nobody answered). A Colibri that does not accept
    /// it is either still starting (its pipe opens last) or exiting (Exit refuses requests and closes the pipe
    /// first; the mutex is released when the process ends). So until <paramref name="giveUpAfter"/> this
    /// alternates between waiting a moment for the mutex, becoming the primary instance once it is free, and
    /// asking again.
    /// </summary>
    /// <remarks>Call on the thread that will later dispose the guard: a mutex belongs to the thread that took it.</remarks>
    public static InstanceElectionResult Run(string mutexName, Func<IpcResponse?> sendToPrimary, TimeSpan giveUpAfter)
    {
        var guard = SingleInstanceGuard.TryAcquire(mutexName, TimeSpan.Zero);
        if (guard is not null)
        {
            return new InstanceElectionResult(InstanceRole.Primary, guard);
        }

        var clock = Stopwatch.StartNew();
        while (true)
        {
            if (sendToPrimary() is { Ok: true })
            {
                return new InstanceElectionResult(InstanceRole.Secondary, null);
            }

            var left = giveUpAfter - clock.Elapsed;
            if (left <= TimeSpan.Zero)
            {
                return new InstanceElectionResult(InstanceRole.NoAnswer, null);
            }

            guard = SingleInstanceGuard.TryAcquire(mutexName, left < RetryInterval ? left : RetryInterval);
            if (guard is not null)
            {
                return new InstanceElectionResult(InstanceRole.PrimaryAfterWaiting, guard);
            }
        }
    }
}
