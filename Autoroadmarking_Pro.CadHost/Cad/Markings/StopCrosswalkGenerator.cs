using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using CivilAlignment = Autodesk.Civil.DatabaseServices.Alignment;
using Autoroadmarking_Pro.CadHost.Cad.Geometry;
using Autoroadmarking_Pro.CadHost.Cad.Layers;
using Autoroadmarking_Pro.CadHost.Cad.Metadata;
using Autoroadmarking_Pro.CadHost.Cad.State;
using Autoroadmarking_Pro.Application.Intersections;

namespace Autoroadmarking_Pro.CadHost.Cad.Markings
{
    /// <summary>
    /// Sinh cặp vạch 7.1 / 7.3 theo từng approach của polygon nút giao.
    ///
    /// Quy tắc đã khóa trong UI/project:
    /// - 7.3 là vạch đi bộ Mẫu 1 dạng ngựa vằn: bề rộng dải sơn và khoảng trống
    ///   được đọc trực tiếp từ template Tab 1; UI chỉ nhập chiều dài/phạm vi qua đường >= 3,0 m.
    /// - Bề rộng vùng 7.3 được chuẩn hóa theo cấp 1,0 m: 3 m, 4 m, 5 m...
    /// - TIM vùng 7.3 lấy trực tiếp từ giao TIM với cạnh polygon đã tạo ở Bước 2;
    ///   Step 4 không được tìm hoặc dịch sang một reference hình học khác.
    /// - Hai MÉP CAD thật chỉ dùng để giới hạn ngang hình học 7.3.
    /// - Khoảng cách 7.3 -> 7.1 được hiểu là khoảng cách TIM-ĐẾN-TIM.
    /// - 7.1 dùng TIM + MÉP CAD thật của hướng vào nút làm giới hạn và chỉ kẻ hết BỀ RỘNG HƯỚNG XE CHẠY
    ///   đi vào nút (giao thông bên phải), không kẻ xuyên cả hai chiều đường.
    /// - Candidate station ngoài miền RoadAxis bị reject, không clamp về endpoint.
    /// - Generation identity ổn định, đổi thông số sẽ UPDATE chứ không để geometry cũ.
    /// - Metadata của từng dải 7.3 dùng PaintRatio=1 vì geometry đã là phần sơn thực tế.
    /// </summary>
    public sealed class StopCrosswalkGenerator
    {
        private readonly CadGeometryService _geometry = new CadGeometryService();
        private readonly StationOffsetService _station = new StationOffsetService();
        private readonly MarkingLayerSynchronizer _layers = new MarkingLayerSynchronizer();
        private readonly EntityMetadataStore _metadata = new EntityMetadataStore();
        private List<(ObjectId Id, ArmEntityMetadata Meta, Curve? Curve)> _cachedEntities = new List<(ObjectId, ArmEntityMetadata, Curve?)>();

