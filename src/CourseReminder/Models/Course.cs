using System;

namespace CourseReminder.Models;

/// <summary>一门课的一个上课安排（星期 + 大节 + 周次规则）。</summary>
public sealed class Course
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>星期：1=一 … 7=日。</summary>
    public int Weekday { get; set; }

    public int Slot { get; set; }
    public string Name { get; set; } = "";
    public string Room { get; set; } = "";
    public string Teacher { get; set; } = "";

    /// <summary>周次规则原文；模型按 WeekRule 解释。</summary>
    public string WeekRuleText { get; set; } = "";

    /// <summary>由 WeekRuleText 派生，仅供运行时使用，不写进 schedule.json。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public WeekRule WeekRule => WeekRule.Parse(WeekRuleText);

    /// <summary>提醒去重与展示用：某课某天的键。</summary>
    public string KeyFor(DateTime day) => day.ToString("yyyyMMdd") + "_" + Id + "_" + Slot;
}
