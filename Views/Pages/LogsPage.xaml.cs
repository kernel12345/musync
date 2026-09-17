using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using MuSync.Utils;

namespace MuSync;

/// <summary>应用内实时日志页：内存环形缓冲订阅、级别过滤、暂停、自动滚动、复制、打开日志目录。</summary>
public partial class LogsPage : Page
{
    private const int MaxUiItems = 1000;

    private sealed class LogLine
    {
        public required DateTime Time { get; init; }
        public required string Level { get; init; }
        public required string Message { get; init; }
        public string Line => $"{Time:HH:mm:ss.fff} [{Level,-5}] {Message}";
    }

    private readonly ObservableCollection<LogLine> _all = new();
    private readonly CollectionViewSource _viewSource = new();
    private bool _paused;
    private bool _autoScroll = true;
    private int _pendingCount;

    public LogsPage()
    {
        InitializeComponent();
        _viewSource.Source = _all;
        _viewSource.Filter += OnFilter;
        LogList.ItemsSource = _viewSource.View;
        Loaded += (_, _) =>
        {
            Logger.EntryLogged += OnEntryLogged;
            // 首次进入回填内存历史
            if (_all.Count == 0)
            {
                foreach (var e in Logger.Snapshot())
                    AddLine(new LogLine { Time = e.Time, Level = e.Level, Message = e.Message });
                UpdateCount();
                ScrollToEnd();
            }
        };
        Unloaded += (_, _) => Logger.EntryLogged -= OnEntryLogged;
        LevelFilter.SelectionChanged += (_, _) => _viewSource.View.Refresh();
    }

    private void OnFilter(object sender, FilterEventArgs e)
    {
        var line = (LogLine)e.Item;
        e.Accepted = LevelFilter.SelectedIndex switch
        {
            1 => line.Level == "INFO",
            2 => line.Level == "WARN",
            3 => line.Level == "ERROR",
            _ => true
        };
    }

    private void OnEntryLogged(LogEntry entry)
    {
        if (_paused) return;
        // 日志来自后台线程：封送到 UI 线程；高频时合并刷新，避免 Dispatcher 队列积压
        var line = new LogLine { Time = entry.Time, Level = entry.Level, Message = entry.Message };
        Dispatcher.BeginInvoke(new Action(() =>
        {
            AddLine(line);
            if (++_pendingCount >= 10)
            {
                UpdateCount();
                _pendingCount = 0;
            }
            if (_autoScroll) ScrollToEnd();
        }), DispatcherPriority.Background);
    }

    private void AddLine(LogLine line)
    {
        _all.Add(line);
        while (_all.Count > MaxUiItems) _all.RemoveAt(0);
    }

    private void UpdateCount() => CountText.Text = $"共 {_all.Count} 条";

    private void ScrollToEnd()
    {
        if (_viewSource.View.IsEmpty) return;
        var last = _viewSource.View.Cast<object>().LastOrDefault();
        if (last != null) LogList.ScrollIntoView(last);
    }

    private void PauseButton_Click(object sender, RoutedEventArgs e)
    {
        _paused = !_paused;
        PauseButton.Content = _paused ? "继续" : "暂停";
        PauseButton.Icon = new Wpf.Ui.Controls.SymbolIcon
        {
            Symbol = _paused ? Wpf.Ui.Controls.SymbolRegular.Play24 : Wpf.Ui.Controls.SymbolRegular.Pause24
        };
    }

    private void AutoScrollButton_Click(object sender, RoutedEventArgs e)
    {
        _autoScroll = !_autoScroll;
        AutoScrollButton.Opacity = _autoScroll ? 1.0 : 0.4;
        if (_autoScroll) ScrollToEnd();
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        _all.Clear();
        UpdateCount();
    }

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        var sb = new StringBuilder();
        foreach (var l in _all) sb.AppendLine(l.Line);
        try
        {
            Clipboard.SetText(sb.ToString());
            CopyButton.Content = "已复制";
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.2) };
            timer.Tick += (s, _) => { CopyButton.Content = "复制"; ((DispatcherTimer)s!).Stop(); };
            timer.Start();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"复制失败：{ex.Message}", "MuSync", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OpenFolderButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", Logger.DirectoryPath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"打开日志目录失败：{ex.Message}\n{Logger.DirectoryPath}", "MuSync",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
