using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using CourseReminder.Models;
using CourseReminder.Services;

namespace CourseReminder.Views;

/// <summary>
/// 手动录入/编辑课表：左侧列出已有课程，右侧表单增删改。
/// 与 Excel 导入共用同一份 schedule.json，导入会整份替换。
/// </summary>
public partial class CourseEditorWindow : Window
{
    private readonly ScheduleService _schedule;
    private readonly List<int> _slotValues = new();

    private string? _editingId;
    private bool _changed;

    public CourseEditorWindow(ScheduleService schedule)
    {
        InitializeComponent();
        _schedule = schedule;
    }

    /// <summary>关闭时课表是否被改动（主窗口据此刷新）。</summary>
    public bool Changed => _changed;

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (_schedule.EnsureDefaultSlots())
        {
            EditorStatus.Foreground = System.Windows.Media.Brushes.DarkGreen;
            EditorStatus.Text = "当前没有课表，已自动补上一套默认节次时间（第 1~6 大节），可直接录入课程。";
        }

        foreach (var name in new[] { "周一", "周二", "周三", "周四", "周五", "周六", "周日" })
        {
            WeekdayCombo.Items.Add(name);
        }

        _slotValues.Clear();
        SlotCombo.Items.Clear();
        foreach (var slot in _schedule.Current.Slots.OrderBy(s => s.Slot))
        {
            _slotValues.Add(slot.Slot);
            SlotCombo.Items.Add("第 " + slot.Slot + " 大节（" + _schedule.SlotRangeText(slot.Slot) + "）");
        }

        WeekdayCombo.SelectedIndex = 0;
        if (SlotCombo.Items.Count > 0) SlotCombo.SelectedIndex = 0;

        ReloadCourses(null);
    }

    private void ReloadCourses(string? selectId)
    {
        var rows = _schedule.Current.Courses
            .OrderBy(c => c.Weekday)
            .ThenBy(c => c.Slot)
            .ThenBy(c => c.Name)
            .Select(c => new CourseRow
            {
                Id = c.Id,
                Text = "周" + _schedule.WeekdayText(c.Weekday) + "  第 " + c.Slot + " 大节  " + c.Name +
                       "  @ " + c.Room +
                       (c.WeekRuleText.Length == 0 ? "  [每周]" : "  [" + c.WeekRuleText + "]")
            })
            .ToList();

        CourseList.ItemsSource = rows;
        if (selectId != null)
        {
            CourseList.SelectedItem = rows.FirstOrDefault(r => r.Id == selectId);
        }
    }

    private void CourseList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CourseList.SelectedItem is not CourseRow row) return;

        var course = _schedule.Current.Courses.FirstOrDefault(c => c.Id == row.Id);
        if (course == null) return;

        _editingId = course.Id;
        EditorTitle.Text = "编辑课程";
        WeekdayCombo.SelectedIndex = Math.Clamp(course.Weekday - 1, 0, 6);

        var slotIndex = _slotValues.IndexOf(course.Slot);
        SlotCombo.SelectedIndex = slotIndex >= 0 ? slotIndex : 0;

        NameBox.Text = course.Name;
        RoomBox.Text = course.Room;
        TeacherBox.Text = course.Teacher;
        WeekRuleBox.Text = course.WeekRuleText;

        SaveBtn.IsEnabled = true;
        DeleteBtn.IsEnabled = true;
        EditorStatus.Text = "";
        EditorStatus.Foreground = System.Windows.Media.Brushes.DarkRed;
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        CourseList.SelectedItem = null;
        _editingId = null;

        EditorTitle.Text = "新增课程";
        WeekdayCombo.SelectedIndex = 0;
        if (SlotCombo.Items.Count > 0) SlotCombo.SelectedIndex = 0;
        NameBox.Text = "";
        RoomBox.Text = "";
        TeacherBox.Text = "";
        WeekRuleBox.Text = "";

        SaveBtn.IsEnabled = true;
        DeleteBtn.IsEnabled = false;
        EditorStatus.Text = "";
        EditorStatus.Foreground = System.Windows.Media.Brushes.DarkRed;
        NameBox.Focus();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        var room = RoomBox.Text.Trim();
        var teacher = TeacherBox.Text.Trim();
        var ruleText = WeekRuleBox.Text.Trim().Replace("星期", "");

        if (name.Length == 0)
        {
            ShowError("课程名不能为空。");
            NameBox.Focus();
            return;
        }

        if (room.Length == 0)
        {
            ShowError("教室不能为空。");
            RoomBox.Focus();
            return;
        }

        if (WeekdayCombo.SelectedIndex < 0)
        {
            ShowError("请选择星期。");
            return;
        }

        if (SlotCombo.SelectedIndex < 0 || SlotCombo.SelectedIndex >= _slotValues.Count)
        {
            ShowError("请选择大节（若没有节次，请先导入一次 Excel 或重开本窗口自动补默认节次）。");
            return;
        }

        if (!WeekRule.IsValidText(ruleText))
        {
            ShowError("周次规则无法识别，请用 每周 / 单周 / 双周 / 1-16 / 1,3,5 这样的写法。");
            WeekRuleBox.Focus();
            return;
        }

        var course = new Course
        {
            Weekday = WeekdayCombo.SelectedIndex + 1,
            Slot = _slotValues[SlotCombo.SelectedIndex],
            Name = name,
            Room = room,
            Teacher = teacher,
            WeekRuleText = ruleText
        };

        string? error;
        string savedId;
        try
        {
            error = _schedule.UpsertCourse(course, _editingId, out savedId);
        }
        catch (Exception ex)
        {
            ShowError("保存失败：" + ex.Message);
            return;
        }

        if (error != null)
        {
            ShowError(error);
            return;
        }

        _editingId = savedId;
        _changed = true;
        ReloadCourses(savedId);

        EditorStatus.Foreground = System.Windows.Media.Brushes.DarkGreen;
        EditorStatus.Text = "已保存到 data/schedule.json。";
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (CourseList.SelectedItem is not CourseRow row) return;

        var confirm = MessageBox.Show(this, "确定删除「" + row.Text + "」吗？", "删除课程",
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        try
        {
            _schedule.DeleteCourse(row.Id);
        }
        catch (Exception ex)
        {
            ShowError("删除失败：" + ex.Message);
            return;
        }

        _changed = true;
        _editingId = null;
        ReloadCourses(null);
        Add_Click(sender, e);

        EditorStatus.Foreground = System.Windows.Media.Brushes.DarkGreen;
        EditorStatus.Text = "已删除。";
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void ShowError(string message)
    {
        EditorStatus.Foreground = System.Windows.Media.Brushes.DarkRed;
        EditorStatus.Text = message;
    }

    /// <summary>列表项：只用于展示与定位 Id。</summary>
    public sealed class CourseRow
    {
        public string Id { get; set; } = "";
        public string Text { get; set; } = "";
        public override string ToString() => Text;
    }
}
