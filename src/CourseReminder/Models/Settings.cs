using System;

namespace CourseReminder.Models;

/// <summary>应用设置，对应 data/settings.json。</summary>
public sealed class Settings
{
    /// <summary>提前分钟数，默认 40。</summary>
    public int AdvanceMinutes { get; set; } = 40;

    // 开机自启状态以注册表 HKCU\...\Run 为准，不写入 settings.json：
    // 两边各存一份必然出现「文件说已开启、注册表其实是关的」这种不一致。
    public bool PlaySound { get; set; } = true;

    public Semester Semester { get; set; } = new();
}
