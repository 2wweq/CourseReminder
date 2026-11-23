using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using CourseReminder.Models;

namespace CourseReminder.Excel;

/// <summary>解析结果：成功则 Deliverables 可用；失败时 Errors 定位到工作表与行号。</summary>
public sealed class ParseResult
{
    public List<Course> Courses { get; } = new();
    public List<TimeSlot> Slots { get; } = new();
    public List<ImportError> Errors { get; } = new();

    public bool Ok => Errors.Count == 0;

    public string ErrorSummary()
    {
        var lines = Errors.Select(e => "· " + e).ToList();
        return lines.Count == 0 ? "无错误" : string.Join(Environment.NewLine, lines);
    }
}

/// <summary>
/// 解析约定模板 .xlsx：工作表「课程」「节次时间」。
/// 校验规则见需求文档 FR-002 / 技术方案 4.2。
/// </summary>
public static class ScheduleExcelParser
{
    public const string CourseSheet = "课程";
    public const string SlotSheet = "节次时间";

    private static readonly string[] CourseHeaders = { "星期", "大节", "课程名", "教室", "教师", "周次规则" };
    private static readonly string[] SlotHeaders = { "大节", "开始时间", "结束时间" };

    public static ParseResult Parse(string path)
    {
        var result = new ParseResult();

        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            AddError(result, "-", 0, "文件", "文件不存在或路径为空");
            return result;
        }

        if (!path.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
        {
            AddError(result, "-", 0, "文件", "只支持 .xlsx；旧版 .xls 请先另存为 .xlsx");
            return result;
        }

        IReadOnlyList<XlsxSheet> sheets;
        try
        {
            sheets = XlsxReader.ReadAll(path);
        }
        catch (Exception ex)
        {
            AddError(result, "-", 0, "文件", "无法读取，文件可能已损坏或被其他程序占用：" + ex.Message);
            return result;
        }

        var courseSheet = sheets.FirstOrDefault(s => s.Name == CourseSheet);
        var slotSheet = sheets.FirstOrDefault(s => s.Name == SlotSheet);

        if (courseSheet == null)
        {
            AddError(result, CourseSheet, 1, "工作表", "缺少「" + CourseSheet + "」工作表");
        }

        if (slotSheet == null)
        {
            AddError(result, SlotSheet, 1, "工作表", "缺少「" + SlotSheet + "」工作表");
        }

        if (courseSheet == null || slotSheet == null)
        {
            return result;
        }

        var courseHeader = FindHeader(courseSheet, CourseHeaders, CourseSheet, result);
        var slotHeader = FindHeader(slotSheet, SlotHeaders, SlotSheet, result);
        if (courseHeader == null || slotHeader == null)
        {
            return result;
        }

        ParseSlots(slotSheet, slotHeader, result);
        ParseCourses(courseSheet, courseHeader, result);

        return result;
    }

    // ---------- 表头 ----------
    /// <summary>表头定位结果：列名→列号映射 + 表头所在行号。</summary>
    private sealed record HeaderInfo(Dictionary<string, int> Columns, int RowNumber);

    /// <summary>
    /// 在前 10 个非空行里找表头：真实课表常有「2026 秋季学期课表」这类标题行或空行，
    /// 只认第一个非空行会导致明明有数据却报「缺少列」。
    /// </summary>
    private static HeaderInfo? FindHeader(
        XlsxSheet sheet, string[] required, string sheetName, ParseResult result)
    {
        var candidates = sheet.Rows.Where(r => !r.IsBlank).Take(10).ToList();
        if (candidates.Count == 0)
        {
            AddError(result, sheetName, 1, "表头", "工作表为空，缺少表头行");
            return null;
        }

        Dictionary<string, int>? best = null;
        var bestRowNumber = candidates[0].RowNumber;
        var bestHit = -1;

        foreach (var row in candidates)
        {
            var map = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var pair in row.Cells)
            {
                var text = pair.Value.Trim();
                if (text.Length == 0) continue;
                if (!map.ContainsKey(text))
                {
                    map[text] = pair.Key;
                }
            }

            var hit = required.Count(map.ContainsKey);
            if (hit > bestHit)
            {
                bestHit = hit;
                best = map;
                bestRowNumber = row.RowNumber;
            }

            if (hit == required.Length)
            {
                return new HeaderInfo(map, row.RowNumber);
            }
        }

