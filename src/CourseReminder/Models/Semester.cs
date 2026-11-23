using System;

namespace CourseReminder.Models;

/// <summary>学期设置：第一周周一、结束日期。未配置时为空。</summary>
public sealed class Semester
{
    public DateTime? FirstMonday { get; set; }
    public DateTime? EndDate { get; set; }

    public bool IsConfigured => FirstMonday.HasValue && EndDate.HasValue;
}
