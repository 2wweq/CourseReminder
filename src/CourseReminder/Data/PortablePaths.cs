using System;
using System.IO;

namespace CourseReminder.Data;

/// <summary>
/// 便携数据根目录：一律 exe 同级 data/，禁止回落到 C 盘用户目录。
/// 对应需求 NFR-006（数据位置）。
/// </summary>
public static class PortablePaths
{
    private static string? _overrideRoot;

    /// <summary>exe 所在目录（发布后即程序文件夹）。</summary>
    public static string Root => _overrideRoot ?? AppContext.BaseDirectory;

    public static string DataDir => Path.Combine(Root, "data");
    public static string ScheduleFile => Path.Combine(DataDir, "schedule.json");
    public static string SettingsFile => Path.Combine(DataDir, "settings.json");
    public static string StateFile => Path.Combine(DataDir, "state.json");
    public static string LogsDir => Path.Combine(DataDir, "logs");
    public static string BackupDir => Path.Combine(DataDir, "backup");

    /// <summary>仅供自检使用：把数据根临时指到别处，避免污染正式数据。</summary>
    public static void UseTestRoot(string root) => _overrideRoot = root;

    /// <summary>
    /// 确保 data 目录结构存在。
    /// 创建失败时抛出带中文指引的异常，由启动流程提示用户挪目录；绝不静默改用 C 盘。
    /// </summary>
    public static void EnsureCreated()
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            Directory.CreateDirectory(LogsDir);
            Directory.CreateDirectory(BackupDir);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "无法在程序文件夹内创建数据目录：" + DataDir + "\n" +
                "请把整个程序文件夹移动到可写目录（如 D 盘，不要放在 Program Files），再重新启动。\n" +
                "原因：" + ex.Message, ex);
        }
    }
}