        public StopCrosswalkGenerationResult Generate(
            Database db,
            Transaction tr,
            ArmProjectState state,
            string stopTemplateId,
            string pedestrianTemplateId,
            double distance,
            double? crossingWidthOverride = null)
        {
            if (double.IsNaN(distance) || double.IsInfinity(distance) || distance <= 0.0)
                throw new InvalidOperationException("Khoảng cách tim vạch 7.3 đến tim vạch 7.1 phải lớn hơn 0 m.");

            ArmMarkingTemplateState? stopTemplate = FindTemplate(state, stopTemplateId, "7.1");
            ArmMarkingTemplateState? pedestrianTemplate = FindTemplate(state, pedestrianTemplateId, "7.3");

            if (stopTemplate == null || pedestrianTemplate == null)
                throw new InvalidOperationException("Thiếu template 7.1 hoặc 7.3 trong Tab 1.");

            BlockTable blockTable = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            BlockTableRecord modelSpace = (BlockTableRecord)tr.GetObject(
                blockTable[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

            _cachedEntities.Clear();
            foreach (ObjectId id in modelSpace)
            {
                if (!(tr.GetObject(id, OpenMode.ForRead, false) is Entity entity)) continue;
                ArmEntityMetadata? md = _metadata.Read(entity, tr);
                if (md != null) {
                    _cachedEntities.Add((id, md, entity as Curve));
                }
            }

            var result = new StopCrosswalkGenerationResult { RequestedDistance = distance };
            var cleanedNodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (ArmComparisonState comparison in state.ComparisonResults
                         .Where(x => x.Status.Equals("matched", StringComparison.OrdinalIgnoreCase) && x.Width > 0.0))
            {
                ObjectId axisId = _geometry.FromHandle(db, comparison.TimHandle);
                if (axisId.IsNull || !(tr.GetObject(axisId, OpenMode.ForRead, false) is Entity axis))
                    continue;

                Curve? proxy = _station.CreatePolylineProxy(axis);
                if (proxy == null)
                    continue;

                try
                {
                    foreach (string polygonHandle in state.IntersectionPolygonHandles)
                    {
                        ObjectId polygonId = _geometry.FromHandle(db, polygonHandle);
                        if (polygonId.IsNull ||
                            !(tr.GetObject(polygonId, OpenMode.ForRead, false) is Polyline polygon) ||
                            !polygon.Closed)
                        {
                            continue;
                        }

                        string nodeKey = ResolveNodeKey(polygon, tr, polygonHandle);
                        List<double> boundaryStations = FindBoundaryStations(proxy, polygon, axis);
                        if (boundaryStations.Count == 0)
                            continue;

                        string cleanupKey = comparison.RoadKey + "|" + comparison.TimHandle + "|" + nodeKey;
                        if (cleanedNodes.Add(cleanupKey))
                            EraseExistingNodeMarkings(db, tr, comparison, nodeKey, polygonHandle);

                        Point3d centroid = PolygonCentroid(polygon);

                        foreach (double boundaryStation in boundaryStations)
                        {
                            // boundaryStation chỉ dùng để nhận approach. Mốc đặt 7.3 thực tế được
                            // dịch tới đúng điểm kết thúc sừng bò (đầu tiếp tuyến phía ngoài nút).
                            Point3d boundaryPoint = _station.PointAtStationOffset(
                                axis, boundaryStation, 0.0, out Vector3d tangent);

                            Vector3d fromNode = boundaryPoint - centroid;
                            int outwardSign = fromNode.DotProduct(tangent) >= 0.0 ? 1 : -1;

                            // Nếu biên nằm phía +station, xe đi vào nút theo chiều REVERSE;
                            // nếu biên nằm phía -station, xe đi vào nút theo chiều FORWARD.
                            string approachDirection = outwardSign > 0 ? "REVERSE" : "FORWARD";

                            double crossingLength = ResolveCrossingWidth(pedestrianTemplate, crossingWidthOverride);

                            // Contract Bước 4:
                            // - giao TIM × cạnh polygon Bước 2 là reference chính thức và cũng là TIM 7.3;
                            // - không dùng tangency/nearest/centroid heuristic để dịch reference;
                            // - 7.3 trải đối xứng quanh mốc này;
                            // - TIM 7.1 cách TIM 7.3 đúng distance theo outwardSign.
                            const string anchorSource = "STEP2_POLYGON_AXIS_INTERSECTION";
                            double crosswalkAnchorStation = boundaryStation;

                            CrosswalkStopStationPlan stationPlan =
                                new MarkingPlacementPlanner().PlanStopCrosswalk(
                                    crosswalkAnchorStation,
                                    outwardSign,
                                    crossingLength,
                                    distance,
                                    _station.StartStation(axis),
                                    _station.EndStation(axis));

                            double crosswalkCenterStation = crosswalkAnchorStation;
                            double crosswalkStartStation = stationPlan.CrosswalkStartStation;
                            double crosswalkOuterEdgeStation = stationPlan.CrosswalkEndStation;
                            double stopStation = stationPlan.StopStation;

                            if (!stationPlan.IsValid)
                            {
                                result.Rejected.Add(new StopCrosswalkRejectedPlacement
                                {
                                    RoadKey = comparison.RoadKey,
                                    RoadName = comparison.Road,
                                    NodeId = nodeKey,
                                    ApproachDirection = approachDirection,
                                    CrosswalkStation = crosswalkCenterStation,
                                    StopStation = stopStation,
                                    Reason = stationPlan.Error
                                });
                                continue;
                            }

                            int zebraCreated = CreateCrosswalkZebra(
                                db, tr, modelSpace, state, polygon, axis, comparison, nodeKey, polygonHandle,
                                approachDirection, pedestrianTemplate, crosswalkStartStation, outwardSign,
                                crossingWidthOverride, anchorSource, out string zebraError);

                            if (zebraCreated <= 0)
                            {
                                result.Rejected.Add(new StopCrosswalkRejectedPlacement
                                {
                                    RoadKey = comparison.RoadKey,
                                    RoadName = comparison.Road,
                                    NodeId = nodeKey,
                                    ApproachDirection = approachDirection,
                                    CrosswalkStation = crosswalkCenterStation,
                                    StopStation = stopStation,
                                    Reason = string.IsNullOrWhiteSpace(zebraError)
                                        ? "Không sinh được hình học vạch 7.3."
                                        : zebraError
                                });
                                continue;
                            }

                            int stopCreated = CreateTransverse(
                                db, tr, modelSpace, axis, comparison, nodeKey, polygonHandle,
                                approachDirection, stopTemplate, stopStation);

                            if (stopCreated <= 0)
                            {
                                // Một approach phải là một cặp hoàn chỉnh 7.3 + 7.1. Nếu 7.1 thất bại,
                                // xóa luôn zebra vừa tạo để không để lại kết quả nửa vời trên mặt bằng.
                                EraseByGenerationPrefix(
                                    db,
                                    tr,
                                    BuildGenerationKey(comparison, nodeKey, approachDirection, pedestrianTemplate.Code));

                                result.Rejected.Add(new StopCrosswalkRejectedPlacement
                                {
                                    RoadKey = comparison.RoadKey,
                                    RoadName = comparison.Road,
                                    NodeId = nodeKey,
                                    ApproachDirection = approachDirection,
                                    CrosswalkStation = crosswalkCenterStation,
                                    StopStation = stopStation,
                                    Reason = "Không xác định được giới hạn vạch mép của hướng đi vào nút để sinh vạch 7.1."
                                });
                                continue;
                            }

                            result.CreatedCount += zebraCreated + stopCreated;
                            result.ApproachPairCount++;
                        }
                    }
                }
                finally
                {
                    proxy.Dispose();
                }
            }

            return result;
        }

        private static ArmMarkingTemplateState? FindTemplate(
            ArmProjectState state,
            string templateId,
            string fallbackCode)
        {
            return state.MarkingTemplates.FirstOrDefault(x =>
                       x.Id.Equals(templateId ?? string.Empty, StringComparison.OrdinalIgnoreCase))
                   ?? state.MarkingTemplates.FirstOrDefault(x =>
                       x.Code.Equals(fallbackCode, StringComparison.OrdinalIgnoreCase));
        }

        private string ResolveNodeKey(Polyline polygon, Transaction tr, string polygonHandle)
        {
            ArmEntityMetadata? md = _metadata.Read(polygon, tr);
            if (md != null)
            {
                if (md.Extra != null &&
                    md.Extra.TryGetValue("NodeKey", out string? extraNodeKey) &&
                    !string.IsNullOrWhiteSpace(extraNodeKey))
                    return extraNodeKey.Trim();

                if (!string.IsNullOrWhiteSpace(md.OwnerId) &&
                    md.OwnerId.StartsWith("NODE|", StringComparison.OrdinalIgnoreCase))
                    return md.OwnerId.Trim();
            }
            return polygonHandle;
        }

        private List<double> FindBoundaryStations(Curve axisProxy, Polyline polygon, Entity axis)
        {
            var intersectionPoints = new Point3dCollection();
            try
            {
                axisProxy.IntersectWith(
                    polygon, Intersect.OnBothOperands, intersectionPoints, IntPtr.Zero, IntPtr.Zero);
            }
            catch
            {
                // Không dừng toàn bộ approach. Với polygon có bulge/vertex trùng tiếp tuyến,
                // IntersectWith đôi khi thất bại; phần fallback theo cạnh polygon bên dưới
                // vẫn có thể khôi phục chính xác station cổ nút.
            }

            var stations = new List<double>();
            foreach (Point3d point in intersectionPoints)
            {
                if (_station.TryStationOffset(axis, point, out double station, out _))
                    stations.Add(station);
            }

            // Fallback hình học cho IntersectWith bị hụt tại vertex/bulge: các cạnh "cổ nút"
            // nhân tạo của polygon thường nối từ một phía TIM sang phía còn lại. Chiếu hai đầu
            // cạnh về station/offset và nội suy vị trí offset=0 để phục hồi boundary station.
            int vertexCount = polygon.NumberOfVertices;
            for (int i = 0; i < vertexCount; i++)
            {
                int j = (i + 1) % vertexCount;
                try
                {
                    Point3d a = polygon.GetPoint3dAt(i);
                    Point3d b = polygon.GetPoint3dAt(j);
                    if (!_station.TryStationOffset(axis, a, out double sa, out double oa) ||
                        !_station.TryStationOffset(axis, b, out double sb, out double ob))
                        continue;

                    if (Math.Abs(oa) <= 0.05)
                        stations.Add(sa);
                    if (Math.Abs(ob) <= 0.05)
                        stations.Add(sb);

                    if (oa * ob < 0.0)
                    {
                        double denom = Math.Abs(oa) + Math.Abs(ob);
                        if (denom > 1e-9)
                        {
                            double t = Math.Abs(oa) / denom;
                            double interpolated = sa + (sb - sa) * t;
                            if (IsStationInside(axis, interpolated))
                                stations.Add(interpolated);
                        }
                    }
                }
                catch { }
            }

            stations.Sort();
            var distinct = new List<double>();
            foreach (double station in stations)
            {
                if (distinct.Count == 0 || Math.Abs(distinct[distinct.Count - 1] - station) > 0.05)
                    distinct.Add(station);
            }
            return distinct;
        }

        private int CreateCrosswalkZebra(
            Database db,
            Transaction tr,
            BlockTableRecord modelSpace,
            ArmProjectState state,
            Polyline polygon,
            Entity axis,
            ArmComparisonState comparison,
            string ownerId,
            string polygonHandle,
            string approachDirection,
            ArmMarkingTemplateState template,
            double crosswalkStartStation,
            int outwardSign,
            double? crossingWidthOverride,
            string anchorSource,
            out string error)
        {
            error = string.Empty;

            // 7.3: thư viện Tab 1 là nguồn dữ liệu kỹ thuật duy nhất cho hình học dải sơn.
            // - Width của template = bề rộng mỗi dải sơn (m), ví dụ 0,40 m.
            // - stripeGap_m / CustomGap1 = khoảng trống giữa các dải, ví dụ 0,60 m.
            // - UI chỉ nhập crossingLength (3/4/5...m); không override Width/Gap của template.
            double templateStripeWidth = template.Width > 0.0
                ? template.Width
                : (template.CustomDash1 > 0.0 ? template.CustomDash1 : 0.40);
            double stripeWidth = ReadPositiveCustom(template, "stripeWidth_m", templateStripeWidth);

            double templateStripeGap = template.CustomGap1 > 0.0
                ? template.CustomGap1
                : (template.GapLength > 0.0 ? template.GapLength : 0.60);
            double stripeGap = ReadNonNegativeCustom(template, "stripeGap_m", templateStripeGap);
            double crossingLength = ResolveCrossingWidth(template, crossingWidthOverride);

            if (crossingLength < 3.0)
            {
                error = "Chiều dài vạch 7.3 phải từ 3,0 m trở lên.";
                return 0;
            }

            double innerStation = crosswalkStartStation;
            double outerStation = crosswalkStartStation + outwardSign * crossingLength;
            if (!IsStationInside(axis, innerStation) || !IsStationInside(axis, outerStation))
            {
                error = "Toàn bộ chiều dài 3/4/5... m của vạch 7.3 phải nằm trong miền station của RoadAxis.";
                return 0;
            }

            // 7.3 dùng MÉP CAD thật làm giới hạn. Lấy giao của hai MÉP với các mặt cắt
            // tại 5 station trong toàn chiều dài dải rồi lấy miền offset chung; nhờ vậy
            // mọi dải có ConstantWidth vẫn nằm trọn giữa hai mép ngay cả khi đường cong/hội tụ.
            if (!TryResolveCommonEdgeEnvelope(
                    db, tr, state, polygon, axis, comparison, innerStation, outerStation,
                    out double leftLimit, out double rightLimit, out string envelopeError))
            {
                error = envelopeError;
                return 0;
            }

            double roadWidth = rightLimit - leftLimit;
            if (roadWidth < stripeWidth)
            {
                error = "Khoảng giữa hai MÉP CAD nhỏ hơn bề rộng một dải 7.3.";
                return 0;
            }

            double pitch = stripeWidth + stripeGap;
            int stripeCount = stripeGap <= 1e-9
                ? Math.Max(1, (int)Math.Floor(roadWidth / stripeWidth))
                : Math.Max(1, (int)Math.Floor((roadWidth + stripeGap) / pitch));

            double occupied = stripeCount * stripeWidth + Math.Max(0, stripeCount - 1) * stripeGap;
            while (stripeCount > 1 && occupied > roadWidth + 1e-9)
            {
                stripeCount--;
                occupied = stripeCount * stripeWidth + Math.Max(0, stripeCount - 1) * stripeGap;
            }

            double sideMargin = Math.Max(0.0, (roadWidth - occupied) * 0.5);
            double firstLateralOffset = leftLimit + sideMargin + stripeWidth * 0.5;

            string generationPrefix = BuildGenerationKey(
                comparison, ownerId, approachDirection, template.Code);
            EraseByGenerationPrefix(db, tr, generationPrefix);

            ObjectId layerId = _layers.EnsureGeneratedLayer(db, tr, comparison.Road, template);
            int created = 0;

            for (int i = 0; i < stripeCount; i++)
            {
                double lateralOffset = firstLateralOffset + i * pitch;

                // IMPORTANT: both endpoints use the SAME lateral offset.
                // Therefore the painted bar follows the longitudinal direction of the approach,
                // while bars themselves are arrayed transversely across the carriageway.
                Point3d inner = _station.PointAtStationOffset(
                    axis, innerStation, lateralOffset, out Vector3d tangentInner);
                Point3d outer = _station.PointAtStationOffset(
                    axis, outerStation, lateralOffset, out Vector3d tangentOuter);

                if (inner.DistanceTo(outer) <= 1e-6)
                    continue;

                string generationKey = generationPrefix + "|STRIPE|" + (i + 1).ToString(CultureInfo.InvariantCulture);

                var polyline = new Polyline();
                polyline.AddVertexAt(0, new Point2d(inner.X, inner.Y), 0.0, stripeWidth, stripeWidth);
                polyline.AddVertexAt(1, new Point2d(outer.X, outer.Y), 0.0, stripeWidth, stripeWidth);
                polyline.ConstantWidth = stripeWidth;
                polyline.Plinegen = true;

                modelSpace.AppendEntity(polyline);
                tr.AddNewlyCreatedDBObject(polyline, true);
                polyline.LayerId = layerId;
                polyline.LinetypeId = db.ContinuousLinetype;
                polyline.LinetypeScale = 1.0;

                var metadata = new ArmEntityMetadata
                {
                    RecordId = "MRK_" + Guid.NewGuid().ToString("N"),
                    GenerationKey = generationKey,
                    Source = "AUTO_INTERSECTION",
                    RoadName = comparison.Road,
                    AxisKey = comparison.RoadKey,
                    AxisHandle = comparison.TimHandle,
                    AxisType = axis is CivilAlignment ? "ALIGNMENT" : "POLYLINE",
                    RoadKey = comparison.RoadKey,
                    OwnerType = "INTERSECTION",
                    OwnerId = ownerId,
                    McnId = comparison.Mcn,
                    MarkingCode = template.Code,
                    TemplateId = template.Id,
                    TemplateLayer = template.Layer,
                    CadLayer = polyline.Layer,
                    Station = innerStation + outwardSign * crossingLength * 0.5,
                    Width = stripeWidth,
                    Extra = new Dictionary<string, string>
                    {
                        ["PaintRatio"] = "1",
                        ["Pattern"] = "CONTINUOUS_GEOMETRY",
                        ["ApproachDirection"] = approachDirection,
                        ["GeometryType"] = "PEDESTRIAN_CROSSWALK_ZEBRA_LONGITUDINAL_BARS",
                        ["CrosswalkModel"] = "1",
                        ["CrossingWidth"] = crossingLength.ToString("0.########", CultureInfo.InvariantCulture),
                        ["CrossingLength"] = crossingLength.ToString("0.########", CultureInfo.InvariantCulture),
                        ["CrosswalkInnerBoundaryStation"] = innerStation.ToString("0.########", CultureInfo.InvariantCulture),
                        ["CrosswalkOuterBoundaryStation"] = outerStation.ToString("0.########", CultureInfo.InvariantCulture),
                        ["CrosswalkAnchorSource"] = anchorSource ?? string.Empty,
                        ["LateralLimitSource"] = "SELECTED_ROAD_EDGES",
                        ["LeftEdgeHandle"] = comparison.LeftEdgeHandle ?? string.Empty,
                        ["RightEdgeHandle"] = comparison.RightEdgeHandle ?? string.Empty,
                        ["PlacementSide"] = "OUTSIDE_INTERSECTION",
                        ["StripeWidth"] = stripeWidth.ToString("0.########", CultureInfo.InvariantCulture),
                        ["StripeGap"] = stripeGap.ToString("0.########", CultureInfo.InvariantCulture),
                        ["StripeIndex"] = (i + 1).ToString(CultureInfo.InvariantCulture),
                        ["StripeCount"] = stripeCount.ToString(CultureInfo.InvariantCulture),
                        ["StripeLateralOffset"] = lateralOffset.ToString("0.########", CultureInfo.InvariantCulture),
                        ["QuantityCategory"] = "INTERSECTION_CROSSWALK",
                        ["QuantityGroup"] = "VACH_DI_BO",
                        ["QuantityRole"] = "CROSSWALK_7_3",
                        ["QuantityScope"] = "INTERSECTION",
                        ["NodeKey"] = ownerId,
                        ["PolygonHandle"] = polygonHandle
                    }
                };

                _metadata.Write(polyline, tr, metadata);
                created++;
            }

            if (created > 0) error = string.Empty;
            return created;
        }

        private int CreateTransverse(
            Database db,
            Transaction tr,
            BlockTableRecord modelSpace,
            Entity axis,
            ArmComparisonState comparison,
            string ownerId,
            string polygonHandle,
            string approachDirection,
            ArmMarkingTemplateState template,
            double station)
        {
            if (!IsStationInside(axis, station))
                return 0;

            string generationKey = BuildGenerationKey(
                comparison, ownerId, approachDirection, template.Code);

            EraseByGeneration(db, tr, generationKey);

            // 7.1 phải dừng tại BIÊN TRONG của vạch mép sơn thực tế, không phải tại
            // đường MÉP hình học gốc. Ưu tiên AUTO_LONGITUDINAL ROAD_EDGE / AUTO_EDGE;
            // chỉ fallback về MÉP CAD gốc khi chưa có vạch mép sơn tại station này.
            string targetEdgeHandle = approachDirection.Equals("FORWARD", StringComparison.OrdinalIgnoreCase)
                ? comparison.RightEdgeHandle
                : comparison.LeftEdgeHandle;

            if (!TryResolveStopBoundary(
                    db,
                    tr,
                    axis,
                    comparison,
                    ownerId,
                    targetEdgeHandle,
                    station,
                    out double edgeOffset,
                    out string stopBoundarySource,
                    out string stopBoundaryHandle))
                return 0;

            Point3d fromPoint = _station.PointAtStationOffset(
                axis, station, 0.0, out Vector3d tangent);
            Point3d toPoint = _station.PointAtStationOffset(
                axis, station, edgeOffset, out _);
            double fromOffset = 0.0;
            double toOffset = edgeOffset;

            // Bề rộng 7.1 luôn lấy từ template Tab 1; không có override từ UI.
            double width = template.Width;
            if (!(width > 0.0) || double.IsNaN(width) || double.IsInfinity(width))
                return 0;
            var polyline = new Polyline();
            polyline.AddVertexAt(0, new Point2d(fromPoint.X, fromPoint.Y), 0.0, width, width);
            polyline.AddVertexAt(1, new Point2d(toPoint.X, toPoint.Y), 0.0, width, width);
            polyline.ConstantWidth = width;
            polyline.Plinegen = true;
            polyline.LayerId = _layers.EnsureGeneratedLayer(db, tr, comparison.Road, template);
            polyline.LinetypeScale = template.LinetypeScale > 0.0 ? template.LinetypeScale : 1.0;

            modelSpace.AppendEntity(polyline);
            tr.AddNewlyCreatedDBObject(polyline, true);

            var metadata = new ArmEntityMetadata
            {
                RecordId = "MRK_" + Guid.NewGuid().ToString("N"),
                GenerationKey = generationKey,
                Source = "AUTO_INTERSECTION",
                RoadName = comparison.Road,
                AxisKey = comparison.RoadKey,
                AxisHandle = comparison.TimHandle,
                AxisType = axis is CivilAlignment ? "ALIGNMENT" : "POLYLINE",
                RoadKey = comparison.RoadKey,
                OwnerType = "INTERSECTION",
                OwnerId = ownerId,
                McnId = comparison.Mcn,
                MarkingCode = template.Code,
                TemplateId = template.Id,
                TemplateLayer = template.Layer,
                CadLayer = polyline.Layer,
                Station = station,
                Width = width,
                Extra = new Dictionary<string, string>
                {
                    ["PaintRatio"] = template.PaintRatio.ToString("0.########", CultureInfo.InvariantCulture),
                    ["Pattern"] = template.Pattern ?? string.Empty,
                    ["ApproachDirection"] = approachDirection,
                    ["TrafficRule"] = "RIGHT_HAND_TRAFFIC",
                    ["StopLineScope"] = "INBOUND_CARRIAGEWAY_ONLY",
                    ["StopFromOffset"] = fromOffset.ToString("0.########", CultureInfo.InvariantCulture),
                    ["StopToOffset"] = toOffset.ToString("0.########", CultureInfo.InvariantCulture),
                    ["StopBoundarySource"] = stopBoundarySource,
                    ["StopEdgeHandle"] = stopBoundaryHandle,
                    ["RawSelectedEdgeHandle"] = targetEdgeHandle ?? string.Empty,
                    ["WidthSource"] = "TAB1_TEMPLATE",
                    ["QuantityCategory"] = "INTERSECTION_STOP",
                    ["QuantityGroup"] = "VACH_DUNG",
                    ["QuantityRole"] = "STOP_LINE_7_1",
                    ["QuantityScope"] = "INTERSECTION",
                    ["NodeKey"] = ownerId,
                    ["PolygonHandle"] = polygonHandle
                }
            };

            _metadata.Write(polyline, tr, metadata);
            return 1;
        }



        private sealed class BoundaryRunInfo
        {
            public int SegmentIndex { get; set; }
            public string Role { get; set; } = string.Empty;
            public string SourceHandle { get; set; } = string.Empty;
            public int SourceSegmentIndex { get; set; } = -1;
        }

        private static Point3d ToXy(Point3d point)
        {
            return new Point3d(point.X, point.Y, 0.0);
        }

        private static Vector3d ToXyNormal(Vector3d vector)
        {
            Vector3d xy = new Vector3d(vector.X, vector.Y, 0.0);
            return xy.Length <= 1e-9 ? new Vector3d(0.0, 0.0, 0.0) : xy.GetNormal();
        }

        private List<BoundaryRunInfo> ReadBoundaryRuns(Polyline polygon, Transaction tr)
        {
            var result = new List<BoundaryRunInfo>();
            ArmEntityMetadata? md = _metadata.Read(polygon, tr);
            if (md?.Extra == null)
                return result;

            // V3: polygonSegment|role|sourceHandle|sourceSegment.
            // sourceSegment là index segment trên chính Polyline MÉP CAD gốc.
            if (md.Extra.TryGetValue("BoundaryRunsV3", out string? rawV3) &&
                !string.IsNullOrWhiteSpace(rawV3))
            {
                foreach (string record in rawV3.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string[] parts = record.Split('|');
                    if (parts.Length < 4 ||
                        !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int segmentIndex))
                        continue;

                    int sourceSegmentIndex = -1;
                    int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out sourceSegmentIndex);
                    result.Add(new BoundaryRunInfo
                    {
                        SegmentIndex = segmentIndex,
                        Role = parts[1]?.Trim() ?? string.Empty,
                        SourceHandle = parts[2]?.Trim() ?? string.Empty,
                        SourceSegmentIndex = sourceSegmentIndex
                    });
                }

                if (result.Count > 0)
                    return result;
            }

            // Tương thích polygon V2.
            if (!md.Extra.TryGetValue("BoundaryRunsV2", out string? raw) ||
                string.IsNullOrWhiteSpace(raw))
                return result;

            foreach (string record in raw.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string[] parts = record.Split('|');
                if (parts.Length < 3 ||
                    !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int segmentIndex))
                    continue;

                result.Add(new BoundaryRunInfo
                {
                    SegmentIndex = segmentIndex,
                    Role = parts[1]?.Trim() ?? string.Empty,
                    SourceHandle = parts[2]?.Trim() ?? string.Empty,
                    SourceSegmentIndex = -1
                });
            }
            return result;
        }

