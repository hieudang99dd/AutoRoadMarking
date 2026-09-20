using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autoroadmarking_Pro.CadHost.Cad.Geometry;
using Autoroadmarking_Pro.CadHost.Cad.Metadata;
using Autoroadmarking_Pro.CadHost.Cad.RoadAxis;
using Autoroadmarking_Pro.CadHost.Cad.State;
using Autoroadmarking_Pro.Domain.Quantities;

using AcEntity = Autodesk.AutoCAD.DatabaseServices.Entity;
using CivilAlignment = Autodesk.Civil.DatabaseServices.Alignment;

namespace Autoroadmarking_Pro.CadHost.Cad.Quantities
{
    public sealed class CadQuantityRow
    {
        public string Id { get; set; } = string.Empty;
        public string Handle { get; set; } = string.Empty;
        public string Road { get; set; } = string.Empty;
        public string Owner { get; set; } = string.Empty;
        public string OwnerIdValue { get; set; } = string.Empty;
        public string AxisKey { get; set; } = string.Empty;
        public string AxisType { get; set; } = string.Empty;
        public string Code { get; set; } = string.Empty;
        public string Template { get; set; } = string.Empty;
        public string CadLayer { get; set; } = string.Empty;
        public double Width { get; set; }
        public double Length { get; set; }
        public double PaintRatioValue { get; set; } = 1.0;
        public double PaintedLengthOverrideValue { get; set; }
        public double PaintedAreaOverrideValue { get; set; }
        public double Area { get; set; }
        public int Count { get; set; }
        public string Source { get; set; } = string.Empty;
        public string QuantityCategoryValue { get; set; } = string.Empty;
        public string QuantityRoleValue { get; set; } = string.Empty;
        public string QuantityGroupValue { get; set; } = string.Empty;
        public string QuantityScopeValue { get; set; } = string.Empty;
        public string OwnerDisplayValue { get; set; } = string.Empty;
        public string WarningText { get; set; } = string.Empty;

        // Alias đúng contract của CodeTab5.js.
        public string RecordId => Id;
        public string ObjectHandle => Handle;
        public string OwnerType => Owner;
        public string OwnerId => OwnerIdValue;
        public string OwnerName => string.IsNullOrWhiteSpace(OwnerDisplayValue) ? Road : OwnerDisplayValue;
        public string RoadName => Road;
        public string QuantityCategory => QuantityCategoryValue;
        public string QuantityRole => QuantityRoleValue;
        public string QuantityGroup => QuantityGroupValue;
        public string QuantityScope => QuantityScopeValue;
        public string ScopeLabel => QuantityScopeValue.Equals("INTERSECTION", StringComparison.OrdinalIgnoreCase)
            ? "NÚT GIAO"
            : QuantityScopeValue.Equals("SUPPLEMENTARY", StringComparison.OrdinalIgnoreCase)
                ? "PHÁT SINH"
                : "TUYẾN";
        public string MarkingCode => Code;
        public string TemplateLayer => Template;
        public double GeometryLength => Length;
        public double PaintRatio => PaintRatioValue;
        public double PaintedLength => PaintedLengthOverrideValue > 0.0
            ? PaintedLengthOverrideValue
            : Length * PaintRatioValue;
        public double PaintedArea => PaintedAreaOverrideValue > 0.0
            ? PaintedAreaOverrideValue
            : (Area > 0.0
                ? Area
                : (Width > 0.0 ? PaintedLength * Width : 0.0));
        public string QuantityUnit => Count > 0
            ? "cái"
            : (PaintedArea > 0.0 ? "m²" : "m");
        public string Warning => WarningText;
    }

    public sealed class QuantityScanResult
    {
        public List<CadQuantityRow> Rows { get; set; } =
            new List<CadQuantityRow>();

        public int UnmanagedCount { get; set; }
    }

    /// <summary>
    /// TAB 5 recognition:
    /// 1. ARM metadata hợp lệ là authoritative.
    /// 2. Nếu không có metadata: ManualLayerRule exact match.
    /// 3. Còn lại: unmanaged, không cộng khối lượng.
    /// </summary>
    public sealed class ModelSpaceQuantityScanner
    {
        private readonly EntityMetadataStore _metadata =
            new EntityMetadataStore();

