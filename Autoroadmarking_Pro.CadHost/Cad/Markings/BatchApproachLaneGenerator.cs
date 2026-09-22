using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autoroadmarking_Pro.Application.Intersections;
using Autoroadmarking_Pro.CadHost.Cad.Geometry;
using Autoroadmarking_Pro.CadHost.Cad.Layers;
using Autoroadmarking_Pro.CadHost.Cad.Metadata;
using Autoroadmarking_Pro.CadHost.Cad.RoadAxis;
using Autoroadmarking_Pro.CadHost.Cad.State;

namespace Autoroadmarking_Pro.CadHost.Cad.Markings
{
    /// <summary>
    /// Sinh vạch tiếp cận tới nút dựa trên vạch dừng 7.1 đã có.
    ///
    /// Contract:
    /// - 1.1 / 1.2 là vạch tim đường => offset 0.
    /// - 2.1 / 2.2 là vạch phân chia làn => lấy các ranh làn nội bộ từ MCN.
    /// - distance là chiều dài đoạn tiếp cận tính từ vạch dừng đi ngược hướng vào nút.
    /// - Candidate không đủ toàn bộ distance trên RoadAxis bị reject, tuyệt đối không clamp.
    /// - GenerationKey không chứa distance để đổi tham số sẽ replace cùng logical entity.
    /// </summary>
    public sealed class BatchApproachLaneGenerator
    {
        private static readonly HashSet<string> SupportedCodes =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "1.1",
                "1.2",
                "2.1",
                "2.2"
            };

        private readonly StationOffsetService _station = new StationOffsetService();
        private readonly MarkingLayerSynchronizer _layers = new MarkingLayerSynchronizer();
        private readonly EntityMetadataStore _metadata = new EntityMetadataStore();
        private readonly RoadAxisCatalogService _axisCatalog = new RoadAxisCatalogService();
        private readonly ArmMetadataMapper _mapper = new ArmMetadataMapper();
        private readonly MarkingPlacementPlanner _planner = new MarkingPlacementPlanner();

