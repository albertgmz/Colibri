using System.ComponentModel;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Colibri.App.Resources;
using Colibri.App.ViewModels;
using Colibri.Core.Settings;

namespace Colibri.App.Views;

public partial class MainWindow
{
    private readonly DispatcherTimer _layoutSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly Dictionary<string, bool> _columnVisibility = new();
    private readonly Dictionary<string, DataGridLength> _defaultColumnWidths = new();
    private bool _applyingLayout;
    private bool _layoutReady;

    private void InitializeLayout()
    {
        SettingsContent.LayoutRequested += OnLayoutMenuClick;
        WorkspaceGrid.ColumnDefinitions[0].MinWidth = 120;
        WorkspaceGrid.ColumnDefinitions[0].MaxWidth = 400;
        foreach (var column in DownloadsGrid.Columns) _defaultColumnWidths[column.SortMemberPath] = column.Width;
        DragDrop.SetAllowDrop(this, true);
        DragDrop.AddDragOverHandler(this, (sender, e) =>
        {
            e.DragEffects = Colibri.Core.Services.UrlPolicy.TryValidate(e.DataTransfer.TryGetText()?.Trim(), out _, out string? _)
                ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        });
        DragDrop.AddDropHandler(this, (_, e) =>
        {
            e.Handled = ViewModel?.ShowAddUrlForText(e.DataTransfer.TryGetText()) == true;
        });
        _layoutSaveTimer.Tick += async (_, _) =>
        {
            _layoutSaveTimer.Stop();
            await PersistLayoutAsync();
        };
        SizeChanged += (_, _) => { UpdateResponsiveColumns(); ScheduleLayoutSave(); };
        PositionChanged += (_, _) => ScheduleLayoutSave();
        AddHandler(PointerReleasedEvent, (_, _) => ScheduleLayoutSave(), RoutingStrategies.Bubble, true);
        Opened += (_, _) =>
        {
            RestorePosition();
            foreach (var button in this.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("command")))
                ToolTip.SetTip(button, AutomationProperties.GetName(button));
        };
        SidebarSplitter.DragDelta += (_, _) => Dispatcher.UIThread.Post(UpdateResponsiveColumns);
        Closed += (_, _) => _layoutSaveTimer.Stop();
        DownloadsGrid.AddHandler(Control.ContextRequestedEvent, OnTableContextRequested, RoutingStrategies.Tunnel);
        HeaderGrip.DragDelta += (_, e) =>
        {
            DownloadsGrid.ColumnHeaderHeight = Math.Clamp(DownloadsGrid.ColumnHeaderHeight + e.Vector.Y, 24, 72);
            HeaderGrip.Margin = new Thickness(0, DownloadsGrid.ColumnHeaderHeight - 2, 0, 0);
            ScheduleLayoutSave();
        };
    }

    protected override async void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || ViewModel is not { IsSettingsOpen: false, IsDeleteConfirmationOpen: false } vm) return;
        var editing = e.Source is Visual visual && visual.GetVisualAncestors().Prepend(visual).OfType<TextBox>().Any();
        if (e.KeyModifiers == KeyModifiers.Control && e.Key == Key.F)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
        else if (!editing && e.KeyModifiers == KeyModifiers.Control && e.Key == Key.V)
        {
            e.Handled = true;
            await vm.PasteUrlAsync();
        }
        else if (!editing && e.KeyModifiers == KeyModifiers.Control && e.Key == Key.A)
        {
            DownloadsGrid.SelectAll();
            e.Handled = true;
        }
        else if (!editing && e.KeyModifiers == KeyModifiers.None && e.Key == Key.Delete && vm.DeleteCommand.CanExecute(null))
        {
            vm.DeleteCommand.Execute(null);
            e.Handled = true;
        }
        else if (!editing && e.KeyModifiers == KeyModifiers.None && e.Key == Key.Space && DownloadsGrid.IsKeyboardFocusWithin)
        {
            if (vm.PauseCommand.CanExecute(null)) await vm.PauseCommand.ExecuteAsync(null);
            else if (vm.ResumeCommand.CanExecute(null)) await vm.ResumeCommand.ExecuteAsync(null);
            e.Handled = true;
        }
    }

    private void ApplyLayout(WindowLayout layout)
    {
        _applyingLayout = true;
        layout.Normalize();
        Width = layout.Width;
        Height = layout.Height;
        _detailsHeight = new GridLength(layout.DetailsHeight);
        ContentGrid.RowDefinitions[DetailsRow].Height = new GridLength(ViewModel?.SelectedDetail is null ? 24 : layout.DetailsHeight);
        WorkspaceGrid.ColumnDefinitions[0].MinWidth = layout.SidebarCollapsed ? 58 : 120;
        WorkspaceGrid.ColumnDefinitions[0].Width = new GridLength(layout.SidebarCollapsed ? 58 : layout.SidebarWidth);
        SidebarSplitter.IsVisible = !layout.SidebarCollapsed;
        Classes.Set("sidebarCollapsed", layout.SidebarCollapsed);
        Classes.Set("iconsOnly", layout.Toolbar != ToolbarMode.Labels);
        Classes.Set("smallIcons", layout.Toolbar == ToolbarMode.SmallIcons);
        DownloadsGrid.RowHeight = layout.Density == LayoutDensity.Compact ? 28 : 36;
        DownloadsGrid.ColumnHeaderHeight = layout.HeaderHeight;
        HeaderGrip.Margin = new Thickness(0, layout.HeaderHeight - 2, 0, 0);
        _columnVisibility.Clear();
        foreach (var column in DownloadsGrid.Columns)
        {
            var saved = layout.Columns.FirstOrDefault(c => c.Key == column.SortMemberPath);
            _columnVisibility[column.SortMemberPath] = column.SortMemberPath == "FileName" || (saved?.Visible ?? DefaultColumnVisible(column.SortMemberPath));
            column.Width = saved is null ? _defaultColumnWidths[column.SortMemberPath]
                : new DataGridLength(saved.Width, saved.Star ? DataGridLengthUnitType.Star : DataGridLengthUnitType.Pixel);
        }
        // Setting DisplayIndex moves neighbouring columns; apply the full desired order once.
        var ordered = DownloadsGrid.Columns.OrderBy(c =>
            layout.Columns.FirstOrDefault(s => s.Key == c.SortMemberPath)?.Order ?? 64 + DownloadsGrid.Columns.IndexOf(c)).ToList();
        for (var i = 0; i < ordered.Count; i++) ordered[i].DisplayIndex = i;
        if (ViewModel is { } vm)
        {
            vm.Downloads.SortDescriptions.Clear();
            foreach (var sort in layout.Sort.Where(s => DownloadsGrid.Columns.Any(c => c.SortMemberPath == s.Key)))
                vm.Downloads.SortDescriptions.Add(DataGridSortDescription.FromPath(sort.Key,
                    sort.Descending ? ListSortDirection.Descending : ListSortDirection.Ascending));
            if (vm.Downloads.SortDescriptions.Count == 0)
                vm.Downloads.SortDescriptions.Add(DataGridSortDescription.FromPath(nameof(DownloadItemViewModel.AddedAt), ListSortDirection.Descending));
        }
        _layoutReady = true;
        UpdateResponsiveColumns();
        _applyingLayout = false;
    }

    private static bool DefaultColumnVisible(string key) => key is "FileName" or "TotalBytes" or "ProgressValue" or "Speed" or "SecondsLeft" or "AddedAt";

    private void RestorePosition()
    {
        if (ViewModel?.Layout is not { } layout) return;
        // A disconnected monitor must not strand the title bar outside the desktop.
        if (layout.X is { } x && layout.Y is { } y && Screens.All.Any(s => s.WorkingArea.Contains(new PixelPoint(x + 80, y + 16))))
            Position = new PixelPoint(x, y);
        if (layout.Maximized) WindowState = WindowState.Maximized;
    }

    private void UpdateResponsiveColumns()
    {
        if (!_layoutReady) return;
        // Labels need a second row at minimum width; retaining the mode keeps every command reachable.
        var searchBelow = Bounds.Width < 760 && ViewModel?.Layout.Toolbar == ToolbarMode.Labels;
        Grid.SetRow(SearchBox, searchBelow ? 1 : 0);
        Grid.SetColumn(SearchBox, searchBelow ? 0 : 1);
        Grid.SetColumnSpan(SearchBox, searchBelow ? 2 : 1);
        SearchBox.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right;
        SearchBox.Margin = new Thickness(0, searchBelow ? 4 : 0, 0, 0);
        NetworkStatus.IsVisible = Bounds.Width >= 900;
        FreeSpaceStatus.IsVisible = Bounds.Width >= 740;
        var available = Bounds.Width - WorkspaceGrid.ColumnDefinitions[0].Width.Value;
        foreach (var column in DownloadsGrid.Columns)
        {
            var threshold = column.SortMemberPath switch
            {
                "AddedAt" => 880, "SecondsLeft" => 720, "Speed" => 540, "TotalBytes" => 440, _ => 0,
            };
            column.IsVisible = _columnVisibility.GetValueOrDefault(column.SortMemberPath, true) && available >= threshold;
        }
    }

    private void ScheduleLayoutSave()
    {
        if (!_layoutReady || _applyingLayout) return;
        _layoutSaveTimer.Stop();
        _layoutSaveTimer.Start();
    }

    internal Task PersistLayoutAsync()
    {
        if (!_layoutReady || _applyingLayout || ViewModel is not { } vm) return Task.CompletedTask;
        var layout = vm.Layout;
        if (WindowState == WindowState.Normal)
        {
            layout.Width = Width;
            layout.Height = Height;
            layout.X = Position.X;
            layout.Y = Position.Y;
        }
        if (WindowState != WindowState.Minimized) layout.Maximized = WindowState == WindowState.Maximized;
        if (!layout.SidebarCollapsed) layout.SidebarWidth = WorkspaceGrid.ColumnDefinitions[0].Width.Value;
        var detailsHeight = ContentGrid.RowDefinitions[DetailsRow].Height.Value;
        if (vm.SelectedDetail is not null && vm.IsDetailsVisible && detailsHeight > 24)
            _detailsHeight = new GridLength(detailsHeight);
        layout.DetailsHeight = _detailsHeight.Value;
        layout.HeaderHeight = DownloadsGrid.ColumnHeaderHeight;
        layout.Columns = DownloadsGrid.Columns.Select(c => new ColumnLayout
        {
            Key = c.SortMemberPath, Width = c.Width.Value, Star = c.Width.IsStar,
            Order = c.DisplayIndex, Visible = _columnVisibility.GetValueOrDefault(c.SortMemberPath, true),
        }).ToList();
        layout.Sort = vm.Downloads.SortDescriptions.Select(s => new ColumnSort
        {
            Key = s.PropertyPath, Descending = s.Direction == ListSortDirection.Descending,
        }).ToList();
        return vm.SaveLayoutAsync(layout);
    }

    internal Task FlushLayoutAsync()
    {
        _layoutSaveTimer.Stop();
        return PersistLayoutAsync();
    }

    private void OnTableContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (e.Source is not Visual visual || !visual.GetVisualAncestors().Prepend(visual).OfType<DataGridColumnHeader>().Any()) return;
        var menu = new ContextMenu();
        foreach (var column in DownloadsGrid.Columns)
        {
            var entry = new MenuItem { Header = column.Header, Icon = new TextBlock { Text = _columnVisibility[column.SortMemberPath] ? "✓" : "" } };
            entry.IsEnabled = column.SortMemberPath != "FileName";
            entry.Click += (_, _) =>
            {
                _columnVisibility[column.SortMemberPath] = !_columnVisibility[column.SortMemberPath];
                UpdateResponsiveColumns();
                ScheduleLayoutSave();
            };
            menu.Items.Add(entry);
        }
        menu.Open(DownloadsGrid);
        e.Handled = true;
    }

    private void OnLayoutMenuClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm || sender is not Control button) return;
        var menu = new ContextMenu();
        AddChoice(Strings.LayoutSidebar, vm.Layout.SidebarCollapsed, () => vm.Layout.SidebarCollapsed = !vm.Layout.SidebarCollapsed);
        menu.Items.Add(new Separator());
        AddChoice(Strings.LayoutCompact, vm.Layout.Density == LayoutDensity.Compact, () => vm.Layout.Density = LayoutDensity.Compact);
        AddChoice(Strings.LayoutComfortable, vm.Layout.Density == LayoutDensity.Comfortable, () => vm.Layout.Density = LayoutDensity.Comfortable);
        menu.Items.Add(new Separator());
        AddChoice(Strings.LayoutLabels, vm.Layout.Toolbar == ToolbarMode.Labels, () => vm.Layout.Toolbar = ToolbarMode.Labels);
        AddChoice(Strings.LayoutIcons, vm.Layout.Toolbar == ToolbarMode.Icons, () => vm.Layout.Toolbar = ToolbarMode.Icons);
        AddChoice(Strings.LayoutSmallIcons, vm.Layout.Toolbar == ToolbarMode.SmallIcons, () => vm.Layout.Toolbar = ToolbarMode.SmallIcons);
        menu.Items.Add(new Separator());
        var reset = new MenuItem { Header = Strings.LayoutReset };
        reset.Click += async (_, _) =>
        {
            _layoutSaveTimer.Stop();
            var defaults = new WindowLayout();
            var save = vm.SaveLayoutAsync(defaults);
            vm.IsDetailsVisible = true;
            WindowState = WindowState.Normal;
            ApplyLayout(defaults);
            UpdateDetailsRow(vm.IsDetailsVisible);
            await save;
            ScheduleLayoutSave();
        };
        menu.Items.Add(reset);
        button.ContextMenu = menu;
        menu.Open(button);

        void AddChoice(string text, bool selected, Action change)
        {
            var entry = new MenuItem { Header = text, Icon = new TextBlock { Text = selected ? "✓" : "" } };
            entry.Click += async (_, _) =>
            {
                await PersistLayoutAsync();
                change();
                ApplyLayout(vm.Layout);
                UpdateDetailsRow(vm.IsDetailsVisible);
                ScheduleLayoutSave();
            };
            menu.Items.Add(entry);
        }
    }
}
