using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MuSync.Utils;
using SteamKit2;

namespace MuSync;

/// <summary>库存中的一个游戏（用于挂时长勾选列表）。</summary>
internal sealed record OwnedGame(uint AppId, string Name);

/// <summary>
/// 通过登录后的 LicenseList + PICS 产品信息解析账号拥有的游戏（仅 common.type == game），
/// 结果缓存到 %LocalAppData%/MuSync/owned-games.json，避免每次进页面都全量请求。
/// </summary>
internal sealed class SteamLibraryResolver(SteamSessionManager session)
{
    private const int PicsChunkSize = 200;
    private static readonly TimeSpan PicsTimeout = TimeSpan.FromSeconds(30);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _cachePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MuSync", "owned-games.json");

    /// <summary>读取拥有的游戏列表；forceRefresh=false 时优先返回磁盘缓存。</summary>
    public async Task<IReadOnlyList<OwnedGame>> GetOwnedGamesAsync(
        bool forceRefresh, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!forceRefresh && TryLoadCache(out var cached) && cached.Count > 0)
        {
            return cached;
        }
        var apps = session.SteamAppsHandler;
        if (apps == null || !session.IsLoggedOn)
        {
            throw new InvalidOperationException("Steam 未登录，无法读取游戏库存");
        }

        progress?.Report("等待 Steam 许可证列表…");
        var packages = await session.WaitForLicensesAsync(cancellationToken).ConfigureAwait(false);
        if (packages.Count == 0)
        {
            throw new InvalidOperationException("未获取到 Steam 许可证列表");
        }

        // 1) 套餐（package）→ 其包含的全部 appid
        progress?.Report("解析套餐包含的应用…");
        var appIds = new HashSet<uint>();
        var packageChunks = Chunk(packages, PicsChunkSize).ToList();
        for (var i = 0; i < packageChunks.Count; i++)
        {
            progress?.Report($"解析套餐 {i + 1}/{packageChunks.Count}…");
            await FetchPackageAppIdsAsync(apps, packageChunks[i], appIds, cancellationToken).ConfigureAwait(false);
        }
        appIds.Remove(0);

        // 2) appid → 名称/类型，只保留真正的「游戏」
        progress?.Report("读取游戏名称…");
        var games = new Dictionary<uint, string>();
        var appChunks = Chunk(appIds, PicsChunkSize).ToList();
        for (var i = 0; i < appChunks.Count; i++)
        {
            progress?.Report($"读取游戏信息 {i + 1}/{appChunks.Count}…");
            await FetchAppNamesAsync(apps, appChunks[i], games, cancellationToken).ConfigureAwait(false);
        }

