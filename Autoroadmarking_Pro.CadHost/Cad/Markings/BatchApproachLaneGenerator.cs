using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autoroadmarking_Pro.CadHost.Cad.Geometry;
using Autoroadmarking_Pro.CadHost.Cad.Layers;
using Autoroadmarking_Pro.CadHost.Cad.Metadata;
using Autoroadmarking_Pro.CadHost.Cad.State;
using Autoroadmarking_Pro.CadHost.Cad.RoadAxis;

namespace Autoroadmarking_Pro.CadHost.Cad.Markings
{
    public sealed class BatchApproachLaneGenerator
    {
        private readonly CadGeometryService _geometry = new CadGeometryService();
        private readonly StationOffsetService _station = new StationOffsetService();
        private readonly MarkingLayerSynchronizer _layers = new MarkingLayerSynchronizer();
        private readonly EntityMetadataStore _metadata = new EntityMetadataStore();
        private readonly RoadAxisCatalogService _axisCatalog = new RoadAxisCatalogService();
        private readonly ArmMetadataMapper _mapper = new ArmMetadataMapper();

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

            string normalizedCode = (markingCode ?? string.Empty).Trim().ToUpperInvariant();
            var supportedCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "1.1", "1.2", "2.1", "2.2"
            };
            if (!supportedCodes.Contains(normalizedCode))
                throw new InvalidOperationException("Bước 5 chỉ hỗ trợ các mã 1.1, 1.2, 2.1 hoặc 2.2.");

            ArmMarkingTemplateState? template = state.MarkingTemplates.FirstOrDefault(x =>
                string.Equals(x.Id, templateId, StringComparison.OrdinalIgnoreCase));
            if (template == null)
            {
                template = state.MarkingTemplates.FirstOrDefault(x =>
                    string.Equals(x.Code, markingCode, StringComparison.OrdinalIgnoreCase));
            }
            if (template == null)
                throw new InvalidOperationException("Không tìm thấy template vạch sơn.");

            BlockTableRecord modelSpace = (BlockTableRecord)tr.GetObject(
                SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForWrite);

            var toErase = new List<ObjectId>();
            var stopLines = new List<ArmEntityMetadata>();

            foreach (ObjectId id in modelSpace)
            {
                if (!(tr.GetObject(id, OpenMode.ForRead, false) is Entity entity)) continue;
                ArmEntityMetadata? md = _metadata.Read(entity, tr);
                if (md == null) continue;

                if (md.Source == "AUTO_APPROACH" && md.MarkingCode == template.Code)
                {
                    toErase.Add(id);
                }
                else if (md.Extra != null && md.Extra.TryGetValue("QuantityRole", out string role) && role == "STOP_LINE_7_1")
                {
                    stopLines.Add(md);
                }
            }

            foreach (ObjectId id in toErase)
            {
                if (tr.GetObject(id, OpenMode.ForWrite, false) is Entity entity && !entity.IsErased)
                    entity.Erase(true);
            }

            int created = 0;
            double totalLength = 0.0;
            var handles = new List<string>();
            var rejected = new List<string>();

