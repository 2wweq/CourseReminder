using System;
using System.Collections.Generic;
using System.Linq;

namespace CourseReminder.Models;

/// <summary>已发送提醒去重状态，对应 data/state.json。</summary>
public sealed class ReminderState
{
    public List<string> SentKeys { get; set; } = new();

    /// <summary>去重：已发送的返回 false，新键加入并返回 true。</summary>
    public bool TryMarkSent(string key)
    {
        if (SentKeys.Contains(key)) return false;
        SentKeys.Add(key);
        return true;
    }

    /// <summary>清理 60 天前的旧键，避免文件无限增长。</summary>
    public void Trim(DateTime now)
    {
        var cutoff = now.AddDays(-60).ToString("yyyyMMdd");
        SentKeys = SentKeys.Where(k => string.CompareOrdinal(k, cutoff) >= 0).ToList();
    }
}
