using System;
using System.Collections.Generic;
using System.Linq;

using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

using Autoroadmarking_Pro.CadHost.Cad.Geometry;
using Autoroadmarking_Pro.CadHost.Cad.Layers;
using Autoroadmarking_Pro.CadHost.Cad.Metadata;
using Autoroadmarking_Pro.CadHost.Cad.State;

using AcEntity = Autodesk.AutoCAD.DatabaseServices.Entity;
using CivilAlignment = Autodesk.Civil.DatabaseServices.Alignment;

namespace Autoroadmarking_Pro.CadHost.Cad.Markings
{
    /// <summary>
    /// Sinh vạch dọc tuyến từ MCN đã đối chiếu.
    /// Ưu tiên đúng các component Role=Marking của Tab 2.
    /// Nếu MCN cũ chưa có marking component thì dùng fallback lane-boundary
    /// để giữ tương thích dữ liệu cũ.
    /// </summary>
    public sealed class LongitudinalMarkingGenerator
    {
        private readonly CadGeometryService _geometry =
            new CadGeometryService();

        private readonly MarkingLayerSynchronizer _layers =
            new MarkingLayerSynchronizer();

        private readonly EntityMetadataStore _metadata =
            new EntityMetadataStore();

        private readonly StationOffsetService _station =
            new StationOffsetService();

