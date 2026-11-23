using System;
using System.IO;
using System.Text;
using System.Text.Json;

namespace CourseReminder.Data;

/// <summary>
/// 本地 JSON 存储：临时文件写入 → 原子替换；旧文件保留最近一次备份到 data/backup/。
/// </summary>
public static class JsonStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    /// <summary>读取；文件不存在或损坏时返回 fallback（损坏文件先备份到 backup/）。</summary>
    public static T Load<T>(string path, T fallback) where T : class
    {
        try
        {
            if (!File.Exists(path)) return fallback;
            var json = File.ReadAllText(path, Encoding.UTF8);
            var obj = JsonSerializer.Deserialize<T>(json, Options);
            return obj ?? fallback;
        }
        catch (Exception ex)
        {
            Log.Error("读取数据文件失败，使用默认值：" + path, ex);
            TryBackupCorrupt(path);
            return fallback;
        }
    }

    /// <summary>原子保存：写 .tmp（强制刷盘）→ 替换正式文件，旧文件进 backup/。</summary>
    public static void Save<T>(string path, T data)
    {
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        var bytes = new UTF8Encoding(false).GetBytes(JsonSerializer.Serialize(data, Options));

        // 先整块写入临时文件并 Flush(true)：断电时最多丢掉这次修改，
        // 不会出现「正式文件被截断半截 JSON」的情况。
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            fs.Write(bytes, 0, bytes.Length);
            fs.Flush(flushToDisk: true);
        }

        if (File.Exists(path))
        {
            // 旧文件先备份；备份只是兜底，失败不能阻断保存
            try
            {
                Directory.CreateDirectory(PortablePaths.BackupDir);
                var backupPath = Path.Combine(PortablePaths.BackupDir, Path.GetFileName(path) + ".bak");
                File.Copy(path, backupPath, overwrite: true);
            }
            catch (Exception ex)
            {
                Log.Error("备份旧数据文件失败（继续保存）：" + path, ex);
            }

            File.Move(tmp, path, overwrite: true);
        }
        else
        {
            File.Move(tmp, path);
        }
    }

    private static void TryBackupCorrupt(string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            Directory.CreateDirectory(PortablePaths.BackupDir);
            var name = Path.GetFileNameWithoutExtension(path) + "-corrupt-" +
                       DateTime.Now.ToString("yyyyMMddHHmmss") + Path.GetExtension(path);
            File.Copy(path, Path.Combine(PortablePaths.BackupDir, name), true);
        }
        catch
        {
            // 备份失败不抛出
        }
    }
}