            foreach (ArmEntityMetadata stopMd in stopLines)
            {
                string roadKey = stopMd.AxisKey;
                ArmRoadAxisState axisInfo;
                try
                {
                    axisInfo = _axisCatalog.ResolveDescriptor(db, tr, state, roadKey);
                }
                catch
                {
                    continue;
                }

                Entity axis = _axisCatalog.ResolveEntity(db, tr, state, roadKey);

                double stopStation = stopMd.Station;
                string approachDirection = stopMd.Extra.TryGetValue("ApproachDirection", out string dir) ? dir : "FORWARD";
                
                // 20m TRƯỚC vạch dừng
                double startStation, endStation;
                if (approachDirection == "FORWARD")
                {
                    endStation = stopStation;
                    startStation = stopStation - distance;
                }
                else
                {
                    startStation = stopStation;
                    endStation = stopStation + distance;
                }

                double axisStart = _station.StartStation(axis);
                double axisEnd = _station.EndStation(axis);
                double domainMin = Math.Min(axisStart, axisEnd);
                double domainMax = Math.Max(axisStart, axisEnd);
                const double stationTolerance = 1e-6;

                // Không silent-clamp đoạn yêu cầu về endpoint của tuyến. Nếu người dùng
                // yêu cầu 20 m thì approach phải có đủ toàn bộ 20 m; thiếu chiều dài thì reject.
                if (startStation < domainMin - stationTolerance ||
                    startStation > domainMax + stationTolerance ||
                    endStation < domainMin - stationTolerance ||
                    endStation > domainMax + stationTolerance)
                {
                    rejected.Add(
                        $"{axisInfo.RoadName}: Không đủ {distance:0.###} m trước vạch dừng " +
                        $"(yêu cầu {startStation:0.###}→{endStation:0.###}, miền tuyến {domainMin:0.###}→{domainMax:0.###}).");
                    continue;
                }

                if (Math.Abs(endStation - startStation) <= 0.1)
                {
                    rejected.Add($"{axisInfo.RoadName}: Đoạn vạch quá ngắn.");
                    continue;
                }

                ArmComparisonState? comparison = state.ComparisonResults.FirstOrDefault(x => string.Equals(x.RoadKey, roadKey, StringComparison.OrdinalIgnoreCase));
                if (comparison == null) continue;
                
                ArmCrossSectionState? crossSection = state.GetEffectiveCrossSections().FirstOrDefault(x => string.Equals(x.Id, comparison.Mcn, StringComparison.OrdinalIgnoreCase));
                if (crossSection == null) continue;

                var offsets = new List<double>();
                offsets.AddRange(GetLaneBoundaryOffsets(crossSection, "Right", 1.0));
                offsets.AddRange(GetLaneBoundaryOffsets(crossSection, "Left", -1.0));

                ObjectId layerId = _layers.EnsureGeneratedLayer(db, tr, axisInfo.RoadName, template, false);
                string resolvedLayer = ((LayerTableRecord)tr.GetObject(layerId, OpenMode.ForRead)).Name;

                foreach (double offset in offsets.Distinct())
                {
                    var segment = new Polyline();
                    int pts = Math.Max(2, (int)Math.Ceiling((endStation - startStation) / 2.0));
                    double step = (endStation - startStation) / pts;
                    for(int i = 0; i <= pts; i++)
                    {
                        double st = startStation + i * step;
                        Point3d pt = _station.PointAtStationOffset(axis, st, offset, out _);
                        segment.AddVertexAt(i, new Point2d(pt.X, pt.Y), 0.0, template.Width, template.Width);
                    }
                    segment.ConstantWidth = template.Width;
                    segment.Layer = resolvedLayer;
                    
                    if (template.LinetypeScale > 0)
                        segment.LinetypeScale = template.LinetypeScale;

                    modelSpace.AppendEntity(segment);
                    tr.AddNewlyCreatedDBObject(segment, true);

                    string nodeKey =
                        stopMd.Extra != null && stopMd.Extra.TryGetValue("NodeKey", out string storedNodeKey) &&
                        !string.IsNullOrWhiteSpace(storedNodeKey)
                            ? storedNodeKey
                            : (!string.IsNullOrWhiteSpace(stopMd.OwnerId) ? stopMd.OwnerId : stopMd.RecordId);

                    string generationKey = string.Join(
                        "|",
                        "APPROACH_LANE",
                        roadKey,
                        nodeKey,
                        approachDirection,
                        template.Code,
                        offset.ToString("0.###", CultureInfo.InvariantCulture));

                    ArmEntityMetadata meta = _mapper.CreateGeneratedMarking(
                        segment,
                        generationKey,
                        "AUTO_APPROACH",
                        axisInfo.RoadName,
                        roadKey,
                        "INTERSECTION_APPROACH",
                        nodeKey,
                        comparison.Mcn,
                        template.Code,
                        template.Id,
                        template.Layer,
                        template.Width,
                        startStation,
                        endStation);

                    _mapper.BindAxis(meta, axisInfo);
                    meta.CadLayer = segment.Layer;
                    meta.Extra["NodeKey"] = nodeKey;
                    meta.Extra["ApproachDirection"] = approachDirection;
                    meta.Extra["AnchorStopLineRecordId"] = stopMd.RecordId ?? string.Empty;
                    meta.Extra["StopLineStation"] = stopStation.ToString("0.########", CultureInfo.InvariantCulture);
                    meta.Extra["RequestedDistance"] = distance.ToString("0.########", CultureInfo.InvariantCulture);
                    meta.Extra["StartStation"] = startStation.ToString("0.########", CultureInfo.InvariantCulture);
                    meta.Extra["EndStation"] = endStation.ToString("0.########", CultureInfo.InvariantCulture);
                    meta.Extra["LaneBoundaryOffset"] = offset.ToString("0.########", CultureInfo.InvariantCulture);
                    meta.Extra["QuantityRole"] = "LANE_LINE";
                    meta.Extra["PaintRatio"] = template.PaintRatio.ToString("0.########", CultureInfo.InvariantCulture);

                    _metadata.Write(segment, tr, meta);

                    created++;
                    totalLength += segment.Length;
                    handles.Add(segment.ObjectId.Handle.ToString());
                }
            }

            return new
            {
                count = created,
                totalLength = totalLength,
                handles = handles,
                rejected = rejected
            };
        }

        private List<double> GetLaneBoundaryOffsets(ArmCrossSectionState crossSection, string side, double sign)
        {
            List<double> result = new List<double>();
            var lanes = crossSection.Components
                .Where(x => string.Equals(x.Role, "Lane", StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(x.Side, side, StringComparison.OrdinalIgnoreCase))
                .OrderBy(x => Math.Abs(x.Offset))
                .ToList();

            double accumulated = 0.0;
            for (int i = 0; i < lanes.Count - 1; i++)
            {
                accumulated += Math.Max(0.0, lanes[i].Width);
                result.Add(sign * accumulated);
            }
            return result;
        }

    }
}

