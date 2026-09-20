using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace Autoroadmarking_Pro.CadHost.Cad.Quantities
{
    /// <summary>
    /// Xuất snapshot khối lượng ARM thành workbook XLSX 4 sheet mà không phụ thuộc
    /// Microsoft Office/COM. Số liệu "sơn thực" luôn dùng PaintedLength/PaintedArea;
    /// Length/Area chỉ là hình học gốc phục vụ kiểm tra truy vết.
    /// </summary>
    public sealed class QuantityExcelExporter
    {
        private const string SummarySheetName = "TỔNG HỢP";
        private const string RoadSheetName = "THEO TUYẾN";
        private const string NodeSheetName = "THEO NÚT";
        private const string DetailSheetName = "CHI TIẾT";

        public string Export(
            List<CadQuantityRow> rows,
            string fileName,
            string drawingPath)
        {
            List<CadQuantityRow> safeRows = rows ?? new List<CadQuantityRow>();
            string path = ResolveOutputPath(fileName, drawingPath);

            if (File.Exists(path))
                File.Delete(path);

            using (var stream = new FileStream(
                       path,
                       FileMode.CreateNew,
                       FileAccess.ReadWrite,
                       FileShare.None))
            using (var archive = new ZipArchive(
                       stream,
                       ZipArchiveMode.Create))
            {
                Write(archive, "[Content_Types].xml", ContentTypes());
                Write(archive, "_rels/.rels", RootRelationships());
                Write(archive, "docProps/app.xml", AppProperties());
                Write(archive, "docProps/core.xml", CoreProperties());
                Write(archive, "xl/workbook.xml", Workbook());
                Write(archive, "xl/_rels/workbook.xml.rels", WorkbookRelationships());
                Write(archive, "xl/styles.xml", Styles());

                Write(
                    archive,
                    "xl/worksheets/sheet1.xml",
                    Worksheet(BuildSummary(safeRows)));

                Write(
                    archive,
                    "xl/worksheets/sheet2.xml",
                    Worksheet(BuildByRoad(safeRows)));

                Write(
                    archive,
                    "xl/worksheets/sheet3.xml",
                    Worksheet(BuildByNode(safeRows)));

                Write(
                    archive,
                    "xl/worksheets/sheet4.xml",
                    Worksheet(BuildDetail(safeRows)));
            }

            return path;
        }

        private static string ResolveOutputPath(
            string fileName,
            string drawingPath)
        {
            string folder = string.Empty;

            if (!string.IsNullOrWhiteSpace(drawingPath))
            {
                folder =
                    Path.GetDirectoryName(drawingPath) ??
                    string.Empty;
            }

            if (string.IsNullOrWhiteSpace(folder) ||
                !Directory.Exists(folder))
            {
                folder = Path.Combine(
                    Environment.GetFolderPath(
                        Environment.SpecialFolder.MyDocuments),
                    "AutoRoadMarking",
                    "Exports");
            }

            Directory.CreateDirectory(folder);

            string resolvedName =
                string.IsNullOrWhiteSpace(fileName)
                    ? "Khoi_luong_vach_son.xlsx"
                    : fileName.Trim();

            if (!resolvedName.EndsWith(
                    ".xlsx",
                    StringComparison.OrdinalIgnoreCase))
            {
                resolvedName += ".xlsx";
            }

            return Path.Combine(
                folder,
                resolvedName);
        }

        private static List<List<object>> BuildSummary(
            IReadOnlyCollection<CadQuantityRow> rows)
        {
            int roadCount = rows
                .Where(x => string.Equals(x.QuantityScope, "ROAD", StringComparison.OrdinalIgnoreCase))
                .Select(RoadGroupingKey)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();

            int nodeCount = rows
                .Where(IsIntersection)
                .Select(NodeGroupingKey)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();

            int supplementaryCount = rows.Count(x =>
                string.Equals(x.QuantityScope, "SUPPLEMENTARY", StringComparison.OrdinalIgnoreCase));

            return new List<List<object>>
            {
                Header("Chỉ tiêu", "Giá trị", "Đơn vị / ghi chú"),
                Row("Số đối tượng quản lý", rows.Count, "entity"),
                Row("Số tuyến", roadCount, "Road Axis"),
                Row("Số nút giao", nodeCount, "NodeKey ổn định"),
                Row("Đối tượng phát sinh", supplementaryCount, "entity"),
                Row("Vạch mép nút", rows.Count(x => x.QuantityCategory.Equals("INTERSECTION_EDGE", StringComparison.OrdinalIgnoreCase)), "entity"),
                Row("Vạch dừng 7.1", rows.Count(x => x.QuantityCategory.Equals("INTERSECTION_STOP", StringComparison.OrdinalIgnoreCase)), "entity"),
                Row("Vạch đi bộ 7.3", rows.Count(x => x.QuantityCategory.Equals("INTERSECTION_CROSSWALK", StringComparison.OrdinalIgnoreCase)), "dải sơn"),
                Row(
                    "Chiều dài hình học",
                    Round(rows.Sum(x => x.Length)),
                    "m"),
                Row(
                    "Chiều dài sơn thực",
                    Round(rows.Sum(x => x.PaintedLength)),
                    "m"),
                Row(
                    "Diện tích sơn thực",
                    Round(rows.Sum(x => x.PaintedArea)),
                    "m²"),
                Row(
                    "Block / ký hiệu",
                    rows.Sum(x => x.Count),
                    "cái"),
                Row(
                    "Số mã vạch / block",
                    rows.Select(x => x.Code)
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Count(),
                    "mã"),
                Row(
                    "Đối tượng nhận dạng bằng ManualLayerRule",
                    rows.Count(x =>
                        string.Equals(
                            x.Source,
                            "MANUAL_LAYER_RULE",
                            StringComparison.OrdinalIgnoreCase)),
                    "cần ưu tiên gắn ARM metadata")
            };
        }

        private static List<List<object>> BuildByRoad(
            IEnumerable<CadQuantityRow> rows)
        {
            var table = new List<List<object>>
            {
                Header(
                    "Tuyến",
                    "AxisKey",
                    "Nhóm khối lượng",
                    "Vai trò",
                    "Mã",
                    "Số entity",
                    "Chiều dài hình học (m)",
                    "Sơn thực (m)",
                    "Diện tích sơn (m²)",
                    "Block (cái)")
            };

            var groups = rows
                .Where(x => !IsIntersection(x) &&
                            !string.Equals(x.QuantityScope, "SUPPLEMENTARY", StringComparison.OrdinalIgnoreCase))
                .GroupBy(x => new
                {
                    Road = RoadGroupingKey(x),
                    Group = NormalizeKey(x.QuantityGroup),
                    Role = NormalizeKey(x.QuantityRole),
                    Code = NormalizeKey(x.Code)
                })
                .OrderBy(g => DisplayRoadName(g), StringComparer.OrdinalIgnoreCase)
                .ThenBy(g => g.Key.Group, StringComparer.OrdinalIgnoreCase)
                .ThenBy(g => g.Key.Code, StringComparer.OrdinalIgnoreCase);

            foreach (var group in groups)
            {
                table.Add(Row(
                    DisplayRoadName(group),
                    DisplayAxisKey(group),
                    FirstNonBlank(group.Select(x => x.QuantityGroup)),
                    FirstNonBlank(group.Select(x => x.QuantityRole)),
                    FirstNonBlank(group.Select(x => x.Code)),
                    group.Count(),
                    Round(group.Sum(x => x.Length)),
                    Round(group.Sum(x => x.PaintedLength)),
                    Round(group.Sum(x => x.PaintedArea)),
                    group.Sum(x => x.Count)));
            }

            return table;
        }

        private static List<List<object>> BuildByNode(
            IEnumerable<CadQuantityRow> rows)
        {
            var table = new List<List<object>>
            {
                Header(
                    "Nút / NodeKey",
                    "Tuyến liên quan",
                    "AxisKey",
                    "Nhóm khối lượng",
                    "Vai trò",
                    "Mã",
                    "Số entity",
                    "Chiều dài hình học (m)",
                    "Sơn thực (m)",
                    "Diện tích sơn (m²)",
                    "Block (cái)")
            };

            var groups = rows
                .Where(IsIntersection)
                .GroupBy(x => new
                {
                    Node = NormalizeKey(x.OwnerId),
                    Group = NormalizeKey(x.QuantityGroup),
                    Role = NormalizeKey(x.QuantityRole),
                    Code = NormalizeKey(x.Code)
                })
                .OrderBy(g => g.Key.Node, StringComparer.OrdinalIgnoreCase)
                .ThenBy(g => g.Key.Group, StringComparer.OrdinalIgnoreCase)
                .ThenBy(g => g.Key.Code, StringComparer.OrdinalIgnoreCase);

            foreach (var group in groups)
            {
                table.Add(Row(
                    FirstNonBlank(group.Select(x => x.OwnerId)),
                    JoinDistinct(group.Select(x => x.RoadName)),
                    DisplayAxisKey(group),
                    FirstNonBlank(group.Select(x => x.QuantityGroup)),
                    FirstNonBlank(group.Select(x => x.QuantityRole)),
                    FirstNonBlank(group.Select(x => x.Code)),
                    group.Count(),
                    Round(group.Sum(x => x.Length)),
                    Round(group.Sum(x => x.PaintedLength)),
                    Round(group.Sum(x => x.PaintedArea)),
                    group.Sum(x => x.Count)));
            }

            return table;
        }

        private static List<List<object>> BuildDetail(
            IEnumerable<CadQuantityRow> rows)
        {
            var table = new List<List<object>>
            {
                Header(
                    "RecordId",
                    "Handle",
                    "Tuyến",
                    "AxisKey",
                    "Loại TIM",
                    "Phạm vi",
                    "OwnerId / NodeKey",
                    "Nhóm khối lượng",
                    "Phân loại",
                    "Vai trò",
                    "Mã",
                    "Layer mẫu",
                    "Layer CAD",
                    "Bề rộng (m)",
                    "Chiều dài hình học (m)",
                    "PaintRatio",
                    "Sơn thực (m)",
                    "Diện tích sơn (m²)",
                    "SL",
                    "ĐVT",
                    "Nguồn",
                    "Cảnh báo")
            };

            foreach (CadQuantityRow row in rows
                         .OrderBy(x => x.Road, StringComparer.OrdinalIgnoreCase)
                         .ThenBy(x => x.Owner, StringComparer.OrdinalIgnoreCase)
                         .ThenBy(x => x.OwnerId, StringComparer.OrdinalIgnoreCase)
                         .ThenBy(x => x.Code, StringComparer.OrdinalIgnoreCase)
                         .ThenBy(x => x.Handle, StringComparer.OrdinalIgnoreCase))
            {
                table.Add(
                    Row(
                        row.RecordId,
                        row.ObjectHandle,
                        row.RoadName,
                        row.AxisKey,
                        row.AxisType,
                        row.ScopeLabel,
                        row.OwnerId,
                        row.QuantityGroup,
                        row.QuantityCategory,
                        row.QuantityRole,
                        row.MarkingCode,
                        row.TemplateLayer,
                        row.CadLayer,
                        Round(row.Width),
                        Round(row.GeometryLength),
                        Round(row.PaintRatio, 4),
                        Round(row.PaintedLength),
                        Round(row.PaintedArea),
                        row.Count,
                        row.QuantityUnit,
                        row.Source,
                        row.Warning));
            }

            return table;
        }

        private static string RoadGroupingKey(
            CadQuantityRow row)
        {
            // Báo cáo/khối lượng gom theo semantic RoadName. AxisKey là khóa liên kết
            // kỹ thuật của từng entity và được giữ ở cột truy vết; chỉ fallback khi
            // bản ghi legacy không có RoadName.
            if (!string.IsNullOrWhiteSpace(row.RoadName))
                return NormalizeKey(row.RoadName);

            return NormalizeKey(row.AxisKey);
        }

        private static string NodeGroupingKey(
            CadQuantityRow row)
        {
            return NormalizeKey(row.OwnerId);
        }

        private static bool IsIntersection(
            CadQuantityRow row)
        {
            return string.Equals(
                       row.OwnerType,
                       "intersection",
                       StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(
                       row.OwnerType,
                       "node",
                       StringComparison.OrdinalIgnoreCase);
        }

        // Dùng trực tiếp IEnumerable<CadQuantityRow> thay vì generic constraint.
        // CadQuantityRow là sealed nên không thể dùng làm generic type constraint.
        private static string DisplayRoadName(
            IEnumerable<CadQuantityRow> source)
        {
            return FirstNonBlank(source.Select(x => x.RoadName));
        }

        private static string DisplayAxisKey(
            IEnumerable<CadQuantityRow> source)
        {
            return JoinDistinct(source.Select(x => x.AxisKey));
        }

        private static string DisplayAxisType(
            IEnumerable<CadQuantityRow> source)
        {
            return JoinDistinct(source.Select(x => x.AxisType));
        }

        private static string FirstNonBlank(
            IEnumerable<string> values)
        {
            return values
                       .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x))?
                       .Trim() ??
                   string.Empty;
        }

        private static string JoinDistinct(
            IEnumerable<string> values)
        {
            return string.Join(
                "; ",
                values
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Select(x => x.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
        }

        private static string NormalizeKey(
            string value)
        {
            return (value ?? string.Empty)
                .Trim()
                .ToUpperInvariant();
        }

        private static double Round(
            double value,
            int digits = 3)
        {
            return Math.Round(
                value,
                digits,
                MidpointRounding.AwayFromZero);
        }

        private static List<object> Header(
            params object[] cells)
        {
            return new List<object>(cells);
        }

        private static List<object> Row(
            params object[] cells)
        {
            return new List<object>(cells);
        }

        private static string Worksheet(
            IReadOnlyList<List<object>> rows)
        {
            var xml = new StringBuilder();
            xml.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            xml.Append("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">");
            xml.Append("<sheetViews><sheetView workbookViewId=\"0\"><pane ySplit=\"1\" topLeftCell=\"A2\" activePane=\"bottomLeft\" state=\"frozen\"/></sheetView></sheetViews>");
            xml.Append("<sheetData>");

            for (int rowIndex = 0;
                 rowIndex < rows.Count;
                 rowIndex++)
            {
                int excelRow = rowIndex + 1;
                xml.Append("<row r=\"")
                    .Append(excelRow)
                    .Append("\">");

                for (int columnIndex = 0;
                     columnIndex < rows[rowIndex].Count;
                     columnIndex++)
                {
                    string address =
                        ColumnName(columnIndex + 1) +
                        excelRow.ToString(CultureInfo.InvariantCulture);

                    AppendCell(
                        xml,
                        address,
                        rows[rowIndex][columnIndex],
                        rowIndex == 0 ? 1 : 0);
                }

                xml.Append("</row>");
            }

            xml.Append("</sheetData></worksheet>");
            return xml.ToString();
        }

        private static void AppendCell(
            StringBuilder xml,
            string address,
            object value,
            int styleIndex)
        {
            object safeValue = value ?? string.Empty;

            if (IsNumeric(safeValue))
            {
                xml.Append("<c r=\"")
                    .Append(address)
                    .Append("\" s=\"")
                    .Append(styleIndex)
                    .Append("\"><v>")
                    .Append(Convert.ToString(
                        safeValue,
                        CultureInfo.InvariantCulture))
                    .Append("</v></c>");

                return;
            }

            xml.Append("<c r=\"")
                .Append(address)
                .Append("\" s=\"")
                .Append(styleIndex)
                .Append("\" t=\"inlineStr\"><is><t xml:space=\"preserve\">")
                .Append(EscapeXml(Convert.ToString(
                    safeValue,
                    CultureInfo.InvariantCulture) ?? string.Empty))
                .Append("</t></is></c>");
        }

        private static bool IsNumeric(
            object value)
        {
            return value is byte ||
                   value is sbyte ||
                   value is short ||
                   value is ushort ||
                   value is int ||
                   value is uint ||
                   value is long ||
                   value is ulong ||
                   value is float ||
                   value is double ||
                   value is decimal;
        }

        private static string ColumnName(
            int number)
        {
            var chars = new StringBuilder();

            while (number > 0)
            {
                number--;
                chars.Insert(
                    0,
                    (char)('A' + number % 26));
                number /= 26;
            }

            return chars.ToString();
        }

        private static string EscapeXml(
            string value)
        {
            return value
                .Replace("&", "&amp;")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;")
                .Replace("\"", "&quot;")
                .Replace("'", "&apos;");
        }

        private static void Write(
            ZipArchive archive,
            string entryName,
            string text)
        {
            ZipArchiveEntry entry = archive.CreateEntry(
                entryName,
                CompressionLevel.Optimal);

            using (var writer = new StreamWriter(
                       entry.Open(),
                       new UTF8Encoding(false)))
            {
                writer.Write(text);
            }
        }

        private static string ContentTypes()
        {
            return "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
                   "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
                   "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
                   "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
                   "<Override PartName=\"/docProps/app.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.extended-properties+xml\"/>" +
                   "<Override PartName=\"/docProps/core.xml\" ContentType=\"application/vnd.openxmlformats-package.core-properties+xml\"/>" +
                   "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>" +
                   "<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>" +
                   "<Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>" +
                   "<Override PartName=\"/xl/worksheets/sheet2.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>" +
                   "<Override PartName=\"/xl/worksheets/sheet3.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>" +
                   "<Override PartName=\"/xl/worksheets/sheet4.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>" +
                   "</Types>";
        }

        private static string RootRelationships()
        {
            return "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
                   "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                   "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>" +
                   "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties\" Target=\"docProps/core.xml\"/>" +
                   "<Relationship Id=\"rId3\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/extended-properties\" Target=\"docProps/app.xml\"/>" +
                   "</Relationships>";
        }

        private static string Workbook()
        {
            return "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
                   "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">" +
                   "<sheets>" +
                   "<sheet name=\"" + SummarySheetName + "\" sheetId=\"1\" r:id=\"rId1\"/>" +
                   "<sheet name=\"" + RoadSheetName + "\" sheetId=\"2\" r:id=\"rId2\"/>" +
                   "<sheet name=\"" + NodeSheetName + "\" sheetId=\"3\" r:id=\"rId3\"/>" +
                   "<sheet name=\"" + DetailSheetName + "\" sheetId=\"4\" r:id=\"rId4\"/>" +
                   "</sheets></workbook>";
        }

        private static string WorkbookRelationships()
        {
            return "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
                   "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                   "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/>" +
                   "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet2.xml\"/>" +
                   "<Relationship Id=\"rId3\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet3.xml\"/>" +
                   "<Relationship Id=\"rId4\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet4.xml\"/>" +
                   "<Relationship Id=\"rId5\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>" +
                   "</Relationships>";
        }

        private static string Styles()
        {
            return "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
                   "<styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">" +
                   "<fonts count=\"2\">" +
                   "<font><sz val=\"10\"/><name val=\"Segoe UI\"/></font>" +
                   "<font><b/><sz val=\"10\"/><name val=\"Segoe UI\"/></font>" +
                   "</fonts>" +
                   "<fills count=\"2\">" +
                   "<fill><patternFill patternType=\"none\"/></fill>" +
                   "<fill><patternFill patternType=\"gray125\"/></fill>" +
                   "</fills>" +
                   "<borders count=\"1\"><border/></borders>" +
                   "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>" +
                   "<cellXfs count=\"2\">" +
                   "<xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/>" +
                   "<xf numFmtId=\"0\" fontId=\"1\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyFont=\"1\"/>" +
                   "</cellXfs>" +
                   "</styleSheet>";
        }

        private static string AppProperties()
        {
            return "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                   "<Properties xmlns=\"http://schemas.openxmlformats.org/officeDocument/2006/extended-properties\" xmlns:vt=\"http://schemas.openxmlformats.org/officeDocument/2006/docPropsVTypes\">" +
                   "<Application>AutoRoadMarking Pro</Application>" +
                   "</Properties>";
        }

        private static string CoreProperties()
        {
            string timestamp = DateTime.UtcNow.ToString(
                "yyyy-MM-ddTHH:mm:ssZ",
                CultureInfo.InvariantCulture);

            return "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                   "<cp:coreProperties xmlns:cp=\"http://schemas.openxmlformats.org/package/2006/metadata/core-properties\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\" xmlns:dcterms=\"http://purl.org/dc/terms/\" xmlns:dcmitype=\"http://purl.org/dc/dcmitype/\" xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\">" +
                   "<dc:creator>AutoRoadMarking Pro</dc:creator>" +
                   "<cp:lastModifiedBy>AutoRoadMarking Pro</cp:lastModifiedBy>" +
                   "<dcterms:created xsi:type=\"dcterms:W3CDTF\">" + timestamp + "</dcterms:created>" +
                   "<dcterms:modified xsi:type=\"dcterms:W3CDTF\">" + timestamp + "</dcterms:modified>" +
                   "</cp:coreProperties>";
        }
    }
}
