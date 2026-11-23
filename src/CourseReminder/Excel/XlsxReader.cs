using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Xml.Linq;

namespace CourseReminder.Excel;

/// <summary>xlsx 中的一行：行号（Excel 从 1 开始）+ 列号→单元格文本。</summary>
public sealed class XlsxRow
{
    public int RowNumber { get; init; }
    public Dictionary<int, string> Cells { get; } = new();

    public string Get(int col) => Cells.TryGetValue(col, out var v) ? v : "";

    public bool IsBlank => Cells.Values.All(string.IsNullOrWhiteSpace);
}

/// <summary>xlsx 中的一张工作表。</summary>
public sealed class XlsxSheet
{
    public string Name { get; init; } = "";
    public List<XlsxRow> Rows { get; } = new();

    public XlsxRow? GetRow(int number) => Rows.FirstOrDefault(r => r.RowNumber == number);
}

/// <summary>
/// 最小 xlsx 读取器：直接解开 OOXML 压缩包读 XML，只用 .NET 内置库，
/// 不依赖 ClosedXML/Office，保证完全离线。对应 NFR-001 / NFR-005。
/// </summary>
public static class XlsxReader
{
    private static readonly XNamespace MainNs = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace RelNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    public static IReadOnlyList<XlsxSheet> ReadAll(string path)
    {
        using var archive = ZipFile.OpenRead(path);

        var sharedStrings = ReadSharedStrings(archive);
        var rels = ReadRels(archive);

        var workbookEntry = archive.GetEntry("xl/workbook.xml")
            ?? throw new InvalidDataException("不是有效的 xlsx：缺少 xl/workbook.xml");

        using var workbookStream = workbookEntry.Open();
        var workbook = XDocument.Load(workbookStream);

        var sheets = new List<XlsxSheet>();
        foreach (var sheetNode in workbook.Descendants(MainNs + "sheet"))
        {
            var name = (string?)sheetNode.Attribute("name") ?? "";
            var relId = (string?)sheetNode.Attribute(RelNs + "id") ?? "";
            if (!rels.TryGetValue(relId, out var target))
            {
                continue;
            }

            var entryPath = NormalizeEntryPath(target);
            var entry = archive.GetEntry(entryPath);
            if (entry == null)
            {
                continue;
            }

            using var sheetStream = entry.Open();
            var sheetDoc = XDocument.Load(sheetStream);
            var parsed = new XlsxSheet { Name = name };

            // 有些导出工具（WPS/第三方报表）不写 r 属性；此时按出现顺序推断行号，
            // 否则整行会被静默丢掉，最后只剩「没有有效的课程数据行」这种难以定位的报错。
            var rowCursor = 0;
            foreach (var rowNode in sheetDoc.Descendants(MainNs + "row"))
            {
                rowCursor++;
                var rowNumberText = (string?)rowNode.Attribute("r");
                var rowNumber = int.TryParse(rowNumberText, out var parsedRow) && parsedRow >= 1
                    ? parsedRow
                    : rowCursor;

                var row = new XlsxRow { RowNumber = rowNumber };
                var colCursor = 0;
                foreach (var cellNode in rowNode.Elements(MainNs + "c"))
                {
                    var col = ColumnIndex((string?)cellNode.Attribute("r") ?? "");
                    if (col <= 0)
                    {
                        col = ++colCursor;
                    }
                    else
                    {
                        colCursor = col;
                    }

                    var text = CellText(cellNode, sharedStrings);
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        row.Cells[col] = text;
                    }
                }

                parsed.Rows.Add(row);
            }

            sheets.Add(parsed);
        }

        return sheets;
    }

    private static List<string> ReadSharedStrings(ZipArchive archive)
    {
        var result = new List<string>();
        var entry = archive.GetEntry("xl/sharedStrings.xml");
        if (entry == null) return result;

        using var stream = entry.Open();
        var doc = XDocument.Load(stream);
        foreach (var si in doc.Descendants(MainNs + "si"))
        {
            // 富文本由多个 <t> 组成，全部拼接；但要排除 <rPh> 里的注音文本，否则「重庆」会变成「重庆chongqing」
            result.Add(string.Concat(
                si.Descendants(MainNs + "t").Where(t => !t.Ancestors(MainNs + "rPh").Any()).Select(t => t.Value)));
        }

        return result;
    }

    private static Dictionary<string, string> ReadRels(ZipArchive archive)
    {
        var map = new Dictionary<string, string>();
        var entry = archive.GetEntry("xl/_rels/workbook.xml.rels");
        if (entry == null) return map;

        using var stream = entry.Open();
        var doc = XDocument.Load(stream);
        foreach (var rel in doc.Descendants())
        {
            var id = (string?)rel.Attribute("Id");
            var target = (string?)rel.Attribute("Target");
            if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(target))
            {
                map[id] = target;
            }
        }

        return map;
    }

    private static string NormalizeEntryPath(string target)
    {
        var t = target.Replace('\\', '/');
        if (t.StartsWith('/')) return t.TrimStart('/');
        if (t.StartsWith("xl/")) return t;
        return "xl/" + t;
    }

    private static string CellText(XElement cell, List<string> sharedStrings)
    {
        var type = (string?)cell.Attribute("t");
        if (type == "inlineStr")
        {
            return string.Concat(
                cell.Descendants(MainNs + "t").Where(t => !t.Ancestors(MainNs + "rPh").Any()).Select(t => t.Value));
        }

        var value = (string?)cell.Element(MainNs + "v") ?? "";
        if (type == "s")
        {
            return int.TryParse(value, out var idx) && idx >= 0 && idx < sharedStrings.Count
                ? sharedStrings[idx]
                : "";
        }

        return value;
    }

    /// <summary>把 "B12" 这类单元格引用转成 1 起的列号；无法解析返回 0。</summary>
    private static int ColumnIndex(string cellRef)
    {
        var letters = new string(cellRef.TakeWhile(char.IsLetter).ToArray()).ToUpperInvariant();
        if (letters.Length == 0) return 0;

        var col = 0;
        foreach (var ch in letters)
        {
            if (ch < 'A' || ch > 'Z') return 0;
            col = col * 26 + (ch - 'A' + 1);
        }

        return col;
    }
}
