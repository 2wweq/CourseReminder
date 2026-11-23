using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CourseReminder.Data;
using CourseReminder.Excel;
using CourseReminder.Models;

namespace CourseReminder.Services;

/// <summary>
/// 课表主服务：加载/保存、Excel 导入（暂存-确认-原子替换）、今日课程列表。
/// </summary>
public sealed class ScheduleService
{
    public ScheduleData Current { get; private set; } = new();
    public Settings Settings { get; private set; } = new();
    public ReminderState State { get; private set; } = new();

    /// <summary>Excel 导入后暂存的结果，用户在预览页确认后才落盘。</summary>
    public ParseResult? PendingImport { get; private set; }

    public ScheduleService()
    {
        try
        {
            Current = JsonStore.Load(PortablePaths.ScheduleFile, new ScheduleData());
            Settings = JsonStore.Load(PortablePaths.SettingsFile, new Settings());
            State = JsonStore.Load(PortablePaths.StateFile, new ReminderState());
        }
        catch (Exception ex)
        {
            Log.Error("启动加载数据失败，使用空数据继续", ex);
            Current = new ScheduleData();
            Settings = new Settings();
            State = new ReminderState();
        }
    }

    // ---------- 保存 ----------
    public void SaveSchedule() => JsonStore.Save(PortablePaths.ScheduleFile, Current);
    public void SaveSettings() => JsonStore.Save(PortablePaths.SettingsFile, Settings);
    public void SaveState() => JsonStore.Save(PortablePaths.StateFile, State);

    /// <summary>
    /// 应用新设置：先落盘成功，再替换内存对象；落盘失败时内存保持不变。
    /// 调用方负责提前校验（提前量范围、学期日期）。
    /// </summary>
    public void UpdateSettings(Settings next)
    {
        JsonStore.Save(PortablePaths.SettingsFile, next);
        Settings = next;
    }

    // ---------- Excel 导入 ----------
    /// <summary>
    /// 解析 Excel 并暂存结果；不直接覆盖现有课表。
    /// 若解析失败，旧课表保持不变。
    /// </summary>
    public ParseResult ImportPreview(string path)
    {
        var result = ScheduleExcelParser.Parse(path);
        if (result.Ok)
        {
            PendingImport = result;
        }
        return result;
    }

    /// <summary>
    /// 用户确认暂存结果后，原子替换 schedule.json。
    /// 成功后 PendingImport 清空。
    /// </summary>
    public void ConfirmPendingImport()
    {
        if (PendingImport == null || !PendingImport.Ok) return;

        var data = new ScheduleData
        {
            Courses = PendingImport.Courses,
            Slots = PendingImport.Slots,
            ImportedAt = DateTime.Now
        };

        Current = data;
        SaveSchedule();
        ClearReminderState();
        PendingImport = null;
    }

    public void CancelPendingImport() => PendingImport = null;

    // ---------- 手动录入 ----------
    /// <summary>手动录入的兜底节次（与模板一致），只在完全没有节次表时使用。</summary>
    private static readonly (int Slot, string Start, string End)[] DefaultSlotRows =
    {
        (1, "08:10", "09:40"),
        (2, "10:00", "11:30"),
        (3, "13:30", "15:00"),
        (4, "15:20", "16:50"),
        (5, "17:00", "18:30"),
        (6, "18:40", "20:10")
    };

    /// <summary>没有任何节次时补齐默认节次并落盘（无 Excel 也能手动录入的前提）。</summary>
    public bool EnsureDefaultSlots()
    {
        if (Current.Slots.Count > 0) return false;

        foreach (var (slot, start, end) in DefaultSlotRows)
        {
            Current.Slots.Add(new TimeSlot
            {
                Slot = slot,
                Start = TimeSpan.Parse(start, System.Globalization.CultureInfo.InvariantCulture),
                End = TimeSpan.Parse(end, System.Globalization.CultureInfo.InvariantCulture)
            });
        }

        SaveSchedule();
        return true;
    }

