using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using MuSync.Utils;
using static MuSync.Win32Api.User32;

namespace MuSync;

/// <summary>
/// 双击桌面空白处隐藏/显示桌面图标（参照 DeskHider / iPhilip 的 AHK 方案实现）。
/// 专用后台线程安装低级鼠标钩子（WH_MOUSE_LL）并泵消息，按系统双击时间/区域识别两次左键按下；
/// 取鼠标下窗口的顶层窗口类名判定是否位于桌面（Progman/WorkerW）：
/// 图标可见时，跨进程枚举每个图标的包围矩形（LVM_GETITEMRECT + VirtualAllocEx/ReadProcessMemory），
/// 鼠标不在任何矩形内（空白处）才隐藏，点在图标上放行正常双击打开；
/// 图标已隐藏时，双击桌面任意位置直接恢复显示。
/// </summary>
internal static partial class DesktopIconService
{
    private const int SwHide = 0;
    private const int SwShow = 5;
    private const int GwlExStyle = -20;
    private const long WsExLayered = 0x00080000;
    private const uint LwaAlpha = 0x00000002;
    private const int FadeSteps = 30;
    private const int FadeStepMs = 12; // 30 步 × 12ms ≈ 400ms 渐变时长
    private const uint WmLButtonDown = 0x0201;
    private const uint WmQuit = 0x0012;
    private const uint LvmGetItemCount = 0x1004; // LVM_FIRST + 4（无指针参数，跨进程安全）
    private const uint LvmGetItemRect = 0x100E; // LVM_FIRST + 14（lParam 指针必须在目标进程内分配）
    private const uint SmtoBlock = 0x0001;
    private const uint SmtoAbortIfHung = 0x0002;
    private const int SmCxDoubleClick = 36;
    private const int SmCyDoubleClick = 37;
    private const int GaRoot = 2;
    private const int ProcessVmOperation = 0x0008;
    private const int ProcessVmRead = 0x0010;
    private const uint MemCommit = 0x1000;
    private const uint MemRelease = 0x8000;
    private const uint PageReadWrite = 0x04;
    private const int RectSize = 16; // RECT：left/top/right/bottom 四个 int

    private static Thread? _thread;
    private static IntPtr _hook;
    private static MouseHookProc? _hookProc; // 持有钩子委托引用，防止被 GC 回收
    private static uint _hookThreadId;
    private static long _lastDownTick;
    private static int _lastDownX;
    private static int _lastDownY;
    private static readonly SemaphoreSlim FadeLock = new(1, 1); // 串行化淡入淡出，避免快速连击时动画交叠

    /// <summary>启动钩子线程（幂等，重复调用无副作用）。</summary>
    public static void Start()
    {
        if (_thread != null) return;
        _thread = new Thread(HookThreadProc)
        {
            IsBackground = true,
            Name = "DesktopIconHook"
        };
        _thread.Start();
    }

    /// <summary>停止钩子并恢复桌面图标显示（关闭功能/退出程序时调用，幂等）。</summary>
    public static void Stop()
    {
        _thread = null;
        var hook = _hook;
        _hook = IntPtr.Zero;
        if (hook != IntPtr.Zero) UnhookWindowsHookEx(hook);
        var threadId = _hookThreadId;
        _hookThreadId = 0;
        if (threadId != 0) PostThreadMessage(threadId, WmQuit, IntPtr.Zero, IntPtr.Zero);
        // 等待进行中的淡入淡出结束（最多 800ms），再把图标恢复为不透明并显示
        var lockTaken = FadeLock.Wait(800);
        try
        {
            var list = FindIconListView();
            if (list != IntPtr.Zero)
            {
                SetLayeredWindowAttributes(list, 0, 255, LwaAlpha);
                if (!IsWindowVisible(list)) ShowWindow(list, SwShow);
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"恢复桌面图标显示失败: {ex.Message}");
        }
        finally
        {
            if (lockTaken) FadeLock.Release();
        }
    }

    private static void HookThreadProc()
    {
        try
        {
            _hookProc = HookProc;
            _hookThreadId = GetCurrentThreadId();
            _hook = SetWindowsHookEx(WhMouseLl, _hookProc, GetModuleHandle(null), 0);
            if (_hook == IntPtr.Zero)
            {
                Logger.Warn($"安装桌面双击钩子失败 (错误码 {Marshal.GetLastWin32Error()})");
                return;
            }
            // 低级钩子回调依赖本线程的消息循环
            while (GetMessage(out _, IntPtr.Zero, 0, 0) > 0)
            {
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"桌面图标钩子线程异常: {ex.Message}");
        }
        finally
        {
            var hook = _hook;
            _hook = IntPtr.Zero;
            if (hook != IntPtr.Zero) UnhookWindowsHookEx(hook);
        }
    }

