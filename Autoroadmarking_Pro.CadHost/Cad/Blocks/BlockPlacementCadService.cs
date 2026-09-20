using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autoroadmarking_Pro.CadHost.Cad.Geometry;
using Autoroadmarking_Pro.CadHost.Cad.Metadata;
using Autoroadmarking_Pro.CadHost.Cad.State;

using CivilAlignment = Autodesk.Civil.DatabaseServices.Alignment;

namespace Autoroadmarking_Pro.CadHost.Cad.Blocks
{
    /// <summary>
    /// Phân tích và sinh Block 7.6 / 9.3.
    /// Dòng xe được biểu diễn độc lập bằng FORWARD / REVERSE;
    /// LEFT / RIGHT chỉ là phía hình học của MCN.
    /// </summary>
    public sealed class BlockPlacementCadService
    {
        private readonly CadGeometryService _geometry =
            new CadGeometryService();

        private readonly StationOffsetService _station =
            new StationOffsetService();

        private readonly BlockDefinitionService _definitions =
            new BlockDefinitionService();

        private readonly EntityMetadataStore _metadata =
            new EntityMetadataStore();

        public List<ArmBlockProposalState> Analyze(
            Database db,
            Transaction tr,
            ArmProjectState state,
            ArmCrossSectionState mcn,
            string approach,
            List<LaneMappingInput> laneMappings,
            double first93,
            int clusters,
            double spacing93,
            double distance76)
        {
            if (state == null)
                throw new ArgumentNullException(nameof(state));

            if (mcn == null)
                throw new ArgumentNullException(nameof(mcn));

            ArmComparisonState? comparison =
                ResolveComparison(
                    state,
                    approach,
                    mcn);

            if (comparison == null)
                throw new InvalidOperationException(
                    "Chưa có kết quả TIM/MÉP/MCN hợp lệ ở Tab 3 cho nhánh đang chọn.");

            string nodeId = ResolveNodeId(approach);
            string approachDirection = ResolveApproachDirection(approach);

            ObjectId axisId =
                _geometry.FromHandle(
                    db,
                    comparison.TimHandle);

            if (axisId.IsNull ||
                !(tr.GetObject(
                    axisId,
                    OpenMode.ForRead,
                    false) is Entity axis))
            {
                throw new InvalidOperationException(
                    "Không tìm thấy TIM tuyến của kết quả đối chiếu.");
            }

            double startStation =
                _station.StartStation(
                    axis);

            // Dùng station thật của Alignment; Start + Length không an toàn khi
            // bản vẽ có station equation. Polyline vẫn trả 0..Length.
            double endStation =
                _station.EndStation(
                    axis);

            double stopReference =
                FindReferenceStation(
                    db,
                    tr,
                    comparison.RoadKey,
                    comparison.TimHandle,
                    "7.1",
                    nodeId,
                    approachDirection);

            // 7.6 và 9.3 có quan hệ mốc khác nhau. Không được tự thay thế 7.1 bằng
            // 7.3 (hoặc ngược lại), vì như vậy sẽ âm thầm đổi quy tắc thiết kế.
            double crosswalkReference =
                FindReferenceStation(
                    db,
                    tr,
                    comparison.RoadKey,
                    comparison.TimHandle,
                    "7.3",
                    nodeId,
                    approachDirection);

            List<LaneMappingInput> mappings =
                laneMappings ?? new List<LaneMappingInput>();

            bool needs76 = mappings.Any(mapping =>
                mapping?.Enable76 == true &&
                !(state.BlockInboundOnly76 &&
                  IsOutbound(mapping)));

            bool needs93 = mappings.Any(mapping => mapping?.Enable93 == true);
            bool anchor93IsCrosswalk =
                string.Equals(state.BlockAnchor93, "73", StringComparison.OrdinalIgnoreCase);

            if (needs76 && double.IsNaN(crosswalkReference))
            {
                throw new InvalidOperationException(
                    "Không tìm thấy vạch 7.3 của đúng nút giao. 7.6 bắt buộc lấy 7.3 làm mốc.");
            }

            if (needs93 &&
                ((anchor93IsCrosswalk && double.IsNaN(crosswalkReference)) ||
                 (!anchor93IsCrosswalk && double.IsNaN(stopReference))))
            {
                throw new InvalidOperationException(
                    anchor93IsCrosswalk
                        ? "Không tìm thấy vạch 7.3 của đúng nút giao để làm mốc 9.3."
                        : "Không tìm thấy vạch 7.1 của đúng nút giao để làm mốc 9.3.");
            }

            int clusterCount =
                Math.Max(
                    1,
                    clusters);

            first93 =
                Math.Max(
                    0.0,
                    first93);

            spacing93 =
                Math.Max(
                    0.0,
                    spacing93);

            distance76 =
                Math.Max(
                    0.0,
                    distance76);

            var proposals =
                new List<ArmBlockProposalState>();

            int sequence = 1;

            foreach (LaneMappingInput mapping in mappings)
            {
                if (mapping == null || string.IsNullOrWhiteSpace(mapping.Lane))
                    continue;

                double laneOffset =
                    ResolveLaneOffset(
                        mcn,
                        mapping.Lane);

                int travelSign =
                    string.Equals(mapping.Direction, "REVERSE", StringComparison.OrdinalIgnoreCase)
                        ? -1
                        : 1;

                if (mapping.Enable93)
                    for (int cluster = 0;
                         cluster < clusterCount;
                         cluster++)
                    {
                        double base93 = IsOutbound(mapping)
                            ? Math.Max(0.0, state.BlockOutboundDistance93)
                            : first93;
                        double upstreamDistance = base93 + cluster * spacing93;
                        double reference93 = string.Equals(state.BlockAnchor93, "73", StringComparison.OrdinalIgnoreCase)
                            ? crosswalkReference
                            : stopReference;

                        double station =
                            reference93 -
                            travelSign * upstreamDistance;

                        // Không ép proposal ra đầu/cuối tuyến. Nếu quy tắc đẩy vị trí ra ngoài
                        // miền station thật thì bỏ proposal đó; clamp có thể chồng nhiều block
                        // lên cùng một endpoint và tạo sai khối lượng.
                        if (!IsStationInside(
                                station,
                                startStation,
                                endStation))
                        {
                            continue;
                        }

                        string blockName =
                            ResolveBlock(
                                state,
                                "9.3",
                                NormalizeMovement(
                                    mapping.Movement));

                        proposals.Add(
                            Build(
                                "P" +
                                sequence++
                                    .ToString(
                                        "000",
                                        CultureInfo.InvariantCulture),
                                comparison,
                                approach,
                                mapping,
                                ParseLaneIndex(mapping.Lane),
                                cluster + 1,
                                "9.3",
                                blockName,
                                station,
                                laneOffset));
                    }

                if (mapping.Enable76 && !(state.BlockInboundOnly76 && IsOutbound(mapping)))
                {
                    double station76 =
                        crosswalkReference -
                        travelSign * distance76;

                    if (!IsStationInside(
                            station76,
                            startStation,
                            endStation))
                    {
                        continue;
                    }

                    proposals.Add(
                        Build(
                        "P" +
                        sequence++
                            .ToString(
                                "000",
                                CultureInfo.InvariantCulture),
                        comparison,
                        approach,
                        mapping,
                        ParseLaneIndex(mapping.Lane),
                        0,
                        "7.6",
                        ResolveBlock(
                            state,
                            "7.6",
                            "DIAMOND"),
                        station76,
                        laneOffset));
                }
            }

            return proposals;
        }

