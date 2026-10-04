using Colibri.Core.Models;
using Colibri.Core.Services;

namespace Colibri.Core.Tests.Services;

public class DownloadStateMachineTests
{
    private static readonly HashSet<(DownloadState From, DownloadState To)> ExpectedAllowed =
    [
        (DownloadState.Queued, DownloadState.Active),
        (DownloadState.Queued, DownloadState.Paused),
        (DownloadState.Queued, DownloadState.Failed),
        (DownloadState.Queued, DownloadState.Completed),
        (DownloadState.Active, DownloadState.Paused),
        (DownloadState.Active, DownloadState.Completed),
        (DownloadState.Active, DownloadState.Failed),
        (DownloadState.Active, DownloadState.Queued),
        (DownloadState.Paused, DownloadState.Queued),
        (DownloadState.Paused, DownloadState.Active),
        (DownloadState.Paused, DownloadState.Failed),
        (DownloadState.Paused, DownloadState.Completed),
        (DownloadState.Failed, DownloadState.Queued),
        (DownloadState.Failed, DownloadState.Active),
        (DownloadState.Failed, DownloadState.Completed),
    ];

    /// <summary>Every (from, to) pair with whether it should be allowed.</summary>
    public static TheoryData<DownloadState, DownloadState, bool> AllPairs()
    {
        var data = new TheoryData<DownloadState, DownloadState, bool>();
        foreach (var from in Enum.GetValues<DownloadState>())
        {
            foreach (var to in Enum.GetValues<DownloadState>())
            {
                data.Add(from, to, from == to || ExpectedAllowed.Contains((from, to)));
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(AllPairs))]
    public void CanTransition_matches_the_rules(DownloadState from, DownloadState to, bool expected)
    {
        Assert.Equal(expected, DownloadStateMachine.CanTransition(from, to));
    }

    [Theory]
    [MemberData(nameof(AllPairs))]
    public void EnsureTransition_throws_only_for_forbidden_changes(DownloadState from, DownloadState to, bool allowed)
    {
        var exception = Record.Exception(() => DownloadStateMachine.EnsureTransition(from, to));

        if (allowed)
        {
            Assert.Null(exception);
        }
        else
        {
            Assert.IsType<InvalidOperationException>(exception);
        }
    }

    [Theory]
    [InlineData(DownloadState.Queued)]
    [InlineData(DownloadState.Active)]
    [InlineData(DownloadState.Paused)]
    [InlineData(DownloadState.Failed)]
    public void Completed_is_terminal(DownloadState to)
    {
        Assert.False(DownloadStateMachine.CanTransition(DownloadState.Completed, to));
    }

    [Fact]
    public void Failed_can_be_retried_or_restored_by_the_engine()
    {
        Assert.True(DownloadStateMachine.CanTransition(DownloadState.Failed, DownloadState.Queued));
        Assert.True(DownloadStateMachine.CanTransition(DownloadState.Failed, DownloadState.Active));
        Assert.False(DownloadStateMachine.CanTransition(DownloadState.Failed, DownloadState.Paused));
        Assert.True(DownloadStateMachine.CanTransition(DownloadState.Failed, DownloadState.Completed));
    }

    [Fact]
    public void Paused_download_can_complete_when_pause_races_completion()
    {
        Assert.True(DownloadStateMachine.CanTransition(DownloadState.Paused, DownloadState.Completed));
    }
}
