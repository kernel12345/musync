using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Input;
using MuSync.Models;
using MuSync.Utils;
namespace MuSync;
/// <summary>
/// 播放器页：三个播放器的轮询详情（运行状态/错误码/当前歌曲）与 Steam 会话状态。
/// 纯只读监控页，数据全部来自 AppServices 公开属性，不触碰后台逻辑。
/// </summary>
public partial class PlayersPage : Page
{
    private static readonly string[] PlayerNames = ["网易云音乐", "QQ音乐", "汽水音乐"];
    private readonly DispatcherTimer _updateTimer;
    private readonly TextBlock[] _activeLabels = new TextBlock[3];
    private readonly TextBlock[] _stateLabels = new TextBlock[3];
    private readonly TextBlock[] _songLabels = new TextBlock[3];
    private readonly TextBlock[] _artistLabels = new TextBlock[3];
    private readonly TextBlock[] _albumLabels = new TextBlock[3];
    private readonly TextBlock[] _progressLabels = new TextBlock[3];

    public PlayersPage()
    {
        InitializeComponent();
        for (var i = 0; i < 3; i++)
        {
            _activeLabels[i] = (TextBlock)FindName($"P{i}Active")!;
            _stateLabels[i] = (TextBlock)FindName($"P{i}State")!;
            _songLabels[i] = (TextBlock)FindName($"P{i}Song")!;
            _artistLabels[i] = (TextBlock)FindName($"P{i}Artist")!;
            _albumLabels[i] = (TextBlock)FindName($"P{i}Album")!;
            _progressLabels[i] = (TextBlock)FindName($"P{i}Progress")!;
        }
        _updateTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _updateTimer.Tick += (_, _) => UpdateFromServices();
        IsVisibleChanged += OnIsVisibleChanged;
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true)
        {
            UpdateFromServices();
            _updateTimer.Start();
        }
        else
        {
            _updateTimer.Stop();
        }
    }

    private void UpdateFromServices()
    {
        try
        {
            var allPlayersStatus = AppServices.Rpc?.GetAllPlayersStatus();
            if (allPlayersStatus != null)
            {
                for (var i = 0; i < 3; i++)
                {
                    var (playerInfo, _, isActive, lastError) = allPlayersStatus[i];
                    UpdatePlayer(i, playerInfo, isActive, lastError);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"[PlayersPage] 刷新详情失败: {ex.Message}");
        }
    }

    private void UpdatePlayer(int index, PlayerInfo? playerInfo, bool isActive, RpcManager.ErrorCode lastError)
    {
        _activeLabels[index].Text = isActive ? "运行中" : "未运行";
        SetForeground(_activeLabels[index],
            isActive ? "SystemFillColorSuccessBrush" : "TextFillColorSecondaryBrush");
        var stateText = lastError switch
        {
            RpcManager.ErrorCode.PermissionDenied => "错误：需要管理员运行",
            RpcManager.ErrorCode.DllNotFound => "错误：播放器组件未加载",
            RpcManager.ErrorCode.VersionNotSupported => "错误：版本不支持/特征码失效",
            _ => "无轮询错误"
        };
        if (_stateLabels[index].Text != stateText) _stateLabels[index].Text = stateText;
        SetForeground(_stateLabels[index],
            lastError == RpcManager.ErrorCode.None ? "TextFillColorSecondaryBrush" : "SystemFillColorCriticalBrush");
        if (playerInfo is { } info)
        {
            _songLabels[index].Text = string.IsNullOrEmpty(info.Title) ? "--" : info.Title;
            _artistLabels[index].Text = string.IsNullOrEmpty(info.Artists) ? "--" : info.Artists;
            _albumLabels[index].Text = string.IsNullOrEmpty(info.Album) ? "--" : info.Album;
            _progressLabels[index].Text = info.Duration > 0
                ? $"{TimeSpan.FromSeconds(info.Schedule):mm\\:ss} / {TimeSpan.FromSeconds(info.Duration):mm\\:ss}" +
                  (info.Pause ? "（已暂停）" : "")
                : "--";
        }
        else
        {
            _songLabels[index].Text = "--";
            _artistLabels[index].Text = "--";
            _albumLabels[index].Text = "--";
            _progressLabels[index].Text = "--";
        }
    }

    private void SetForeground(TextBlock label, string brushKey)
    {
        if (TryFindResource(brushKey) is Brush brush) label.Foreground = brush;
    }
}