        public int Generate(
            Database db,
            Transaction tr,
            ArmProjectState state,
            List<ArmBlockProposalState> proposals)
        {
            BlockTable blockTable =
                (BlockTable)tr.GetObject(
                    db.BlockTableId,
                    OpenMode.ForRead);

            BlockTableRecord modelSpace =
                (BlockTableRecord)tr.GetObject(
                    blockTable[BlockTableRecord.ModelSpace],
                    OpenMode.ForWrite);

            int count = 0;

            foreach (ArmBlockProposalState proposal in
                     (proposals ?? new List<ArmBlockProposalState>())
                         .Where(x => x?.Selected == true))
            {
                ObjectId axisId =
                    _geometry.FromHandle(
                        db,
                        proposal.AxisHandle);

                if (axisId.IsNull ||
                    !(tr.GetObject(
                        axisId,
                        OpenMode.ForRead,
                        false) is Entity axis))
                {
                    continue;
                }

                ArmBlockCatalogState? catalogItem =
                    state.BlockCatalog?
                        .FirstOrDefault(x => x != null &&
                            string.Equals(x.Name, proposal.Block, StringComparison.OrdinalIgnoreCase));

                string sourceDwg =
                    catalogItem?.SourceDwgPath ??
                    string.Empty;

                ObjectId definitionId;

                try
                {
                    definitionId =
                        _definitions.EnsureDefinition(
                            db,
                            tr,
                            proposal.Block,
                            sourceDwg);
                }
                catch
                {
                    continue;
                }

                // Idempotent: PlacementKey là identity của một vị trí block.
                EraseByGeneration(
                    db,
                    tr,
                    proposal.PlacementKey);

                Vector3d tangent;

                Point3d insertionPoint =
                    _station.PointAtStationOffset(
                        axis,
                        proposal.StationValue,
                        proposal.OffsetValue,
                        out tangent);

                bool reverse =
                    string.Equals(proposal.Direction, "REVERSE", StringComparison.OrdinalIgnoreCase);

                double rotation = state.BlockRotateWithTraffic
                    ? Math.Atan2(tangent.Y, tangent.X) + (reverse ? Math.PI : 0.0)
                    : proposal.Rotation;

                var blockReference =
                    new BlockReference(
                        insertionPoint,
                        definitionId)
                    {
                        Rotation = rotation
                    };

                modelSpace.AppendEntity(
                    blockReference);

                tr.AddNewlyCreatedDBObject(
                    blockReference,
                    true);

                _metadata.Write(
                    blockReference,
                    tr,
                    new ArmEntityMetadata
                    {
                        RecordId =
                            "BLK_" +
                            Guid.NewGuid()
                                .ToString("N"),
                        GenerationKey =
                            proposal.PlacementKey,
                        Source = "AUTO_BLOCK",
                        RoadName = proposal.Road,
                        AxisKey = proposal.RoadKey,
                        AxisHandle = proposal.AxisHandle,
                        AxisType = axis is CivilAlignment ? "ALIGNMENT" : "POLYLINE",
                        RoadKey = proposal.RoadKey,
                        OwnerType = "INTERSECTION",
                        // Tab 6 nhóm theo nút, không nhóm mỗi approach thành một nút riêng.
                        OwnerId = !string.IsNullOrWhiteSpace(proposal.NodeId)
                            ? proposal.NodeId
                            : ResolveNodeId(proposal.Approach),
                        MarkingCode = proposal.Code,
                        BlockName = proposal.Block,
                        LaneIndex = proposal.LaneIndex,
                        ClusterIndex = proposal.Cluster,
                        Station = proposal.StationValue,
                        Offset = proposal.OffsetValue,
                        CadLayer = blockReference.Layer
                    });

                count++;
            }

            return count;
        }

