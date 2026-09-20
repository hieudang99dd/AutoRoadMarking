using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Autodesk.AutoCAD.DatabaseServices;
using Autoroadmarking_Pro.CadHost.Cad.State;

namespace Autoroadmarking_Pro.CadHost.Cad.Layers
{
    /// <summary>
    /// Tạo linetype AutoCAD theo kích thước thật của template. Với CUSTOM_REAL,
    /// pattern trong DWG là nguồn biểu diễn; các thông số kỹ thuật vẫn được giữ
    /// trong ArmMarkingTemplateState để generator/quantity dùng độc lập.
    /// </summary>
    public sealed class CadLinetypeService
    {
        private const double Epsilon = 1e-9;

        public ObjectId EnsureForTemplate(Database db, Transaction tr, ArmMarkingTemplateState template)
        {
            if (db == null) throw new ArgumentNullException(nameof(db));
            if (tr == null) throw new ArgumentNullException(nameof(tr));
            if (template == null) throw new ArgumentNullException(nameof(template));

            // Một số template CUSTOM_REAL dùng Dash/Gap như tham số hình học của generator
            // (7.3 zebra, GGT), không phải pattern dọc theo chính entity. Các entity được
            // generator sinh thành phần sơn thật nên layer CAD phải là Continuous.
            if (IsGeometryDriven(template))
                return db.ContinuousLinetype;

            List<double> segments = BuildSegments(template);
            if (segments.Count == 0 || segments.All(x => x >= -Epsilon))
                return db.ContinuousLinetype;

            string name = BuildName(template);
            LinetypeTable table = (LinetypeTable)tr.GetObject(db.LinetypeTableId, OpenMode.ForRead);
            LinetypeTableRecord record;

            if (table.Has(name))
            {
                record = (LinetypeTableRecord)tr.GetObject(table[name], OpenMode.ForWrite);
            }
            else
            {
                table.UpgradeOpen();
                record = new LinetypeTableRecord { Name = name };
                table.Add(record);
                tr.AddNewlyCreatedDBObject(record, true);
            }

            ApplyPattern(record, segments);
            record.Comments = BuildComment(template);
            return record.ObjectId;
        }


        private static bool IsGeometryDriven(ArmMarkingTemplateState template)
        {
            string code = (template.Code ?? string.Empty).Trim().ToUpperInvariant();
            if (code == "7.3" || code == "GGT") return true;

            if (MarkingTemplateManagementService.ReadCustom(template, "geometryDriven", out string? geometryDriven) &&
                bool.TryParse(geometryDriven, out bool enabled) && enabled)
                return true;

            if (MarkingTemplateManagementService.ReadCustom(template, "geometryType", out string? geometryType))
            {
                string value = (geometryType ?? string.Empty).Trim().ToUpperInvariant();
                if (value.Contains("CROSSWALK") || value.Contains("SPEED_HUMP")) return true;
            }

            return false;
        }

        private static List<double> BuildSegments(ArmMarkingTemplateState t)
        {
            string pattern = (t.Pattern ?? string.Empty).Trim().ToUpperInvariant();
            if (pattern == "CONTINUOUS" || pattern == "SOLID" || string.IsNullOrWhiteSpace(pattern))
                return new List<double>();

            var raw = new List<double>();
            if (pattern == "CUSTOM_REAL")
            {
                Append(raw, t.CustomDash1, true);
                Append(raw, t.CustomGap1, false);
                Append(raw, t.CustomDash2, true);
                Append(raw, t.CustomGap2, false);
                return RotateByPhase(raw, t.CustomPhase);
            }

            Append(raw, t.DashLength, true);
            Append(raw, t.GapLength, false);
            return raw;
        }

        private static void Append(List<double> target, double length, bool painted)
        {
            if (double.IsNaN(length) || double.IsInfinity(length) || length <= Epsilon) return;
            target.Add(painted ? Math.Abs(length) : -Math.Abs(length));
        }

        private static List<double> RotateByPhase(List<double> source, double phase)
        {
            if (source.Count == 0 || phase <= Epsilon || double.IsNaN(phase) || double.IsInfinity(phase))
                return source;

            double cycle = source.Sum(x => Math.Abs(x));
            if (cycle <= Epsilon) return source;
            phase %= cycle;
            if (phase <= Epsilon) return source;

            var expanded = new List<double>(source);
            double remaining = phase;
            int index = 0;
            while (index < expanded.Count && remaining > Epsilon)
            {
                double signed = expanded[index];
                double length = Math.Abs(signed);
                if (remaining < length - Epsilon)
                {
                    double sign = signed >= 0 ? 1.0 : -1.0;
                    double tail = length - remaining;
                    double head = remaining;
                    var rotated = new List<double>();
                    rotated.Add(sign * tail);
                    for (int j = index + 1; j < expanded.Count; j++) rotated.Add(expanded[j]);
                    for (int j = 0; j < index; j++) rotated.Add(expanded[j]);
                    if (head > Epsilon) rotated.Add(sign * head);
                    return MergeAdjacent(rotated);
                }

                remaining -= length;
                index++;
                if (index == expanded.Count && remaining > Epsilon) index = 0;
            }

            if (index <= 0 || index >= source.Count) return source;
            return MergeAdjacent(source.Skip(index).Concat(source.Take(index)).ToList());
        }

        private static List<double> MergeAdjacent(List<double> source)
        {
            var result = new List<double>();
            foreach (double value in source)
            {
                if (Math.Abs(value) <= Epsilon) continue;
                if (result.Count > 0 && Math.Sign(result[result.Count - 1]) == Math.Sign(value))
                    result[result.Count - 1] += value;
                else
                    result.Add(value);
            }
            return result;
        }

        private static void ApplyPattern(LinetypeTableRecord record, List<double> segments)
        {
            record.NumDashes = segments.Count;
            record.PatternLength = segments.Sum(x => Math.Abs(x));
            for (int i = 0; i < segments.Count; i++)
                record.SetDashLengthAt(i, segments[i]);
        }

        private static string BuildName(ArmMarkingTemplateState template)
        {
            string source = !string.IsNullOrWhiteSpace(template.Id) ? template.Id : template.Code;
            var sb = new StringBuilder("ARM_");
            foreach (char c in (source ?? string.Empty).ToUpperInvariant())
            {
                if (char.IsLetterOrDigit(c) || c == '_') sb.Append(c);
                else if (sb[sb.Length - 1] != '_') sb.Append('_');
            }
            string name = sb.ToString().TrimEnd('_');
            return name.Length > 240 ? name.Substring(0, 240) : name;
        }

        private static string BuildComment(ArmMarkingTemplateState t)
        {
            return string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "ARM {0} | {1} | D1={2:0.###};G1={3:0.###};D2={4:0.###};G2={5:0.###};P={6:0.###}",
                t.Code, t.Pattern, t.CustomDash1, t.CustomGap1, t.CustomDash2, t.CustomGap2, t.CustomPhase);
        }
    }
}