        var missing = required.Where(h => !best!.ContainsKey(h)).ToList();
        AddError(result, sheetName, bestRowNumber, "表头",
            "缺少列：" + string.Join("、", missing) + "（表头请填写：" + string.Join(" | ", required) + "）");
        return null;
    }

    // ---------- 节次时间 ----------
    private static void ParseSlots(XlsxSheet sheet, HeaderInfo header, ParseResult result)
    {
        var cols = header.Columns;
        var headerRowNumber = header.RowNumber;

        foreach (var row in sheet.Rows.Where(r => r.RowNumber > headerRowNumber && !r.IsBlank))
        {
            var slotText = row.Get(cols["大节"]).Trim();
            if (slotText.Length == 0)
            {
                AddError(result, SlotSheet, row.RowNumber, "大节", "大节不能为空");
                continue;
            }

            if (!TryPositiveInt(slotText, out var slot))
            {
                AddError(result, SlotSheet, row.RowNumber, "大节", "大节必须是正整数，当前为「" + slotText + "」");
                continue;
            }

            var startText = row.Get(cols["开始时间"]).Trim();
            var endText = row.Get(cols["结束时间"]).Trim();

            if (!TryTime(startText, out var start))
            {
                AddError(result, SlotSheet, row.RowNumber, "开始时间",
                    "时间格式应为 HH:mm，当前为「" + startText + "」");
                continue;
            }

            if (!TryTime(endText, out var end))
            {
                AddError(result, SlotSheet, row.RowNumber, "结束时间",
                    "时间格式应为 HH:mm，当前为「" + endText + "」");
                continue;
            }

            if (end <= start)
            {
                AddError(result, SlotSheet, row.RowNumber, "结束时间", "结束时间必须晚于开始时间");
                continue;
            }

            if (result.Slots.Any(t => t.Slot == slot))
            {
                AddError(result, SlotSheet, row.RowNumber, "大节", "大节 " + slot + " 重复");
                continue;
            }

            result.Slots.Add(new TimeSlot { Slot = slot, Start = start, End = end });
        }

        if (result.Slots.Count == 0)
        {
            AddError(result, SlotSheet, headerRowNumber, "大节", "「节次时间」没有任何有效数据行");
        }
    }

    // ---------- 课程 ----------
    private static void ParseCourses(XlsxSheet sheet, HeaderInfo header, ParseResult result)
    {
        var cols = header.Columns;
        var headerRowNumber = header.RowNumber;
        var validSlots = result.Slots.Select(t => t.Slot).ToHashSet();

        foreach (var row in sheet.Rows.Where(r => r.RowNumber > headerRowNumber && !r.IsBlank))
        {
            var weekdayText = row.Get(cols["星期"]).Trim();
            var slotText = row.Get(cols["大节"]).Trim();
            var name = row.Get(cols["课程名"]).Trim();
            var room = row.Get(cols["教室"]).Trim();
            var teacher = cols.ContainsKey("教师") ? row.Get(cols["教师"]).Trim() : "";
            var weekRuleText = cols.ContainsKey("周次规则") ? row.Get(cols["周次规则"]).Trim() : "";

            // 整行都空则跳过（已在过滤里排除），但字段全空时仍按缺失报错
            var anyContent = weekdayText.Length + slotText.Length + name.Length + room.Length > 0;
            if (!anyContent) continue;

            var weekRule = WeekRule.Parse(weekRuleText);
            var weekRuleInvalid = IsWeekRuleInvalid(weekRuleText, weekRule);

            var rowValid = true;

            if (!TryWeekday(weekdayText, out var weekday))
            {
                AddError(result, CourseSheet, row.RowNumber, "星期",
                    "应为 一/二/三/四/五/六/日（或 1~7），当前为「" + weekdayText + "」");
                rowValid = false;
            }

            if (!TryPositiveInt(slotText, out var slot))
            {
                AddError(result, CourseSheet, row.RowNumber, "大节",
                    "大节必须是正整数，当前为「" + slotText + "」");
                rowValid = false;
            }
            else if (validSlots.Count > 0 && !validSlots.Contains(slot))
            {
                AddError(result, CourseSheet, row.RowNumber, "大节",
                    "第 " + slot + " 大节在「节次时间」中找不到对应时间");
                rowValid = false;
            }

            if (name.Length == 0)
            {
                AddError(result, CourseSheet, row.RowNumber, "课程名", "课程名不能为空");
                rowValid = false;
            }

            if (room.Length == 0)
            {
                AddError(result, CourseSheet, row.RowNumber, "教室", "教室不能为空");
                rowValid = false;
            }

            if (weekRuleInvalid)
            {
                AddError(result, CourseSheet, row.RowNumber, "周次规则",
                    "无法识别的周次规则「" + weekRuleText + "」，请用 每周/单周/双周 或 1-16 这样的范围");
                rowValid = false;
            }

            if (!rowValid) continue;

            var course = new Course
            {
                Weekday = weekday,
                Slot = slot,
                Name = name,
                Room = room,
                Teacher = teacher,
                WeekRuleText = weekRuleText
            };

            // 同一星期同一大节：已有课中只要有一门周次重叠就拦截（可能存在单双周互补的两门课）
            var conflict = result.Courses.FirstOrDefault(c =>
                c.Weekday == weekday && c.Slot == slot && WeekRulesOverlap(c.WeekRule, course.WeekRule));
            if (conflict != null)
            {
                AddError(result, CourseSheet, row.RowNumber, "星期/大节",
                    "与「" + conflict.Name + "」在同一星期同一大节同时生效，请调整周次规则或改时间");
                continue;
            }

            result.Courses.Add(course);
        }

        if (result.Courses.Count == 0 && result.Errors.Count == 0)
        {
            AddError(result, CourseSheet, headerRowNumber, "课程", "没有有效的课程数据行");
        }
    }

    // ---------- 工具 ----------
    private static bool IsWeekRuleInvalid(string raw, WeekRule rule)
    {
        var text = (raw ?? "").Trim();
        if (text.Length == 0) return false;
        if (text == WeekRule.Every || text == "单周" || text == "双周" ||
            text == "奇数周" || text == "偶数周") return false;
        // 解析后既不是每周也不是受限 → 说明无法识别
        return !rule.IsRestricted;
    }

    private static bool WeekRulesOverlap(WeekRule a, WeekRule b) => WeekRule.Overlaps(a, b);

    private static bool TryPositiveInt(string text, out int value)
    {
        value = 0;
        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)) return false;
        if (parsed < 1) return false;
        value = parsed;
        return true;
    }

    private static bool TryWeekday(string text, out int weekday)
    {
        weekday = 0;
        var t = text.Trim().Replace("星期", "").Trim();
        var map = new Dictionary<string, int>
        {
            ["一"] = 1, ["二"] = 2, ["三"] = 3, ["四"] = 4, ["五"] = 5, ["六"] = 6, ["日"] = 7, ["天"] = 7
        };

        if (map.TryGetValue(t, out weekday)) return true;
        if (int.TryParse(t, out var n) && n >= 1 && n <= 7)
        {
            weekday = n;
            return true;
        }

        return false;
    }

    /// <summary>支持 "08:10"、"8:10"、"08:10:00"，也支持 Excel 内部存储的小数（一天的比例）。</summary>
    private static bool TryTime(string text, out TimeSpan value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var t = text.Trim();

        if (t.Contains(':'))
        {
            var parts = t.Split(':');
            if (parts.Length is 2 or 3)
            {
                if (int.TryParse(parts[0], out var h) && int.TryParse(parts[1], out var m))
                {
                    var s = 0;
                    if (parts.Length == 3 && !int.TryParse(parts[2], out s)) return false;
                    if (h < 0 || h > 23 || m < 0 || m > 59 || s < 0 || s > 59) return false;
                    value = new TimeSpan(h, m, s);
                    return true;
                }
            }

            return false;
        }

        // Excel 时间单元格常以一天的比例存储，如 0.3402777 = 08:10
        if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var fraction) &&
            fraction >= 0 && fraction < 1)
        {
            value = TimeSpan.FromSeconds(Math.Round(fraction * 86400));
            return true;
        }

        return false;
    }

    private static void AddError(ParseResult result, string sheet, int row, string field, string reason)
        => result.Errors.Add(new ImportError { Sheet = sheet, Row = row, Field = field, Reason = reason });
}
