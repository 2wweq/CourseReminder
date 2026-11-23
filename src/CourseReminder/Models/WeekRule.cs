using System;

namespace CourseReminder.Models;

/// <summary>
/// 周次规则：每周 / 单周 / 双周 / 指定周次范围（如 1-16）。
/// 由字符串解析；无法解析时视为每周，并记录是否明确指定。
/// </summary>
public sealed class WeekRule
{
    public const string Every = "每周";

    /// <summary>原始文本（留空或「每周」= 每周）。</summary>
    public string Raw { get; init; } = "";

    /// <summary>是否明确指定了规则（非留空、非「每周」）。</summary>
    public bool IsRestricted { get; init; }

    /// <summary>单周。与 IsOddWeek 配合：null 表示未启用该模式。</summary>
    public bool IsOddWeek { get; init; }

    /// <summary>双周。与 IsEvenWeek 配合：null 表示未启用该模式。</summary>
    public bool IsEvenWeek { get; init; }

    /// <summary>指定周次集合（1 起）；null 表示未启用该模式。</summary>
    public System.Collections.Generic.HashSet<int>? SpecifiedWeeks { get; init; }

    /// <summary>把模板中的周次规则文本解析为 WeekRule。</summary>
    public static WeekRule Parse(string? raw)
    {
        var text = (raw ?? "").Trim();
        if (text.Length == 0 || text == Every)
        {
            return new WeekRule { Raw = text };
        }

        if (text == "单周" || text == "奇数周")
        {
            return new WeekRule { Raw = text, IsRestricted = true, IsOddWeek = true };
        }

        if (text == "双周" || text == "偶数周")
        {
            return new WeekRule { Raw = text, IsRestricted = true, IsEvenWeek = true };
        }

        // 范围形式：1-16 或 1,3,5 或 1-16,18
        var weeks = new System.Collections.Generic.HashSet<int>();
        var ok = true;
        foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var dash = part.IndexOf('-');
            if (dash > 0 && int.TryParse(part[..dash], out var a) && int.TryParse(part[(dash + 1)..], out var b))
            {
                var (lo, hi) = a <= b ? (a, b) : (b, a);
                if (lo < 1 || hi > 60) { ok = false; break; }
                for (var w = lo; w <= hi; w++) weeks.Add(w);
            }
            else if (int.TryParse(part, out var single) && single >= 1 && single <= 60)
            {
                weeks.Add(single);
            }
            else
            {
                ok = false;
                break;
            }
        }

        if (!ok || weeks.Count == 0)
        {
            // 无法解析：按每周处理，但保留原文供展示
            return new WeekRule { Raw = text };
        }

        return new WeekRule { Raw = text, IsRestricted = true, SpecifiedWeeks = weeks };
    }

    /// <summary>判断给定教学周是否生效。第一周为 1。</summary>
    public bool IsActive(int teachingWeek)
    {
        if (teachingWeek < 1) return false;
        if (!IsRestricted) return true;
        if (IsOddWeek) return teachingWeek % 2 == 1;
        if (IsEvenWeek) return teachingWeek % 2 == 0;
        if (SpecifiedWeeks != null) return SpecifiedWeeks.Contains(teachingWeek);
        return true;
    }

    /// <summary>文本是否是合法的周次规则（供手动录入校验，避免把错字静默当「每周」）。</summary>
    public static bool IsValidText(string? raw)
    {
        var text = (raw ?? "").Trim();
        if (text.Length == 0 || text == Every) return true;
        if (text is "单周" or "奇数周" or "双周" or "偶数周") return true;

        var any = false;
        foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var dash = part.IndexOf('-');
            if (dash > 0 && int.TryParse(part[..dash], out var a) && int.TryParse(part[(dash + 1)..], out var b))
            {
                var (lo, hi) = a <= b ? (a, b) : (b, a);
                if (lo < 1 || hi > 60) return false;
                any = true;
            }
            else if (int.TryParse(part, out var single) && single >= 1 && single <= 60)
            {
                any = true;
            }
            else
            {
                return false;
            }
        }

        return any;
    }

    /// <summary>两条规则是否有任何一周同时生效（单周 vs 双周 → false）。</summary>
    public static bool Overlaps(WeekRule a, WeekRule b)
    {
        if (!a.IsRestricted || !b.IsRestricted) return true;

        if (a.IsOddWeek && b.IsOddWeek) return true;
        if (a.IsEvenWeek && b.IsEvenWeek) return true;
        if (a.IsOddWeek && b.IsEvenWeek) return false;
        if (a.IsEvenWeek && b.IsOddWeek) return false;

        if (a.SpecifiedWeeks != null && b.SpecifiedWeeks != null) return a.SpecifiedWeeks.Overlaps(b.SpecifiedWeeks);
        if (a.SpecifiedWeeks != null && b.IsOddWeek) return a.SpecifiedWeeks.Any(w => w % 2 == 1);
        if (a.SpecifiedWeeks != null && b.IsEvenWeek) return a.SpecifiedWeeks.Any(w => w % 2 == 0);
        if (b.SpecifiedWeeks != null && a.IsOddWeek) return b.SpecifiedWeeks.Any(w => w % 2 == 1);
        if (b.SpecifiedWeeks != null && a.IsEvenWeek) return b.SpecifiedWeeks.Any(w => w % 2 == 0);

        return true;
    }

    public override string ToString() => Raw.Length == 0 ? Every : Raw;
}
