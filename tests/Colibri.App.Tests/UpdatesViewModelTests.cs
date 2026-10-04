using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.Controls;
using Colibri.App.Views;
using Colibri.App.Resources;
using Colibri.App.ViewModels;
using Colibri.Core.Settings;
using Colibri.Core.Updates;
using Microsoft.Extensions.Logging.Abstractions;

namespace Colibri.App.Tests;

public class UpdatesViewModelTests
{
    [AvaloniaFact]
    public async Task General_card_is_reachable_at_minimum_width_and_portable_has_no_install_action()
    {
        await using var ui = await UiHarness.StartAsync();
        var updates = new UpdatesViewModel(new FakeUpdates(), ui.Settings, ui.SettingsStore, ui.Shell, NullLogger<UpdatesViewModel>.Instance, TimeProvider.System);
        var settings = new SettingsViewModel(ui.Settings, ui.SettingsStore, ui.Manager, ui.Autostart, ui.Shell, ui.Paths,
            NullLogger<SettingsViewModel>.Instance, updates: updates);
        await settings.LoadAsync();
        var view = new SettingsView { DataContext = settings };
        var window = new Window { Content = view, Width = 640, Height = 400 };
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            var check = view.FindControl<Button>("CheckUpdateButton")!;
            Assert.True(check.IsEffectivelyVisible); Assert.True(check.Focus());
            Assert.False(view.FindControl<Button>("InstallUpdateButton")!.IsEffectivelyVisible);
            await updates.CheckNowCommand.ExecuteAsync(null);
            Assert.True(updates.HasUpdate); Assert.True(updates.CanDownload);
        }
        finally { window.Close(); await updates.StopAsync(); }
    }
    [AvaloniaFact]
    public async Task Startup_and_hourly_checks_share_manual_operation_and_stop_with_lifetime()
    {
        var service = new FakeUpdates(); var clock = new ControlledClock();
        var settings = new AppSettings(); var store = new FakeSettingsStore();
        var vm = new UpdatesViewModel(service, settings, store, new FakeShell(), NullLogger<UpdatesViewModel>.Instance, clock);
        vm.Start(); vm.Start();
        await PumpUntilAsync(() => service.Checks == 1 && !vm.IsBusy && clock.HasTimer);
        clock.Advance(TimeSpan.FromMinutes(59));
        Dispatcher.UIThread.RunJobs(); Assert.Equal(1, service.Checks);
        clock.Advance(TimeSpan.FromMinutes(1));
        await PumpUntilAsync(() => service.Checks == 2 && !vm.IsBusy && clock.HasTimer);
        vm.AutoCheckUpdates = false;
        Assert.False(store.Saved!.AutoCheckUpdates);
        clock.Advance(TimeSpan.FromHours(1));
        await PumpUntilAsync(() => clock.HasTimer);
        Assert.Equal(2, service.Checks);
        await vm.CheckNowCommand.ExecuteAsync(null);
        Assert.Equal(3, service.Checks);
        await vm.StopAsync();
        clock.Advance(TimeSpan.FromHours(2));
        Dispatcher.UIThread.RunJobs(); Assert.Equal(3, service.Checks);
    }

    [AvaloniaFact]
    public async Task Verified_download_can_be_revealed_and_handoff_failure_does_not_request_exit()
    {
        var service = new FakeUpdates { Kind = UpdatePackageKind.Installer };
        var shell = new FakeShell(); var vm = new UpdatesViewModel(service, new(), new FakeSettingsStore(), shell,
            NullLogger<UpdatesViewModel>.Instance, TimeProvider.System);
        var exits = 0; vm.InstallRequested += () => exits++;
        await vm.CheckNowCommand.ExecuteAsync(null);
        Assert.True(vm.CanDownload);
        await vm.DownloadCommand.ExecuteAsync(null);
        Assert.True(vm.CanReveal); Assert.True(vm.CanInstall);
        await vm.CheckNowCommand.ExecuteAsync(null);
        Assert.Equal(Strings.UpdateReady, vm.Status);
        await vm.RevealCommand.ExecuteAsync(null);
        Assert.Equal(new[] { "fixture-update.exe" }, shell.Revealed);
        service.FailInstall = true;
        await vm.InstallCommand.ExecuteAsync(null);
        Assert.Equal(0, exits); Assert.Equal(Strings.UpdateInstallFailed, vm.Status);
        service.FailInstall = false;
        await vm.InstallCommand.ExecuteAsync(null);
        Assert.Equal(1, exits);
        await vm.StopAsync();
    }

    [AvaloniaFact]
    public async Task Unavailable_feed_is_not_reported_as_current()
    {
        var service = new FakeUpdates { Unavailable = true };
        var vm = new UpdatesViewModel(service, new(), new FakeSettingsStore(), new FakeShell(), NullLogger<UpdatesViewModel>.Instance, TimeProvider.System);
        await vm.CheckNowCommand.ExecuteAsync(null);
        Assert.Equal(Strings.UpdateFeedUnavailable, vm.Status);
        Assert.False(vm.HasUpdate); Assert.False(vm.CanDownload);
        await vm.StopAsync();
    }

    [AvaloniaFact]
    public async Task Manual_check_during_startup_reuses_the_active_operation()
    {
        var service = new FakeUpdates { CheckGate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var vm = new UpdatesViewModel(service, new(), new FakeSettingsStore(), new FakeShell(), NullLogger<UpdatesViewModel>.Instance, TimeProvider.System);
        vm.Start();
        await PumpUntilAsync(() => vm.IsBusy && service.Checks == 1);
        await vm.CheckNowCommand.ExecuteAsync(null);
        Assert.Equal(1, service.Checks);
        service.CheckGate.SetResult(null);
        await PumpUntilAsync(() => !vm.IsBusy);
        await vm.StopAsync();
    }

    private static async Task PumpUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++) { Dispatcher.UIThread.RunJobs(); await Task.Delay(5, TestContext.Current.CancellationToken); }
        Assert.True(condition());
    }

    private sealed class FakeUpdates : IReleaseUpdateService
    {
        public int Checks; public bool FailInstall; public bool Unavailable;
        public TaskCompletionSource<ReleaseUpdate?>? CheckGate;
        public UpdatePackageKind Kind = UpdatePackageKind.Portable;
        public string CurrentVersion => "2.0.0";
        public UpdatePackageKind PackageKind => Kind;
        private readonly ReleasePackage _package = new("fixture-update.exe", new Uri("https://github.com/albertgmz/Colibri"), 1, new string('a', 64));
        public Task<ReleaseUpdate?> CheckAsync(CancellationToken cancellationToken)
        {
            Checks++; if (Unavailable) throw new UpdateFeedUnavailableException("fixture");
            if (CheckGate is not null) return CheckGate.Task.WaitAsync(cancellationToken);
            return Task.FromResult<ReleaseUpdate?>(new("2.0.9", _package, _package));
        }
        public Task<VerifiedUpdatePackage> DownloadAsync(ReleaseUpdate update, CancellationToken cancellationToken) => Task.FromResult(new VerifiedUpdatePackage("fixture-update.exe", _package));
        public Task PrepareInstallAfterExitAsync(VerifiedUpdatePackage package, CancellationToken cancellationToken) => FailInstall ? Task.FromException(new IOException("fixture failure")) : Task.CompletedTask;
    }

    private sealed class ControlledClock : TimeProvider
    {
        private readonly List<ControlledTimer> _timers = [];
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public bool HasTimer => _timers.Any(timer => !timer.Disposed && timer.Due != DateTimeOffset.MaxValue);
        public override DateTimeOffset GetUtcNow() => _now;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ControlledTimer(this, callback, state); _timers.Add(timer); timer.Change(dueTime, period); return timer;
        }
        public void Advance(TimeSpan amount)
        {
            _now += amount;
            foreach (var timer in _timers.ToArray()) if (!timer.Disposed && timer.Due <= _now) { timer.Due = DateTimeOffset.MaxValue; timer.Callback(timer.State); }
        }
        private sealed class ControlledTimer(ControlledClock owner, TimerCallback callback, object? state) : ITimer
        {
            public readonly TimerCallback Callback = callback; public readonly object? State = state;
            public DateTimeOffset Due = DateTimeOffset.MaxValue; public bool Disposed;
            public bool Change(TimeSpan dueTime, TimeSpan period) { Due = dueTime == Timeout.InfiniteTimeSpan ? DateTimeOffset.MaxValue : owner._now + dueTime; return !Disposed; }
            public void Dispose() => Disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
