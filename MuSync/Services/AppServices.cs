using System;
using System.Threading;
using System.Threading.Tasks;
namespace MuSync;
/// <summary>
/// 后台服务静态访问器（替代原 Program.GetRpcManager 等全局入口）。
/// UI 层通过此类型读取轮询结果，后台逻辑完全复用原有实现。
/// </summary>
internal static class AppServices
{
    private static CancellationTokenSource? _cts;
    public static SteamSessionManager? Session { get; private set; }
    public static SteamStatusManager? Steam { get; private set; }
    public static RpcManager? Rpc { get; private set; }
    public static void Initialize()
    {
        _cts = new CancellationTokenSource();
        Session = new SteamSessionManager();
        Session.Start();
        // 无论登录与否，先让界面与轮询跑起来（未登录时状态同步自动跳过）
        Steam = new SteamStatusManager(Session);
        Rpc = new RpcManager(Steam);
        var token = _cts.Token;
        Task.Run(Rpc.Start, token);
    }
    /// <summary>停止后台轮询（程序退出前调用，.ClearStatus/Dispose 由 OnExit 顺序处理）。</summary>
    public static void StopWorkers()
    {
        _cts?.Cancel();
    }
}