        public int Generate(
            Database db,
            Transaction tr,
            ArmProjectState state)
        {
            int createdCount = 0;

            var blockTable =
                (BlockTable)tr.GetObject(
                    db.BlockTableId,
                    OpenMode.ForRead);

            var modelSpace =
                (BlockTableRecord)tr.GetObject(
                    blockTable[BlockTableRecord.ModelSpace],
                    OpenMode.ForWrite);

            foreach (ArmComparisonState comparison in
                     state.ComparisonResults
                         .Where(x =>
                             string.Equals(
                                 x.Status,
                                 "matched",
                                 StringComparison.OrdinalIgnoreCase)))
            {
                ArmCrossSectionState? crossSection =
                    state.GetEffectiveCrossSections()
                        .FirstOrDefault(x =>
                            string.Equals(
                                x.Id,
                                comparison.Mcn,
                                StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(
                                x.Name,
                                comparison.Mcn,
                                StringComparison.OrdinalIgnoreCase));

                if (crossSection == null)
                    continue;

                ObjectId axisId =
                    _geometry.FromHandle(
                        db,
                        comparison.TimHandle);

                if (axisId.IsNull ||
                    !(tr.GetObject(
                        axisId,
                        OpenMode.ForRead,
                        false) is AcEntity axis))
                {
                    continue;
                }

                List<MarkingPlacement> placements =
                    BuildPlacements(
                        crossSection,
                        state.MarkingTemplates);

                foreach (MarkingPlacement placement in placements)
                {
                    ArmMarkingTemplateState? template =
                        placement.Template;

                    if (template == null ||
                        !template.Verified)
                    {
                        continue;
                    }

                    if (template.Width <= 1e-9)
                    {
                        throw new InvalidOperationException(
                            $"Template vạch {template.Code} đang có Width = 0. Hãy sửa bề rộng tại Tab 1 trước khi sinh vạch.");
                    }

                    string generationKey =
                        string.Join(
                            "|",
                            comparison.RoadKey,
                            "LONG",
                            comparison.Mcn,
                            template.Id,
                            placement.Offset
                                .ToString(
                                    "0.###",
                                    System.Globalization.CultureInfo.InvariantCulture));

                    EraseGeneration(
                        db,
                        tr,
                        generationKey);

                    DBObjectCollection generated =
                        BuildCurves(
                            axis,
                            placement.Offset);

                    foreach (DBObject obj in generated)
                    {
                        if (!(obj is Curve curve))
                        {
                            obj.Dispose();
                            continue;
                        }

                        // Chuẩn hóa Line/Arc thành Polyline khi có thể để AutoCAD có
                        // Global width thật thay vì chỉ lưu Width trong metadata.
                        curve = NormalizeToWidthCapableCurve(curve);
                        ApplyPhysicalWidth(curve, template.Width);

                        modelSpace.AppendEntity(
                            curve);

                        tr.AddNewlyCreatedDBObject(
                            curve,
                            true);

                        curve.LayerId =
                            _layers.EnsureGeneratedLayer(
                                db,
                                tr,
                                comparison.Road,
                                template);

                        // Linetype được gắn ở Layer; scale lấy đúng từ Tab 1.
                        curve.LinetypeScale = template.LinetypeScale > 0.0
                            ? template.LinetypeScale
                            : 1.0;

                        string cadLayer =
                            curve.Layer;

                        var metadata = new ArmEntityMetadata
                        {
                            RecordId = "MRK_" + Guid.NewGuid().ToString("N"),
                            GenerationKey = generationKey,
                            Source = "AUTO_LONGITUDINAL",
                            RoadName = comparison.Road,
                            AxisKey = comparison.RoadKey,
                            AxisHandle = comparison.TimHandle,
                            AxisType = axis is CivilAlignment ? "ALIGNMENT" : "POLYLINE",
                            RoadKey = comparison.RoadKey,
                            OwnerType = "ROAD",
                            OwnerId = comparison.RoadKey,
                            McnId = comparison.Mcn,
                            MarkingCode = template.Code,
                            TemplateId = template.Id,
                            TemplateLayer = template.Layer,
                            CadLayer = cadLayer,
                            Offset = placement.Offset,
                            Width = template.Width
                        };
                        metadata.Extra["PaintRatio"] = template.PaintRatio.ToString(
                            "0.########",
                            System.Globalization.CultureInfo.InvariantCulture);
                        metadata.Extra["Pattern"] = template.Pattern ?? string.Empty;
                        metadata.Extra["QuantityCategory"] = "ROAD_LONGITUDINAL";
                        metadata.Extra["QuantityGroup"] = "VACH_DOC_TUYEN";
                        metadata.Extra["QuantityRole"] = ResolveLongitudinalQuantityRole(template.Code, placement.Offset);
                        metadata.Extra["QuantityScope"] = "ROAD";
                        _metadata.Write(curve, tr, metadata);

                        createdCount++;
                    }
                }
            }

            return createdCount;
        }

private static Curve NormalizeToWidthCapableCurve(Curve source)
{
    if (source is Polyline || source is Polyline2d)
        return source;

    if (source is Line line)
    {
        var pl = new Polyline(2);
        pl.AddVertexAt(0, new Point2d(line.StartPoint.X, line.StartPoint.Y), 0.0, 0.0, 0.0);
        pl.AddVertexAt(1, new Point2d(line.EndPoint.X, line.EndPoint.Y), 0.0, 0.0, 0.0);
        source.Dispose();
        return pl;
    }

    if (source is Arc arc)
    {
        double bulge = Math.Tan(Math.Abs(arc.TotalAngle) / 4.0);
        if (arc.Normal.Z < 0.0) bulge = -bulge;
        var pl = new Polyline(2);
        pl.AddVertexAt(0, new Point2d(arc.StartPoint.X, arc.StartPoint.Y), bulge, 0.0, 0.0);
        pl.AddVertexAt(1, new Point2d(arc.EndPoint.X, arc.EndPoint.Y), 0.0, 0.0, 0.0);
        source.Dispose();
        return pl;
    }

    return source;
}

private static void ApplyPhysicalWidth(
    Curve curve,
    double width)
{
    if (curve == null || width <= 1e-9)
        return;

    if (curve is Polyline polyline)
    {
        try
        {
            polyline.ConstantWidth = width;
            polyline.Plinegen = true;
            return;
        }
        catch
        {
            int count = Math.Max(0, polyline.NumberOfVertices - (polyline.Closed ? 0 : 1));
            for (int i = 0; i < count; i++)
            {
                try
                {
                    polyline.SetStartWidthAt(i, width);
                    polyline.SetEndWidthAt(i, width);
                }
                catch { }
            }
        }
    }
    else if (curve is Polyline2d polyline2d)
    {
        polyline2d.ConstantWidth = width;
        polyline2d.LinetypeGenerationOn = true;
    }
}

private DBObjectCollection BuildCurves(
    AcEntity axis,
    double offset)
{
    var result =
        new DBObjectCollection();

    if (axis is CivilAlignment)
    {
        Curve? proxy =
            _station.CreatePolylineProxy(
                axis,
                offset);

        if (proxy != null)
            result.Add(proxy);

        return result;
    }

    if (!(axis is Curve cadCurve))
        return result;

    if (Math.Abs(offset) <= 1e-9)
    {
        result.Add(
            (Curve)cadCurve.Clone());

        return result;
    }

    // AutoCAD có thể trả phía offset khác nhau tùy loại Curve/hướng hình học.
    // Không suy đoán dấu: thử cả hai phía và chọn tập curve có signed-offset
    // gần nhất với quy ước ARM (LEFT < 0, RIGHT > 0).
    double distance =
        Math.Abs(offset);

    var candidates =
        new List<OffsetCandidateSet>();

    foreach (double rawOffset in
             new[]
             {
                 distance,
                 -distance
             })
    {
        DBObjectCollection offsets;

        try
        {
            offsets =
                cadCurve.GetOffsetCurves(
                    rawOffset);
        }
        catch
        {
            continue;
        }

        var curves =
            new List<Curve>();

        foreach (DBObject obj in offsets)
        {
            if (obj is Curve curve)
                curves.Add(curve);
            else
                obj.Dispose();
        }

        if (curves.Count == 0)
            continue;

        double score =
            ComputeOffsetScore(
                cadCurve,
                curves,
                offset);

        candidates.Add(
            new OffsetCandidateSet
            {
                Score = score,
                Curves = curves
            });
    }

    OffsetCandidateSet? best =
        candidates
            .OrderBy(x => x.Score)
            .FirstOrDefault();

    foreach (OffsetCandidateSet candidate in
             candidates)
    {
        if (!ReferenceEquals(
                candidate,
                best))
        {
            foreach (Curve curve in
                     candidate.Curves)
            {
                curve.Dispose();
            }
        }
    }

    if (best == null)
        return result;

    foreach (Curve curve in
             best.Curves)
    {
        result.Add(curve);
    }

    return result;
}

private static double ComputeOffsetScore(
    Curve source,
    IEnumerable<Curve> candidates,
    double desiredOffset)
{
    var errors =
        new List<double>();

    foreach (Curve candidate in candidates)
    {
        foreach (double fraction in
                 new[]
                 {
                     0.20,
                     0.50,
                     0.80
                 })
        {
            try
            {
                Point3d point =
                    CurveGeometryHelper
                        .PointAtFraction(
                            candidate,
                            fraction);

                double actual =
                    CurveGeometryHelper
                        .SignedOffset(
                            source,
                            point);

                errors.Add(
                    Math.Abs(
                        actual -
                        desiredOffset));
            }
            catch
            {
            }
        }
    }

    return errors.Count == 0
        ? double.MaxValue
        : CurveGeometryHelper.Median(
            errors);
}

private sealed class OffsetCandidateSet
{
    public double Score { get; set; }

    public List<Curve> Curves { get; set; } =
        new List<Curve>();
}

        private static string ResolveLongitudinalQuantityRole(string code, double offset)
        {
            if (Math.Abs(offset) <= 1e-4)
                return "CENTER_LINE";

            string normalized = (code ?? string.Empty).Trim();
            if (normalized.StartsWith("3.", StringComparison.OrdinalIgnoreCase))
                return "ROAD_EDGE";

            return "LANE_LINE";
        }

        private static List<MarkingPlacement> BuildPlacements(
            ArmCrossSectionState crossSection,
            List<ArmMarkingTemplateState> templates)
        {
            var result =
                new List<MarkingPlacement>();

            // Nguồn chính: VachSon/Marking đã cấu hình trong Tab 2.
            List<ArmCrossSectionPartState> configured =
                crossSection.Components
                    .Where(x =>
                        string.Equals(
                            x.Role,
                            "Marking",
                            StringComparison.OrdinalIgnoreCase) &&
                        !string.IsNullOrWhiteSpace(
                            x.Template))
                    .ToList();

            foreach (ArmCrossSectionPartState component in configured)
            {
                ArmMarkingTemplateState? template =
                    ResolveTemplate(
                        templates,
                        component.Template);

                if (template == null)
                    continue;

                AddConfiguredPlacement(
                    result,
                    component.Offset,
                    template);
            }

            if (result.Count > 0)
                return RemoveDuplicatePlacements(result);

            // Fallback cho MCN dữ liệu cũ chưa lưu component VachSon.
            ArmMarkingTemplateState? centerTemplate =
                ResolveTemplate(
                    templates,
                    "1.1");

            if (centerTemplate != null)
            {
                result.Add(
                    new MarkingPlacement
                    {
                        Offset = 0.0,
                        Template = centerTemplate
                    });
            }

            ArmMarkingTemplateState? laneTemplate =
                ResolveTemplate(
                    templates,
                    "1.2");

            if (laneTemplate == null)
                return RemoveDuplicatePlacements(result);

            AddLaneBoundaryPlacements(
                result,
                crossSection,
                templates,
                "Right",
                1.0,
                laneTemplate);

            AddLaneBoundaryPlacements(
                result,
                crossSection,
                templates,
                "Left",
                -1.0,
                laneTemplate);

            return RemoveDuplicatePlacements(result);
        }

        private static void AddConfiguredPlacement(
            List<MarkingPlacement> result,
            double offset,
            ArmMarkingTemplateState template)
        {
            if (!RequiresDoublePresentation(template))
            {
                result.Add(new MarkingPlacement { Offset = offset, Template = template });
                return;
            }

            // Vạch 1.3 được quản lý bằng MỘT template, nhưng hình học thực tế là hai
            // nét liền song song. Khoảng trong mặc định dùng giá trị phổ thông nhỏ nhất
            // của QCVN (0,15 m); dự án có thể ghi standardInnerGap_m để thay đổi.
            double stripeWidth = template.Width > 0.0 ? template.Width : 0.15;
            double innerGap = 0.15;
            if (MarkingTemplateManagementService.TryReadDouble(template, "standardInnerGap_m", out double configuredGap) && configuredGap >= 0.0)
                innerGap = configuredGap;
            else if (MarkingTemplateManagementService.TryReadDouble(template, "standardInnerGapMin_m", out double minimumGap) && minimumGap >= 0.0)
                innerGap = minimumGap;

            double centerDistance = stripeWidth + innerGap;
            result.Add(new MarkingPlacement { Offset = offset - centerDistance * 0.5, Template = template });
            result.Add(new MarkingPlacement { Offset = offset + centerDistance * 0.5, Template = template });
        }

        private static bool RequiresDoublePresentation(ArmMarkingTemplateState template)
        {
            if (string.Equals(template.Code, "1.3", StringComparison.OrdinalIgnoreCase))
                return true;

            if (MarkingTemplateManagementService.ReadCustom(template, "presentationRule", out string? rule) &&
                string.Equals(rule, "DUPLICATE_ON_CROSS_SECTION", StringComparison.OrdinalIgnoreCase))
                return true;

            if (MarkingTemplateManagementService.ReadCustom(template, "standardGeometry", out string? geometry) &&
                string.Equals(geometry, "DOUBLE_PARALLEL_SOLID", StringComparison.OrdinalIgnoreCase))
                return true;

            return false;
        }

        private static void AddLaneBoundaryPlacements(
            List<MarkingPlacement> result,
            ArmCrossSectionState crossSection,
            List<ArmMarkingTemplateState> templates,
            string side,
            double sign,
            ArmMarkingTemplateState template)
        {
            List<ArmCrossSectionPartState> lanes =
                crossSection.Components
                    .Where(x =>
                        string.Equals(
                            x.Role,
                            "Lane",
                            StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(
                            x.Side,
                            side,
                            StringComparison.OrdinalIgnoreCase))
                    .OrderBy(x =>
                        Math.Abs(x.Offset))
                    .ToList();

            double accumulated = 0.0;

            for (int i = 0;
                 i < lanes.Count - 1;
                 i++)
            {
                accumulated +=
                    Math.Max(
                        0.0,
                        lanes[i].Width);

                result.Add(
                    new MarkingPlacement
                    {
                        Offset =
                            sign *
                            accumulated,
                        Template =
                            template
                    });
            }
        }

        private static ArmMarkingTemplateState? ResolveTemplate(
            IEnumerable<ArmMarkingTemplateState> templates,
            string key)
        {
            string normalized =
                (key ?? string.Empty)
                    .Trim();

            if (string.IsNullOrWhiteSpace(normalized))
                return null;

            ArmMarkingTemplateState? exact =
                templates.FirstOrDefault(x =>
                    string.Equals(
                        x.Id,
                        normalized,
                        StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(
                        x.Code,
                        normalized,
                        StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(
                        x.Layer,
                        normalized,
                        StringComparison.OrdinalIgnoreCase));

            if (exact != null)
                return exact;

            // Tab 2 cũ thường lưu tên layer, ví dụ _1.VS_MARKING.1.2.
            return templates.FirstOrDefault(x =>
                normalized.EndsWith(
                    x.Code,
                    StringComparison.OrdinalIgnoreCase));
        }

        private static List<MarkingPlacement> RemoveDuplicatePlacements(
            IEnumerable<MarkingPlacement> source)
        {
            return source
                .GroupBy(x =>
                    (x.Template?.Id ?? string.Empty) +
                    "|" +
                    Math.Round(
                        x.Offset,
                        4)
                        .ToString(
                            System.Globalization.CultureInfo.InvariantCulture),
                    StringComparer.OrdinalIgnoreCase)
                .Select(g =>
                    g.First())
                .OrderBy(x =>
                    x.Offset)
                .ToList();
        }

        private void EraseGeneration(
            Database db,
            Transaction tr,
            string generationKey)
        {
            var blockTable =
                (BlockTable)tr.GetObject(
                    db.BlockTableId,
                    OpenMode.ForRead);

            var modelSpace =
                (BlockTableRecord)tr.GetObject(
                    blockTable[BlockTableRecord.ModelSpace],
                    OpenMode.ForRead);

            foreach (ObjectId id in modelSpace)
            {
                if (!(tr.GetObject(
                    id,
                    OpenMode.ForRead,
                    false) is AcEntity entity))
                {
                    continue;
                }

                ArmEntityMetadata? md =
                    _metadata.Read(
                        entity,
                        tr);

                if (md == null ||
                    !string.Equals(
                        md.GenerationKey,
                        generationKey,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                entity.UpgradeOpen();
                entity.Erase(true);
            }
        }

        private sealed class MarkingPlacement
        {
            public double Offset { get; set; }

            public ArmMarkingTemplateState? Template { get; set; }
        }
    }
}
