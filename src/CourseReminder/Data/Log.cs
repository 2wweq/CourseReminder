using System;
using System.IO;
using System.Text;

namespace CourseReminder.Data;

/// <summary>极简本地日志：写入 data/logs/yyyy-MM-dd.log；日志失败不影响主流程。</summary>
public static class Log
{
    private static readonly object Gate = new();

    public static void Info(string message) => Write("INFO", message);

    public static void Error(string message, Exception? ex = null)
        => Write("ERROR", ex == null ? message : message + "\n" + ex);

    private static void Write(string level, string message)
    {
        try
        {
            var file = Path.Combine(PortablePaths.LogsDir, DateTime.Now.ToString("yyyy-MM-dd") + ".log");
            var line = DateTime.Now.ToString("HH:mm:ss") + " [" + level + "] " + message + Environment.NewLine;
            lock (Gate)
            {
                File.AppendAllText(file, line, Encoding.UTF8);
            }
        }
        catch
        {
            // 日志写不进去时不抛出，避免影响主流程
        }
    }
}