        private readonly ArmMetadataMapper _mapper =
            new ArmMetadataMapper();

        public List<CadQuantityRow> Scan(
            Database db,
            Transaction tr)
        {
            return ScanWithSummary(
                db,
                tr,
                null).Rows;
        }

        public QuantityScanResult ScanWithSummary(
            Database db,
            Transaction tr,
            ArmProjectState? state)
        {
            if (db == null)
                throw new ArgumentNullException(nameof(db));

            if (tr == null)
                throw new ArgumentNullException(nameof(tr));

            var result =
                new QuantityScanResult();

            BlockTable blockTable =
                (BlockTable)tr.GetObject(
                    db.BlockTableId,
                    OpenMode.ForRead);

            BlockTableRecord modelSpace =
                (BlockTableRecord)tr.GetObject(
                    blockTable[BlockTableRecord.ModelSpace],
                    OpenMode.ForRead);

            List<ArmRoadAxisState> roadCatalog =
                state == null
                    ? new List<ArmRoadAxisState>()
                    : new RoadAxisCatalogService()
                        .Build(
                            db,
                            tr,
                            state,
                            refreshPolylineRegistry: false);

            foreach (ObjectId id in modelSpace)
            {
                if (!(tr.GetObject(
                        id,
                        OpenMode.ForRead,
                        false) is AcEntity entity))
                {
                    continue;
                }

                ArmEntityMetadata? metadata =
                    _metadata.Read(
                        entity,
                        tr);

                if (_mapper.IsValidManagedRecord(metadata))
                {
                    result.Rows.Add(
                        BuildManagedRow(
                            id,
                            entity,
                            metadata!,
                            roadCatalog));

                    continue;
                }

                ManualLayerRule? rule =
                    FindManualRule(
                        state,
                        entity.Layer);

                if (rule != null)
                {
                    result.Rows.Add(
                        BuildManualRow(
                            id,
                            entity,
                            rule,
                            state));

                    continue;
                }

                if (LooksLikeRoadMarking(
                    entity.Layer,
                    state))
                {
                    result.UnmanagedCount++;
                }
            }

            result.Rows =
                result.Rows
                    .OrderBy(x => x.QuantityScopeValue)
                    .ThenBy(x => x.OwnerIdValue)
                    .ThenBy(x => x.QuantityGroupValue)
                    .ThenBy(x => x.Code)
                    .ThenBy(x => x.Handle)
                    .ToList();

            return result;
        }

        private static ManualLayerRule? FindManualRule(
            ArmProjectState? state,
            string layerName)
        {
            if (state?.ManualLayerRules == null)
                return null;

            return state.ManualLayerRules
                .FirstOrDefault(rule =>
                    rule.Enabled &&
                    string.Equals(
                        rule.LayerName,
                        layerName,
                        StringComparison.OrdinalIgnoreCase));
        }