        /// <summary>
        /// Lấy miền offset chung giữa hai MÉP CAD trong toàn chiều dài 7.3.
        /// Không dùng LeftWidth/RightWidth làm endpoint hình học.
        /// </summary>
        private sealed class EdgeStationHit
        {
            public string Handle { get; set; } = string.Empty;
            public double Offset { get; set; }
            public Point3d Point { get; set; }
        }

        /// <summary>
        /// Xác định miền ngang chung của 7.3 bằng MÉP CAD thật.
        ///
        /// Điểm khác biệt so với thuật toán cũ:
        /// - Không buộc một LeftEdgeHandle/RightEdgeHandle duy nhất phải sống xuyên suốt 3/4/5 m.
        ///   Tại nút T/ngã tư, một MÉP có thể bị chia thành LINE + ARC sừng bò + LINE khác handle.
        /// - Mỗi station ưu tiên cặp MÉP đã match; nếu một handle kết thúc tại tiếp tuyến thì tự tìm
        ///   cặp thay thế trong EdgeHandles của polygon và SelectedEdgeHandles của phiên hiện tại.
        /// - Không sample đúng endpoint 0/1 để tránh lỗi IntersectWith tại điểm nối/tangent.
        /// - Chọn cặp hai phía TIM có bề rộng gần nhất với MCN đã match, tránh bắt nhầm mép đường khác.
        /// </summary>
        private bool TryResolveCommonEdgeEnvelope(
            Database db,
            Transaction tr,
            ArmProjectState state,
            Polyline polygon,
            Entity axis,
            ArmComparisonState comparison,
            double innerStation,
            double outerStation,
            out double leftLimit,
            out double rightLimit,
            out string error)
        {
            leftLimit = double.NegativeInfinity;
            rightLimit = double.PositiveInfinity;
            error = string.Empty;

            List<string> candidateHandles = BuildCrosswalkEdgeCandidates(state, polygon, tr, comparison);
            if (candidateHandles.Count < 2)
            {
                error = "7.3 không có đủ hai MÉP CAD hợp lệ để xác định giới hạn ngang.";
                return false;
            }

            // Không lấy đúng 0 và 1: điểm đầu 7.3 thường đúng endpoint sừng bò, nơi AutoCAD
            // IntersectWith rất nhạy tolerance. Các mẫu nội bộ vẫn khóa hình học toàn dải.
            double[] fractions = { 0.03, 0.17, 0.33, 0.50, 0.67, 0.83, 0.97 };
            int validSamples = 0;
            var failures = new List<string>();

            foreach (double f in fractions)
            {
                double station = innerStation + (outerStation - innerStation) * f;
                if (!TryResolveRoadEdgesAtStation(
                        db,
                        tr,
                        axis,
                        comparison,
                        candidateHandles,
                        station,
                        out double a,
                        out double b,
                        out string pairSource))
                {
                    failures.Add(station.ToString("0.00", CultureInfo.InvariantCulture));
                    continue;
                }

                double localLeft = Math.Min(a, b);
                double localRight = Math.Max(a, b);
                if (localRight - localLeft <= 0.50)
                {
                    failures.Add(station.ToString("0.00", CultureInfo.InvariantCulture));
                    continue;
                }

                leftLimit = Math.Max(leftLimit, localLeft);
                rightLimit = Math.Min(rightLimit, localRight);
                validSamples++;
            }

            // Một lỗi giao cắt đơn lẻ không được làm mất cả approach. Nhưng phải có đủ dữ liệu
            // trên phần lớn chiều dài 7.3 để không sinh hình học suy đoán.
            if (validSamples < 5)
            {
                error = "7.3 chỉ xác định được " + validSamples.ToString(CultureInfo.InvariantCulture) +
                        "/7 mặt cắt MÉP CAD. Station lỗi: " +
                        (failures.Count > 0 ? string.Join(", ", failures) : "không xác định") + ".";
                return false;
            }

            if (double.IsInfinity(leftLimit) || double.IsInfinity(rightLimit) || rightLimit - leftLimit <= 0.50)
            {
                error = "Không tìm được miền chung ổn định giữa hai MÉP CAD cho toàn bộ chiều dài 7.3.";
                return false;
            }

            return true;
        }

