using System;
using System.Collections.Generic;
using System.Linq;
using CourseReminder.Models;

namespace CourseReminder.Services;

public sealed class PendingReminder
{
    public required Course Course { get; init; }
    public required TimeSpan ClassStart { get; init; }
    public required DateTime DueAt { get; init; }
    public required string Key { get; init; }
}

/// <summary>
/// 提醒计算：每分钟扫描一次，找出 dueAt 已到且未发送的提醒。
/// 对应需求 NFR-002（误差 ≤ 1 分钟）。
/// </summary>
public static class ReminderCalculator
{
    /// <summary>
    /// 返回「现在应该提醒但还没提醒」的课程列表。
    /// 算法：扫描今天与明天两天的课程（提前量可能跨到前一天，故需看明天），
    /// 计算 dueAt = 上课时刻 - 提前量；若 now ≥ dueAt 且未到上课时刻（错过不补发），且未发送过，则返回。
    /// </summary>
    public static List<PendingReminder> Evaluate(
        ScheduleService schedule,
        DateTime now,
        int advanceMinutes)
    {
        var result = new List<PendingReminder>();
        var advance = TimeSpan.FromMinutes(advanceMinutes);

        foreach (var day in new[] { now.Date, now.Date.AddDays(1) })
        {
            if (schedule.TeachingWeek(day) < 1) continue;

            foreach (var course in schedule.CoursesOn(day))
            {
                var startTime = schedule.SlotStart(course.Slot);
                var classStart = day + startTime;

                // 错过不补发：已经到上课时刻的不再提醒
                if (now >= classStart) continue;

                var dueAt = classStart - advance;
                if (now < dueAt) continue;

                var key = course.KeyFor(day);
                if (schedule.State.SentKeys.Contains(key)) continue;

                result.Add(new PendingReminder
                {
                    Course = course,
                    ClassStart = startTime,
                    DueAt = dueAt,
                    Key = key
                });
            }
        }

        return result;
    }
}
