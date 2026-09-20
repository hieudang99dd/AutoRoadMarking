using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Autodesk.AutoCAD.DatabaseServices;
using Autoroadmarking_Pro.CadHost.Cad.Layers;
using Autoroadmarking_Pro.CadHost.UI;

namespace Autoroadmarking_Pro.CadHost.Cad.State
{
    /// <summary>
    /// Adapter cho UI đã chốt của Tab 1 và Tab 2.
    /// Giữ nguyên model giao diện cũ, nhưng chuyển dữ liệu sang state mới của CadHost.
    /// </summary>
    public sealed class LegacyUiStateAdapter
    {
        private readonly JsonSerializerOptions _json = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        public int SyncLayers(Database db, Transaction tr, ArmProjectState state, JsonElement payload)
        {
            List<LegacyLayerUi> layers = JsonPayload.List<LegacyLayerUi>(payload, "Layers", _json);
            int count = 0;

            foreach (LegacyLayerUi layer in layers)
            {
                if (string.IsNullOrWhiteSpace(layer.Ten))
                    continue;

                ArmMarkingTemplateState template = ToTemplate(layer);

                state.MarkingTemplates.RemoveAll(x =>
                    string.Equals(x.Id, template.Id, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(x.Layer, template.Layer, StringComparison.OrdinalIgnoreCase) ||
                    (!string.IsNullOrWhiteSpace(template.Code) &&
                     string.Equals(x.Code, template.Code, StringComparison.OrdinalIgnoreCase)));

                state.MarkingTemplates.Add(template);
                new MarkingLayerSynchronizer().EnsureTemplateLayer(db, tr, template);
                count++;
            }

            return count;
        }

        public int SyncCrossSections(Database db, Transaction tr, ArmProjectState state, JsonElement payload)
        {
            // Tab 2 gửi cả Layers và Assemblies. Đồng bộ layer trước để các template tham chiếu có sẵn.
            SyncLayers(db, tr, state, payload);

            List<LegacyAssemblyUi> assemblies = JsonPayload.List<LegacyAssemblyUi>(payload, "Assemblies", _json);
            int count = 0;

            foreach (LegacyAssemblyUi assembly in assemblies)
            {
                string name = (assembly.TenMatCat ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                ArmCrossSectionState mapped = new ArmCrossSectionState
                {
                    Id = name,
                    Name = name,
                    Components = MapParts(assembly.ThanhPhan ?? new List<LegacyPartUi>())
                };

                state.CrossSections.RemoveAll(x =>
                    string.Equals(x.Id, mapped.Id, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(x.Name, mapped.Name, StringComparison.OrdinalIgnoreCase));

                state.CrossSections.Add(mapped);
                count++;
            }

            return count;
        }

        private static ArmMarkingTemplateState ToTemplate(LegacyLayerUi layer)
        {
            string code = InferCode(layer.Ten);
            string geometry = IsBlockCode(code)
                ? "block"
                : IsAreaCode(code)
                    ? "area"
                    : "line";

            string category = IsBlockCode(code)
                ? "symbol"
                : IsAreaCode(code)
                    ? "intersection"
                    : "longitudinal";

            return new ArmMarkingTemplateState
            {
                Id = "LAYER_" + MakeId(layer.Ten),
                Code = code,
                Name = string.IsNullOrWhiteSpace(layer.MoTa) ? ("Vạch " + code) : layer.MoTa.Trim(),
                Category = category,
                Layer = layer.Ten.Trim(),
                Geometry = geometry,
                Width = Math.Max(0.0, layer.Rong),
                LinetypeScale = layer.Scale > 0.0 ? layer.Scale : 1.0,
                Pattern = string.IsNullOrWhiteSpace(layer.Kieu) ? "CONTINUOUS" : layer.Kieu.Trim(),
                Color = string.IsNullOrWhiteSpace(layer.MauRGB) ? "255, 255, 255" : layer.MauRGB.Trim(),
                Reference = "QCVN 41:2024/BGTVT",
                Note = layer.MoTa ?? string.Empty,
                Verified = true
            };
        }

        private static List<ArmCrossSectionPartState> MapParts(List<LegacyPartUi> source)
        {
            var result = new List<ArmCrossSectionPartState>();
            double leftAccumulated = 0.0;
            double rightAccumulated = 0.0;
            int sequence = 0;

            foreach (LegacyPartUi item in source)
            {
                sequence++;
                string role = MapRole(item.Loai);
                string side = NormalizeSide(item.Side, role);
                double width = Math.Max(0.0, item.BeRong);
                bool occupiesWidth = item.IsChiemDienTich;
                double offset;

                if (string.Equals(side, "Center", StringComparison.OrdinalIgnoreCase))
                {
                    offset = 0.0;
                }
                else if (string.Equals(side, "Left", StringComparison.OrdinalIgnoreCase))
                {
                    double occupied = occupiesWidth ? width : 0.0;
                    offset = occupied > 0.0
                        ? -(leftAccumulated + occupied * 0.5)
                        : -leftAccumulated;
                    leftAccumulated += occupied;
                }
                else
                {
                    double occupied = occupiesWidth ? width : 0.0;
                    offset = occupied > 0.0
                        ? rightAccumulated + occupied * 0.5
                        : rightAccumulated;
                    rightAccumulated += occupied;
                }

                result.Add(new ArmCrossSectionPartState
                {
                    Id = "P" + sequence.ToString("000"),
                    Side = side,
                    Role = role,
                    Order = sequence,
                    Name = BuildPartName(role, side, sequence),
                    Width = width,
                    Offset = offset,
                    OccupiesWidth = occupiesWidth,
                    Template = item.Kieu ?? string.Empty
                });
            }

            return result;
        }

        private static string NormalizeSide(string? side, string role)
        {
            if (string.Equals(role, "Centerline", StringComparison.OrdinalIgnoreCase))
                return "Center";

            if (string.Equals(side, "Left", StringComparison.OrdinalIgnoreCase))
                return "Left";

            if (string.Equals(side, "Right", StringComparison.OrdinalIgnoreCase))
                return "Right";

            return "Center";
        }

        private static string MapRole(string? value)
        {
            string v = value ?? string.Empty;
            if (v.Equals("TimTuyen", StringComparison.OrdinalIgnoreCase)) return "Centerline";
            if (v.Equals("LanXe", StringComparison.OrdinalIgnoreCase)) return "Lane";
            if (v.Equals("VachSon", StringComparison.OrdinalIgnoreCase)) return "Marking";
            if (v.Equals("DaiPhanCach", StringComparison.OrdinalIgnoreCase)) return "Median";
            if (v.Equals("ViaHe", StringComparison.OrdinalIgnoreCase)) return "Sidewalk";
            return string.IsNullOrWhiteSpace(v) ? "Component" : v;
        }

        private static string BuildPartName(string role, string side, int sequence)
        {
            if (role == "Centerline") return "TIM";
            return role + " " + side + " " + sequence;
        }

        private static string InferCode(string layerName)
        {
            string name = layerName.Trim();
            int index = name.IndexOf("VS_MARKING", StringComparison.OrdinalIgnoreCase);
            if (index >= 0)
            {
                string tail = name.Substring(index + "VS_MARKING".Length).TrimStart('.', '_');
                if (!string.IsNullOrWhiteSpace(tail))
                    return tail;
            }

            int lastDot = name.LastIndexOf('.');
            if (lastDot >= 0 && lastDot + 1 < name.Length)
                return name.Substring(lastDot + 1);

            return name;
        }

        private static bool IsAreaCode(string code)
        {
            return code.Equals("7.1", StringComparison.OrdinalIgnoreCase) ||
                   code.Equals("7.3", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsBlockCode(string code)
        {
            return code.Equals("7.6", StringComparison.OrdinalIgnoreCase) ||
                   code.StartsWith("9.3", StringComparison.OrdinalIgnoreCase);
        }

        private static string MakeId(string value)
        {
            char[] chars = value.ToUpperInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray();
            return new string(chars);
        }
    }

    public sealed class LegacyLayerUi
    {
        public string Ten { get; set; } = string.Empty;
        public string MoTa { get; set; } = string.Empty;
        public string Kieu { get; set; } = "CONTINUOUS";
        public double Scale { get; set; } = 1.0;
        public double Rong { get; set; }
        public string MauHex { get; set; } = "#ffffff";
        public string MauRGB { get; set; } = "255, 255, 255";
        public bool IsSynced { get; set; }
    }

    public sealed class LegacyAssemblyUi
    {
        public string TenMatCat { get; set; } = string.Empty;
        public List<LegacyPartUi> ThanhPhan { get; set; } = new List<LegacyPartUi>();
    }

    public sealed class LegacyPartUi
    {
        public string Loai { get; set; } = string.Empty;
        public string Side { get; set; } = string.Empty;
        public bool IsChiemDienTich { get; set; }
        public double BeRong { get; set; }
        public string Kieu { get; set; } = string.Empty;
    }
}