        private static bool LooksLikeRoadMarking(
            string layerName,
            ArmProjectState? state)
        {
            if (string.IsNullOrWhiteSpace(layerName))
                return false;

            if (layerName.IndexOf(
                    "VS_MARKING",
                    StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            if (state?.MarkingTemplates == null)
                return false;

            foreach (ArmMarkingTemplateState template in
                     state.MarkingTemplates)
            {
                if (string.IsNullOrWhiteSpace(template.Layer))
                    continue;

                if (string.Equals(
                        layerName,
                        template.Layer,
                        StringComparison.OrdinalIgnoreCase) ||
                    layerName.EndsWith(
                        "__" + template.Layer,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private CadQuantityRow BuildManagedRow(
            ObjectId id,
            AcEntity entity,
            ArmEntityMetadata metadata,
            IReadOnlyList<ArmRoadAxisState> roadCatalog)
        {
            GeometryMetrics metrics =
                ReadGeometry(
                    entity);

            string owner =
                NormalizeOwnerType(
                    metadata.OwnerType);

            QuantityClassification classification = ResolveQuantityClassification(metadata, owner);
            if (classification.Scope.Equals("INTERSECTION", StringComparison.OrdinalIgnoreCase))
                owner = "intersection";
            else if (classification.Scope.Equals("SUPPLEMENTARY", StringComparison.OrdinalIgnoreCase))
                owner = "supplementary";

            string axisKey =
                !string.IsNullOrWhiteSpace(metadata.AxisKey)
                    ? metadata.AxisKey
                    : metadata.RoadKey;

            ArmRoadAxisState? currentAxis =
                roadCatalog.FirstOrDefault(x =>
                    string.Equals(
                        x.EffectiveAxisKey,
                        axisKey,
                        StringComparison.OrdinalIgnoreCase) ||
                    (!string.IsNullOrWhiteSpace(x.LegacyRoadKey) &&
                     string.Equals(
                         x.LegacyRoadKey,
                         axisKey,
                         StringComparison.OrdinalIgnoreCase)));

            string ownerName =
                currentAxis != null
                    ? currentAxis.RoadName
                    : (!string.IsNullOrWhiteSpace(metadata.RoadName)
                        ? metadata.RoadName
                        : (!string.IsNullOrWhiteSpace(metadata.OwnerId)
                            ? metadata.OwnerId
                            : axisKey));

            double paintRatio =
                ResolvePaintRatio(
                    metadata);

            string resolvedOwnerId = string.IsNullOrWhiteSpace(metadata.OwnerId)
                ? axisKey
                : metadata.OwnerId;
            string ownerDisplay = owner.Equals("intersection", StringComparison.OrdinalIgnoreCase)
                ? BuildNodeDisplayName(resolvedOwnerId)
                : ownerName;

            return new CadQuantityRow
            {
                Id = metadata.RecordId,
                Handle = id.Handle.ToString(),
                Road = ownerName,
                Owner = owner,
                OwnerIdValue = resolvedOwnerId,
                AxisKey = axisKey,
                AxisType =
                    currentAxis?.AxisType ??
                    (!string.IsNullOrWhiteSpace(metadata.AxisType)
                        ? metadata.AxisType
                        : string.Empty),
                Code = metadata.MarkingCode,
                Template = metadata.TemplateLayer,
                CadLayer = entity.Layer,
                Width = Math.Max(0.0, metadata.Width),
                // Length/Area luôn là hình học gốc. Override là khối lượng sơn thực đã
                // được tính ở nguồn và không được nhân PaintRatio lần thứ hai.
                Length = metrics.Length,
                PaintRatioValue = paintRatio,
                PaintedLengthOverrideValue = Math.Max(0.0, metadata.PaintedLengthOverride),
                PaintedAreaOverrideValue = Math.Max(0.0, metadata.PaintedAreaOverride),
                Area = metrics.Area,
                Count = metadata.QuantityCount > 0 ? metadata.QuantityCount : metrics.Count,
                Source = metadata.Source,
                QuantityCategoryValue = classification.Category,
                QuantityRoleValue = classification.Role,
                QuantityGroupValue = classification.Group,
                QuantityScopeValue = classification.Scope,
                OwnerDisplayValue = ownerDisplay,
                WarningText =
                    paintRatio < 0.999
                        ? string.Empty
                        : string.Empty
            };
        }

        private CadQuantityRow BuildManualRow(
            ObjectId id,
            AcEntity entity,
            ManualLayerRule rule,
            ArmProjectState? state)
        {
            GeometryMetrics metrics =
                ReadGeometry(
                    entity);

            ArmMarkingTemplateState? template =
                state?.MarkingTemplates
                    .FirstOrDefault(x =>
                        string.Equals(
                            x.Code,
                            rule.MarkingCode,
                            StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(
                            x.Layer,
                            entity.Layer,
                            StringComparison.OrdinalIgnoreCase));

            double width =
                template == null
                    ? 0.0
                    : Math.Max(
                        0.0,
                        template.Width);

            return new CadQuantityRow
            {
                Id = "MANUAL_" + id.Handle,
                Handle = id.Handle.ToString(),
                Road =
                    string.IsNullOrWhiteSpace(rule.RoadName)
                        ? "CHƯA_GÁN_TUYẾN"
                        : rule.RoadName,
                Owner = "road",
                OwnerIdValue =
                    string.IsNullOrWhiteSpace(rule.RoadName)
                        ? "CHƯA_GÁN_TUYẾN"
                        : ArmMetadataMapper.NormalizeKey(
                            rule.RoadName),
                AxisType = entity.GetType().Name,
                Code =
                    string.IsNullOrWhiteSpace(rule.MarkingCode)
                        ? "-"
                        : rule.MarkingCode,
                Template =
                    template?.Layer ??
                    entity.Layer,
                CadLayer = entity.Layer,
                Width = width,
                Length = metrics.Length,
                PaintRatioValue = 1.0,
                Area = metrics.Area,
                Count = metrics.Count,
                Source = "MANUAL_LAYER_RULE",
                QuantityCategoryValue = "ROAD_EXISTING",
                QuantityRoleValue = "MANUAL_LAYER",
                QuantityGroupValue = "VACH_DOC_TUYEN",
                QuantityScopeValue = "ROAD",
                OwnerDisplayValue = string.IsNullOrWhiteSpace(rule.RoadName) ? "CHƯA_GÁN_TUYẾN" : rule.RoadName,
                WarningText =
                    "Đối tượng chưa có ARM metadata; đang nhận dạng bằng ManualLayerRule exact match."
            };
        }

        private static GeometryMetrics ReadGeometry(
            AcEntity entity)
        {
            double length = 0.0;
            double area = 0.0;
            int count = 0;

            if (entity is CivilAlignment alignment)
            {
                length =
                    Math.Max(
                        0.0,
                        alignment.Length);
            }
            else if (entity is Curve curve)
            {
                length =
                    Math.Max(
                        0.0,
                        CurveGeometryHelper.Length(
                            curve));
            }

            if (entity is Polyline polyline &&
                polyline.Closed)
            {
                try
                {
                    area =
                        Math.Abs(
                            polyline.Area);
                }
                catch
                {
                    area = 0.0;
                }
            }

            if (entity is BlockReference)
                count = 1;

            return new GeometryMetrics
            {
                Length = length,
                Area = area,
                Count = count
            };
        }

        private static QuantityClassification ResolveQuantityClassification(
            ArmEntityMetadata metadata,
            string normalizedOwner)
        {
            string category = ReadExtra(metadata, "QuantityCategory");
            string role = ReadExtra(metadata, "QuantityRole");
            string group = ReadExtra(metadata, "QuantityGroup");
            string scope = ReadExtra(metadata, "QuantityScope");
            string source = (metadata.Source ?? string.Empty).Trim().ToUpperInvariant();
            string code = (metadata.MarkingCode ?? string.Empty).Trim();

            if (string.IsNullOrWhiteSpace(category))
            {
                if (source.Equals("AUTO_EDGE", StringComparison.OrdinalIgnoreCase))
                    category = "INTERSECTION_EDGE";
                else if (source.Equals("AUTO_INTERSECTION", StringComparison.OrdinalIgnoreCase) && code.Equals("7.1", StringComparison.OrdinalIgnoreCase))
                    category = "INTERSECTION_STOP";
                else if (source.Equals("AUTO_INTERSECTION", StringComparison.OrdinalIgnoreCase) && code.Equals("7.3", StringComparison.OrdinalIgnoreCase))
                    category = "INTERSECTION_CROSSWALK";
                else if (source.Equals("AUTO_LONGITUDINAL", StringComparison.OrdinalIgnoreCase))
                    category = "ROAD_LONGITUDINAL";
                else if (source.StartsWith("SUPPLEMENTARY", StringComparison.OrdinalIgnoreCase))
                    category = "SUPPLEMENTARY";
                else
                    category = normalizedOwner.Equals("intersection", StringComparison.OrdinalIgnoreCase)
                        ? "INTERSECTION_OTHER"
                        : "ROAD_OTHER";
            }

            if (string.IsNullOrWhiteSpace(group))
            {
                switch (category.ToUpperInvariant())
                {
                    case "INTERSECTION_EDGE": group = "VACH_MEP_NUT"; break;
                    case "INTERSECTION_STOP": group = "VACH_DUNG"; break;
                    case "INTERSECTION_CROSSWALK": group = "VACH_DI_BO"; break;
                    case "SUPPLEMENTARY": group = "PHAT_SINH"; break;
                    default: group = category.StartsWith("INTERSECTION_", StringComparison.OrdinalIgnoreCase)
                        ? "VACH_NUT_KHAC"
                        : "VACH_DOC_TUYEN"; break;
                }
            }

            if (string.IsNullOrWhiteSpace(role))
            {
                switch (category.ToUpperInvariant())
                {
                    case "INTERSECTION_EDGE": role = "BULLHORN_EDGE"; break;
                    case "INTERSECTION_STOP": role = "STOP_LINE_7_1"; break;
                    case "INTERSECTION_CROSSWALK": role = "CROSSWALK_7_3"; break;
                    case "ROAD_LONGITUDINAL":
                        role = Math.Abs(metadata.Offset) <= 1e-4
                            ? "CENTER_LINE"
                            : (code.StartsWith("3.", StringComparison.OrdinalIgnoreCase) ? "ROAD_EDGE" : "LANE_LINE");
                        break;
                    case "SUPPLEMENTARY":
                        role = ReadExtra(metadata, "SupplementaryType");
                        if (string.IsNullOrWhiteSpace(role)) role = code;
                        break;
                    default: role = "OTHER"; break;
                }
            }

            if (string.IsNullOrWhiteSpace(scope))
            {
                scope = category.StartsWith("INTERSECTION_", StringComparison.OrdinalIgnoreCase)
                    ? "INTERSECTION"
                    : category.Equals("SUPPLEMENTARY", StringComparison.OrdinalIgnoreCase)
                        ? "SUPPLEMENTARY"
                        : "ROAD";
            }

            return new QuantityClassification
            {
                Category = category.Trim().ToUpperInvariant(),
                Role = role.Trim().ToUpperInvariant(),
                Group = group.Trim().ToUpperInvariant(),
                Scope = scope.Trim().ToUpperInvariant()
            };
        }

        private static string ReadExtra(ArmEntityMetadata metadata, string key)
        {
            if (metadata.Extra != null &&
                metadata.Extra.TryGetValue(key, out string? value) &&
                !string.IsNullOrWhiteSpace(value))
                return value.Trim();
            return string.Empty;
        }

        private static string BuildNodeDisplayName(string ownerId)
        {
            if (string.IsNullOrWhiteSpace(ownerId)) return "Nút giao";
            string[] parts = ownerId.Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 3 && parts[0].Equals("NODE", StringComparison.OrdinalIgnoreCase))
                return "Nút " + string.Join("–", parts.Skip(1));
            return "Nút " + ownerId;
        }

        private static string NormalizeOwnerType(
            string? value)
        {
            string owner =
                (value ?? string.Empty)
                    .Trim();

            if (owner.Equals(
                    "INTERSECTION",
                    StringComparison.OrdinalIgnoreCase) ||
                owner.Equals(
                    "NODE",
                    StringComparison.OrdinalIgnoreCase))
            {
                return "intersection";
            }

            return "road";
        }

        private static double ResolvePaintRatio(
            ArmEntityMetadata metadata)
        {
            if (metadata.Extra != null &&
                metadata.Extra.TryGetValue(
                    "PaintRatio",
                    out string? raw) &&
                double.TryParse(
                    raw,
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out double ratio))
            {
                return Math.Max(
                    0.0,
                    Math.Min(
                        1.0,
                        ratio));
            }

            return 1.0;
        }

        private sealed class QuantityClassification
        {
            public string Category { get; set; } = string.Empty;
            public string Role { get; set; } = string.Empty;
            public string Group { get; set; } = string.Empty;
            public string Scope { get; set; } = string.Empty;
        }

        private sealed class GeometryMetrics
        {
            public double Length { get; set; }
            public double Area { get; set; }
            public int Count { get; set; }
        }
    }
}
