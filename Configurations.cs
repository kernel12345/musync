using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using MuSync.Utils;
namespace MuSync;
internal class ConfigData
{
    public bool AutoStart { get; set; }
    public bool CloseToTray { get; set; } = true;
    public bool StartInTray { get; set; }
    /// <summary>是否已完成过首次启动。首次启动无论 StartInTray 设置如何都显示主窗口，之后才按设置驻留托盘。</summary>
    public bool HasLaunchedBefore { get; set; }
    public bool ShowArtistName { get; set; } = true;
    public bool ShowProgressBar { get; set; } = true;
    public bool PauseWhenPlayingGame { get; set; } = true;
    public bool EnableSteamSync { get; set; } = true;
    public string SteamUsername { get; set; } = "";
    public string SteamRefreshToken { get; set; } = "";
    public string SteamGuardData { get; set; } = "";
    public SteamStatusPriority StatusPriority { get; set; } = SteamStatusPriority.Artist;
    public bool EnableCustomPrefix { get; set; }
    public string CustomPrefix { get; set; } = "";
    /// <summary>没有音乐可同步时，是否在 Steam 状态位显示自定义在线签名。</summary>
    public bool EnableCustomSignature { get; set; }
    public string CustomSignature { get; set; } = "";
    /// <summary>推送实时操作：没有音乐可同步时，把鼠标当前操作的应用名推送到 Steam 状态位。</summary>
    public bool PushRealtimeActivity { get; set; }
    /// <summary>桌面增强：双击桌面空白处隐藏桌面图标（再次双击恢复）。</summary>
    public bool DoubleClickHideDesktopIcons { get; set; }
    /// <summary>桌面增强：桌面图标不透明度百分比（10-100，100 为系统默认完全不透明）。</summary>
    public int DesktopIconOpacity { get; set; } = 100;
    /// <summary>挂游戏时长：当前是否处于挂时长状态（登录后自动恢复）。</summary>
    public bool GameIdleEnabled { get; set; }
    /// <summary>挂游戏时长：正在挂的 Steam AppID 列表（最多 30 个，Steam 并发游玩上限）。</summary>
    public List<uint> GameIdleAppIds { get; set; } = new();
}
public enum SteamStatusPriority
{
    Artist,
    ProgressBar
}
internal class Configurations
{
    public static readonly Configurations Instance = new();
    private static readonly JsonSerializerOptions SJsonOptions = new() { WriteIndented = true };
    public ConfigData Settings { get; private set; }
    [JsonIgnore] public bool IsFirstLoad { get; }
    [JsonIgnore] private readonly string _path;
    private Configurations()
    {
        Settings = new ConfigData();
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MuSync");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "config.json");
        if (File.Exists(_path))
        {
            IsFirstLoad = false;
            Load();
        }
        else
        {
            IsFirstLoad = true;
            Save();
        }
    }
    public void Save()
    {
        try
        {
            var node = JsonSerializer.SerializeToNode(Settings, SJsonOptions);
            if (node is JsonObject root)
            {
                ProtectField(root, nameof(ConfigData.SteamRefreshToken));
                ProtectField(root, nameof(ConfigData.SteamGuardData));
            }
            File.WriteAllText(_path, node?.ToJsonString(SJsonOptions) ?? "{}", Encoding.UTF8);
        }
        catch (Exception e)
        {
            Logger.Error($"保存配置失败: {e.Message}");
        }
    }
    private void Load()
    {
        try
        {
            var json = File.ReadAllText(_path, Encoding.UTF8);
            var node = JsonNode.Parse(json);
            if (node is JsonObject root)
            {
                UnprotectField(root, nameof(ConfigData.SteamRefreshToken));
                UnprotectField(root, nameof(ConfigData.SteamGuardData));
            }
            var loadedConfig = node.Deserialize<ConfigData>(SJsonOptions);
            if (loadedConfig == null) return;
            Settings = loadedConfig;
        }
        catch (Exception e)
        {
            Logger.Error($"加载配置失败，使用默认值: {e.Message}");
            try
            {
                File.Copy(_path, _path + ".bak", overwrite: true);
                Logger.Warn($"已备份可能损坏的配置文件到 {_path}.bak");
            }
            catch (Exception ex)
            {
                Logger.Error($"备份损坏配置文件失败: {ex.Message}");
            }
            Save();
        }
    }
    private static void ProtectField(JsonObject root, string name)
    {
        if (root[name] is JsonValue value &&
            value.GetValue<string>() is { Length: > 0 } plain)
        {
            root[name] = TokenProtector.Protect(plain);
        }
    }
    private static void UnprotectField(JsonObject root, string name)
    {
        if (root[name] is JsonValue value &&
            value.GetValue<string>() is { Length: > 0 } stored)
        {
            root[name] = TokenProtector.Unprotect(stored);
        }
    }
}