        var result = games
            .Select(kv => new OwnedGame(kv.Key, kv.Value))
            .OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(g => g.AppId)
            .ToList();
        SaveCache(result);
        progress?.Report($"库存读取完成，共 {result.Count} 个游戏");
        return result;
    }

    private static async Task FetchPackageAppIdsAsync(
        SteamApps apps, IReadOnlyCollection<uint> packageIds, HashSet<uint> appIds, CancellationToken ct)
    {
        var tokens = new Dictionary<uint, ulong>();
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var requests = packageIds
                .Select(id => new SteamApps.PICSRequest(id, tokens.TryGetValue(id, out var token) ? token : 0))
                .ToList();
            var callbacks = await RunJobsAsync(
                apps.PICSGetProductInfo(Array.Empty<SteamApps.PICSRequest>(), requests, false), ct)
                .ConfigureAwait(false);
            var unknown = new HashSet<uint>();
            foreach (var cb in callbacks)
            {
                foreach (var package in cb.Packages.Values)
                {
                    var appIdsNode = package.KeyValues["appids"];
                    if (appIdsNode == KeyValue.Invalid) continue;
                    foreach (var child in appIdsNode.Children)
                    {
                        if (uint.TryParse(child.Value, out var appId) && appId > 0)
                        {
                            appIds.Add(appId);
                        }
                    }
                }
                unknown.UnionWith(cb.UnknownPackages);
            }
            if (unknown.Count == 0) return;
            var tokenCb = await RunJobAsync(
                apps.PICSGetAccessTokens(Array.Empty<uint>(), unknown), ct)
                .ConfigureAwait(false);
            foreach (var kv in tokenCb.PackageTokens)
            {
                tokens[kv.Key] = kv.Value;
            }
        }
    }

    private static async Task FetchAppNamesAsync(
        SteamApps apps, IReadOnlyCollection<uint> appIdsToFetch, Dictionary<uint, string> games, CancellationToken ct)
    {
        var tokens = new Dictionary<uint, ulong>();
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var requests = appIdsToFetch
                .Select(id => new SteamApps.PICSRequest(id, tokens.TryGetValue(id, out var token) ? token : 0))
                .ToList();
            var callbacks = await RunJobsAsync(
                apps.PICSGetProductInfo(requests, Array.Empty<SteamApps.PICSRequest>(), false), ct)
                .ConfigureAwait(false);
            var unknown = new HashSet<uint>();
            foreach (var cb in callbacks)
            {
                foreach (var app in cb.Apps.Values)
                {
                    var common = app.KeyValues["common"];
                    if (common == KeyValue.Invalid) continue;
                    var type = common["type"].AsString();
                    if (!string.Equals(type, "game", StringComparison.OrdinalIgnoreCase)) continue;
                    var name = common["name"].AsString();
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    games[app.ID] = name.Trim();
                }
                unknown.UnionWith(cb.UnknownApps);
            }
            if (unknown.Count == 0) return;
            var tokenCb = await RunJobAsync(
                apps.PICSGetAccessTokens(unknown, Array.Empty<uint>()), ct)
                .ConfigureAwait(false);
            foreach (var kv in tokenCb.AppTokens)
            {
                tokens[kv.Key] = kv.Value;
            }
        }
    }

    private static async Task<T> RunJobAsync<T>(AsyncJob<T> job, CancellationToken ct) where T : CallbackMsg
    {
        var jobTask = job.ToTask();
        var completed = await Task.WhenAny(jobTask, Task.Delay(PicsTimeout, ct)).ConfigureAwait(false);
        if (completed != jobTask)
        {
            throw new TimeoutException("Steam PICS 请求超时");
        }
        return await jobTask.ConfigureAwait(false);
    }

    private static async Task<IEnumerable<T>> RunJobsAsync<T>(AsyncJobMultiple<T> job, CancellationToken ct)
        where T : CallbackMsg
    {
        var jobTask = job.ToTask();
        var completed = await Task.WhenAny(jobTask, Task.Delay(PicsTimeout, ct)).ConfigureAwait(false);
        if (completed != jobTask)
        {
            throw new TimeoutException("Steam PICS 请求超时");
        }
        var resultSet = await jobTask.ConfigureAwait(false);
        return (IEnumerable<T>?)resultSet.Results ?? Array.Empty<T>();
    }

    private static IEnumerable<List<T>> Chunk<T>(IEnumerable<T> source, int size)
    {
        var list = source as IList<T> ?? source.ToList();
        for (var i = 0; i < list.Count; i += size)
        {
            yield return list.Skip(i).Take(size).ToList();
        }
    }

    private bool TryLoadCache(out List<OwnedGame> games)
    {
        games = new List<OwnedGame>();
        try
        {
            if (!File.Exists(_cachePath)) return false;
            using var doc = JsonDocument.Parse(File.ReadAllText(_cachePath));
            if (doc.RootElement.TryGetProperty("games", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in arr.EnumerateArray())
                {
                    var appId = el.GetProperty("appId").GetUInt32();
                    var name = el.GetProperty("name").GetString() ?? $"App {appId}";
                    games.Add(new OwnedGame(appId, name));
                }
            }
            return games.Count > 0;
        }
        catch (Exception ex)
        {
            Logger.Warn($"[SteamLibrary] 读取库存缓存失败，将重新拉取: {ex.Message}");
            games = new List<OwnedGame>();
            return false;
        }
    }

    private void SaveCache(IReadOnlyList<OwnedGame> games)
    {
        try
        {
            var dto = new LibraryCacheDto
            {
                UpdatedAt = DateTimeOffset.Now.ToString("O"),
                Games = games.Select(g => new GameDto { AppId = g.AppId, Name = g.Name }).ToList()
            };
            File.WriteAllText(_cachePath, JsonSerializer.Serialize(dto, JsonOptions));
        }
        catch (Exception ex)
        {
            Logger.Warn($"[SteamLibrary] 写入库存缓存失败: {ex.Message}");
        }
    }

    private sealed class LibraryCacheDto
    {
        public string UpdatedAt { get; set; } = "";
        public List<GameDto> Games { get; set; } = new();
    }

    private sealed class GameDto
    {
        public uint AppId { get; set; }
        public string Name { get; set; } = "";
    }
}
