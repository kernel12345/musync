using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Wpf.Ui.Controls;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;

namespace MuSync;

/// <summary>挂游戏时长页：读取 Steam 库存、勾选游戏、开始/停止挂时长。</summary>
public partial class GameIdlePage : Page
{
    private readonly ObservableCollection<GameListItem> _viewItems = new();
    private readonly List<GameListItem> _allItems = new();
    private readonly DispatcherTimer _elapsedTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private DateTime? _idleStartedUtc;
    private bool _loadedOnce;

    public GameIdlePage()
    {
        InitializeComponent();
        GamesListBox.ItemsSource = _viewItems;
        IsVisibleChanged += OnIsVisibleChanged;
        _elapsedTimer.Tick += (_, _) => UpdateElapsedText();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_loadedOnce) return;
        _loadedOnce = true;
        await EnsureListAsync(forceRefresh: false, auto: true);
        SyncIdleState();
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true) SyncIdleState();
    }

    /// <summary>载入库存：优先本地缓存；auto 模式下无缓存且已登录时自动在线拉取。</summary>
    private async Task EnsureListAsync(bool forceRefresh, bool auto)
    {
        var session = AppServices.Session;
        if (forceRefresh && (session == null || !session.IsLoggedOn))
        {
            MessageBox.Show("需要先登录 Steam 才能读取库存。", "挂游戏时长",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (session == null) return;

        SetLoading(true);
        try
        {
            var progress = new Progress<string>(msg => ProgressText.Text = msg);
            var games = await Task.Run(() =>
                new SteamLibraryResolver(session).GetOwnedGamesAsync(forceRefresh, progress));
            ApplyGames(games);
        }
        catch (Exception ex)
        {
            if (forceRefresh || !auto)
            {
                MessageBox.Show($"读取库存失败：{ex.Message}", "挂游戏时长",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        finally
        {
            SetLoading(false);
        }
    }

    private void ApplyGames(IReadOnlyList<OwnedGame> games)
    {
        _allItems.Clear();
        var activeIds = AppServices.Session?.IdleAppIds ?? new List<uint>();
        foreach (var game in games)
        {
            _allItems.Add(new GameListItem(game) { IsSelected = activeIds.Contains(game.AppId) });
        }
        ApplyFilter();
        ProgressText.Text = $"共 {_allItems.Count} 个游戏";
        UpdateSelectionCount();
    }

    private void ApplyFilter()
    {
        var keyword = SearchBox.Text.Trim();
        IEnumerable<GameListItem> filtered = _allItems;
        if (keyword.Length > 0)
        {
            filtered = _allItems.Where(i =>
                i.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                i.AppId.ToString().Contains(keyword));
        }
        _viewItems.Clear();
        foreach (var item in filtered) _viewItems.Add(item);
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loadedOnce) ApplyFilter();
    }

    private void GameCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        // Steam 并发游玩上限 30：超出时回退本次勾选
        if (sender is CheckBox { DataContext: GameListItem item, IsChecked: true } &&
            _allItems.Count(x => x.IsSelected) > SteamSessionManager.MaxIdleGames)
        {
            item.IsSelected = false;
            ProgressText.Text = $"最多同时挂 {SteamSessionManager.MaxIdleGames} 个游戏";
        }
        UpdateSelectionCount();
    }

    private async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        var session = AppServices.Session;
        if (session == null || !session.IsLoggedOn)
        {
            MessageBox.Show("Steam 未登录，无法开始挂时长。", "挂游戏时长",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var selected = _allItems.Where(i => i.IsSelected).Select(i => i.AppId).ToList();
        if (selected.Count == 0)
        {
            ProgressText.Text = "请先勾选至少一个游戏";
            return;
        }
        StartButton.IsEnabled = false;
        try
        {
            await session.SetIdleGamesAsync(selected);
            _idleStartedUtc = DateTime.UtcNow;
            ProgressText.Text = $"正在挂 {selected.Count} 个游戏";
        }
        finally
        {
            StartButton.IsEnabled = true;
        }
        SyncIdleState();
    }

    private async void StopButton_Click(object sender, RoutedEventArgs e)
    {
        var session = AppServices.Session;
        if (session == null) return;
        await session.SetIdleGamesAsync(Array.Empty<uint>());
        _idleStartedUtc = null;
        SyncIdleState();
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        await EnsureListAsync(forceRefresh: true, auto: false);
        SyncIdleState();
    }

    private void SyncIdleState()
    {
        var session = AppServices.Session;
        var isIdling = session?.IsIdling == true;
        StartButton.Visibility = isIdling ? Visibility.Collapsed : Visibility.Visible;
        StopButton.Visibility = isIdling ? Visibility.Visible : Visibility.Collapsed;
        GamesListBox.IsEnabled = !isIdling;
        SearchBox.IsEnabled = !isIdling;
        RefreshButton.IsEnabled = session?.IsLoggedOn == true;
        if (isIdling)
        {
            var count = session?.IdleAppIds.Count ?? 0;
            IdleStateText.Text = $"正在挂时长（{count} 个游戏）";
            if (_idleStartedUtc == null) _idleStartedUtc = DateTime.UtcNow;
            _elapsedTimer.Start();
        }
        else
        {
            IdleStateText.Text = session?.IsLoggedOn == true ? "未开始" : "Steam 未登录";
            _elapsedTimer.Stop();
            IdleElapsedText.Text = "";
        }
        UpdateElapsedText();
        UpdateSelectionCount();
    }

    private void UpdateElapsedText()
    {
        if (_idleStartedUtc is { } start)
        {
            IdleElapsedText.Text = $"本次已挂 {DateTime.UtcNow - start:hh\\:mm\\:ss}";
        }
    }

    private void UpdateSelectionCount()
    {
        var selected = _allItems.Count(x => x.IsSelected);
        SelectionCountText.Text = $"已选 {selected}/{SteamSessionManager.MaxIdleGames}";
    }

    private void SetLoading(bool loading)
    {
        LoadingRing.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
        RefreshButton.IsEnabled = !loading;
    }

    private sealed class GameListItem(OwnedGame game) : INotifyPropertyChanged
    {
        private bool _isSelected;

        public OwnedGame Game { get; } = game;
        public uint AppId => Game.AppId;
        public string Name => Game.Name;
        public string AppIdText => $"({Game.AppId})";

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected == value) return;
                _isSelected = value;
                OnPropertyChanged();
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
