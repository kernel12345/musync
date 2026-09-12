using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
namespace MuSync.Win32Api;
internal static partial class User32
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
    [LibraryImport("user32.dll")]
    private static partial IntPtr GetForegroundWindow();
    [LibraryImport("user32.dll")]
    private static partial IntPtr GetDesktopWindow();
    [LibraryImport("user32.dll")]
    private static partial IntPtr GetShellWindow();
    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial int GetWindowRect(IntPtr hwnd, out Rect rc);
    [LibraryImport("user32.dll", EntryPoint = "FindWindowW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    internal static partial IntPtr FindWindow(string? lpClassName, string? lpWindowName);
    private delegate bool EnumWindowsProc(IntPtr hWnd, int lParam);
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnumWindows(EnumWindowsProc enumProc, IntPtr lParam);
    [LibraryImport("user32.dll", EntryPoint = "GetClassNameW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int GetClassName(IntPtr hwnd, [Out] char[] lpClassName, int nMaxCount);
    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int GetWindowText(IntPtr hWnd, [Out] char[] lpString, int nMaxCount);
    [LibraryImport("user32.dll")]
    private static partial int GetWindowTextLength(IntPtr hWnd);
    [LibraryImport("user32.dll")]
    internal static partial int GetWindowThreadProcessId(IntPtr handle, out int pid);
    private static string GetClassName(IntPtr hwnd)
    {
        var buffer = new char[256];
        var charsCopied = GetClassName(hwnd, buffer, buffer.Length);
        return charsCopied > 0 ? new string(buffer, 0, charsCopied) : string.Empty;
    }
    private static string GetWindowTitle(IntPtr hwnd)
    {
        var length = GetWindowTextLength(hwnd);
        if (length == 0) return string.Empty;
        var buffer = new char[length + 1];
        var charsCopied = GetWindowText(hwnd, buffer, buffer.Length);
        return charsCopied > 0 ? new string(buffer, 0, charsCopied) : string.Empty;
    }
    public static bool GetWindowTitle(string match, out string text, out int pid)
    {
        var title = string.Empty;
        var processId = 0;
        EnumWindows(delegate(IntPtr handle, int param)
            {
                var classname = GetClassName(handle);
                if (!match.Equals(classname, StringComparison.OrdinalIgnoreCase) ||
                    GetWindowThreadProcessId(handle, out var xpid) == 0 || xpid == 0)
                {
                    return true;
                }
                title = GetWindowTitle(handle);
                processId = xpid;
                return false;
            },
            IntPtr.Zero);
        text = title;
        pid = processId;
        return !string.IsNullOrEmpty(title) && pid > 0;
    }

    /// <summary>
    /// 读取鼠标当前操作的（前台窗口所属）应用名：优先取文件描述/产品名，失败时回退到进程名。
    /// 排除 MuSync 自身；前台无有效窗口时返回 null。
    /// </summary>
    public static string? GetForegroundAppName()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return null;
        if (GetWindowThreadProcessId(hwnd, out var pid) == 0 || pid == 0) return null;
        if (pid == Environment.ProcessId) return null;
        try
        {
            using var process = Process.GetProcessById(pid);
            var processName = process.ProcessName;
            try
            {
                var info = process.MainModule?.FileVersionInfo;
                if (!string.IsNullOrWhiteSpace(info?.FileDescription)) return info!.FileDescription;
                if (!string.IsNullOrWhiteSpace(info?.ProductName)) return info!.ProductName;
            }
            catch
            {
                // 系统/受保护进程无法读取模块信息时，回退到进程名
            }
            return string.IsNullOrEmpty(processName) ? null : processName;
        }
        catch
        {
            return null;
        }
    }
}