using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using MuSync.Utils;
using static MuSync.Win32Api.User32;

namespace MuSync;

/// <summary>
/// 桌面增强效果：
/// 1) 双击桌面空白处渐隐隐藏/渐显恢复桌面图标（参照 DeskHider / iPhilip 的 AHK 方案）；
/// 2) 桌面图标常驻不透明度调节（WS_EX_LAYERED + SetLayeredWindowAttributes）。
/// 专用后台线程安装低级鼠标钩子（WH_MOUSE_LL），但回调里只做轻量双击判定并 PostMessage 到本线程的
/// 仅消息窗口——昂贵的桌面命中检测（几十次跨进程消息）全部在消息处理中完成，保证回调在
/// LowLevelHooksTimeout 内返回（超时会被 Windows 静默吊销钩子，导致功能永久失效）。
/// 仅当“确认”鼠标不在任何图标矩形内（空白处）才隐藏——任何枚举失败都按“点在图标上”处理，
/// 宁可漏隐藏也绝不在用户双击图标打开应用时误触发渐隐；图标已隐藏时双击桌面任意位置直接恢复。
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
    private const int IconRectPadding = 8; // 图标命中矩形外扩物理像素，避免点在图标边缘误判为空白
    private static readonly IntPtr HwndMessage = new(-3);
    private const uint WmToggle = 0x0400; // WM_USER：钩子回调通知本线程执行桌面切换
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
    private const int ProcessVmWrite = 0x0020;
    private const uint MemCommit = 0x1000;
    private const uint MemRelease = 0x8000;
    private const uint PageReadwrite = 0x04;
    private const int RectSize = 16; // RECT：left/top/right/bottom 四个 int

    private static Thread? _thread;
    private static IntPtr _hook;
    private static IntPtr _msgWnd;
    private static MouseHookProc? _hookProc;   // 持有钩子委托引用，防止被 GC 回收
    private static WndProc? _wndProc;         // 持有窗口过程委托引用，防止被 GC 回收
    private static uint _hookThreadId;
    private static long _lastDownTick;
    private static int _lastDownX;
    private static int _lastDownY;
    private static int _opacityPercent = 100;
    private static IntPtr _cachedList = IntPtr.Zero; // 桌面图标列表缓存句柄（explorer 重启后自动失效重建）
    private static int _opacityVersion;               // 透明度设置版本号，用于合并拖动滑块时的高频请求
    private static int _opacityApplyScheduled;        // 0/1：是否已有一个应用任务在途
    private static readonly SemaphoreSlim FadeLock = new(1, 1); // 串行化透明度变更与淡入淡出，避免动画交叠

    /// <summary>当前配置的图标不透明度（10-100）。</summary>
    private static byte TargetAlpha => (byte)(_opacityPercent * 255 / 100);

    /// <summary>启动双击隐藏钩子线程（幂等，重复调用无副作用）。</summary>
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

    /// <summary>仅停止双击隐藏钩子（关闭该开关时调用），保留常驻透明度效果；幂等。</summary>
    public static void StopHook()
    {
        _thread = null;
        var hook = _hook;
        _hook = IntPtr.Zero;
        if (hook != IntPtr.Zero) UnhookWindowsHookEx(hook);
        var threadId = _hookThreadId;
        _hookThreadId = 0;
        if (threadId != 0) PostThreadMessage(threadId, WmQuit, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>
    /// 设置桌面图标常驻不透明度（10-100，100 为系统默认），立即生效。
    /// 高频调用（拖动滑块）会被合并：只保留最新值，由单个后台任务以约 60fps 应用，
    /// 不做窗口枚举风暴，也不在 UI 线程落盘（落盘由调用方防抖处理）。
    /// </summary>
    public static void SetOpacity(int percent)
    {
        _opacityPercent = Math.Clamp(percent, 10, 100);
        Interlocked.Increment(ref _opacityVersion);
        if (Interlocked.CompareExchange(ref _opacityApplyScheduled, 1, 0) != 0) return;
        _ = Task.Run(ApplyOpacityLoopAsync);
    }

    private static async Task ApplyOpacityLoopAsync()
    {
        var appliedVersion = -1;
        try
        {
            while (true)
            {
                await Task.Delay(16); // 约 60fps 合并拖动期间的高频更新
                var latestVersion = Volatile.Read(ref _opacityVersion);
                if (appliedVersion == latestVersion) break;
                // 淡入淡出进行中则跳过本轮（下一轮再试），绝不让滑块与动画互相打架
                if (!FadeLock.Wait(0)) continue;
                try
                {
                    ApplyOpacity(TargetAlpha);
                    appliedVersion = latestVersion;
                }
                catch (Exception ex)
                {
                    Logger.Warn($"设置桌面图标透明度失败: {ex.Message}");
                    appliedVersion = latestVersion; // 出错也退出循环，避免刷屏
                }
                finally
                {
                    FadeLock.Release();
                }
            }
        }
        finally
        {
            Volatile.Write(ref _opacityApplyScheduled, 0);
            // 退出瞬间又有新设置进来时补一次调度
            if (Volatile.Read(ref _opacityVersion) != appliedVersion &&
                Interlocked.CompareExchange(ref _opacityApplyScheduled, 1, 0) == 0)
            {
                _ = Task.Run(ApplyOpacityLoopAsync);
            }
        }
    }

    /// <summary>程序退出时调用：停钩子并把桌面图标完全恢复（显示、不透明、去分层样式），幂等。</summary>
    public static void Stop()
    {
        StopHook();
        // 等待进行中的透明度/动画操作结束（最多 800ms），再完整还原 explorer 桌面列表
        var lockTaken = FadeLock.Wait(800);
        try
        {
            var list = FindIconListView();
            if (list == IntPtr.Zero) return;
            SetLayeredWindowAttributes(list, 0, 255, LwaAlpha);
            if (!IsWindowVisible(list)) ShowWindow(list, SwShow);
            // 移除我们加上的分层样式，把窗口还给 explorer 的原始状态
            var exStyle = GetWindowLongPtr(list, GwlExStyle).ToInt64();
            SetWindowLongPtr(list, GwlExStyle, (IntPtr)(exStyle & ~WsExLayered));
            _cachedList = IntPtr.Zero;
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
            // 1) 创建仅消息窗口：钩子回调只 PostMessage，昂贵处理在本线程消息循环中完成
            _wndProc = MessageWndProc;
            var classNamePtr = Marshal.StringToHGlobalUni("MuSyncDesktopMsgWnd");
            try
            {
                var wc = new WndClassEx
                {
                    CbSize = (uint)Marshal.SizeOf<WndClassEx>(),
                    LpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
                    HInstance = GetModuleHandle(null),
                    LpszClassName = classNamePtr
                };
                RegisterClassEx(ref wc); // 重复注册（线程重启）返回 0，类仍可用，忽略
            }
            finally
            {
                Marshal.FreeHGlobal(classNamePtr);
            }
            _msgWnd = CreateWindowExEx(0, "MuSyncDesktopMsgWnd", null, 0, 0, 0, 0, 0,
                HwndMessage, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);

            // 2) 安装低级鼠标钩子
            _hookProc = HookProc;
            _hookThreadId = GetCurrentThreadId();
            _hook = SetWindowsHookEx(WhMouseLl, _hookProc, GetModuleHandle(null), 0);
            if (_hook == IntPtr.Zero)
            {
                Logger.Warn($"安装桌面双击钩子失败 (错误码 {Marshal.GetLastWin32Error()})");
                return;
            }
            Logger.Info("桌面双击隐藏图标钩子已安装");
            // 低级钩子回调依赖本线程的消息循环；必须 DispatchMessage，否则仅消息窗口的
            // WndProc 永远不会被调用（PostMessage 进来的 WmToggle 会一直留在队列里）
            while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                DispatchMessage(ref msg);
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
            if (_msgWnd != IntPtr.Zero)
            {
                DestroyWindow(_msgWnd);
                _msgWnd = IntPtr.Zero;
            }
        }
    }

    private static IntPtr MessageWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        // 托管窗口过程由非托管代码回调，任何异常穿越该边界都会直接终止进程，必须整体兜底
        try
        {
            if (msg == WmToggle)
            {
                ToggleIfDesktopEmptyArea(new POINT { X = wParam.ToInt32(), Y = lParam.ToInt32() });
                return IntPtr.Zero;
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"桌面双击处理异常: {ex.Message}");
            return IntPtr.Zero;
        }
        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private static IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        // 回调必须极快返回（LowLevelHooksTimeout 约 300ms，超时钩子会被系统静默吊销）：
        // 这里只做纯本地双击判定，命中检测/跨进程枚举全部交给消息窗口处理
        try
        {
            if (nCode >= 0 && wParam.ToInt64() == WmLButtonDown)
            {
                var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                if (IsDoubleClick(info.pt) && _msgWnd != IntPtr.Zero)
                {
                    Logger.Info($"[桌面] 检测到双击 {info.pt.X},{info.pt.Y}");
                    PostMessage(_msgWnd, WmToggle, (IntPtr)info.pt.X, (IntPtr)info.pt.Y);
                }
            }
        }
        catch
        {
            // 钩子回调内绝不能抛异常
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    /// <summary>按系统双击时间/区域设置识别两次连续左键按下（区域阈值按鼠标所在显示器 DPI 缩放）。</summary>
    private static bool IsDoubleClick(POINT pt)
    {
        var now = Environment.TickCount64;
        var interval = GetDoubleClickTime();
        if (interval == 0) interval = 500;
        var dpi = GetDpiAtPoint(pt);
        var maxX = GetSystemMetricsForDpi(SmCxDoubleClick, dpi);
        var maxY = GetSystemMetricsForDpi(SmCyDoubleClick, dpi);
        if (maxX <= 0) maxX = GetSystemMetrics(SmCxDoubleClick);
        if (maxY <= 0) maxY = GetSystemMetrics(SmCyDoubleClick);
        var isDouble = now - _lastDownTick <= interval &&
                       Math.Abs(pt.X - _lastDownX) <= maxX &&
                       Math.Abs(pt.Y - _lastDownY) <= maxY;
        if (isDouble)
        {
            // 与 Windows 双击语义一致：本次双击成立后立即“解散”配对，连续快按 4 下只会产生
            // 两次双击（第 3 下是新配对的起点），否则连击流中每一下都会被误判为双击
            _lastDownTick = 0;
            _lastDownX = 0;
            _lastDownY = 0;
        }
        else
        {
            _lastDownTick = now;
            _lastDownX = pt.X;
            _lastDownY = pt.Y;
        }
        return isDouble;
    }

    private static uint GetDpiAtPoint(POINT pt)
    {
        try
        {
            var hwnd = WindowFromPoint(pt);
            if (hwnd != IntPtr.Zero)
            {
                var dpi = GetDpiForWindow(GetAncestor(hwnd, GaRoot));
                if (dpi != 0) return dpi;
            }
        }
        catch
        {
            // 旧系统等情况下回退 96
        }
        return 96;
    }

    private static void ToggleIfDesktopEmptyArea(POINT pt)
    {
        var hwnd = WindowFromPoint(pt);
        if (hwnd == IntPtr.Zero) return;
        // 取顶层窗口类名判定桌面：图标可见时命中 SysListView32、隐藏时命中 SHELLDLL_DefView，
        // 两种情况下顶层窗口都是 Progman（壁纸轮播场景为 WorkerW）
        var rootClass = GetClassName(GetAncestor(hwnd, GaRoot));
        if (rootClass is not ("Progman" or "WorkerW")) return;

        var list = GetIconList();
        if (list == IntPtr.Zero) return;

        if (!IsWindowVisible(list))
        {
            // 图标已隐藏：双击桌面任意位置直接恢复（淡入到配置的透明度）
            FadeWindow(list, true);
            Logger.Info("双击桌面：恢复桌面图标");
            return;
        }

        // 图标可见：仅当确认鼠标不在任何图标矩形内（空白处）才隐藏；
        // 点在图标上或判定不确定时一律放行，保证双击图标打开应用绝不触发渐隐
        var overIcon = IsMouseOverIconOrUnknown(list, pt);
        if (overIcon)
        {
            Logger.Info("[桌面] 双击落在图标上或判定不确定，不隐藏");
            return;
        }
        FadeWindow(list, false);
        Logger.Info("双击桌面空白处：隐藏桌面图标");
    }

    /// <summary>取桌面图标列表句柄（带缓存）；缓存失效（explorer 重启）时重新枚举并恢复透明度。</summary>
    private static IntPtr GetIconList()
    {
        var cached = _cachedList;
        if (cached != IntPtr.Zero && IsWindow(cached) &&
            GetClassName(cached) == "SysListView32")
        {
            return cached;
        }
        var found = FindIconListView();
        _cachedList = found;
        if (found != IntPtr.Zero)
        {
            // explorer 重启后新窗口不是分层窗口，按当前配置重新应用常驻透明度
            try { ApplyOpacity(TargetAlpha); }
            catch (Exception ex) { Logger.Warn($"explorer 重启后恢复图标透明度失败: {ex.Message}"); }
        }
        return found;
    }

    /// <summary>
    /// 淡出/淡入切换图标列表显隐。AnimateWindow 对跨进程子窗口不生效，
    /// 改用 WS_EX_LAYERED + SetLayeredWindowAttributes 手动渐变 Alpha；
    /// 动画放线程池执行，FadeLock 保证多个效果串行。
    /// 动画结束停留在当前配置的不透明度；分层样式保留（退出时由 Stop 统一移除）。
    /// </summary>
    private static void FadeWindow(IntPtr listHwnd, bool show)
    {
        var target = TargetAlpha;
        _ = Task.Run(() =>
        {
            if (!FadeLock.Wait(1000)) return;
            try
            {
                FadeWindowCore(listHwnd, show, target);
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

    private static void FadeWindowCore(IntPtr listHwnd, bool show, byte targetAlpha)
    {
        EnsureLayered(listHwnd);
        // show：先置全透明再显示；hide：从当前配置的不透明度开始渐隐
        if (!SetLayeredWindowAttributes(listHwnd, 0, show ? (byte)0 : targetAlpha, LwaAlpha))
        {
            // 分层属性未生效时兜底：直接显隐
            ShowWindow(listHwnd, show ? SwShow : SwHide);
            return;
        }
        if (show)
        {
            Thread.Sleep(30); // 等 DWM 应用透明属性，避免显示首帧以不透明状态闪现
            ShowWindow(listHwnd, SwShow);
        }
        for (var i = 1; i <= FadeSteps; i++)
        {
            var alpha = (byte)(show
                ? i * targetAlpha / FadeSteps
                : targetAlpha - i * targetAlpha / FadeSteps);
            SetLayeredWindowAttributes(listHwnd, 0, alpha, LwaAlpha);
            Thread.Sleep(FadeStepMs);
        }
        SetLayeredWindowAttributes(listHwnd, 0, show ? targetAlpha : (byte)0, LwaAlpha);
        if (!show) ShowWindow(listHwnd, SwHide);
    }

    /// <summary>给桌面列表加分层样式（已存在则不动）并设置目标 Alpha。</summary>
    private static void ApplyOpacity(byte alpha)
    {
        var list = GetIconList();
        if (list == IntPtr.Zero) return;
        var exStyle = GetWindowLongPtr(list, GwlExStyle).ToInt64();
        var alreadyLayered = (exStyle & WsExLayered) != 0;
        // 100% 且窗口本就不是分层窗口：无需任何改动；其余情况保证分层样式在
        if (alpha >= 255 && !alreadyLayered) return;
        if (!alreadyLayered) SetWindowLongPtr(list, GwlExStyle, (IntPtr)(exStyle | WsExLayered));
        SetLayeredWindowAttributes(list, 0, alpha, LwaAlpha);
    }

    private static void EnsureLayered(IntPtr listHwnd)
    {
        var exStyle = GetWindowLongPtr(listHwnd, GwlExStyle).ToInt64();
        if ((exStyle & WsExLayered) == 0)
            SetWindowLongPtr(listHwnd, GwlExStyle, (IntPtr)(exStyle | WsExLayered));
    }

    /// <summary>
    /// 判定鼠标是否“在图标上”或“无法确认”：两种情况都返回 true（不隐藏）。
    /// 只有成功枚举且确认不在任意图标包围矩形（外扩 8px 容差）内才返回 false。
    /// 跨进程读 ListView 矩形：lParam 指针必须用 VirtualAllocEx 在 explorer 内分配，
    /// 再 ReadProcessMemory 取回（沿用 DeskHider/iPhilip 的安全做法，严禁直接传本进程指针）。
    /// </summary>
    private static bool IsMouseOverIconOrUnknown(IntPtr listHwnd, POINT screenPt)
    {
        var pt = screenPt;
        if (!ScreenToClient(listHwnd, ref pt)) return true;
        if (SendMessageTimeout(listHwnd, LvmGetItemCount, IntPtr.Zero, IntPtr.Zero,
                SmtoBlock | SmtoAbortIfHung, 100, out var countResult) == IntPtr.Zero)
        {
            Logger.Warn("查询桌面图标数量超时/失败，本次双击按图标区域处理，不隐藏图标");
            return true;
        }
        var count = countResult.ToInt32();
        if (count <= 0) return false; // 桌面上一个图标都没有：整个桌面都是空白

        GetWindowThreadProcessId(listHwnd, out var pid);
        if (pid == 0) return true;
        var process = OpenProcess(ProcessVmOperation | ProcessVmRead | ProcessVmWrite, false, pid);
        if (process == IntPtr.Zero)
        {
            Logger.Warn($"打开 explorer 进程读取图标区域失败 (错误码 {Marshal.GetLastWin32Error()})，本次双击不隐藏图标");
            return true;
        }
        try
        {
            var remote = VirtualAllocEx(process, IntPtr.Zero, (nuint)RectSize, MemCommit, PageReadwrite);
            if (remote == IntPtr.Zero) return true;
            try
            {
                // 全零 RECT 入参 = LVIR_BOUNDS（完整包围矩形，含图标与文字标签），每次发送前需重置输入字段
                var zeroBuf = new byte[RectSize];
                var rectBuf = new byte[RectSize];
                var readOk = 0;
                for (var i = 0; i < count; i++)
                {
                    // 重置输入 RECT（left=0 即 LVIR_BOUNDS）。若写被拦截，则释放后重新 VirtualAllocEx
                    // （新页保证零初始化），而不是直接放弃该图标
                    if (!WriteProcessMemory(process, remote, zeroBuf, RectSize, IntPtr.Zero))
                    {
                        VirtualFreeEx(process, remote, nuint.Zero, MemRelease);
                        remote = VirtualAllocEx(process, IntPtr.Zero, (nuint)RectSize, MemCommit, PageReadwrite);
                        if (remote == IntPtr.Zero) break;
                    }
                    if (SendMessageTimeout(listHwnd, LvmGetItemRect, (IntPtr)i, remote,
                            SmtoBlock | SmtoAbortIfHung, 200, out _) == IntPtr.Zero) continue;
                    if (!ReadProcessMemory(process, remote, rectBuf, RectSize, IntPtr.Zero)) continue;
                    readOk++;
                    var left = BitConverter.ToInt32(rectBuf, 0) - IconRectPadding;
                    var top = BitConverter.ToInt32(rectBuf, 4) - IconRectPadding;
                    var right = BitConverter.ToInt32(rectBuf, 8) + IconRectPadding;
                    var bottom = BitConverter.ToInt32(rectBuf, 12) + IconRectPadding;
                    if (pt.X >= left && pt.X < right && pt.Y >= top && pt.Y < bottom) return true;
                }
                if (readOk == 0)
                {
                    Logger.Warn("所有桌面图标区域均读取失败，本次双击按图标区域处理，不隐藏图标");
                    return true;
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