    /// <summary>手动新增（originalId 为 null）或修改一门课；返回错误文本，成功返回 null 并回传课程 Id。</summary>
    public string? UpsertCourse(Course course, string? originalId, out string savedId)
    {
        savedId = originalId ?? "";

        var conflict = Current.Courses.FirstOrDefault(c =>
            c.Id != originalId && c.Weekday == course.Weekday && c.Slot == course.Slot &&
            WeekRule.Overlaps(c.WeekRule, course.WeekRule));

        if (conflict != null)
        {
            return "与「" + conflict.Name + "」在同一星期同一大节同时生效，请调整周次规则或换个时间。";
        }

        var existing = originalId == null ? null : Current.Courses.FirstOrDefault(c => c.Id == originalId);
        if (existing == null)
        {
            Current.Courses.Add(course);
            savedId = course.Id;
        }
        else
        {
            existing.Weekday = course.Weekday;
            existing.Slot = course.Slot;
            existing.Name = course.Name;
            existing.Room = course.Room;
            existing.Teacher = course.Teacher;
            existing.WeekRuleText = course.WeekRuleText;
            savedId = existing.Id;
        }

        SaveSchedule();
        ClearReminderState();
        return null;
    }

    /// <summary>手动删除一门课。</summary>
    public bool DeleteCourse(string id)
    {
        var course = Current.Courses.FirstOrDefault(c => c.Id == id);
        if (course == null) return false;

        Current.Courses.Remove(course);
        SaveSchedule();
        ClearReminderState();
        return true;
    }

    /// <summary>课表变更后清空去重键，让改过的课能重新提醒。</summary>
    private void ClearReminderState()
    {
        if (State.SentKeys.Count == 0) return;
        State.SentKeys.Clear();
        SaveState();
    }

    // ---------- 业务查询 ----------
    /// <summary>学期第一周周一（已对齐到所在周的周一）。</summary>
    public DateTime? FirstMondayAligned =>
        Settings.Semester.FirstMonday.HasValue ? MondayOf(Settings.Semester.FirstMonday.Value) : null;

    /// <summary>学期总教学周数（按结束日期所在周计）；未配置学期返回 0。</summary>
    public int TotalWeeks
    {
        get
        {
            var sem = Settings.Semester;
            if (!sem.IsConfigured) return 0;

            var monday = MondayOf(sem.FirstMonday!.Value);
            var days = (sem.EndDate!.Value.Date - monday).TotalDays;
            if (days < 0) return 0;
            return (int)Math.Floor(days / 7.0) + 1;
        }
    }

    /// <summary>给定日期是否落在学期内（未配置学期时视为始终在学期内）。</summary>
    public bool InSemester(DateTime day) => TeachingWeek(day) > 0;

    /// <summary>当前学期周数；未配置学期时返回 1，学期开始前/结束后返回 0。</summary>
    public int TeachingWeek(DateTime now)
    {
        var sem = Settings.Semester;
        if (!sem.IsConfigured) return 1;

        var monday = MondayOf(sem.FirstMonday!.Value);
        var diff = now.Date - monday;
        var week = (int)Math.Floor(diff.TotalDays / 7.0) + 1;
        return week >= 1 && week <= TotalWeeks ? week : 0;
    }

    /// <summary>返回某一天生效的课程（含学期与周次过滤），按开始时间排序。</summary>
    public List<Course> CoursesOn(DateTime day)
    {
        var week = TeachingWeek(day);
        if (week < 1) return new List<Course>();

        var weekday = ((int)day.DayOfWeek + 6) % 7 + 1; // 周一=1 ... 周日=7

        return Current.Courses
            .Where(c => c.Weekday == weekday && c.WeekRule.IsActive(week))
            .OrderBy(c => SlotStart(c.Slot))
            .ToList();
    }

    /// <summary>今天剩余（含当前正在上的）课程。</summary>
    public List<Course> TodayRemaining(DateTime now)
    {
        return CoursesOn(now.Date)
            .Where(c => SlotEnd(c.Slot) > now.TimeOfDay)
            .ToList();
    }