        public object Generate(
            Database db,
            Transaction tr,
            ArmProjectState state,
            string markingCode,
            string templateId,
            double distance)
        {
            if (double.IsNaN(distance) || double.IsInfinity(distance) || distance <= 0.0)
                throw new InvalidOperationException("Khoảng cách lùi từ vạch dừng phải lớn hơn 0 m.");

            ArmMarkingTemplateState? template = state.MarkingTemplates.FirstOrDefault(x =>
                string.Equals(x.Id, templateId, StringComparison.OrdinalIgnoreCase));

            if (template == null)
            {
                template = state.MarkingTemplates.FirstOrDefault(x =>
                    string.Equals(x.Code, markingCode, StringComparison.OrdinalIgnoreCase));
            }

            if (template == null)
                throw new InvalidOperationException("Không tìm thấy template vạch sơn.");

            string code = (template.Code ?? string.Empty).Trim();
            if (!SupportedCodes.Contains(code))
                throw new InvalidOperationException(
                    "Bước 5 chỉ hỗ trợ vạch tiếp cận 1.1, 1.2, 2.1 hoặc 2.2.");

            if (!(template.Width > 0.0) ||
                double.IsNaN(template.Width) ||
                double.IsInfinity(template.Width))
            {
                throw new InvalidOperationException(
                    "Template " + code + " không có bề rộng vạch hợp lệ.");
            }

            BlockTableRecord modelSpace = (BlockTableRecord)tr.GetObject(
                SymbolUtilityServices.GetBlockModelSpaceId(db),
                OpenMode.ForWrite);

            var existingGenerated = new List<ObjectId>();
            var stopLines = new List<ArmEntityMetadata>();

            // Chỉ scan ModelSpace một lần cho command này.
            foreach (ObjectId id in modelSpace)
            {
                if (!(tr.GetObject(id, OpenMode.ForRead, false) is Entity entity))
                    continue;

                ArmEntityMetadata? md = _metadata.Read(entity, tr);
                if (md == null)
                    continue;

                if (string.Equals(md.Source, "AUTO_APPROACH", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(md.MarkingCode, code, StringComparison.OrdinalIgnoreCase))
                {
                    existingGenerated.Add(id);
                    continue;
                }

                if (md.Extra != null &&
                    md.Extra.TryGetValue("QuantityRole", out string? role) &&
                    string.Equals(role, "STOP_LINE_7_1", StringComparison.OrdinalIgnoreCase))
                {
                    stopLines.Add(md);
                }
            }

            var plans = new List<PlannedApproach>();
            var rejected = new List<string>();

            foreach (ArmEntityMetadata stopMd in stopLines)
            {
                string roadKey = !string.IsNullOrWhiteSpace(stopMd.AxisKey)
                    ? stopMd.AxisKey
                    : stopMd.RoadKey;

                if (string.IsNullOrWhiteSpace(roadKey))
                {
                    rejected.Add("Vạch 7.1 " + stopMd.RecordId + ": thiếu AxisKey/RoadKey.");
                    continue;
                }

                ArmRoadAxisState axisInfo;
                Entity axis;
                try
                {
                    axisInfo = _axisCatalog.ResolveDescriptor(db, tr, state, roadKey);
                    axis = _axisCatalog.ResolveEntity(db, tr, state, roadKey);
                }
                catch (Exception ex)
                {
                    rejected.Add(
                        roadKey + ": không resolve được RoadAxis cho vạch 7.1 (" + ex.Message + ").");
                    continue;
                }

                string approachDirection = string.Empty;
                if (stopMd.Extra != null &&
                    stopMd.Extra.TryGetValue("ApproachDirection", out string? rawDirection))
                {
                    approachDirection = (rawDirection ?? string.Empty).Trim().ToUpperInvariant();
                }

                ApproachSegmentPlan stationPlan = _planner.PlanApproachSegment(
                    stopMd.Station,
                    approachDirection,
                    distance,
                    _station.StartStation(axis),
                    _station.EndStation(axis));

                if (!stationPlan.IsValid)
                {
                    rejected.Add(
                        axisInfo.RoadName + " · " +
                        (string.IsNullOrWhiteSpace(stopMd.OwnerId) ? stopMd.RecordId : stopMd.OwnerId) +
                        ": " + stationPlan.Error);
                    continue;
                }

                ArmComparisonState? comparison = state.ComparisonResults.FirstOrDefault(x =>
                    string.Equals(x.RoadKey, roadKey, StringComparison.OrdinalIgnoreCase) ||
                    (!string.IsNullOrWhiteSpace(stopMd.AxisHandle) &&
                     string.Equals(x.TimHandle, stopMd.AxisHandle, StringComparison.OrdinalIgnoreCase)));

                if (comparison == null)
                {
                    rejected.Add(axisInfo.RoadName + ": không có kết quả MCN matched cho approach.");
                    continue;
                }

                ArmCrossSectionState? crossSection = state.GetEffectiveCrossSections().FirstOrDefault(x =>
                    string.Equals(x.Id, comparison.Mcn, StringComparison.OrdinalIgnoreCase));

                if (crossSection == null)
                {
                    rejected.Add(
                        axisInfo.RoadName + ": không tìm thấy MCN hoạt động " +
                        (comparison.Mcn ?? string.Empty) + ".");
                    continue;
                }

                List<double> offsets = ResolveOffsets(code, crossSection);
                if (offsets.Count == 0)
                {
                    rejected.Add(
                        axisInfo.RoadName + ": MCN không có ranh làn phù hợp để sinh vạch " + code + ".");
                    continue;
                }

                plans.Add(new PlannedApproach
                {
                    AxisInfo = axisInfo,
                    Axis = axis,
                    Comparison = comparison,
                    StopMetadata = stopMd,
                    ApproachDirection = approachDirection,
                    StartStation = stationPlan.StartStation,
                    EndStation = stationPlan.EndStation,
                    Offsets = offsets
                });
            }

            // Không xóa kết quả đang có nếu lần chạy mới không tạo nổi một plan hợp lệ nào.
            if (plans.Count == 0)
            {
                return new
                {
                    count = 0,
                    totalLength = 0.0,
                    handles = new List<string>(),
                    rejected,
                    requestedDistance = distance,
                    markingCode = code
                };
            }

            // Idempotency: khi đã có plan mới hợp lệ, replace toàn bộ logical set cùng code.
            foreach (ObjectId id in existingGenerated)
            {
                if (tr.GetObject(id, OpenMode.ForWrite, false) is Entity entity &&
                    !entity.IsErased)
                {
                    entity.Erase(true);
                }
            }

            int created = 0;
            double totalLength = 0.0;
            var handles = new List<string>();

            foreach (PlannedApproach plan in plans)
            {
                ObjectId layerId = _layers.EnsureGeneratedLayer(
                    db,
                    tr,
                    plan.AxisInfo.RoadName,
                    template,
                    false);

                foreach (double offset in plan.Offsets)
                {
                    var segment = new Polyline();

                    double plannedLength = Math.Abs(plan.EndStation - plan.StartStation);
                    int intervalCount = Math.Max(
                        2,
                        (int)Math.Ceiling(plannedLength / 2.0));

                    double step = (plan.EndStation - plan.StartStation) / intervalCount;

                    for (int i = 0; i <= intervalCount; i++)
                    {
                        double station = plan.StartStation + i * step;
                        Point3d point = _station.PointAtStationOffset(
                            plan.Axis,
                            station,
                            offset,
                            out _);

                        segment.AddVertexAt(
                            i,
                            new Point2d(point.X, point.Y),
                            0.0,
                            template.Width,
                            template.Width);
                    }

                    segment.ConstantWidth = template.Width;
                    segment.Plinegen = true;
                    segment.LayerId = layerId;
                    segment.LinetypeScale = template.LinetypeScale > 0.0
                        ? template.LinetypeScale
                        : 1.0;

                    modelSpace.AppendEntity(segment);
                    tr.AddNewlyCreatedDBObject(segment, true);

                    string logicalOwnerId = !string.IsNullOrWhiteSpace(plan.StopMetadata.OwnerId)
                        ? plan.StopMetadata.OwnerId
                        : plan.StopMetadata.RecordId;

                    string generationKey = string.Join(
                        "|",
                        "APPROACH_LANE",
                        code,
                        plan.AxisInfo.EffectiveAxisKey,
                        logicalOwnerId,
                        plan.ApproachDirection,
                        offset.ToString("0.###", CultureInfo.InvariantCulture));

                    ArmEntityMetadata meta = _mapper.CreateGeneratedMarking(
                        segment,
                        generationKey,
                        "AUTO_APPROACH",
                        plan.AxisInfo.RoadName,
                        plan.AxisInfo.EffectiveAxisKey,
                        "INTERSECTION_APPROACH",
                        logicalOwnerId,
                        plan.Comparison.Mcn,
                        code,
                        template.Id,
                        template.Layer,
                        template.Width,
                        plan.StartStation,
                        plan.EndStation);

                    _mapper.BindAxis(meta, plan.AxisInfo);
                    meta.CadLayer = segment.Layer;
                    meta.Extra["ApproachDirection"] = plan.ApproachDirection;
                    meta.Extra["QuantityRole"] =
                        IsCenterLineCode(code) ? "CENTER_LINE" : "LANE_LINE";
                    meta.Extra["PaintRatio"] =
                        template.PaintRatio.ToString("0.########", CultureInfo.InvariantCulture);
                    meta.Extra["AnchorStopLineRecordId"] =
                        plan.StopMetadata.RecordId ?? string.Empty;
                    meta.Extra["StopLineStation"] =
                        plan.StopMetadata.Station.ToString("0.########", CultureInfo.InvariantCulture);
                    meta.Extra["RequestedDistance"] =
                        distance.ToString("0.########", CultureInfo.InvariantCulture);
                    meta.Extra["StartStation"] =
                        plan.StartStation.ToString("0.########", CultureInfo.InvariantCulture);
                    meta.Extra["EndStation"] =
                        plan.EndStation.ToString("0.########", CultureInfo.InvariantCulture);
                    meta.Extra["LaneOffset"] =
                        offset.ToString("0.########", CultureInfo.InvariantCulture);
                    meta.Extra["PlacementRule"] =
                        "SEGMENT_BEFORE_STOP_NO_CLAMP";
                    meta.Extra["NodeKey"] =
                        logicalOwnerId;

                    _metadata.Write(segment, tr, meta);

                    created++;
                    totalLength += segment.Length;
                    handles.Add(segment.ObjectId.Handle.ToString());
                }
            }

            return new
            {
                count = created,
                totalLength,
                handles,
                rejected,
                requestedDistance = distance,
                markingCode = code
            };
        }

        private static bool IsCenterLineCode(string code) =>
            string.Equals(code, "1.1", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(code, "1.2", StringComparison.OrdinalIgnoreCase);

        private List<double> ResolveOffsets(
            string code,
            ArmCrossSectionState crossSection)
        {
            if (IsCenterLineCode(code))
                return new List<double> { 0.0 };

            var offsets = new List<double>();
            offsets.AddRange(GetLaneBoundaryOffsets(crossSection, "Right", 1.0));
            offsets.AddRange(GetLaneBoundaryOffsets(crossSection, "Left", -1.0));

            // Tránh duplicate do sai số số thực và không tạo vạch 2.x trùng TIM.
            return offsets
                .Where(x => Math.Abs(x) > 1e-6)
                .GroupBy(x => Math.Round(x, 6))
                .Select(g => g.First())
                .OrderBy(x => x)
                .ToList();
        }

        private List<double> GetLaneBoundaryOffsets(
            ArmCrossSectionState crossSection,
            string side,
            double sign)
        {
            var result = new List<double>();

            List<ArmCrossSectionPartState> lanes = crossSection.Components
                .Where(x =>
                    string.Equals(x.Role, "Lane", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(x.Side, side, StringComparison.OrdinalIgnoreCase))
                .OrderBy(x => Math.Abs(x.Offset))
                .ToList();

            double accumulated = 0.0;

            // Chỉ lấy ranh giữa các làn; mép ngoài cùng thuộc vạch mép, không phải 2.1/2.2.
            for (int i = 0; i < lanes.Count - 1; i++)
            {
                accumulated += Math.Max(0.0, lanes[i].Width);
                if (accumulated > 1e-6)
                    result.Add(sign * accumulated);
            }

            return result;
        }

        private sealed class PlannedApproach
        {
            public ArmRoadAxisState AxisInfo { get; set; } = new ArmRoadAxisState();
            public Entity Axis { get; set; } = null!;
            public ArmComparisonState Comparison { get; set; } = null!;
            public ArmEntityMetadata StopMetadata { get; set; } = null!;
            public string ApproachDirection { get; set; } = string.Empty;
            public double StartStation { get; set; }
            public double EndStation { get; set; }
            public List<double> Offsets { get; set; } = new List<double>();
        }
    }
}
