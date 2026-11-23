using System;

namespace CourseReminder.Models;

/// <summary>节次时间：大节编号与起止时间。</summary>
public sealed class TimeSlot
{
    public int Slot { get; set; }
    public TimeSpan Start { get; set; }
    public TimeSpan End { get; set; }
}