    public TimeSpan SlotStart(int slot) => Current.Slots.FirstOrDefault(s => s.Slot == slot)?.Start ?? TimeSpan.Zero;
    public TimeSpan SlotEnd(int slot) => Current.Slots.FirstOrDefault(s => s.Slot == slot)?.End ?? TimeSpan.Zero;

    public string SlotRangeText(int slot)
    {
        var s = Current.Slots.FirstOrDefault(x => x.Slot == slot);
        // 注意：TimeSpan 自定义格式里的冒号必须转义（\\:），否则抛 FormatException
        return s == null ? "未知节次" : s.Start.ToString(@"hh\:mm") + "–" + s.End.ToString(@"hh\:mm");
    }

    /// <summary>该日期所在周的周一。</summary>
    public static DateTime MondayOf(DateTime date) =>
        date.Date.AddDays(-(((int)date.DayOfWeek + 6) % 7));

    /// <summary>
    /// 组装一周视图矩阵：列＝周一~周日，行＝节次表里的每个大节。
    /// 本周不上课的格子也保留（HasCourse=true、IsActive=false），供界面显示灰色「本周不上」。
    /// </summary>
    public List<WeekCell> WeekMatrix(DateTime anyDayInWeek)
    {
        var monday = MondayOf(anyDayInWeek);
        var week = TeachingWeek(monday);
        var cells = new List<WeekCell>();

        foreach (var slot in Current.Slots.OrderBy(s => s.Slot))
        {
            for (var weekday = 1; weekday <= 7; weekday++)
            {
                // 同一星期同一大节可能有多门课（单周/双周互补），全部保留
                var courses = Current.Courses
                    .Where(c => c.Weekday == weekday && c.Slot == slot.Slot)
                    .ToList();

                var cell = new WeekCell
                {
                    Weekday = weekday,
                    Slot = slot.Slot,
                    HasCourse = courses.Count > 0
                };

                foreach (var c in courses)
                {
                    cell.Courses.Add(new WeekCourse
                    {
                        Name = c.Name,
                        Room = c.Room,
                        WeekRuleText = c.WeekRuleText,
                        IsActive = week >= 1 && c.WeekRule.IsActive(week)
                    });
                }

                var first = cell.Courses.FirstOrDefault();
                cell.CourseName = first?.Name ?? "";
                cell.Room = first?.Room ?? "";
                cell.IsActive = cell.Courses.Any(x => x.IsActive);
                cells.Add(cell);
            }
        }

        return cells;
    }

    public string WeekdayText(int weekday)
    {
        var map = new[] { "一", "二", "三", "四", "五", "六", "日" };
        return weekday >= 1 && weekday <= 7 ? map[weekday - 1] : "?";
    }
}

/// <summary>一周视图的单个格子。</summary>
public sealed class WeekCell
{
    public int Weekday { get; set; }
    public int Slot { get; set; }
    /// <summary>该星期该大节是否有课（不管本周是否生效）。</summary>
    public bool HasCourse { get; set; }
    /// <summary>本周是否至少有一门课生效。</summary>
    public bool IsActive { get; set; }
    /// <summary>第一门课的名字（兼容旧字段；界面请用 Courses）。</summary>
    public string CourseName { get; set; } = "";
    /// <summary>第一门课的教室（兼容旧字段）。</summary>
    public string Room { get; set; } = "";
    /// <summary>该格子里的全部课程（单双周互补课时会有多门）。</summary>
    public List<WeekCourse> Courses { get; set; } = new();
}

/// <summary>周视图格子里的单门课。</summary>
public sealed class WeekCourse
{
    public string Name { get; set; } = "";
    public string Room { get; set; } = "";
    public string WeekRuleText { get; set; } = "";
    public bool IsActive { get; set; }
}

/// <summary>课表数据持久结构。</summary>
public sealed class ScheduleData
{
    public List<Course> Courses { get; set; } = new();
    public List<TimeSlot> Slots { get; set; } = new();
    public DateTime ImportedAt { get; set; }
}
