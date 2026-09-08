using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using FloatingTransferStation.Mac.Services;
using FloatingTransferStation.Models;
using FloatingTransferStation.Services;

namespace FloatingTransferStation.Mac;

public sealed partial class MainWindow : Window
{
    private static readonly IBrush Ink = Brush.Parse("#243447");
    private static readonly IBrush Accent = Brush.Parse("#327A72");
    private readonly BoardService _board = new();
    private readonly SelectionState _selection = new();
    private readonly BoardOperationGate _gate = new();
    private readonly TaskCompletionSource<bool> _initialized = new();
    private readonly LocalStore _store;
    private readonly BoardMutationService _mutations;
    private readonly TransferImportService _imports;
    private readonly AvaloniaTransferReader _reader = new();
    private readonly ClipboardMonitorService _monitor;
    private readonly string? _smokeDirectory;
    private readonly Task? _beforeLoad;
    private readonly Grid _shell = new() { ColumnDefinitions = new ColumnDefinitions("*,58") };
    private readonly Grid _panel = new() { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto") };
    private readonly StackPanel _rail = new() { Spacing = 6, Margin = new Thickness(4, 12) };
    private readonly TextBlock _title = new() { FontSize = 21, FontWeight = FontWeight.SemiBold, Foreground = Ink };
    private readonly TextBlock _count = new() { Foreground = Brushes.Gray, FontSize = 12 };
    private readonly TextBlock _status = new() { FontSize = 12, Foreground = Accent, TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _rename = new() { IsVisible = false, Watermark = "分类名称（最多6字）" };
    private readonly ListBox _list = new() { Background = Brushes.Transparent, BorderThickness = new Thickness(0), SelectionMode = SelectionMode.Multiple };
    private readonly Dictionary<BoardCategory, Button> _tabs = [];
    private readonly List<Button> _mutationButtons = [];
    private readonly DispatcherTimer _collapseTimer = new() { Interval = TimeSpan.FromMilliseconds(900) };
    private WindowSettings _settings = WindowSettings.Default;
    private BoardCategory _active = BoardCategory.Inbox;
    private BoardCategory _captureCategory = BoardCategory.Inbox;
    private bool _expanded = true;
    private bool _busy;
    private bool _closing;
    private bool _canClose;
    private bool _dragging;
    private bool _positioning;

    public MainWindow(AppPaths paths, string? smokeDirectory = null,
        IAtomicTextWriter? writer = null, Task? beforeLoad = null,
        IPasteboardStateReader? pasteboardStateReader = null)
    {
        _smokeDirectory = smokeDirectory;
        _beforeLoad = beforeLoad;
        _store = new LocalStore(paths, writer ?? new AtomicTextWriter());
        _mutations = new BoardMutationService(_board, _store, ShowStatus, _gate);
        _imports = new TransferImportService(new MacImageNormalizer(paths.ImagesDirectory), _board, _store, ShowStatus, _gate);
        _monitor = new ClipboardMonitorService(() => Clipboard, () => _captureCategory, _reader, _imports, ShowStatus, pasteboardStateReader);
        Title = ProductIdentity.DisplayName;
        Width = _settings.PanelWidth + WindowSettings.TabWidth;
        Height = _settings.WindowHeight;
        MinWidth = WindowSettings.TabWidth;
        MinHeight = 150;
        CanResize = false;
        Topmost = true;
        ShowInTaskbar = true;
        SystemDecorations = SystemDecorations.None;
        Background = Brush.Parse("#F3F5F2");
        Content = BuildContent();
        _shell.IsEnabled = false;
        Opened += OnOpened;
        Closing += OnClosing;
        PositionChanged += (_, _) =>
        {
            if (!_positioning && _initialized.Task.IsCompletedSuccessfully && _initialized.Task.Result) RememberTop();
        };
        PointerEntered += (_, _) => { _collapseTimer.Stop(); if (!_expanded) Expand(_captureCategory); };
        PointerExited += (_, _) => { if (_smokeDirectory is null) _collapseTimer.Start(); };
        _collapseTimer.Tick += (_, _) =>
        {
            _collapseTimer.Stop();
            if (!IsPointerOver && !_rename.IsVisible && !_dragging && !_busy) Collapse();
        };
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
    }

    private Control BuildContent()
    {
        _panel.Margin = new Thickness(16, 16, 12, 10);
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var heading = new StackPanel { Spacing = 4, Children = { _title, _count } };
        heading.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && !_rename.IsVisible) BeginMoveDrag(e);
        };
        heading.PointerReleased += (_, _) => { RememberTop(); DockToRight(); };
        header.Children.Add(heading);
        var quit = MakeButton("×", "退出悬浮中转站", async () => await CloseSafelyAsync());
        Grid.SetColumn(quit, 1);
        header.Children.Add(quit);
        _panel.Children.Add(header);

        var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, Margin = new Thickness(0, 14, 0, 10) };
        tools.Children.Add(MakeButton("粘贴", "手动收集剪贴板内容", async () => await CaptureAsync()));
        AddMutationButton(tools, "置顶", "批量置顶 / 取消置顶", PinSelectionAsync);
        var move = MakeButton("移动", "将选中内容移至其他分类", () => Task.CompletedTask);
        move.Click += (_, _) =>
        {
            var menu = new ContextMenu();
            foreach (var category in BoardCategoryCatalog.Ordered.Where(c => c != _active))
            {
                var item = new MenuItem { Header = _settings.CategoryName(category) };
                item.Click += async (_, _) => await RunMutationAsync(async () =>
                    await _mutations.MoveManyToCategoryTopAsync(_selection.Ids.ToArray(), category));
                menu.Items.Add(item);
            }
            menu.Open(move);
        };
        tools.Children.Add(move);
        _mutationButtons.Add(move);
        AddMutationButton(tools, "删除", "有选择时删除选中项，否则清空当前分类", () => DeleteAsync(true));
        Grid.SetRow(tools, 1);
        _panel.Children.Add(tools);
        _rename.KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Enter) { e.Handled = true; await CommitRenameAsync(); }
            else if (e.Key == Key.Escape) { e.Handled = true; CancelRename(); }
        };
        Grid.SetRow(_rename, 1);
        _panel.Children.Add(_rename);

        _list.ItemTemplate = new FuncDataTemplate<BoardItem>((item, _) => item is null ? new Border() : BuildCard(item));
        DragDrop.SetAllowDrop(_list, true);
        _list.AddHandler(DragDrop.DragOverEvent, OnDragOver);
        _list.AddHandler(DragDrop.DropEvent, OnDrop);
        Grid.SetRow(_list, 2);
        _panel.Children.Add(_list);
        var footer = new StackPanel
        {
            Spacing = 5,
            Margin = new Thickness(0, 10, 0, 0),
            Children = { _status, new TextBlock { Text = "拖入收集 · 拖出使用 · 内容仅保存在本机", FontSize = 10, Foreground = Brushes.Gray } }
        };
        Grid.SetRow(footer, 3);
        _panel.Children.Add(footer);
        _shell.Children.Add(_panel);

        foreach (var category in BoardCategoryCatalog.Ordered)
        {
            var tab = new Button { Width = 50, MinHeight = 72, Padding = new Thickness(3), HorizontalContentAlignment = HorizontalAlignment.Center };
            tab.Content = new TextBlock { Text = _settings.CategoryName(category), TextWrapping = TextWrapping.Wrap, MaxWidth = 42, TextAlignment = TextAlignment.Center, FontSize = 13 };
            tab.Click += (_, _) => { if (_rename.IsVisible) return; _captureCategory = category; Expand(category); };
            tab.DoubleTapped += (_, e) => { Expand(category); BeginRename(); e.Handled = true; };
            tab.PointerEntered += (_, _) => { _collapseTimer.Stop(); if (!_rename.IsVisible) Expand(category); };
            DragDrop.SetAllowDrop(tab, true);
            tab.AddHandler(DragDrop.DragOverEvent, (_, e) => { Expand(category); OnDragOver(tab, e); });
            tab.AddHandler(DragDrop.DropEvent, async (_, e) => await DropAsync(e, category, 0));
            _tabs.Add(category, tab);
            _rail.Children.Add(tab);
        }
        Grid.SetColumn(_rail, 1);
        _shell.Children.Add(_rail);
        return new Border { BorderBrush = Brush.Parse("#D8DEDA"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12, 0, 0, 12), Child = _shell };
    }

    private Control BuildCard(BoardItem item)
    {
        var card = new Border { Background = Brushes.White, CornerRadius = new CornerRadius(9), Padding = new Thickness(10), Margin = new Thickness(0, 3), HorizontalAlignment = HorizontalAlignment.Stretch };
        var layout = new StackPanel { Spacing = 8 };
        var controls = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var select = new Button { Content = "选择", FontSize = 11, Padding = new Thickness(7, 2), HorizontalAlignment = HorizontalAlignment.Left };
        select.Click += (_, _) => SelectItem(item, true, false);
        controls.Children.Add(select);
        var pin = MakeButton(item.IsPinned ? "● 已置顶" : "○ 置顶", "切换此项置顶状态",
            () => RunMutationAsync(async () => await _mutations.SetPinnedAsync([item.Id], !item.IsPinned)));
        pin.FontSize = 11;
        pin.Padding = new Thickness(7, 2);
        Grid.SetColumn(pin, 1);
        controls.Children.Add(pin);
        pin.Bind(ContentControl.ContentProperty, new Avalonia.Data.Binding(nameof(BoardItem.IsPinned))
        {
            Source = item,
            Converter = new Avalonia.Data.Converters.FuncValueConverter<bool, string>(value => value ? "● 已置顶" : "○ 置顶")
        });
        layout.Children.Add(controls);
        if (item.Kind == BoardItemKind.Image && item.ImageAbsolutePath is { } path)
        {
            layout.Children.Add(new ManagedThumbnail(_store.ImagesDirectory, path));
        }
        else
        {
            layout.Children.Add(new TextBlock { Text = item.Text, TextWrapping = TextWrapping.Wrap, MaxHeight = 230, Foreground = Ink, FontSize = 14 });
        }
        card.Child = layout;
        WireCardDrag(card, item);
        return card;
    }

    private static Button MakeButton(string text, string tip, Func<Task> action)
    {
        var button = new Button { Content = text, Padding = new Thickness(9, 6), FontSize = 12 };
        ToolTip.SetTip(button, tip);
        button.Click += async (_, _) => await action();
        return button;
    }

    private void AddMutationButton(StackPanel parent, string text, string tip, Func<Task> action)
    {
        var button = MakeButton(text, tip, action);
        _mutationButtons.Add(button);
        parent.Children.Add(button);
    }

    private void SelectItem(BoardItem item, bool toggle, bool range)
    {
        _selection.Select(item.Id, _board.Items(_active).Select(i => i.Id).ToArray(), toggle, range);
        SyncSelection();
    }

    private void SyncSelection()
    {
        _selection.Prune(_board.Items(_active).Select(i => i.Id));
        _list.SelectedItems?.Clear();
        foreach (var item in _board.Items(_active).Where(i => _selection.Ids.Contains(i.Id))) _list.SelectedItems?.Add(item);
        _count.Text = $"{_board.Items(_active).Count} 项内容" + (_selection.Ids.Count > 0 ? $" · 已选 {_selection.Ids.Count} 项" : "");
    }

    private void Expand(BoardCategory category)
    {
        if (_closing) return;
        if (_active != category) _selection.Clear();
        _active = category;
        _expanded = true;
        _panel.IsVisible = true;
        Width = _settings.PanelWidth + WindowSettings.TabWidth;
        Height = _settings.WindowHeight;
        _list.ItemsSource = _board.Items(category);
        _title.Text = _settings.CategoryName(category);
        UpdateTabs();
        SyncSelection();
        DockToRight();
    }

    private void Collapse()
    {
        if (_rename.IsVisible || _busy || _dragging || _closing) return;
        _expanded = false;
        _panel.IsVisible = false;
        Width = WindowSettings.TabWidth;
        Height = 160;
        UpdateTabs();
        DockToRight();
    }

    private void UpdateTabs()
    {
        foreach (var (category, tab) in _tabs)
        {
            tab.IsVisible = _expanded || category == _captureCategory;
            tab.Background = category == _captureCategory ? Accent : Brush.Parse("#E6EBE7");
            tab.Foreground = category == _captureCategory ? Brushes.White : Ink;
            if (tab.Content is TextBlock label) label.Text = _settings.CategoryName(category);
        }
    }

    private void DockToRight()
    {
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen is null) return;
        var work = screen.WorkingArea;
        var scale = screen.Scaling;
        _settings = _settings.Normalize(work.Width / scale, work.Height / scale);
        Width = _expanded ? _settings.PanelWidth + WindowSettings.TabWidth : WindowSettings.TabWidth;
        Height = _expanded ? _settings.WindowHeight : Math.Min(160, work.Height / scale);
        _positioning = true;
        try
        {
            Position = new PixelPoint(work.Right - (int)Math.Round(Width * scale),
                work.Y + (int)Math.Round(_settings.Top * scale));
        }
        finally { _positioning = false; }
    }

    private void RememberTop()
    {
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen is not null) _settings = _settings with { Top = (Position.Y - screen.WorkingArea.Y) / screen.Scaling };
    }

    private void ShowStatus(string message) => _status.Text = message;
}