        private List<string> BuildCrosswalkEdgeCandidates(
            ArmProjectState state,
            Polyline polygon,
            Transaction tr,
            ArmComparisonState comparison)
        {
            var handles = new List<string>();

            void Add(string? handle)
            {
                if (!string.IsNullOrWhiteSpace(handle))
                    handles.Add(handle!.Trim());
            }

            // Ưu tiên đúng cặp đã match với TIM.
            Add(comparison.LeftEdgeHandle);
            Add(comparison.RightEdgeHandle);

            // Sau đó là các MÉP thực đã tạo polygon: quan trọng khi LINE/ARC bị tách handle.
            foreach (BoundaryRunInfo run in ReadBoundaryRuns(polygon, tr))
                Add(run.SourceHandle);

            // Cuối cùng dùng toàn bộ MÉP người dùng đã chọn làm fallback hình học.
            foreach (string handle in state.SelectedEdgeHandles ?? new List<string>())
                Add(handle);

            return handles
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private bool TryResolveRoadEdgesAtStation(
            Database db,
            Transaction tr,
            Entity axis,
            ArmComparisonState comparison,
            List<string> candidateHandles,
            double station,
            out double firstOffset,
            out double secondOffset,
            out string source)
        {
            firstOffset = 0.0;
            secondOffset = 0.0;
            source = string.Empty;

            // Fast path: cặp match gốc vẫn là dữ liệu đáng tin nhất khi cả hai cùng tồn tại.
            if (!string.IsNullOrWhiteSpace(comparison.LeftEdgeHandle) &&
                !string.IsNullOrWhiteSpace(comparison.RightEdgeHandle) &&
                TryFindEdgeHitAtStation(db, tr, axis, comparison.LeftEdgeHandle, station, out double primaryA, out _) &&
                TryFindEdgeHitAtStation(db, tr, axis, comparison.RightEdgeHandle, station, out double primaryB, out _) &&
                Math.Min(primaryA, primaryB) < -0.05 &&
                Math.Max(primaryA, primaryB) > 0.05)
            {
                double width = Math.Abs(primaryB - primaryA);
                if (width >= 1.0 && width <= 80.0)
                {
                    firstOffset = primaryA;
                    secondOffset = primaryB;
                    source = "MATCHED_EDGE_PAIR";
                    return true;
                }
            }

            var hits = new List<EdgeStationHit>();
            foreach (string handle in candidateHandles)
            {
                if (!TryFindEdgeHitAtStation(db, tr, axis, handle, station, out double offset, out Point3d point))
                    continue;

                if (Math.Abs(offset) <= 0.05 || Math.Abs(offset) > 80.0)
                    continue;

                hits.Add(new EdgeStationHit { Handle = handle, Offset = offset, Point = point });
            }

            // Cùng một entity có thể trả nhiều kết quả qua fallback; giữ một hit/handle.
            hits = hits
                .GroupBy(x => x.Handle, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.OrderBy(x => Math.Abs(x.Offset)).First())
                .ToList();

            if (hits.Count < 2)
                return false;

            double targetWidth = comparison.Width > 0.5
                ? comparison.Width
                : Math.Max(0.0, comparison.LeftWidth) + Math.Max(0.0, comparison.RightWidth);

            EdgeStationHit? bestA = null;
            EdgeStationHit? bestB = null;
            double bestScore = double.MaxValue;

            for (int i = 0; i < hits.Count; i++)
            {
                for (int j = i + 1; j < hits.Count; j++)
                {
                    EdgeStationHit a = hits[i];
                    EdgeStationHit b = hits[j];
                    double min = Math.Min(a.Offset, b.Offset);
                    double max = Math.Max(a.Offset, b.Offset);

                    // Hai MÉP ngoài phải kẹp TIM ở giữa.
                    if (!(min < -0.05 && max > 0.05))
                        continue;

                    double width = max - min;
                    if (width < 1.0 || width > 80.0)
                        continue;

                    double centerBias = Math.Abs((min + max) * 0.5);
                    double widthError = targetWidth > 0.5 ? Math.Abs(width - targetWidth) : 0.0;
                    double score = widthError * 10.0 + centerBias;

                    if (score < bestScore)
                    {
                        bestScore = score;
                        bestA = a;
                        bestB = b;
                    }
                }
            }

            if (bestA == null || bestB == null)
                return false;

            firstOffset = bestA.Offset;
            secondOffset = bestB.Offset;
            source = "DYNAMIC_SELECTED_EDGE_PAIR";
            return true;
        }

        /// <summary>
        /// Tìm giới hạn ngoài của 7.1. Ưu tiên vạch mép SƠN đã sinh (ROAD_EDGE/AUTO_EDGE)
        /// để 7.1 dừng tại biên trong của nét sơn, không chạy xuyên qua vạch mép tới MÉP CAD gốc.
        /// </summary>
        private bool TryResolveStopBoundary(
            Database db,
            Transaction tr,
            Entity axis,
            ArmComparisonState comparison,
            string ownerId,
            string rawEdgeHandle,
            double station,
            out double boundaryOffset,
            out string source,
            out string sourceHandle)
        {
            boundaryOffset = 0.0;
            source = string.Empty;
            sourceHandle = string.Empty;

            bool hasRaw = TryFindEdgeHitAtStation(
                db, tr, axis, rawEdgeHandle, station, out double rawOffset, out _);
            int expectedSign = hasRaw
                ? Math.Sign(rawOffset)
                : TryInferEdgeSideSign(db, tr, axis, rawEdgeHandle, station);

            // Nếu chính MÉP đích bị phân đoạn và không suy được dấu, dùng MÉP đối diện
            // để suy ra dấu ngược lại. Cách này không phụ thuộc quy ước offset trái/phải của Alignment.
            if (expectedSign == 0)
            {
                bool targetIsRight = string.Equals(
                    rawEdgeHandle,
                    comparison.RightEdgeHandle,
                    StringComparison.OrdinalIgnoreCase);
                string otherHandle = targetIsRight
                    ? comparison.LeftEdgeHandle
                    : comparison.RightEdgeHandle;

                if (!string.IsNullOrWhiteSpace(otherHandle) &&
                    TryFindEdgeHitAtStation(db, tr, axis, otherHandle, station, out double otherOffset, out _) &&
                    Math.Abs(otherOffset) > 0.05)
                {
                    expectedSign = -Math.Sign(otherOffset);
                }
            }

            double bestScore = double.MaxValue;
            bool foundPaintedEdge = false;
            double bestOffset = 0.0;
            string bestHandle = string.Empty;
            string bestSource = string.Empty;

            foreach (var item in _cachedEntities)
            {
                if (item.Curve == null || item.Curve.IsErased) continue;
                ArmEntityMetadata md = item.Meta;
                Curve curve = item.Curve;

                bool isLongitudinal = string.Equals(md.Source, "AUTO_LONGITUDINAL", StringComparison.OrdinalIgnoreCase);
                bool isNodeEdge = string.Equals(md.Source, "AUTO_EDGE", StringComparison.OrdinalIgnoreCase);
                if (!isLongitudinal && !isNodeEdge)
                    continue;

                bool sameAxis =
                    (!string.IsNullOrWhiteSpace(comparison.TimHandle) &&
                     string.Equals(md.AxisHandle, comparison.TimHandle, StringComparison.OrdinalIgnoreCase)) ||
                    (!string.IsNullOrWhiteSpace(comparison.RoadKey) &&
                     (string.Equals(md.AxisKey, comparison.RoadKey, StringComparison.OrdinalIgnoreCase) ||
                      string.Equals(md.RoadKey, comparison.RoadKey, StringComparison.OrdinalIgnoreCase)));
                if (!sameAxis)
                    continue;

                string quantityRole = string.Empty;
                if (md.Extra != null &&
                    md.Extra.TryGetValue("QuantityRole", out string? roleValue) &&
                    !string.IsNullOrWhiteSpace(roleValue))
                {
                    quantityRole = roleValue;
                }

                // Vạch dọc chỉ nhận đúng ROAD_EDGE; AUTO_EDGE đã là vạch mép nút theo định nghĩa.
                if (isLongitudinal && !string.Equals(quantityRole, "ROAD_EDGE", StringComparison.OrdinalIgnoreCase))
                    continue;

                // AUTO_EDGE của nút khác không được dùng làm giới hạn 7.1.
                if (isNodeEdge &&
                    !string.Equals(md.OwnerId, ownerId, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!TryFindCurveHitAtStation(axis, curve, station, out double paintedOffset, out _))
                    continue;

                int sign = Math.Sign(paintedOffset);
                if (sign == 0 || (expectedSign != 0 && sign != expectedSign))
                    continue;

                // Vạch mép sơn phải nằm ở giữa TIM và MÉP CAD gốc. Cho tolerance nhỏ vì
                // một số bản vẽ có sai số offset/fit polyline.
                if (hasRaw && Math.Abs(paintedOffset) > Math.Abs(rawOffset) + 0.35)
                    continue;

                double markingWidth = ResolvePhysicalCurveWidth(curve, md);
                double insideOffset = paintedOffset - sign * markingWidth * 0.5;
                if (Math.Abs(insideOffset) <= 0.05)
                    continue;

                // Ưu tiên ROAD_EDGE dọc tuyến; sau đó AUTO_EDGE. Trong cùng loại, chọn nét
                // gần MÉP CAD gốc nhất để không nhầm với vạch làn phía trong.
                double sourcePenalty = isLongitudinal ? 0.0 : 5.0;
                double edgeDistance = hasRaw
                    ? Math.Abs(rawOffset - paintedOffset)
                    : -Math.Abs(paintedOffset);
                double score = sourcePenalty + edgeDistance;

                if (score < bestScore)
                {
                    bestScore = score;
                    bestOffset = insideOffset;
                    bestHandle = curve.Handle.ToString();
                    bestSource = isLongitudinal
                        ? "GENERATED_LONGITUDINAL_ROAD_EDGE_INNER_FACE"
                        : "GENERATED_INTERSECTION_EDGE_INNER_FACE";
                    foundPaintedEdge = true;
                }
            }

            if (foundPaintedEdge)
            {
                boundaryOffset = bestOffset;
                source = bestSource;
                sourceHandle = bestHandle;
                return true;
            }

            if (hasRaw)
            {
                boundaryOffset = rawOffset;
                source = "RAW_SELECTED_ROAD_EDGE_FALLBACK";
                sourceHandle = rawEdgeHandle ?? string.Empty;
                return true;
            }

            return false;
        }

        private int TryInferEdgeSideSign(
            Database db,
            Transaction tr,
            Entity axis,
            string edgeHandle,
            double station)
        {
            if (string.IsNullOrWhiteSpace(edgeHandle))
                return 0;

            // Edge có thể kết thúc đúng tại station 7.1 nhưng vẫn tồn tại ngay trước/sau đó.
            // Lấy dấu offset ở lân cận để biết đây là phía nào của TIM.
            foreach (double delta in new[] { 0.50, -0.50, 1.0, -1.0, 2.0, -2.0, 5.0, -5.0, 10.0, -10.0 })
            {
                double probe = station + delta;
                if (!IsStationInside(axis, probe))
                    continue;
                if (TryFindEdgeHitAtStation(db, tr, axis, edgeHandle, probe, out double offset, out _) &&
                    Math.Abs(offset) > 0.05)
                {
                    return Math.Sign(offset);
                }
            }
            return 0;
        }

        private double ResolvePhysicalCurveWidth(Curve curve, ArmEntityMetadata md)
        {
            if (md.Width > 0.0 && !double.IsNaN(md.Width) && !double.IsInfinity(md.Width))
                return md.Width;

            if (curve is Polyline pl && pl.ConstantWidth > 0.0)
                return pl.ConstantWidth;
            if (curve is Polyline2d pl2 && pl2.ConstantWidth > 0.0)
                return pl2.ConstantWidth;

            return 0.0;
        }

        private bool TryFindCurveHitAtStation(
            Entity axis,
            Curve curve,
            double station,
            out double offset,
            out Point3d hitPoint)
        {
            offset = 0.0;
            hitPoint = Point3d.Origin;
            if (curve == null || curve.IsErased || !IsStationInside(axis, station))
                return false;

            Point3d origin = _station.PointAtStationOffset(axis, station, 0.0, out Vector3d tangent);
            Vector3d xyTangent = new Vector3d(tangent.X, tangent.Y, 0.0);
            if (xyTangent.Length <= 1e-9)
                return false;
            xyTangent = xyTangent.GetNormal();
            Vector3d normal = new Vector3d(-xyTangent.Y, xyTangent.X, 0.0);

            const double halfLength = 150.0;
            using (var section = new Line(origin - normal * halfLength, origin + normal * halfLength))
            using (var points = new Point3dCollection())
            {
                try
                {
                    section.IntersectWith(curve, Intersect.OnBothOperands, points, IntPtr.Zero, IntPtr.Zero);
                }
                catch
                {
                    return false;
                }

                bool found = false;
                double bestScore = double.MaxValue;
                foreach (Point3d p in points)
                {
                    if (!_station.TryStationOffset(axis, p, out double candidateStation, out double candidateOffset))
                        continue;
                    double stationError = Math.Abs(candidateStation - station);
                    if (stationError > 0.40)
                        continue;

                    double score = stationError * 100.0 + Math.Abs(candidateOffset);
                    if (score < bestScore)
                    {
                        bestScore = score;
                        offset = candidateOffset;
                        hitPoint = p;
                        found = true;
                    }
                }
                return found;
            }
        }

        /// <summary>
        /// Cắt một mặt cắt vuông góc TIM tại station với chính Curve MÉP được chọn.
        /// Trả về offset thật và điểm giao thật trên MÉP.
        /// </summary>
        private bool TryFindEdgeHitAtStation(
            Database db,
            Transaction tr,
            Entity axis,
            string edgeHandle,
            double station,
            out double offset,
            out Point3d hitPoint)
        {
            offset = 0.0;
            hitPoint = Point3d.Origin;
            if (string.IsNullOrWhiteSpace(edgeHandle) || !IsStationInside(axis, station))
                return false;

            ObjectId edgeId;
            try { edgeId = _geometry.FromHandle(db, edgeHandle.Trim()); }
            catch { return false; }
            if (edgeId.IsNull)
                return false;

            Curve? edge;
            try { edge = tr.GetObject(edgeId, OpenMode.ForRead, false) as Curve; }
            catch { return false; }
            if (edge == null || edge.IsErased)
                return false;

            Point3d origin = _station.PointAtStationOffset(axis, station, 0.0, out Vector3d tangent);
            Vector3d xyTangent = new Vector3d(tangent.X, tangent.Y, 0.0);
            if (xyTangent.Length <= 1e-9)
                return false;
            xyTangent = xyTangent.GetNormal();
            Vector3d normal = new Vector3d(-xyTangent.Y, xyTangent.X, 0.0);

            const double halfLength = 150.0;
            using (var section = new Line(origin - normal * halfLength, origin + normal * halfLength))
            using (var points = new Point3dCollection())
            {
                try
                {
                    section.IntersectWith(edge, Intersect.OnBothOperands, points, IntPtr.Zero, IntPtr.Zero);
                }
                catch { }

                bool found = false;
                double bestScore = double.MaxValue;
                foreach (Point3d p in points)
                {
                    if (!_station.TryStationOffset(axis, p, out double candidateStation, out double candidateOffset))
                        continue;
                    double stationError = Math.Abs(candidateStation - station);
                    if (stationError > 0.75)
                        continue;

                    double score = stationError * 100.0 + Math.Abs(candidateOffset);
                    if (score < bestScore)
                    {
                        bestScore = score;
                        offset = candidateOffset;
                        hitPoint = p;
                        found = true;
                    }
                }

                if (found)
                    return true;
            }

            // Fallback tolerance cho proxy/polyline có sai số nhỏ: chỉ chấp nhận closest point
            // nếu station của điểm đó thực sự gần station yêu cầu.
            try
            {
                Point3d closest = edge.GetClosestPointTo(origin, false);
                if (_station.TryStationOffset(axis, closest, out double closestStation, out double closestOffset) &&
                    Math.Abs(closestStation - station) <= 0.35)
                {
                    offset = closestOffset;
                    hitPoint = closest;
                    return true;
                }
            }
            catch { }

            return false;
        }

        private static string BuildGenerationKey(
            ArmComparisonState comparison,
            string ownerId,
            string approachDirection,
            string code)
        {
            return string.Join(
                "|",
                "INTERSECTION_MARKING",
                comparison.RoadKey,
                comparison.TimHandle,
                ownerId,
                approachDirection,
                code ?? string.Empty);
        }

        private static double ResolveCrossingWidth(ArmMarkingTemplateState template, double? overrideWidth = null)
        {
            double minimum = ReadPositiveCustom(template, "minCrossingWidth_m", 3.0);
            double requested = overrideWidth.HasValue &&
                               !double.IsNaN(overrideWidth.Value) &&
                               !double.IsInfinity(overrideWidth.Value) &&
                               overrideWidth.Value > 0.0
                ? overrideWidth.Value
                : ReadPositiveCustom(template, "crossingWidth_m", minimum);
            double step = ReadPositiveCustom(template, "crossingWidthStep_m", 1.0);

            minimum = Math.Max(3.0, minimum);
            step = step <= 1e-6 ? 1.0 : step;
            requested = Math.Max(minimum, requested);

            // QCVN: chiều rộng tối thiểu 3 m, khi nâng thì mỗi cấp 1 m.
            double levels = Math.Ceiling((requested - minimum - 1e-9) / step);
            return minimum + Math.Max(0.0, levels) * step;
        }

        private static void ResolveRoadSideWidths(
            ArmComparisonState comparison,
            out double leftWidth,
            out double rightWidth)
        {
            leftWidth = Math.Max(0.0, comparison.LeftWidth);
            rightWidth = Math.Max(0.0, comparison.RightWidth);

            // Dữ liệu cũ có thể chỉ lưu tổng Width. Chỉ fallback đối xứng khi hai phía chưa có.
            if (leftWidth <= 0.05 && rightWidth <= 0.05 && comparison.Width > 0.10)
            {
                leftWidth = comparison.Width * 0.5;
                rightWidth = comparison.Width * 0.5;
            }
            else if (comparison.Width > 0.10)
            {
                // Nếu chỉ thiếu một phía, suy ra từ tổng bề rộng nhưng không tạo giá trị âm.
                if (leftWidth <= 0.05 && rightWidth < comparison.Width)
                    leftWidth = Math.Max(0.0, comparison.Width - rightWidth);
                if (rightWidth <= 0.05 && leftWidth < comparison.Width)
                    rightWidth = Math.Max(0.0, comparison.Width - leftWidth);
            }
        }

        private static double ReadPositiveCustom(ArmMarkingTemplateState template, string key, double fallback)
        {
            if (MarkingTemplateManagementService.TryReadDouble(template, key, out double value) &&
                value > 0.0 && !double.IsNaN(value) && !double.IsInfinity(value))
            {
                return value;
            }
            return fallback;
        }

        private static double ReadNonNegativeCustom(ArmMarkingTemplateState template, string key, double fallback)
        {
            if (MarkingTemplateManagementService.TryReadDouble(template, key, out double value) &&
                value >= 0.0 && !double.IsNaN(value) && !double.IsInfinity(value))
            {
                return value;
            }
            return fallback;
        }

        private void EraseExistingNodeMarkings(
            Database db,
            Transaction tr,
            ArmComparisonState comparison,
            string ownerId,
            string legacyPolygonHandle)
        {
            var ids = new List<ObjectId>();

            foreach (var item in _cachedEntities)
            {
                ArmEntityMetadata md = item.Meta;
                if (!md.Source.Equals("AUTO_INTERSECTION", StringComparison.OrdinalIgnoreCase))
                    continue;

                ObjectId id = item.Id;

                string key = !string.IsNullOrWhiteSpace(md.AxisKey) ? md.AxisKey : md.RoadKey;
                bool sameAxis = string.Equals(key, comparison.RoadKey, StringComparison.OrdinalIgnoreCase) ||
                                (!string.IsNullOrWhiteSpace(comparison.TimHandle) &&
                                 string.Equals(md.AxisHandle, comparison.TimHandle, StringComparison.OrdinalIgnoreCase));

                if (sameAxis &&
                    (md.OwnerId.Equals(ownerId, StringComparison.OrdinalIgnoreCase) ||
                     md.OwnerId.Equals(legacyPolygonHandle, StringComparison.OrdinalIgnoreCase)) &&
                    (md.MarkingCode.Equals("7.1", StringComparison.OrdinalIgnoreCase) ||
                     md.MarkingCode.Equals("7.3", StringComparison.OrdinalIgnoreCase)))
                {
                    ids.Add(id);
                }
            }

            foreach (ObjectId id in ids)
            {
                if (tr.GetObject(id, OpenMode.ForWrite, false) is Entity entity && !entity.IsErased)
                    entity.Erase(true);
            }
        }

        private void EraseByGeneration(Database db, Transaction tr, string generationKey)
        {
            var toErase = new List<ObjectId>();

            foreach (var item in _cachedEntities)
            {
                ArmEntityMetadata md = item.Meta;
                if (md.GenerationKey.Equals(generationKey, StringComparison.Ordinal))
                    toErase.Add(item.Id);
            }

            foreach (ObjectId id in toErase)
            {
                if (tr.GetObject(id, OpenMode.ForWrite, false) is Entity entity && !entity.IsErased)
                    entity.Erase(true);
            }
        }

        private void EraseByGenerationPrefix(Database db, Transaction tr, string generationPrefix)
        {
            var toErase = new List<ObjectId>();

            foreach (var item in _cachedEntities)
            {
                ArmEntityMetadata md = item.Meta;
                if (md.GenerationKey.Equals(generationPrefix, StringComparison.Ordinal) ||
                    md.GenerationKey.StartsWith(generationPrefix + "|", StringComparison.Ordinal))
                {
                    toErase.Add(item.Id);
                }
            }

            foreach (ObjectId id in toErase)
            {
                if (tr.GetObject(id, OpenMode.ForWrite, false) is Entity entity && !entity.IsErased)
                    entity.Erase(true);
            }
        }

        private bool IsStationInside(Entity axis, double station)
        {
            double start = _station.StartStation(axis);
            double end = _station.EndStation(axis);
            if (end < start)
            {
                double t = start;
                start = end;
                end = t;
            }

            const double tolerance = 1e-6;
            return station >= start - tolerance && station <= end + tolerance;
        }

        private static Point3d PolygonCentroid(Polyline polygon)
        {
            int count = polygon.NumberOfVertices;
            if (count <= 0)
                return Point3d.Origin;

            // Vertex-average chỉ dùng xác định phía approach, không dùng như centroid kỹ thuật.
            double x = 0.0;
            double y = 0.0;
            for (int i = 0; i < count; i++)
            {
                Point2d point = polygon.GetPoint2dAt(i);
                x += point.X;
                y += point.Y;
            }
            return new Point3d(x / count, y / count, 0.0);
        }
    }

    public sealed class StopCrosswalkGenerationResult
    {
        public double RequestedDistance { get; set; }
        public int CreatedCount { get; set; }
        public int ApproachPairCount { get; set; }
        public List<StopCrosswalkRejectedPlacement> Rejected { get; set; } =
            new List<StopCrosswalkRejectedPlacement>();
    }

    public sealed class StopCrosswalkRejectedPlacement
    {
        public string RoadKey { get; set; } = string.Empty;
        public string RoadName { get; set; } = string.Empty;
        public string NodeId { get; set; } = string.Empty;
        public string ApproachDirection { get; set; } = string.Empty;
        public double CrosswalkStation { get; set; }
        public double StopStation { get; set; }
        public string Reason { get; set; } = string.Empty;
    }
}
