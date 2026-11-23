using System;
using System.IO;
using System.Linq;
using System.Text;
using CourseReminder.Data;
using CourseReminder.Excel;
using CourseReminder.Models;
using CourseReminder.Services;

namespace CourseReminder;

/// <summary>
/// 自检：周次规则单测 + Excel 导入用例 + 业务层集成（导入落盘、星期匹配、提醒时刻、错过不补发、去重）。
/// 用 --selftest &lt;测试目录&gt; &lt;报告文件&gt; 触发，跑完直接退出。
/// </summary>
public static class SelfTest
{
    public static int Run(string testDir, string reportPath)
    {
        var sb = new StringBuilder();
        var fail = 0;

        sb.AppendLine("== 课表提醒 自检报告 ==");
        sb.AppendLine("时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));

        if (!Directory.Exists(testDir))
        {
            sb.AppendLine("测试目录不存在：" + testDir);
            File.WriteAllText(reportPath, sb.ToString(), Encoding.UTF8);
            return 2;
        }

        // ---- 1. 周次规则 ----
        sb.AppendLine();
        sb.AppendLine("[1] 周次规则");
        fail += Check(sb, "留空→每周", WeekRule.Parse("").IsActive(3), true);
        fail += Check(sb, "每周→每周", WeekRule.Parse("每周").IsActive(2), true);
        fail += Check(sb, "单周 第1周", WeekRule.Parse("单周").IsActive(1), true);
        fail += Check(sb, "单周 第2周", WeekRule.Parse("单周").IsActive(2), false);
        fail += Check(sb, "双周 第2周", WeekRule.Parse("双周").IsActive(2), true);
        fail += Check(sb, "双周 第1周", WeekRule.Parse("双周").IsActive(1), false);
        fail += Check(sb, "1-16 第16周", WeekRule.Parse("1-16").IsActive(16), true);
        fail += Check(sb, "1-16 第17周", WeekRule.Parse("1-16").IsActive(17), false);
        fail += Check(sb, "1,3,5 第3周", WeekRule.Parse("1,3,5").IsActive(3), true);
        fail += Check(sb, "1,3,5 第2周", WeekRule.Parse("1,3,5").IsActive(2), false);

        // ---- 2. Excel 导入 ----
        sb.AppendLine();
        sb.AppendLine("[2] Excel 导入");
        foreach (var file in Directory.GetFiles(testDir, "*.xlsx").OrderBy(f => f))
        {
            var name = Path.GetFileName(file);
            sb.AppendLine("--- " + name);
            try
            {
                var r = ScheduleExcelParser.Parse(file);
                if (r.Ok)
                {
                    sb.AppendLine("    通过：课程 " + r.Courses.Count + " 条，节次 " + r.Slots.Count + " 条");
                }
                else
                {
                    sb.AppendLine("    拦截 " + r.Errors.Count + " 条错误：");
                    foreach (var e in r.Errors)
                    {
                        sb.AppendLine("      " + e);
                    }
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine("    异常：" + ex.Message);
                fail++;
            }
        }

        // ---- 3. 扩展名校验 ----
        sb.AppendLine();
        sb.AppendLine("[3] 扩展名校验");
        var bad = ScheduleExcelParser.Parse(Path.Combine(testDir, "旧格式.xls"));
        var blocked = !bad.Ok && bad.Errors.Any(e => e.Reason.Contains(".xls"));
        sb.AppendLine("    .xls 被拦截：" + (blocked ? "是" : "否"));
        if (!blocked) fail++;

        // ---- 4. 业务层集成 ----
        sb.AppendLine();
        sb.AppendLine("[4] 业务层集成");
        fail += RunBusinessTests(sb, testDir);

        sb.AppendLine();
        sb.AppendLine(fail == 0 ? "结论：全部通过" : "结论：失败 " + fail + " 项");
        File.WriteAllText(reportPath, sb.ToString(), Encoding.UTF8);
        return fail == 0 ? 0 : 1;
    }

    private static int RunBusinessTests(StringBuilder sb, string testDir)
    {
        var fail = 0;
        // 测试数据根放在项目目录内，不写 C 盘
        var testRoot = Path.Combine(testDir, "_selftest-data");
        if (Directory.Exists(testRoot)) Directory.Delete(testRoot, true);
        PortablePaths.UseTestRoot(testRoot);
        PortablePaths.EnsureCreated();

        try
        {
            var schedule = new ScheduleService();

            // 4.1 导入预览 → 确认落盘
            var legal = Path.Combine(testDir, "01-合法模板.xlsx");
            var preview = schedule.ImportPreview(legal);
            fail += Check(sb, "导入预览解析成功", preview.Ok, true);
            fail += Check(sb, "预览阶段未写入课表", schedule.Current.Courses.Count, 0);

            schedule.ConfirmPendingImport();
            var reloaded = new ScheduleService();
            fail += Check(sb, "确认后课表已落盘", reloaded.Current.Courses.Count, 14);
            fail += Check(sb, "节次已落盘", reloaded.Current.Slots.Count, 6);
            fail += Check(sb, "确认后暂存已清空", schedule.PendingImport == null, true);

            // 4.2 失败导入不得覆盖旧课表
            var broken = Path.Combine(testDir, "07-同槽冲突.xlsx");
            var badPreview = reloaded.ImportPreview(broken);
            fail += Check(sb, "冲突文件被拦截", badPreview.Ok, false);
            fail += Check(sb, "失败后旧课表保持", reloaded.Current.Courses.Count, 14);

            // 4.3 星期匹配（周一第2大节 = 高等数学 10:00）
            var monday = FindWeekday(new DateTime(2026, 11, 23), DayOfWeek.Monday);
            var mondayCourses = reloaded.CoursesOn(monday);
            fail += Check(sb, "周一课程数", mondayCourses.Count, 3);
            fail += Check(sb, "周一第1节为高等数学", mondayCourses[0].Name, "高等数学");
            fail += Check(sb, "周一第1节开始 10:00", reloaded.SlotStart(2), new TimeSpan(10, 0, 0));

            // 周日连堂 4 节
            var sunday = FindWeekday(monday, DayOfWeek.Sunday);
            fail += Check(sb, "周日连堂课程数", reloaded.CoursesOn(sunday).Count, 4);

            // 4.4 提醒时刻：提前 40 分钟，09:19 不提醒 / 09:20 提醒
            reloaded.Settings.AdvanceMinutes = 40;
            var at0919 = monday.AddHours(9).AddMinutes(19);
            var at0920 = monday.AddHours(9).AddMinutes(20);
            fail += Check(sb, "09:19 不提醒", ReminderCalculator.Evaluate(reloaded, at0919, 40).Count, 0);
            fail += Check(sb, "09:20 提醒 1 条", ReminderCalculator.Evaluate(reloaded, at0920, 40).Count, 1);
            fail += Check(sb, "09:20 提醒到第2大节",
                ReminderCalculator.Evaluate(reloaded, at0920, 40)[0].Course.Name, "高等数学");

            // 4.5 错过不补发：10:05 已在课上
            var at1005 = monday.AddHours(10).AddMinutes(5);
            fail += Check(sb, "10:05 不补发", ReminderCalculator.Evaluate(reloaded, at1005, 40).Count, 0);

            // 4.6 去重：标记已发后不再返回
            var key = ReminderCalculator.Evaluate(reloaded, at0920, 40)[0].Key;
            reloaded.State.TryMarkSent(key);
            fail += Check(sb, "已发送后不重复", ReminderCalculator.Evaluate(reloaded, at0920, 40).Count, 0);

            // 4.7 状态持久化
            reloaded.SaveState();
            var reopened = new ScheduleService();
            fail += Check(sb, "去重键已持久化", reopened.State.SentKeys.Contains(key), true);

            // 4.8 数据只写在指定根目录
            fail += Check(sb, "schedule.json 存在", File.Exists(PortablePaths.ScheduleFile), true);
            fail += Check(sb, "数据根为测试目录", PortablePaths.DataDir.StartsWith(testRoot), true);

            // 4.9 时间格式化回归（曾因 TimeSpan 格式串冒号未转义抛 FormatException）
            fail += Check(sb, "节次时间文本", reloaded.SlotRangeText(2), "10:00–11:30");
            fail += Check(sb, "未知节次文本", reloaded.SlotRangeText(99), "未知节次");
            fail += Check(sb, "TimeSpan 格式化可用",
                new TimeSpan(8, 5, 0).ToString(@"hh\:mm"), "08:05");

            // 4.10 本周视图矩阵（列＝周一~周日，行＝节次）
            fail += Check(sb, "周一计算正确",
                ScheduleService.MondayOf(new DateTime(2026, 11, 22)), new DateTime(2026, 11, 16));

            var matrix = reloaded.WeekMatrix(monday);
            fail += Check(sb, "周矩阵格子数", matrix.Count, reloaded.Current.Slots.Count * 7);

            var cellMon2 = matrix.FirstOrDefault(c => c.Weekday == 1 && c.Slot == 2);
            fail += Check(sb, "周一第2大节有课", cellMon2?.HasCourse, true);
            fail += Check(sb, "周一第2大节课程名", cellMon2?.CourseName, "高等数学");
            fail += Check(sb, "周一第2大节本周生效", cellMon2?.IsActive, true);

            var cellMon1 = matrix.FirstOrDefault(c => c.Weekday == 1 && c.Slot == 1);
            fail += Check(sb, "周一第1大节无课", cellMon1?.HasCourse, false);

            // 4.11 提醒卡片文案（路线 C 自绘卡片，纯文本部分可在此断言）
            var sample = new Course { Weekday = 1, Slot = 2, Name = "高等数学", Room = "教三-201" };
            var (title, body) = Notifications.ToastNotifier.BuildToastText(
                sample, new TimeSpan(10, 0, 0).ToString(@"hh\:mm"), reloaded.SlotRangeText(2), reloaded.WeekdayText(1));
            fail += Check(sb, "提醒标题", title, "课前提醒：高等数学");
            fail += Check(sb, "提醒正文含上课时间", body.Contains("10:00 上课"), true);
            fail += Check(sb, "提醒正文含地点", body.Contains("地点：教三-201"), true);
            fail += Check(sb, "提醒正文含节次", body.Contains("节次：第 2 大节（10:00–11:30）"), true);

            // ---- 5. 学期边界 / 同槽互补课 / 跨天调度（v0.1.5 修复回归）----
            sb.AppendLine();
            sb.AppendLine("[5] 学期与提醒边界");

            reloaded.Settings.Semester = new Semester
            {
                FirstMonday = new DateTime(2026, 9, 7),
                EndDate = new DateTime(2026, 12, 27)
            };
            fail += Check(sb, "学期总周数", reloaded.TotalWeeks, 16);
            fail += Check(sb, "学期内第12周", reloaded.TeachingWeek(new DateTime(2026, 11, 23)), 12);
            fail += Check(sb, "开学前算学期外", reloaded.TeachingWeek(new DateTime(2026, 8, 31)), 0);
            fail += Check(sb, "开学前不返回课程", reloaded.CoursesOn(new DateTime(2026, 8, 31)).Count, 0);
            fail += Check(sb, "学期后不返回课程", reloaded.CoursesOn(new DateTime(2027, 1, 4)).Count, 0);
            fail += Check(sb, "学期后不产生提醒",
                ReminderCalculator.Evaluate(reloaded, new DateTime(2027, 1, 4, 9, 0, 0), 40).Count, 0);

            // 5.2 跨天：周日 23:50、提前 700 分钟 → 周一 10:00 的课在周日就该提醒
            // 4.6 已把 11-23 这门课的键标记为已发，跨天断言前需清空，否则会被去重逻辑挡住
            reloaded.State.SentKeys.Clear();
            var crossNow = new DateTime(2026, 11, 22, 23, 50, 0);
            var cross = ReminderCalculator.Evaluate(reloaded, crossNow, 700);
            fail += Check(sb, "跨天提醒条数", cross.Count, 1);
            fail += Check(sb, "跨天提醒键为周一日期",
                cross.Count > 0 && cross[0].Key.StartsWith("20261123"), true);

            // 5.3 同槽互补课：周矩阵保留两门，按单双周分别生效
            reloaded.Current.Courses.Add(new Course
            {
                Weekday = 6, Slot = 1, Name = "单周课", Room = "A101", WeekRuleText = "单周"
            });
            reloaded.Current.Courses.Add(new Course
            {
                Weekday = 6, Slot = 1, Name = "双周课", Room = "B202", WeekRuleText = "双周"
            });

            var pairCell = reloaded.WeekMatrix(new DateTime(2026, 11, 23))
                .FirstOrDefault(c => c.Weekday == 6 && c.Slot == 1);
            fail += Check(sb, "同槽互补课门数", pairCell?.Courses.Count, 2);
            fail += Check(sb, "单周课第12周不上",
                pairCell?.Courses.FirstOrDefault(x => x.Name == "单周课")?.IsActive, false);
            fail += Check(sb, "双周课第12周上",
                pairCell?.Courses.FirstOrDefault(x => x.Name == "双周课")?.IsActive, true);

            // ---- 6. 周次规则校验、重叠判定、手动录入增删改（v0.1.6 回归）----
            sb.AppendLine();
            sb.AppendLine("[6] 手动录入与规则校验");

            fail += Check(sb, "IsValidText 留空=每周", WeekRule.IsValidText(""), true);
            fail += Check(sb, "IsValidText 单周", WeekRule.IsValidText("单周"), true);
            fail += Check(sb, "IsValidText 1-16", WeekRule.IsValidText("1-16"), true);
            fail += Check(sb, "IsValidText 1,3,5", WeekRule.IsValidText("1,3,5"), true);
            fail += Check(sb, "IsValidText 0 拒绝", WeekRule.IsValidText("0"), false);
            fail += Check(sb, "IsValidText 61-70 拒绝", WeekRule.IsValidText("61-70"), false);
            fail += Check(sb, "IsValidText 乱写拒绝", WeekRule.IsValidText("隔周上课"), false);

            fail += Check(sb, "Overlaps 每周×单周",
                WeekRule.Overlaps(WeekRule.Parse(""), WeekRule.Parse("单周")), true);
            fail += Check(sb, "Overlaps 单周×双周",
                WeekRule.Overlaps(WeekRule.Parse("单周"), WeekRule.Parse("双周")), false);
            fail += Check(sb, "Overlaps 1-16×17-20",
                WeekRule.Overlaps(WeekRule.Parse("1-16"), WeekRule.Parse("17-20")), false);
            fail += Check(sb, "Overlaps 1-16×12-20",
                WeekRule.Overlaps(WeekRule.Parse("1-16"), WeekRule.Parse("12-20")), true);

            // 6.2 已有节次时不再补默认节次
            fail += Check(sb, "已有节次不补默认", reloaded.EnsureDefaultSlots(), false);

            // 6.3 手动新增 → 冲突拦截 → 修改 → 删除
            var beforeManual = reloaded.Current.Courses.Count;
            var manual = new Course { Weekday = 3, Slot = 3, Name = "手动课", Room = "C303", WeekRuleText = "1-16" };
            var addError = reloaded.UpsertCourse(manual, null, out var newId);
            fail += Check(sb, "手动新增无错误", addError, null);
            fail += Check(sb, "手动新增门数+1", reloaded.Current.Courses.Count, beforeManual + 1);
            fail += Check(sb, "手动新增已落盘",
                new ScheduleService().Current.Courses.Any(c => c.Id == newId), true);

            var dup = new Course { Weekday = 3, Slot = 3, Name = "重复课", Room = "C303", WeekRuleText = "5-10" };
            fail += Check(sb, "同槽重叠被拦截", reloaded.UpsertCourse(dup, null, out _) != null, true);

            var edited = new Course { Weekday = 3, Slot = 3, Name = "手动课改名", Room = "C304" };
            fail += Check(sb, "修改课程无错误", reloaded.UpsertCourse(edited, newId, out var sameId), null);
            fail += Check(sb, "修改保持原 Id", sameId, newId);
            fail += Check(sb, "修改已落盘",
                new ScheduleService().Current.Courses.First(c => c.Id == newId).Name, "手动课改名");

            fail += Check(sb, "删除课程成功", reloaded.DeleteCourse(newId), true);
            fail += Check(sb, "删除后门数复原", reloaded.Current.Courses.Count, beforeManual);
            fail += Check(sb, "删除后再删返回 false", reloaded.DeleteCourse(newId), false);
        }
        catch (Exception ex)
        {
            sb.AppendLine("    异常：" + ex);
            fail++;
        }

        return fail;
    }

    private static DateTime FindWeekday(DateTime from, DayOfWeek target)
    {
        var d = from.Date;
        while (d.DayOfWeek != target) d = d.AddDays(1);
        return d;
    }

    private static int Check(StringBuilder sb, string name, object? actual, object? expected)
    {
        var ok = Equals(actual?.ToString(), expected?.ToString());
        sb.AppendLine("    " + (ok ? "OK  " : "FAIL") + " " + name +
                      "（期望 " + expected + "，实际 " + actual + "）");
        return ok ? 0 : 1;
    }
}
