namespace CourseReminder.Models;

/// <summary>Excel 校验错误：定位到工作表 + Excel 行号 + 字段 + 原因。</summary>
public sealed class ImportError
{
    public string Sheet { get; set; } = "";
    public int Row { get; set; }
    public string Field { get; set; } = "";
    public string Reason { get; set; } = "";

    public override string ToString() => $"{Sheet} 第{Row}行 [{Field}]：{Reason}";
}
