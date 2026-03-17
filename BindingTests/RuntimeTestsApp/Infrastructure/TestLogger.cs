// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Text;

namespace RuntimeTestsApp.Infrastructure;

/// <summary>
/// Structured logging for test output with categories and timestamps.
/// </summary>
public static class TestLogger
{
    private static readonly object _lock = new();
    private static readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private static readonly StringBuilder _fullLog = new();

    public enum Category
    {
        Info,
        Success,
        Warning,
        Error,
        Memory,
        Perf,
        Test,
        Debug
    }

    public static void Log(Category category, string message)
    {
        var timestamp = _stopwatch.Elapsed.TotalSeconds;
        var prefix = category switch
        {
            Category.Success => "[PASS]",
            Category.Warning => "[WARN]",
            Category.Error => "[FAIL]",
            Category.Memory => "[MEM]",
            Category.Perf => "[PERF]",
            Category.Test => "[TEST]",
            Category.Debug => "[DBG]",
            _ => "[INFO]"
        };

        var line = $"[{timestamp:F3}s] {prefix} {message}";

        lock (_lock)
        {
            Console.WriteLine(line);
            _fullLog.AppendLine(line);
        }
    }

    public static void Info(string message) => Log(Category.Info, message);
    public static void Success(string message) => Log(Category.Success, message);
    public static void Warning(string message) => Log(Category.Warning, message);
    public static void Error(string message) => Log(Category.Error, message);
    public static void Memory(string message) => Log(Category.Memory, message);
    public static void Perf(string message) => Log(Category.Perf, message);
    public static void Test(string message) => Log(Category.Test, message);
    public static void Debug(string message) => Log(Category.Debug, message);

    public static void Exception(Exception ex, string context = "")
    {
        var prefix = string.IsNullOrEmpty(context) ? "" : $"{context}: ";
        Error($"{prefix}{ex.GetType().Name}: {ex.Message}");
        if (ex.InnerException != null)
        {
            Error($"  Inner: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
        }
        if (!string.IsNullOrEmpty(ex.StackTrace))
        {
            Debug($"  Stack: {ex.StackTrace.Split('\n').FirstOrDefault()?.Trim()}");
        }
    }

    public static string GetFullLog()
    {
        lock (_lock)
        {
            return _fullLog.ToString();
        }
    }

    public static void Clear()
    {
        lock (_lock)
        {
            _fullLog.Clear();
        }
        _stopwatch.Restart();
    }

    /// <summary>
    /// Returns elapsed time since logger start/clear.
    /// </summary>
    public static TimeSpan Elapsed => _stopwatch.Elapsed;
}
