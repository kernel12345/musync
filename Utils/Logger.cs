using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
namespace MuSync.Utils;

/// <summary>一条日志记录（供日志页实时显示）。</summary>
internal sealed class LogEntry
{
    public required DateTime Time { get; init; }
    public required string Level { get; init; }
    public required string Message { get; init; }
}

/// <summary>
/// 分级日志：Info/Warn/Error 写入 %LocalAppData%\MuSync\logs\ 下的按天日志文件（Release 也生效），
/// 并同步输出到调试器；Debug/Diagnose/Memory 仅 DEBUG 构建生效，供高频细节排查。
/// 同时在内存保留最近 <see cref="BufferCapacity"/> 条，并通过 <see cref="EntryLogged"/> 实时广播，
/// 供应用内「日志」页订阅（事件在写日志的原始线程触发，订阅方需自行切回 UI 线程）。
/// </summary>
internal static class Logger
{
    private const int BufferCapacity = 1000;
    private static readonly object SyncRoot = new();
    private static readonly Queue<LogEntry> Buffer = new();
    private static readonly string LogDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MuSync", "logs");
    private static string _currentDate = "";
    private static string? _currentFilePath;

    /// <summary>每条日志写入后触发（在调用方线程上），订阅方需自行处理线程封送。</summary>
    public static event Action<LogEntry>? EntryLogged;

    public static void Info(string message) => Write("INFO", message);

    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string message) => Write("ERROR", message);

    [Conditional("DEBUG")]
    public static void Debug(string message)
    {
        System.Diagnostics.Debug.WriteLine($"[DEBUG] {message}");
    }

    [Conditional("DEBUG")]
    public static void Diagnose(string message)
    {
        System.Diagnostics.Debug.WriteLine($"[DIAGNOSE] {message}");
    }

    [Conditional("DEBUG")]
    public static void Memory(string message)
    {
        System.Diagnostics.Debug.WriteLine($"[MEMORY] {message}");
    }

    /// <summary>取当前内存日志快照（新→旧顺序），供日志页打开时回填历史。</summary>
    public static LogEntry[] Snapshot()
    {
        lock (SyncRoot)
        {
            var arr = Buffer.ToArray();
            Array.Reverse(arr);
            return arr;
        }
    }

    /// <summary>当前按天日志文件所在目录（供“打开日志目录”按钮使用）。</summary>
    public static string DirectoryPath => LogDirectory;

    private static void Write(string level, string message)
    {
        // 调试器输出（DEBUG 构建可见，Release 下该调用被剥离）
        System.Diagnostics.Debug.WriteLine($"[{level}] {message}");
        LogEntry? entry = null;
        try
        {
            lock (SyncRoot)
            {
                var now = DateTime.Now;
                var date = now.ToString("yyyyMMdd");
                if (date != _currentDate)
                {
                    _currentDate = date;
                    _currentFilePath = null;
                }
                _currentFilePath ??= Path.Combine(LogDirectory, $"MuSync-{date}.log");
                Directory.CreateDirectory(LogDirectory);
                File.AppendAllText(_currentFilePath,
                    $"{now:HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}");

                entry = new LogEntry { Time = now, Level = level, Message = message };
                Buffer.Enqueue(entry);
                while (Buffer.Count > BufferCapacity) Buffer.Dequeue();
            }
        }
        catch
        {
            // 日志失败绝不影响主流程
        }
        // 事件在锁外触发，避免订阅方回调导致死锁
        if (entry != null)
        {
            try { EntryLogged?.Invoke(entry); } catch { /* 订阅方异常不影响日志 */ }
        }
    }
}