    private static IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode >= 0 && wParam.ToInt64() == WmLButtonDown)
            {
                var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                if (IsDoubleClick(info.pt)) ToggleIfDesktopEmptyArea(info.pt);
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"桌面双击处理异常: {ex.Message}");
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    /// <summary>按系统双击时间/区域设置识别两次连续左键按下。</summary>
    private static bool IsDoubleClick(POINT pt)
    {
        var now = Environment.TickCount64;
        var interval = GetDoubleClickTime();
        if (interval == 0) interval = 500;
        var isDouble = now - _lastDownTick <= interval &&
                       Math.Abs(pt.X - _lastDownX) <= GetSystemMetrics(SmCxDoubleClick) &&
                       Math.Abs(pt.Y - _lastDownY) <= GetSystemMetrics(SmCyDoubleClick);
        _lastDownTick = now;
        _lastDownX = pt.X;
        _lastDownY = pt.Y;
        return isDouble;
    }

    private static void ToggleIfDesktopEmptyArea(POINT pt)
    {
        var hwnd = WindowFromPoint(pt);
        if (hwnd == IntPtr.Zero) return;
        // 取顶层窗口类名判定桌面：图标可见时命中 SysListView32、隐藏时命中 SHELLDLL_DefView，
        // 两种情况下顶层窗口都是 Progman（壁纸轮播场景为 WorkerW）
        var rootClass = GetClassName(GetAncestor(hwnd, GaRoot));
        if (rootClass is not ("Progman" or "WorkerW")) return;

        var list = FindIconListView();
        if (list == IntPtr.Zero) return;

        if (!IsWindowVisible(list))
        {
            // 图标已隐藏：双击桌面任意位置直接恢复（淡入）
            FadeWindow(list, true);
            Logger.Info("双击桌面：恢复桌面图标");
            return;
        }

        // 图标可见：鼠标不在任何图标矩形内（空白处）才隐藏，点在图标上放行正常双击打开
        if (IsMouseOverIcon(list, pt)) return;
        FadeWindow(list, false);
        Logger.Info("双击桌面空白处：隐藏桌面图标");
    }

    /// <summary>
    /// 淡出/淡入切换图标列表显隐。AnimateWindow 对跨进程子窗口不生效，
    /// 改用 WS_EX_LAYERED + SetLayeredWindowAttributes 手动渐变 Alpha；
    /// 动画放线程池执行，不阻塞鼠标钩子线程，FadeLock 保证多个动画串行。
    /// </summary>
    private static void FadeWindow(IntPtr listHwnd, bool show)
    {
        _ = Task.Run(() =>
        {
            if (!FadeLock.Wait(1000)) return;
            try
            {
                FadeWindowCore(listHwnd, show);
            }
            catch (Exception ex)
            {
                Logger.Warn($"桌面图标淡入淡出异常: {ex.Message}");
                ShowWindow(listHwnd, show ? SwShow : SwHide);
            }
            finally
            {
                FadeLock.Release();
            }
        });
    }

    private static void FadeWindowCore(IntPtr listHwnd, bool show)
    {
        var originalExStyle = GetWindowLongPtr(listHwnd, GwlExStyle);
        SetWindowLongPtr(listHwnd, GwlExStyle, (IntPtr)(originalExStyle.ToInt64() | WsExLayered));
        // show 先置全透明再显示；hide 从完全不透明开始渐隐
        if (!SetLayeredWindowAttributes(listHwnd, 0, show ? (byte)0 : (byte)255, LwaAlpha))
        {
            // 分层样式未生效时兜底：直接显隐
            SetWindowLongPtr(listHwnd, GwlExStyle, originalExStyle);
            ShowWindow(listHwnd, show ? SwShow : SwHide);
            return;
        }
        if (show) Thread.Sleep(30); // 等 DWM 应用透明属性，避免显示首帧以不透明状态闪现
        if (show) ShowWindow(listHwnd, SwShow);
        for (var i = 1; i <= FadeSteps; i++)
        {
            var alpha = (byte)(show ? i * 255 / FadeSteps : 255 - i * 255 / FadeSteps);
            SetLayeredWindowAttributes(listHwnd, 0, alpha, LwaAlpha);
            Thread.Sleep(FadeStepMs);
        }
        if (!show)
        {
            ShowWindow(listHwnd, SwHide);
            // 窗口已隐藏才还原样式；可见状态下移除分层样式会触发一次重绘闪烁
            SetWindowLongPtr(listHwnd, GwlExStyle, originalExStyle);
        }
        // show 结束后保留 WS_EX_LAYERED（alpha=255 视觉等同正常状态），避免样式切换闪烁，下次渐隐直接复用
    }

    /// <summary>
    /// 鼠标（屏幕坐标）是否落在某个桌面图标的包围矩形内。
    /// 跨进程读 ListView 矩形：lParam 指针必须用 VirtualAllocEx 在 explorer 内分配，
    /// 再 ReadProcessMemory 取回（沿用 DeskHider/iPhilip 的安全做法，严禁直接传本进程指针）。
    /// </summary>
    private static bool IsMouseOverIcon(IntPtr listHwnd, POINT screenPt)
    {
        var pt = screenPt;
        if (!ScreenToClient(listHwnd, ref pt)) return false;
        var sent = SendMessageTimeout(listHwnd, LvmGetItemCount, IntPtr.Zero, IntPtr.Zero,
            SmtoBlock | SmtoAbortIfHung, 100, out var countResult);
        if (sent == IntPtr.Zero) return false;
        var count = countResult.ToInt32();
        if (count <= 0) return false;

        GetWindowThreadProcessId(listHwnd, out var pid);
        if (pid == 0) return false;
        var process = OpenProcess(ProcessVmOperation | ProcessVmRead, false, pid);
        if (process == IntPtr.Zero)
        {
            Logger.Warn($"打开 explorer 进程读取图标区域失败 (错误码 {Marshal.GetLastWin32Error()})");
            return false;
        }
        try
        {
            var remote = VirtualAllocEx(process, IntPtr.Zero, (nuint)RectSize, MemCommit, PageReadWrite);
            if (remote == IntPtr.Zero) return false;
            try
            {
                // 全零 RECT 入参 = LVIR_BOUNDS（完整包围矩形），每次发送前需重置输入字段
                var zeroBuf = new byte[RectSize];
                var rectBuf = new byte[RectSize];
                for (var i = 0; i < count; i++)
                {
                    if (!WriteProcessMemory(process, remote, zeroBuf, RectSize, IntPtr.Zero)) continue;
                    if (SendMessageTimeout(listHwnd, LvmGetItemRect, (IntPtr)i, remote,
                            SmtoBlock | SmtoAbortIfHung, 100, out _) == IntPtr.Zero) continue;
                    if (!ReadProcessMemory(process, remote, rectBuf, RectSize, IntPtr.Zero)) continue;
                    var left = BitConverter.ToInt32(rectBuf, 0);
                    var top = BitConverter.ToInt32(rectBuf, 4);
                    var right = BitConverter.ToInt32(rectBuf, 8);
                    var bottom = BitConverter.ToInt32(rectBuf, 12);
                    if (pt.X >= left && pt.X < right && pt.Y >= top && pt.Y < bottom) return true;
                }
                return false;
            }
            finally
            {
                VirtualFreeEx(process, remote, nuint.Zero, MemRelease);
            }
        }
        finally
        {
            CloseHandle(process);
        }
    }

    /// <summary>定位桌面图标列表窗口：Progman（或 WorkerW）→ SHELLDLL_DefView → SysListView32。</summary>
    private static IntPtr FindIconListView()
    {
        var progman = FindWindow("Progman", null);
        var defView = progman != IntPtr.Zero
            ? FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null)
            : IntPtr.Zero;
        if (defView == IntPtr.Zero)
        {
            // 壁纸轮播等场景下列表挂在 WorkerW 下，需枚举顶层窗口查找
            EnumWindows((hWnd, _) =>
                {
                    var dv = FindWindowEx(hWnd, IntPtr.Zero, "SHELLDLL_DefView", null);
                    if (dv == IntPtr.Zero) return true;
                    defView = dv;
                    return false;
                }, IntPtr.Zero);
        }
        return defView == IntPtr.Zero
            ? IntPtr.Zero
            : FindWindowEx(defView, IntPtr.Zero, "SysListView32", "FolderView");
    }

    // ---------------- kernel32：跨进程读取图标矩形所需的进程内存 API ----------------
    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr OpenProcess(int dwDesiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, int dwProcessId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr hObject);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr VirtualAllocEx(IntPtr hProcess, IntPtr lpAddress, nuint dwSize,
        uint flAllocationType, uint flProtect);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool VirtualFreeEx(IntPtr hProcess, IntPtr lpAddress, nuint dwSize, uint dwFreeType);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ReadProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress,
        [Out] byte[] lpBuffer, int nSize, IntPtr lpNumberOfBytesRead);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WriteProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress,
        byte[] lpBuffer, int nSize, IntPtr lpNumberOfBytesWritten);
}