        private static ArmComparisonState? ResolveComparison(
            ArmProjectState state,
            string approach,
            ArmCrossSectionState mcn)
        {
            string comparisonId =
                string.Empty;

            if (!string.IsNullOrWhiteSpace(approach))
            {
                string[] tokens =
                    approach.Split('|');

                if (tokens.Length > 0)
                    comparisonId = tokens[0];
            }

            if (!string.IsNullOrWhiteSpace(comparisonId))
            {
                ArmComparisonState? exact =
                    state.ComparisonResults?
                        .FirstOrDefault(x => x != null &&
                            string.Equals(x.Status, "matched", StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(x.Id, comparisonId, StringComparison.OrdinalIgnoreCase));

                if (exact != null)
                    return exact;
            }

            ArmComparisonState? sameMcn =
                state.ComparisonResults?
                    .FirstOrDefault(x => x != null &&
                        string.Equals(x.Status, "matched", StringComparison.OrdinalIgnoreCase) &&
                        (string.Equals(x.Mcn, mcn.Id, StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(x.Mcn, mcn.Name, StringComparison.OrdinalIgnoreCase)));

            return sameMcn ??
                   state.ComparisonResults?
                       .FirstOrDefault(x => x != null &&
                           string.Equals(x.Status, "matched", StringComparison.OrdinalIgnoreCase));
        }

        private static ArmBlockProposalState Build(
            string id,
            ArmComparisonState comparison,
            string approach,
            LaneMappingInput mapping,
            int laneIndex,
            int cluster,
            string code,
            string block,
            double station,
            double offset)
        {
            string movement =
                code == "7.6"
                    ? "DIAMOND"
                    : NormalizeMovement(
                        mapping.Movement);

            return new ArmBlockProposalState
            {
                Id = id,
                Selected = true,
                Road = comparison.Road,
                RoadKey = comparison.RoadKey,
                AxisHandle = comparison.TimHandle,
                Approach = approach,
                NodeId = ResolveNodeId(approach),
                Direction =
                    string.Equals(mapping.Direction, "REVERSE", StringComparison.OrdinalIgnoreCase)
                        ? "REVERSE"
                        : "FORWARD",
                Lane = mapping.Lane,
                LaneIndex = laneIndex,
                Cluster = cluster,
                Code = code,
                Movement = movement,
                Block = block,
                StationValue = station,
                Station = FormatStation(station),
                OffsetValue = offset,
                Offset =
                    (offset >= 0.0
                        ? "+"
                        : string.Empty) +
                    offset.ToString(
                        "0.00",
                        CultureInfo.InvariantCulture),
                // Stable identity không chứa station/offset. Khi người dùng đổi khoảng
                // cách 7.6/9.3 hoặc MCN thay đổi offset, Generate sẽ thay đúng block cũ.
                PlacementKey =
                    string.Join(
                        "|",
                        "AUTO_BLOCK",
                        comparison.RoadKey,
                        ResolveNodeId(approach),
                        string.Equals(mapping.Direction, "REVERSE", StringComparison.OrdinalIgnoreCase) ? "REVERSE" : "FORWARD",
                        mapping.Lane,
                        code,
                        cluster.ToString(CultureInfo.InvariantCulture)),
                Status = "MỚI"
            };
        }

        private static double ResolveLaneOffset(
            ArmCrossSectionState mcn,
            string lane)
        {
            string laneKey = (lane ?? string.Empty).Trim();
            bool left = laneKey.StartsWith("L", StringComparison.OrdinalIgnoreCase) ||
                        laneKey.StartsWith("LEFT", StringComparison.OrdinalIgnoreCase);

            int laneIndex =
                ParseLaneIndex(
                    lane);

            if (laneIndex <= 0)
                laneIndex = 1;

            List<ArmCrossSectionPartState> sideLanes =
                mcn.Components
                    .Where(x => x != null &&
                        string.Equals(x.Role, "Lane", StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(x.Side, left ? "Left" : "Right", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(x =>
                        Math.Abs(x.Offset))
                    .ToList();

            if (laneIndex <= sideLanes.Count)
            {
                ArmCrossSectionPartState component =
                    sideLanes[laneIndex - 1];

                // Offset đã được Tab 2 adapter tính từ TIM,
                // nên đây là nguồn hình học ưu tiên.
                if (Math.Abs(component.Offset) > 1e-9)
                    return component.Offset;
            }

            // Fallback tương thích dữ liệu MCN cũ chưa lưu offset.
            double accumulated = 0.0;

            for (int i = 0;
                 i < sideLanes.Count;
                 i++)
            {
                double center =
                    accumulated +
                    sideLanes[i].Width * 0.5;

                if (i + 1 == laneIndex)
                    return left
                        ? -center
                        : center;

                accumulated +=
                    sideLanes[i].Width;
            }

            return 0.0;
        }

        private static int ParseLaneIndex(string? lane)
        {
            if (string.IsNullOrWhiteSpace(lane)) return 0;
            string value = (lane ?? string.Empty).Trim();
            int end = value.Length - 1;
            while (end >= 0 && !char.IsDigit(value[end])) end--;
            if (end < 0) return 0;
            int start = end;
            while (start > 0 && char.IsDigit(value[start - 1])) start--;
            return int.TryParse(value.Substring(start, end - start + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out int result)
                ? result
                : 0;
        }

        private static bool IsOutbound(LaneMappingInput? mapping)
        {
            return string.Equals(
                mapping?.TrafficRole,
                "OUTBOUND",
                StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeMovement(
            string? movement)
        {
            string value =
                (movement ?? string.Empty)
                    .Trim()
                    .ToUpperInvariant()
                    .Replace('-', '_')
                    .Replace(' ', '_');

            switch (value)
            {
                case "LEFT":
                case "RIGHT":
                case "STRAIGHT":
                case "STRAIGHT_LEFT":
                case "STRAIGHT_RIGHT":
                case "LEFT_RIGHT":
                    return value;

                default:
                    return "STRAIGHT";
            }
        }

        private static string ResolveBlock(
            ArmProjectState state,
            string code,
            string movement)
        {
            string variant =
                MapVariant(
                    movement);

            ArmBlockCatalogState? catalog =
                state.BlockCatalog?
                    .FirstOrDefault(x => x != null &&
                        string.Equals(x.Code, code, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(x.Variant, variant, StringComparison.OrdinalIgnoreCase));

            if (catalog != null)
                return catalog.Name;

            if (code == "7.6")
                return "ARM_7_6_DIAMOND";

            switch (movement)
            {
                case "LEFT":
                    return "RETRAI";
                case "RIGHT":
                    return "REPHAI";
                case "STRAIGHT_LEFT":
                    return "DITHANGTRAI";
                case "STRAIGHT_RIGHT":
                    return "DITHANGPHAI";
                case "LEFT_RIGHT":
                    return "TRAIPHAI";
                default:
                    return "DITHANG";
            }
        }

        private static string MapVariant(
            string movement)
        {
            switch (movement)
            {
                case "LEFT":
                    return "Left";
                case "RIGHT":
                    return "Right";
                case "STRAIGHT_LEFT":
                    return "StraightLeft";
                case "STRAIGHT_RIGHT":
                    return "StraightRight";
                case "LEFT_RIGHT":
                    return "LeftRight";
                case "DIAMOND":
                    return "Diamond";
                default:
                    return "Straight";
            }
        }

        private static string ResolveNodeId(string approach)
        {
            if (string.IsNullOrWhiteSpace(approach)) return string.Empty;
            string[] tokens = approach.Split('|');
            return tokens.Length >= 4 ? tokens[3] : string.Empty;
        }

        private double FindReferenceStation(
            Database db,
            Transaction tr,
            string roadKey,
            string axisHandle,
            string code,
            string nodeId,
            string approachDirection)
        {
            BlockTable blockTable = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            BlockTableRecord modelSpace = (BlockTableRecord)tr.GetObject(
                blockTable[BlockTableRecord.ModelSpace], OpenMode.ForRead);

            var directedValues = new List<double>();
            var legacyValues = new List<double>();

            foreach (ObjectId id in modelSpace)
            {
                if (!(tr.GetObject(id, OpenMode.ForRead, false) is Entity entity))
                    continue;

                ArmEntityMetadata? md = _metadata.Read(entity, tr);
                if (md == null)
                    continue;

                string metadataAxisKey = !string.IsNullOrWhiteSpace(md.AxisKey) ? md.AxisKey : md.RoadKey;
                bool sameAxis =
                    string.Equals(metadataAxisKey, roadKey, StringComparison.OrdinalIgnoreCase) ||
                    (!string.IsNullOrWhiteSpace(axisHandle) &&
                     string.Equals(md.AxisHandle, axisHandle, StringComparison.OrdinalIgnoreCase));

                if (!sameAxis ||
                    !string.Equals(md.MarkingCode, code, StringComparison.OrdinalIgnoreCase) ||
                    (!string.IsNullOrWhiteSpace(nodeId) &&
                     !string.Equals(md.OwnerId, nodeId, StringComparison.OrdinalIgnoreCase)) ||
                    double.IsNaN(md.Station) || double.IsInfinity(md.Station))
                {
                    continue;
                }

                string metadataDirection = ReadExtra(md, "ApproachDirection");
                if (string.IsNullOrWhiteSpace(metadataDirection))
                {
                    legacyValues.Add(md.Station);
                    continue;
                }

                if (string.IsNullOrWhiteSpace(approachDirection) ||
                    string.Equals(metadataDirection, approachDirection, StringComparison.OrdinalIgnoreCase))
                {
                    directedValues.Add(md.Station);
                }
            }

            // Schema mới luôn thắng schema cũ. Legacy chỉ là fallback để bản vẽ cũ vẫn
            // mở được; sau lần sinh 7.1/7.3 mới, metadata approach-specific sẽ thay thế.
            List<double> values = directedValues.Count > 0 ? directedValues : legacyValues;
            return MedianOrNaN(values);
        }

        private static double MedianOrNaN(List<double> values)
        {
            if (values == null || values.Count == 0)
                return double.NaN;

            values = values
                .Where(x => !double.IsNaN(x) && !double.IsInfinity(x))
                .OrderBy(x => x)
                .ToList();
            if (values.Count == 0)
                return double.NaN;

            int m = values.Count / 2;
            return values.Count % 2 == 1
                ? values[m]
                : (values[m - 1] + values[m]) * 0.5;
        }

        private static string ResolveApproachDirection(string approach)
        {
            if (string.IsNullOrWhiteSpace(approach)) return string.Empty;
            string[] tokens = approach.Split('|');
            if (tokens.Length >= 3)
            {
                string direction = tokens[2].Trim().ToUpperInvariant();
                if (direction == "FORWARD" || direction == "REVERSE")
                    return direction;
            }
            return approach.IndexOf("REVERSE", StringComparison.OrdinalIgnoreCase) >= 0
                ? "REVERSE"
                : "FORWARD";
        }

        private static string ReadExtra(ArmEntityMetadata metadata, string key)
        {
            return metadata.Extra != null && metadata.Extra.TryGetValue(key, out string? value)
                ? value ?? string.Empty
                : string.Empty;
        }

        private void EraseByGeneration(
            Database db,
            Transaction tr,
            string generationKey)
        {
            if (string.IsNullOrWhiteSpace(generationKey))
                return;

            BlockTable blockTable =
                (BlockTable)tr.GetObject(
                    db.BlockTableId,
                    OpenMode.ForRead);

            BlockTableRecord modelSpace =
                (BlockTableRecord)tr.GetObject(
                    blockTable[BlockTableRecord.ModelSpace],
                    OpenMode.ForRead);

            var toErase =
                new List<ObjectId>();

            foreach (ObjectId id in modelSpace)
            {
                if (!(tr.GetObject(
                        id,
                        OpenMode.ForRead,
                        false) is Entity entity))
                {
                    continue;
                }

                ArmEntityMetadata? md =
                    _metadata.Read(
                        entity,
                        tr);

                if (md != null &&
                    string.Equals(md.GenerationKey, generationKey, StringComparison.Ordinal))
                {
                    toErase.Add(id);
                }
            }

            foreach (ObjectId id in toErase)
            {
                Entity entity =
                    (Entity)tr.GetObject(
                        id,
                        OpenMode.ForWrite,
                        false);

                entity.Erase(true);
            }
        }

        private static bool IsStationInside(
            double station,
            double start,
            double end)
        {
            if (end < start)
            {
                double temporary = start;
                start = end;
                end = temporary;
            }

            const double tolerance = 1e-6;
            return station >= start - tolerance &&
                   station <= end + tolerance;
        }

        private static string FormatStation(
            double station)
        {
            int km =
                (int)Math.Floor(
                    station / 1000.0);

            double metres =
                station -
                km * 1000.0;

            return km.ToString(
                       CultureInfo.InvariantCulture) +
                   "+" +
                   metres.ToString(
                       "000.00",
                       CultureInfo.InvariantCulture);
        }
    }

    public sealed class LaneMappingInput
    {
        /// <summary>Hướng chạy tuyệt đối theo chiều tăng/giảm station: FORWARD/REVERSE.</summary>
        public string Direction { get; set; } = string.Empty;

        /// <summary>Vai trò của làn đối với approach đang xét: INBOUND/OUTBOUND.</summary>
        public string TrafficRole { get; set; } = "INBOUND";

        public string Lane { get; set; } = string.Empty;
        public string Movement { get; set; } = string.Empty;
        public bool Enable76 { get; set; } = true;
        public bool Enable93 { get; set; } = true;
    }
}