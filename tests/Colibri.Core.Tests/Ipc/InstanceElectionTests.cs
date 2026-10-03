using Colibri.Core.Ipc;

namespace Colibri.Core.Tests.Ipc;

/// <summary>With real named mutexes (a unique name per test); the "other Colibri" holds it on its own thread.</summary>
public sealed class InstanceElectionTests
{
    private static readonly TimeSpan LongWait = TimeSpan.FromSeconds(10);

    private readonly string _mutexName = "colibri-test-instance-" + Guid.NewGuid().ToString("N")[..12];

    [Fact]
    public void A_free_mutex_makes_this_process_primary_without_asking_anyone()
    {
        var asked = false;

        var result = InstanceElection.Run(_mutexName, () => { asked = true; return null; }, LongWait);

        using var guard = result.Guard;
        Assert.Equal(InstanceRole.Primary, result.Role);
        Assert.NotNull(guard);
        Assert.False(asked);
    }

    [Fact]
    public void A_running_colibri_that_accepts_the_request_stays_primary()
    {
        using var other = OtherInstance.Start(_mutexName);

        var result = InstanceElection.Run(_mutexName, () => IpcResponse.Success, LongWait);

        Assert.Equal(InstanceRole.Secondary, result.Role);
        Assert.Null(result.Guard);
    }

    [Fact]
    public void A_colibri_that_is_still_starting_is_asked_again_until_it_answers()
    {
        using var other = OtherInstance.Start(_mutexName);
        var calls = 0;

        // Its pipe opens last: the first two attempts find nobody.
        var result = InstanceElection.Run(_mutexName, () => ++calls < 3 ? null : IpcResponse.Success, LongWait);

        Assert.Equal(InstanceRole.Secondary, result.Role);
        Assert.Equal(3, calls);
    }

    [Fact]
    public void A_colibri_that_refuses_because_it_is_exiting_is_waited_for_and_replaced()
    {
        using var other = OtherInstance.Start(_mutexName);

        var result = InstanceElection.Run(
            _mutexName, () => { other.ReleaseAfter(TimeSpan.FromMilliseconds(300)); return IpcResponse.Failure("Colibri is exiting."); }, LongWait);

        using var guard = result.Guard;
        Assert.Equal(InstanceRole.PrimaryAfterWaiting, result.Role);
    }

    [Fact]
    public void A_colibri_that_is_exiting_is_waited_for_and_replaced()
    {
        using var other = OtherInstance.Start(_mutexName);

        // The exiting Colibri no longer answers and lets go of the mutex a moment later.
        var result = InstanceElection.Run(_mutexName, () => { other.ReleaseAfter(TimeSpan.FromMilliseconds(300)); return null; }, LongWait);

        using var guard = result.Guard;
        Assert.Equal(InstanceRole.PrimaryAfterWaiting, result.Role);
        Assert.NotNull(guard);
    }

    [Fact]
    public void A_colibri_that_neither_answers_nor_exits_is_given_up_on()
    {
        using var other = OtherInstance.Start(_mutexName);

        var result = InstanceElection.Run(_mutexName, () => null, TimeSpan.FromMilliseconds(200));

        Assert.Equal(InstanceRole.NoAnswer, result.Role);
        Assert.Null(result.Guard);
    }

    /// <summary>Holds the mutex on a thread of its own, as another process would, until released or disposed.</summary>
    private sealed class OtherInstance : IDisposable
    {
        private readonly ManualResetEventSlim _acquired = new();
        private readonly ManualResetEventSlim _release = new();
        private readonly Thread _thread;
        private TimeSpan _delay;

        private OtherInstance(string mutexName)
        {
            _thread = new Thread(() =>
            {
                using (SingleInstanceGuard.TryAcquire(mutexName, TimeSpan.Zero) ?? throw new InvalidOperationException("The mutex was taken."))
                {
                    _acquired.Set();
                    _release.Wait();
                    Thread.Sleep(_delay);
                }
            });
        }

        public static OtherInstance Start(string mutexName)
        {
            var other = new OtherInstance(mutexName);
            other._thread.Start();
            Assert.True(other._acquired.Wait(LongWait));
            return other;
        }

        public void ReleaseAfter(TimeSpan delay)
        {
            _delay = delay;
            _release.Set();
        }

        public void Dispose()
        {
            _release.Set();
            _thread.Join();
            _acquired.Dispose();
            _release.Dispose();
        }
    }
}
