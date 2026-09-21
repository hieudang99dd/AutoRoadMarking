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

using AcEntity = Autodesk.AutoCAD.DatabaseServices.Entity;

namespace Autoroadmarking_Pro.CadHost.Cad.Intersections
{
    /// <summary>
    /// Tự động dựng polygon vùng nút giao từ hình học TIM + MÉP.
    ///
    /// Chiến lược chính:
    /// 1) tìm tâm nút từ các TIM;
    /// 2) xác định các hướng nhánh TIM rời tâm và chia không gian thành các sector;
    /// 3) nhận dạng Arc / đoạn bulge của MÉP và chọn đúng sừng bò theo sector + hướng tiếp tuyến;
    /// 4) riêng ngã ba chữ T: nhận 2 sừng bò + 1 mép thẳng đối diện và dựng polygon 6 điểm;
    /// 5) nút nhiều nhánh: lấy các đầu tiếp tuyến sừng bò, kiểm tra topology / polygon;
    /// 6) nếu dữ liệu cong không đủ, fallback sang dò cổ nút bằng tia trái/phải ổn định.
    ///
    /// Không yêu cầu người dùng pick từng đỉnh. Polygon vẫn là Polyline kín có grip để
    /// chỉnh tay khi cần; RefreshPolygons chỉ đọc lại hình học hiện có.
    /// </summary>
    public sealed class IntersectionPolygonService
    {
        private const double PointMergeTolerance = 0.08;
        private const double NodeMergeTolerance = 1.00;
        private const double TimAttachTolerance = 2.50;
        private const double ApproachAngleToleranceDeg = 5.0;
        private const double MinPolygonArea = 1.0;
        private const double MinCurveTurnDeg = 4.0;
        private const double MaxTangentMismatchDeg = 38.0;
        private const double MinSearchRadius = 12.0;
        private const double MaxSearchRadius = 50.0;
        private const double MinRoadWidth = 2.0;
        private const double MaxRoadWidth = 60.0;
        private const double MaxRayLength = 120.0;

        // Ngã ba chữ T: hai hướng tuyến chính gần đối nhau, hướng còn lại là nhánh.
        // Polygon ưu tiên 6 điểm = 4 đầu sừng bò + 2 điểm trên mép thẳng đối diện.
        private const double TJunctionOppositeToleranceDeg = 25.0;
        private const double TJunctionEdgeParallelToleranceDeg = 25.0;
        private const double TJunctionMinAcrossDistance = 0.50;
        private const double TJunctionMaxWidthDifferenceAbs = 1.50;
        private const double TJunctionMaxWidthDifferenceRatio = 0.35;

        private readonly CadLayerService _layers = new CadLayerService();
        private readonly EntityMetadataStore _metadata = new EntityMetadataStore();
        private readonly CadGeometryService _geometry = new CadGeometryService();
        private readonly StationOffsetService _station = new StationOffsetService();

        public List<string> DrawPolygons(
            Database db,
            Transaction tr,
            ArmProjectState state,
            string layerName)
        {
            if (db == null) throw new ArgumentNullException(nameof(db));
            if (tr == null) throw new ArgumentNullException(nameof(tr));
            if (state == null) throw new ArgumentNullException(nameof(state));

            if (state.SelectedTimHandles == null || state.SelectedTimHandles.Count < 2)
                throw new InvalidOperationException("Cần tối thiểu 2 TIM để nhận diện nút giao.");

            if (state.SelectedEdgeHandles == null || state.SelectedEdgeHandles.Count == 0)
                throw new InvalidOperationException("Chưa chọn MÉP đường. Hãy chọn các mép ngoài phần xe chạy trước khi tạo polygon nút giao.");

            List<CurveProxy> tims = OpenTimProxies(db, tr, state);
            try
            {
                if (tims.Count < 2)
                    throw new InvalidOperationException(
                        "Không còn đủ TIM hợp lệ để dựng nút. Một hoặc nhiều TIM đã bị xóa/thay thế trong CAD; hãy chọn lại TIM.");

                List<Point3d> centers = FindNodeCenters(tims.Select(x => x.Curve).ToList());
                if (centers.Count == 0)
                    throw new InvalidOperationException("Không tìm được tâm nút từ các TIM đã chọn.");

                List<EdgeRef> selectedEdges = OpenSelectedEdges(db, tr, state);
                if (selectedEdges.Count == 0)
                    throw new InvalidOperationException("Không đọc được hình học MÉP đã chọn.");

                List<EdgeRef> candidateEdges = ExpandCurvedEdgesOnSelectedLayers(db, tr, selectedEdges, centers);
                var drafts = new List<PolygonDraft>();

                foreach (Point3d center in centers)
                {
                    List<double> approaches = CollectApproachAngles(center, tims);
                    if (approaches.Count < 2)
                        continue;

                    List<string> nodeTimHandles = CollectAttachedTimHandles(center, tims);
                    string nodeKey = BuildStableNodeKey(center, nodeTimHandles);

                    double searchRadius = EstimateSearchRadius(center, selectedEdges);
                    List<BullhornSegment> bullhorns = CollectBullhornSegments(candidateEdges, center, searchRadius);

                    List<Point3d> points = new List<Point3d>();
                    List<double> bulges = new List<double>();
                    List<string> edgeHandles = new List<string>();
                    List<string> bullhornHandles = new List<string>();
                    List<string> oppositeEdgeHandles = new List<string>();
                    string mode = string.Empty;

                    // Trường hợp đặc biệt của ngã ba chữ T: thường chỉ có 2 sừng bò ở phía đường nhánh,
                    // còn phía đối diện là một MÉP thẳng liên tục của tuyến chính. Không được chấp nhận
                    // polygon 4 điểm từ 2 sừng bò quá sớm; phải bổ sung 2 giao điểm trên mép đối diện.
                    if (approaches.Count == 3 && TryBuildTJunctionPolygon(
                        center, approaches, bullhorns, selectedEdges, out List<Point3d> tPoints, out List<string> tHandles))
                    {
                        points = tPoints;
                        bulges = BuildBulgesForOrderedPoints(points, bullhorns);
                        bullhornHandles = FindBullhornHandlesForOrderedPoints(points, bullhorns);
                        edgeHandles = tHandles;
                        oppositeEdgeHandles = edgeHandles
                            .Where(x => !bullhornHandles.Contains(x, StringComparer.OrdinalIgnoreCase))
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToList();
                        mode = "AUTO_T_JUNCTION_2_BULLHORN_1_STRAIGHT";
                    }
                    else
                    {
                        List<BullhornSegment> chosen = SelectBullhornsBySector(center, approaches, bullhorns);

                        // Với 3 nhánh chỉ cho phép polygon thuần sừng bò khi đủ 3 sừng bò (6 đầu mút).
                        // Nếu chỉ tìm được 2, phải đi qua nhánh xử lý T ở trên hoặc fallback cổ nút.
                        bool enoughBullhornTopology = approaches.Count == 3
                            ? chosen.Count >= 3
                            : chosen.Count >= Math.Max(2, approaches.Count - 1);

                        if (enoughBullhornTopology)
                        {
                            List<Point3d> bullhornPoints = PreparePolygonPoints(
                                center,
                                chosen.SelectMany(x => x.Points));

                            if (IsValidPolygon(center, bullhornPoints))
                            {
                                points = bullhornPoints;
                                bulges = BuildBulgesForOrderedPoints(points, chosen);
                                edgeHandles = chosen
                                    .Select(x => x.SourceHandle)
                                    .Where(x => !string.IsNullOrWhiteSpace(x))
                                    .Distinct(StringComparer.OrdinalIgnoreCase)
                                    .ToList();
                                bullhornHandles = edgeHandles.ToList();
                                mode = "AUTO_SECTOR_BULLHORN";
                            }
                        }
                    }

                    if (!IsValidPolygon(center, points))
                    {
                        List<ThroatHit> throats = CollectStableThroats(center, tims, selectedEdges);
                        points = PreparePolygonPoints(
                            center,
                            throats.SelectMany(x => new[] { x.LeftPoint, x.RightPoint }));

                        bulges = Enumerable.Repeat(0.0, points.Count).ToList();
                        edgeHandles = throats
                            .SelectMany(x => new[] { x.LeftHandle, x.RightHandle })
                            .Where(x => !string.IsNullOrWhiteSpace(x))
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToList();
                        bullhornHandles.Clear();
                        oppositeEdgeHandles.Clear();
                        mode = "AUTO_STABLE_THROAT";
                    }

                    if (!IsValidPolygon(center, points))
                        continue;

                    if (bulges.Count != points.Count)
                        bulges = Enumerable.Repeat(0.0, points.Count).ToList();

                    drafts.Add(new PolygonDraft
                    {
                        Center = center,
                        Points = points,
                        Bulges = bulges,
                        EdgeHandles = edgeHandles,
                        BullhornHandles = bullhornHandles,
                        OppositeEdgeHandles = oppositeEdgeHandles,
                        NodeKey = nodeKey,
                        NodeTimHandles = nodeTimHandles,
                        Mode = mode,
                        ApproachCount = approaches.Count
                    });
                }

                // Không xóa polygon cũ khi lần nhận diện mới thất bại.
                if (drafts.Count == 0)
                {
                    throw new InvalidOperationException(
                        "Chưa dựng được polygon hợp lệ. Hãy kiểm tra TIM giao nhau và MÉP có Arc/bulge sừng bò; " +
                        "nếu không có cung rõ ràng, cần bảo đảm các mép hai bên cắt được mặt cắt ngang của từng nhánh.");
                }

                EraseCurrentPolygons(db, tr, state);

                BlockTableRecord modelSpace = (BlockTableRecord)tr.GetObject(
                    SymbolUtilityServices.GetBlockModelSpaceId(db),
                    OpenMode.ForWrite);

                ObjectId polygonLayerId = _layers.EnsureLayer(
                    db,
                    tr,
                    string.IsNullOrWhiteSpace(layerName)
                        ? "_9.HOTRO_VUNG_NUT_GIAO"
                        : layerName.Trim());

                var handles = new List<string>();
                foreach (PolygonDraft draft in drafts)
                {
                    var polygon = new Polyline(draft.Points.Count);
                    for (int i = 0; i < draft.Points.Count; i++)
                    {
                        polygon.AddVertexAt(
                            i,
                            new Point2d(draft.Points[i].X, draft.Points[i].Y),
                            draft.Bulges != null && i < draft.Bulges.Count ? draft.Bulges[i] : 0.0,
                            0.0,
                            0.0);
                    }

                    polygon.Closed = true;
                    polygon.LayerId = polygonLayerId;

                    ObjectId id = modelSpace.AppendEntity(polygon);
                    tr.AddNewlyCreatedDBObject(polygon, true);

                    string handle = id.Handle.ToString();
                    var metadata = new ArmEntityMetadata
                    {
                        RecordId = "NODE_POLY_" + handle,
                        GenerationKey = "INTERSECTION_POLYGON_AUTO|" + handle,
                        Source = "INTERSECTION_POLYGON",
                        OwnerType = "INTERSECTION",
                        OwnerId = draft.NodeKey,
                        CadLayer = polygon.Layer
                    };
                    metadata.Extra["PolygonMode"] = draft.Mode;
                    metadata.Extra["Topology"] = draft.Mode == "AUTO_T_JUNCTION_2_BULLHORN_1_STRAIGHT"
                        ? "T_JUNCTION_2_BULLHORN_1_STRAIGHT"
                        : draft.ApproachCount == 3 ? "THREE_APPROACH" : "MULTI_APPROACH";
                    metadata.Extra["VertexSource"] = draft.Mode == "AUTO_T_JUNCTION_2_BULLHORN_1_STRAIGHT"
                        ? "T_JUNCTION_BULLHORN_PLUS_OPPOSITE_EDGE"
                        : draft.Mode == "AUTO_SECTOR_BULLHORN"
                            ? "CURB_RETURN_TANGENCY_POINTS"
                            : "STABLE_APPROACH_THROATS";
                    metadata.Extra["NodeCenterX"] = draft.Center.X.ToString("0.###", CultureInfo.InvariantCulture);
                    metadata.Extra["NodeCenterY"] = draft.Center.Y.ToString("0.###", CultureInfo.InvariantCulture);
                    metadata.Extra["NodeKey"] = draft.NodeKey;
                    metadata.Extra["NodeTimHandles"] = string.Join(",", draft.NodeTimHandles);
                    metadata.Extra["PolygonHandle"] = handle;
                    metadata.Extra["ApproachCount"] = draft.ApproachCount.ToString(CultureInfo.InvariantCulture);
                    metadata.Extra["EdgeHandles"] = string.Join(",", draft.EdgeHandles);
                    metadata.Extra["BullhornHandles"] = string.Join(",", draft.BullhornHandles);
                    metadata.Extra["OppositeEdgeHandles"] = string.Join(",", draft.OppositeEdgeHandles);

                    // BoundaryRunsV2 là contract hình học chính giữa Bước 2 và Bước 3.
                    // Thay vì chỉ lưu Handle của cả Polyline MÉP (mơ hồ khi một entity chứa
                    // nhiều segment), ta ghi chính xác INDEX của cạnh polygon nào thực sự bám
                    // theo MÉP CAD, Handle nguồn và vai trò nghiệp vụ. Các cạnh đóng polygon
                    // nhân tạo/cổ nút không khớp MÉP CAD nên không xuất hiện trong danh sách này.
                    // Bước 3 vì vậy có thể offset đúng từng đoạn biên thật cho cả ngã ba,
                    // ngã tư, Line, Arc và Polyline nhiều segment.
                    List<BoundaryRun> boundaryRuns = BuildBoundaryRuns(draft, selectedEdges);
                    metadata.Extra["BoundaryRunsV2"] = SerializeBoundaryRuns(boundaryRuns);
                    // V3 bổ sung SourceSegmentIndex của chính MÉP CAD gốc. Bước 4 dùng
                    // thông tin này để tìm đúng vertex chuyển ARC/BULGE -> LINE trên MÉP thật,
                    // không suy tiếp tuyến từ polygon helper nữa. V2 vẫn được giữ để tương thích.
                    metadata.Extra["BoundaryRunsV3"] = SerializeBoundaryRunsV3(boundaryRuns);
                    metadata.Extra["BoundaryRunCount"] = boundaryRuns.Count.ToString(CultureInfo.InvariantCulture);
                    metadata.Extra["CurvedBoundary"] = draft.Bulges.Any(x => Math.Abs(x) > 1e-8) ? "1" : "0";
                    _metadata.Write(polygon, tr, metadata);

                    handles.Add(handle);
                }

                state.IntersectionPolygonHandles = handles;
                return handles;
            }
            finally
            {
                foreach (CurveProxy tim in tims)
                    tim.Dispose();
            }
        }

        public List<string> RefreshPolygons(Database db, Transaction tr, ArmProjectState state)
        {
            if (db == null) throw new ArgumentNullException(nameof(db));
            if (tr == null) throw new ArgumentNullException(nameof(tr));
            if (state == null) throw new ArgumentNullException(nameof(state));

            var valid = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string handle in state.IntersectionPolygonHandles ?? new List<string>())
            {
                if (!TryOpenHandleEntity(db, tr, handle, OpenMode.ForRead, out Polyline? polygon) || polygon == null)
                    continue;

                if (polygon.Closed && polygon.NumberOfVertices >= 3)
                    valid.Add(polygon.ObjectId.Handle.ToString());
            }

            BlockTableRecord modelSpace = (BlockTableRecord)tr.GetObject(
                SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForRead);

            foreach (ObjectId id in modelSpace)
            {
                if (!TryOpenObject(tr, id, OpenMode.ForRead, out Polyline? polygon) ||
                    polygon == null || !polygon.Closed || polygon.NumberOfVertices < 3)
                    continue;

                ArmEntityMetadata? metadata = _metadata.Read(polygon, tr);
                if (metadata != null && string.Equals(metadata.Source, "INTERSECTION_POLYGON", StringComparison.OrdinalIgnoreCase))
                    valid.Add(polygon.ObjectId.Handle.ToString());
            }

            state.IntersectionPolygonHandles = valid.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
            return state.IntersectionPolygonHandles;
        }

        private List<CurveProxy> OpenTimProxies(Database db, Transaction tr, ArmProjectState state)
        {
            var result = new List<CurveProxy>();
            var validHandles = new List<string>();
            foreach (string handle in state.SelectedTimHandles ?? new List<string>())
            {
                if (!TryOpenHandleEntity(db, tr, handle, OpenMode.ForRead, out AcEntity? entity) || entity == null)
                    continue;

                Curve? proxy = _station.CreatePolylineProxy(entity, 0.0);
                if (proxy == null)
                    continue;

                result.Add(new CurveProxy(handle, proxy));
                validHandles.Add(handle);
            }

            // Dọn state stale ngay khi phát hiện để lần chạy sau không lặp lại eWasErased.
            state.SelectedTimHandles = validHandles
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            return result;
        }

        private List<EdgeRef> OpenSelectedEdges(Database db, Transaction tr, ArmProjectState state)
        {
            var result = new List<EdgeRef>();
            var validHandles = new List<string>();
            foreach (string handle in state.SelectedEdgeHandles ?? new List<string>())
            {
                if (!TryOpenHandleEntity(db, tr, handle, OpenMode.ForRead, out Curve? curve) || curve == null)
                    continue;

                result.Add(new EdgeRef(curve.ObjectId, handle, curve));
                validHandles.Add(handle);
            }

            // MÉP có thể đã bị xóa/grip-edit/recreate. Không giữ handle chết trong state.
            state.SelectedEdgeHandles = validHandles
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            return result;
        }

        private List<EdgeRef> ExpandCurvedEdgesOnSelectedLayers(
            Database db,
            Transaction tr,
            List<EdgeRef> selected,
            IReadOnlyList<Point3d> centers)
        {
            var result = new List<EdgeRef>(selected);
            var ids = new HashSet<ObjectId>(selected.Select(x => x.Id));
            var layers = new HashSet<string>(
                selected.Select(x => x.Curve.Layer).Where(x => !string.IsNullOrWhiteSpace(x)),
                StringComparer.OrdinalIgnoreCase);

            if (layers.Count == 0 || centers.Count == 0)
                return result;

            BlockTableRecord modelSpace = (BlockTableRecord)tr.GetObject(
                SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForRead);

            foreach (ObjectId id in modelSpace)
            {
                if (ids.Contains(id)) continue;
                if (!TryOpenObject(tr, id, OpenMode.ForRead, out Curve? curve) || curve == null) continue;
                if (!layers.Contains(curve.Layer)) continue;
                if (!HasUsableCurvature(curve)) continue;

                bool nearNode = centers.Any(center => DistanceToCurve(curve, center) <= MaxSearchRadius);
                if (!nearNode) continue;

                result.Add(new EdgeRef(id, id.Handle.ToString(), curve));
                ids.Add(id);
            }

            return result;
        }

        private List<Point3d> FindNodeCenters(List<Curve> timCurves)
        {
            var raw = new List<Point3d>();
            for (int i = 0; i < timCurves.Count; i++)
            {
                for (int j = i + 1; j < timCurves.Count; j++)
                {
                    using (var points = new Point3dCollection())
                    {
                        try
                        {
                            timCurves[i].IntersectWith(timCurves[j], Intersect.OnBothOperands, points, IntPtr.Zero, IntPtr.Zero);
                            foreach (Point3d p in points)
                                AddUniquePoint(raw, ToXY(p), NodeMergeTolerance);
                        }
                        catch { }
                    }

                    AddNearEndpointCenter(timCurves[i], timCurves[j], raw);
                    AddNearEndpointCenter(timCurves[j], timCurves[i], raw);
                }
            }
            return MergeNearbyPoints(raw, NodeMergeTolerance);
        }

        private static void AddNearEndpointCenter(Curve source, Curve target, List<Point3d> centers)
        {
            foreach (Point3d endpoint in new[] { source.StartPoint, source.EndPoint })
            {
                try
                {
                    Point3d closest = target.GetClosestPointTo(endpoint, false);
                    if (endpoint.DistanceTo(closest) > 1.5) continue;
                    Point3d midpoint = new Point3d(
                        (endpoint.X + closest.X) * 0.5,
                        (endpoint.Y + closest.Y) * 0.5,
                        0.0);
                    AddUniquePoint(centers, midpoint, NodeMergeTolerance);
                }
                catch { }
            }
        }

        private List<double> CollectApproachAngles(Point3d center, List<CurveProxy> tims)
        {
            var angles = new List<double>();
            foreach (CurveProxy tim in tims)
            {
                Curve curve = tim.Curve;
                try
                {
                    Point3d closest = ToXY(curve.GetClosestPointTo(center, false));
                    if (closest.DistanceTo(center) > TimAttachTolerance) continue;

                    double param = curve.GetParameterAtPoint(curve.GetClosestPointTo(center, false));
                    Vector3d tangent = ToXY(curve.GetFirstDerivative(param));
                    if (tangent.Length <= 1e-9) continue;
                    tangent = tangent.GetNormal();

                    double toStart = closest.DistanceTo(ToXY(curve.StartPoint));
                    double toEnd = closest.DistanceTo(ToXY(curve.EndPoint));
                    bool nearStart = toStart <= TimAttachTolerance * 1.5;
                    bool nearEnd = toEnd <= TimAttachTolerance * 1.5;

                    if (nearStart && !nearEnd)
                        AddUniqueAngle(angles, AngleOf(tangent), ApproachAngleToleranceDeg);
                    else if (nearEnd && !nearStart)
                        AddUniqueAngle(angles, AngleOf(tangent.Negate()), ApproachAngleToleranceDeg);
                    else
                    {
                        AddUniqueAngle(angles, AngleOf(tangent), ApproachAngleToleranceDeg);
                        AddUniqueAngle(angles, AngleOf(tangent.Negate()), ApproachAngleToleranceDeg);
                    }
                }
                catch { }
            }
            return angles.OrderBy(x => x).ToList();
        }

        private List<string> CollectAttachedTimHandles(Point3d center, IEnumerable<CurveProxy> tims)
        {
            var handles = new List<string>();
            foreach (CurveProxy tim in tims ?? Enumerable.Empty<CurveProxy>())
            {
                if (tim == null || tim.Curve == null || string.IsNullOrWhiteSpace(tim.Handle))
                    continue;

                try
                {
                    Point3d closest = ToXY(tim.Curve.GetClosestPointTo(center, false));
                    if (closest.DistanceTo(center) <= TimAttachTolerance * 1.25)
                        handles.Add(tim.Handle.Trim().ToUpperInvariant());
                }
                catch { }
            }

            return handles
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static string BuildStableNodeKey(Point3d center, IEnumerable<string> timHandles)
        {
            string[] handles = (timHandles ?? Enumerable.Empty<string>())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim().ToUpperInvariant())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (handles.Length >= 2)
                return "NODE|" + string.Join("|", handles);

            // Fallback chỉ dùng khi topology TIM không đủ để lập key bằng handle.
            // Làm tròn 0.10 m giúp key ổn định trước nhiễu số học rất nhỏ khi dựng lại polygon.
            double x = Math.Round(center.X, 1, MidpointRounding.AwayFromZero);
            double y = Math.Round(center.Y, 1, MidpointRounding.AwayFromZero);
            return string.Format(CultureInfo.InvariantCulture, "NODE|XY|{0:0.0}|{1:0.0}", x, y);
        }

        private double EstimateSearchRadius(Point3d center, List<EdgeRef> edges)
        {
            double[] distances = edges
                .Select(x => DistanceToCurve(x.Curve, center))
                .Where(x => x > 0.05 && x < MaxSearchRadius)
                .OrderBy(x => x)
                .Take(8)
                .ToArray();

            if (distances.Length == 0) return 28.0;
            double baseDistance = Median(distances);
            return Clamp(baseDistance * 4.0 + 6.0, MinSearchRadius, MaxSearchRadius);
        }

        private List<BullhornSegment> CollectBullhornSegments(
            List<EdgeRef> edges,
            Point3d center,
            double searchRadius)
        {
            var result = new List<BullhornSegment>();
            foreach (EdgeRef edge in edges)
            {
                foreach (BullhornSegment segment in ExtractBullhornSegments(edge))
                {
                    if (segment.Mid.DistanceTo(center) > searchRadius) continue;
                    if (Math.Min(segment.Start.DistanceTo(center), segment.End.DistanceTo(center)) > searchRadius * 1.20)
                        continue;
                    result.Add(segment);
                }
            }

            var unique = new List<BullhornSegment>();
            foreach (BullhornSegment segment in result.OrderBy(x => x.Mid.DistanceTo(center)))
            {
                bool duplicate = unique.Any(existing =>
                    SamePointPair(existing.Start, existing.End, segment.Start, segment.End, PointMergeTolerance));
                if (!duplicate) unique.Add(segment);
            }
            return unique;
        }

        private IEnumerable<BullhornSegment> ExtractBullhornSegments(EdgeRef edge)
        {
            Curve curve = edge.Curve;
            if (curve is Polyline pl)
            {
                int n = pl.NumberOfVertices;
                int segmentCount = pl.Closed ? n : Math.Max(0, n - 1);
                
                var points = new List<Point3d>();
                var bulges = new List<double>();
                Point3d firstMid = Point3d.Origin;
                Vector3d firstTs = Vector3d.XAxis;
                Vector3d lastTe = Vector3d.XAxis;
                double turnSum = 0.0;

                for (int i = 0; i < segmentCount; i++)
                {
                    double bulge = 0.0;
                    try { bulge = pl.GetBulgeAt(i); }
                    catch { continue; }
                    
                    if (Math.Abs(bulge) > 1e-8)
                    {
                        int j = (i + 1) % n;
                        Point3d start = ToXY(pl.GetPoint3dAt(i));
                        Point3d end = ToXY(pl.GetPoint3dAt(j));
                        if (start.DistanceTo(end) <= 1e-5) continue;

                        Point3d mid = Midpoint(start, end);
                        Vector3d ts = Vector3d.XAxis;
                        Vector3d te = Vector3d.XAxis;
                        try
                        {
                            double p0 = i + 1e-4;
                            double p1 = i + 1.0 - 1e-4;
                            mid = ToXY(pl.GetPointAtParameter(i + 0.5));
                            ts = SafeNormal(ToXY(pl.GetFirstDerivative(p0)), end - start);
                            te = SafeNormal(ToXY(pl.GetFirstDerivative(p1)), end - start);
                        }
                        catch
                        {
                            Vector3d chord = SafeNormal(end - start, Vector3d.XAxis);
                            ts = chord;
                            te = chord;
                        }

                        if (points.Count == 0)
                        {
                            points.Add(start);
                            firstMid = mid;
                            firstTs = ts;
                        }
                        points.Add(end);
                        bulges.Add(bulge);
                        lastTe = te;
                        turnSum += Math.Abs(4.0 * Math.Atan(bulge));
                    }
                    else if (points.Count > 0)
                    {
                        yield return new BullhornSegment(edge.Handle, points, bulges, firstMid, firstTs, lastTe, turnSum);
                        points = new List<Point3d>();
                        bulges = new List<double>();
                        turnSum = 0.0;
                    }
                }
                
                if (points.Count > 0)
                {
                    yield return new BullhornSegment(edge.Handle, points, bulges, firstMid, firstTs, lastTe, turnSum);
                }
                yield break;
            }

            if (curve is Arc arc)
            {
                Point3d start = ToXY(arc.StartPoint);
                Point3d end = ToXY(arc.EndPoint);
                if (start.DistanceTo(end) <= 1e-5) yield break;
                Point3d mid = Midpoint(start, end);
                Vector3d ts = SafeNormal(end - start, Vector3d.XAxis);
                Vector3d te = ts;
                try
                {
                    mid = ToXY(arc.GetPointAtParameter((arc.StartParam + arc.EndParam) * 0.5));
                    ts = SafeNormal(ToXY(arc.GetFirstDerivative(arc.StartParam)), end - start);
                    te = SafeNormal(ToXY(arc.GetFirstDerivative(arc.EndParam)), end - start);
                }
                catch { }
                double turn = Math.Abs(NormalizeSignedAngle(AngleOf(te) - AngleOf(ts)));
                double arcBulge = Math.Tan(Math.Abs(arc.TotalAngle) / 4.0);
                if (arc.Normal.Z < 0.0) arcBulge = -arcBulge;
                yield return new BullhornSegment(edge.Handle, new List<Point3d> { start, end }, new List<double> { arcBulge }, mid, ts, te, turn);
                yield break;
            }

            // Spline/Ellipse chỉ được dùng khi toàn curve là một đoạn cong cục bộ rõ ràng.
            if (!HasUsableCurvature(curve)) yield break;
            double length = CurveLength(curve);
            if (length <= 1e-5 || length > MaxSearchRadius * 2.5) yield break;
            BullhornSegment? fallbackSegment = null;
            try
            {
                Point3d start = ToXY(curve.StartPoint);
                Point3d end = ToXY(curve.EndPoint);
                if (start.DistanceTo(end) > 1e-5)
                {
                    Vector3d ts = SafeNormal(ToXY(curve.GetFirstDerivative(curve.StartParam)), end - start);
                    Vector3d te = SafeNormal(ToXY(curve.GetFirstDerivative(curve.EndParam)), end - start);
                    double turn = Math.Abs(NormalizeSignedAngle(AngleOf(te) - AngleOf(ts)));
                    Point3d mid = ToXY(curve.GetPointAtParameter((curve.StartParam + curve.EndParam) * 0.5));
                    fallbackSegment = new BullhornSegment(edge.Handle, new List<Point3d> { start, end }, new List<double> { 0.0 }, mid, ts, te, turn);
                }
            }
            catch { }

            if (fallbackSegment != null)
                yield return fallbackSegment;
        }

        private List<BullhornSegment> SelectBullhornsBySector(
            Point3d center,
            List<double> approaches,
            List<BullhornSegment> candidates)
        {
            if (approaches.Count < 2 || candidates.Count == 0)
                return new List<BullhornSegment>();

            var selected = new List<BullhornSegment>();
            var used = new HashSet<BullhornSegment>();
            double tangentLimit = MaxTangentMismatchDeg * Math.PI / 180.0;

            for (int i = 0; i < approaches.Count; i++)
            {
                double a = approaches[i];
                double b = i == approaches.Count - 1 ? approaches[0] + Math.PI * 2.0 : approaches[i + 1];
                double gap = b - a;
                if (gap < 12.0 * Math.PI / 180.0) continue;

                BullhornSegment? best = null;
                double bestScore = double.MaxValue;

                foreach (BullhornSegment candidate in candidates)
                {
                    if (used.Contains(candidate)) continue;
                    double midAngle = NormalizeAngle(AngleFrom(center, candidate.Mid));
                    double unwrapped = midAngle < a ? midAngle + Math.PI * 2.0 : midAngle;
                    if (unwrapped <= a + 1e-6 || unwrapped >= b - 1e-6) continue;

                    double ts = NormalizeAngle(AngleOf(candidate.StartTangent));
                    double te = NormalizeAngle(AngleOf(candidate.EndTangent));
                    double m1 = LineAngleDifference(ts, a) + LineAngleDifference(te, b);
                    double m2 = LineAngleDifference(ts, b) + LineAngleDifference(te, a);
                    double tangentMismatch = Math.Min(m1, m2);
                    if (tangentMismatch > tangentLimit * 2.0) continue;

                    double sectorMid = a + gap * 0.5;
                    double radialAnglePenalty = Math.Abs(NormalizeSignedAngle(unwrapped - sectorMid));
                    double distancePenalty = candidate.Mid.DistanceTo(center) * 0.025;
                    double weakCurvePenalty = candidate.TurnRadians < MinCurveTurnDeg * Math.PI / 180.0 ? 10.0 : 0.0;
                    double score = tangentMismatch * 3.0 + radialAnglePenalty * 0.25 + distancePenalty + weakCurvePenalty;

                    if (score < bestScore)
                    {
                        bestScore = score;
                        best = candidate;
                    }
                }

                if (best != null)
                {
                    selected.Add(best);
                    used.Add(best);
                }
            }

            return selected;
        }

        /// <summary>
        /// Dựng polygon chuyên biệt cho ngã ba chữ T có 2 sừng bò + 1 mép thẳng đối diện.
        /// Hai hướng TIM gần 180° được xem là tuyến chính; hướng còn lại là đường nhánh.
        /// Mỗi sừng bò cung cấp 1 điểm tiếp tuyến với tuyến chính + 1 điểm tiếp tuyến với nhánh.
        /// Từ hai điểm tiếp tuyến tuyến chính, bắn tia vuông góc sang phía đối diện đường nhánh
        /// để tìm 2 điểm trên mép thẳng đối diện. Polygon cuối có 6 đỉnh.
        /// </summary>
        private bool TryBuildTJunctionPolygon(
            Point3d center,
            List<double> approaches,
            List<BullhornSegment> candidates,
            List<EdgeRef> selectedEdges,
            out List<Point3d> points,
            out List<string> edgeHandles)
        {
            points = new List<Point3d>();
            edgeHandles = new List<string>();

            if (!TryGetTJunctionDirections(
                approaches,
                out double mainA,
                out double mainB,
                out double branchAngle))
                return false;

            var used = new HashSet<BullhornSegment>();
            BullhornSegment? hornA = SelectTJunctionBullhorn(
                center, mainA, branchAngle, candidates, used);
            if (hornA == null) return false;
            used.Add(hornA);

            BullhornSegment? hornB = SelectTJunctionBullhorn(
                center, mainB, branchAngle, candidates, used);
            if (hornB == null) return false;

            if (!TryAssignBullhornTangencies(
                    hornA, mainA, branchAngle,
                    out Point3d mainPointA, out Point3d branchPointA) ||
                !TryAssignBullhornTangencies(
                    hornB, mainB, branchAngle,
                    out Point3d mainPointB, out Point3d branchPointB))
                return false;

            Vector3d mainAxis = new Vector3d(Math.Cos(mainA), Math.Sin(mainA), 0.0);
            Vector3d branchDir = new Vector3d(Math.Cos(branchAngle), Math.Sin(branchAngle), 0.0);
            mainAxis = SafeNormal(mainAxis, Vector3d.XAxis);
            branchDir = SafeNormal(branchDir, new Vector3d(-mainAxis.Y, mainAxis.X, 0.0));

            // Hai sừng bò phải nằm ở hai phía dọc tuyến chính; nếu cùng một phía thì đã bắt nhầm curve.
            double mainProjectionA = (mainPointA - center).DotProduct(mainAxis);
            double mainProjectionB = (mainPointB - center).DotProduct(mainAxis);
            if (mainProjectionA * mainProjectionB > 0.25)
                return false;

            // Chọn pháp tuyến tuyến chính hướng về phía đối diện đường nhánh.
            Vector3d across = new Vector3d(-mainAxis.Y, mainAxis.X, 0.0);
            if (across.DotProduct(branchDir) > 0.0)
                across = across.Negate();
            across = SafeNormal(across, branchDir.Negate());

            RayHit? farA = FindNearestParallelRayHit(
                mainPointA, across, selectedEdges, mainAxis, TJunctionMinAcrossDistance);
            RayHit? farB = FindNearestParallelRayHit(
                mainPointB, across, selectedEdges, mainAxis, TJunctionMinAcrossDistance);
            if (farA == null || farB == null)
                return false;

            if (farA.Distance < MinRoadWidth || farA.Distance > MaxRoadWidth ||
                farB.Distance < MinRoadWidth || farB.Distance > MaxRoadWidth)
                return false;

            double averageWidth = (farA.Distance + farB.Distance) * 0.5;
            double allowedDifference = Math.Max(
                TJunctionMaxWidthDifferenceAbs,
                averageWidth * TJunctionMaxWidthDifferenceRatio);
            if (Math.Abs(farA.Distance - farB.Distance) > allowedDifference)
                return false;

            // Hai điểm mép đối diện phải thực sự nằm về phía đối diện nhánh.
            double farSideA = (farA.Point - center).DotProduct(branchDir);
            double farSideB = (farB.Point - center).DotProduct(branchDir);
            if (farSideA >= -PointMergeTolerance || farSideB >= -PointMergeTolerance)
                return false;

            // Hai điểm tiếp tuyến ở phía đường nhánh không nên nằm sâu sang phía đối diện.
            double branchSideA = (branchPointA - center).DotProduct(branchDir);
            double branchSideB = (branchPointB - center).DotProduct(branchDir);
            if (branchSideA < -0.50 || branchSideB < -0.50)
                return false;

            if (farA.Point.DistanceTo(farB.Point) <= Math.Max(0.50, PointMergeTolerance * 4.0))
                return false;

            double farProjectionA = (farA.Point - center).DotProduct(mainAxis);
            double farProjectionB = (farB.Point - center).DotProduct(mainAxis);
            if (farProjectionA * farProjectionB > 0.25)
                return false;

            List<Point3d> raw = new List<Point3d>();
            raw.AddRange(hornA.Points);
            raw.Add(farA.Point);
            raw.Add(farB.Point);
            raw.AddRange(hornB.Points);

            List<Point3d> ordered = PreparePolygonPoints(center, raw);
            if (!IsValidTJunctionPolygon(center, ordered, branchDir, farA.Point, farB.Point))
                return false;

            points = ordered;
            edgeHandles = new[]
                {
                    hornA.SourceHandle,
                    hornB.SourceHandle,
                    farA.EdgeHandle,
                    farB.EdgeHandle
                }
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            return true;
        }

        private bool TryGetTJunctionDirections(
            List<double> approaches,
            out double mainA,
            out double mainB,
            out double branch)
        {
            mainA = 0.0;
            mainB = 0.0;
            branch = 0.0;
            if (approaches == null || approaches.Count != 3)
                return false;

            double tolerance = TJunctionOppositeToleranceDeg * Math.PI / 180.0;
            double bestError = double.MaxValue;
            int bestI = -1;
            int bestJ = -1;

            for (int i = 0; i < approaches.Count; i++)
            {
                for (int j = i + 1; j < approaches.Count; j++)
                {
                    double separation = CircularAngleDifference(approaches[i], approaches[j]);
                    double error = Math.Abs(Math.PI - separation);
                    if (error < bestError)
                    {
                        bestError = error;
                        bestI = i;
                        bestJ = j;
                    }
                }
            }

            if (bestI < 0 || bestJ < 0 || bestError > tolerance)
                return false;

            mainA = NormalizeAngle(approaches[bestI]);
            mainB = NormalizeAngle(approaches[bestJ]);
            for (int k = 0; k < approaches.Count; k++)
            {
                if (k == bestI || k == bestJ) continue;
                branch = NormalizeAngle(approaches[k]);
                break;
            }

            // Đường nhánh phải tạo góc có ý nghĩa với tuyến chính; tránh nhận nhầm 3 hướng gần thẳng hàng.
            double branchVsMain = LineAngleDifference(branch, mainA);
            return branchVsMain >= 25.0 * Math.PI / 180.0;
        }

        private BullhornSegment? SelectTJunctionBullhorn(
            Point3d center,
            double mainAngle,
            double branchAngle,
            List<BullhornSegment> candidates,
            HashSet<BullhornSegment> used)
        {
            BullhornSegment? best = null;
            double bestScore = double.MaxValue;
            double tangentLimit = MaxTangentMismatchDeg * Math.PI / 180.0;

            foreach (BullhornSegment candidate in candidates ?? new List<BullhornSegment>())
            {
                if (candidate == null || used.Contains(candidate)) continue;

                double midAngle = NormalizeAngle(AngleFrom(center, candidate.Mid));
                if (!IsAngleInsideMinorSector(midAngle, mainAngle, branchAngle, 2.0 * Math.PI / 180.0))
                    continue;

                double ts = NormalizeAngle(AngleOf(candidate.StartTangent));
                double te = NormalizeAngle(AngleOf(candidate.EndTangent));
                double assignmentA = LineAngleDifference(ts, mainAngle) + LineAngleDifference(te, branchAngle);
                double assignmentB = LineAngleDifference(te, mainAngle) + LineAngleDifference(ts, branchAngle);
                double tangentMismatch = Math.Min(assignmentA, assignmentB);
                if (tangentMismatch > tangentLimit * 2.0)
                    continue;

                double sectorMid = MinorSectorMidAngle(mainAngle, branchAngle);
                double radialPenalty = CircularAngleDifference(midAngle, sectorMid);
                double distancePenalty = candidate.Mid.DistanceTo(center) * 0.025;
                double turnPenalty = candidate.TurnRadians < MinCurveTurnDeg * Math.PI / 180.0 ? 10.0 : 0.0;
                double score = tangentMismatch * 3.0 + radialPenalty * 0.30 + distancePenalty + turnPenalty;

                if (score < bestScore)
                {
                    bestScore = score;
                    best = candidate;
                }
            }

            return best;
        }

        private bool TryAssignBullhornTangencies(
            BullhornSegment horn,
            double mainAngle,
            double branchAngle,
            out Point3d mainPoint,
            out Point3d branchPoint)
        {
            mainPoint = Point3d.Origin;
            branchPoint = Point3d.Origin;
            if (horn == null) return false;

            double startAngle = NormalizeAngle(AngleOf(horn.StartTangent));
            double endAngle = NormalizeAngle(AngleOf(horn.EndTangent));
            double optionA = LineAngleDifference(startAngle, mainAngle) + LineAngleDifference(endAngle, branchAngle);
            double optionB = LineAngleDifference(endAngle, mainAngle) + LineAngleDifference(startAngle, branchAngle);
            double limit = MaxTangentMismatchDeg * Math.PI / 180.0 * 2.0;

            if (Math.Min(optionA, optionB) > limit)
                return false;

            if (optionA <= optionB)
            {
                mainPoint = horn.Start;
                branchPoint = horn.End;
            }
            else
            {
                mainPoint = horn.End;
                branchPoint = horn.Start;
            }
            return true;
        }

        private RayHit? FindNearestParallelRayHit(
            Point3d origin,
            Vector3d direction,
            List<EdgeRef> edges,
            Vector3d requiredParallelDirection,
            double minDistance)
        {
            if (direction.Length <= 1e-9 || requiredParallelDirection.Length <= 1e-9)
                return null;

            Vector3d dir = ToXY(direction).GetNormal();
            Vector3d parallel = ToXY(requiredParallelDirection).GetNormal();
            Point3d end = origin + dir * MaxRayLength;
            RayHit? best = null;
            double parallelTolerance = TJunctionEdgeParallelToleranceDeg * Math.PI / 180.0;

            using (var probe = new Line(origin, end))
            {
                foreach (EdgeRef edge in edges ?? new List<EdgeRef>())
                {
                    if (edge?.Curve == null || edge.Curve.IsErased) continue;

                    using (var hits = new Point3dCollection())
                    {
                        try
                        {
                            probe.IntersectWith(edge.Curve, Intersect.OnBothOperands, hits, IntPtr.Zero, IntPtr.Zero);
                        }
                        catch { continue; }

                        foreach (Point3d raw in hits)
                        {
                            Point3d point = ToXY(raw);
                            Vector3d v = point - origin;
                            double forward = v.DotProduct(dir);
                            if (forward < minDistance || forward > MaxRayLength) continue;

                            double lateral = Math.Abs(dir.X * v.Y - dir.Y * v.X);
                            if (lateral > 0.05) continue;

                            if (!TryGetCurveTangentAtPoint(edge.Curve, point, out Vector3d tangent))
                                continue;
                            if (LineAngleDifference(AngleOf(tangent), AngleOf(parallel)) > parallelTolerance)
                                continue;

                            if (best == null || forward < best.Distance)
                            {
                                best = new RayHit
                                {
                                    Point = point,
                                    Distance = forward,
                                    EdgeHandle = edge.Handle
                                };
                            }
                        }
                    }
                }
            }
            return best;
        }

        private static bool TryGetCurveTangentAtPoint(Curve curve, Point3d point, out Vector3d tangent)
        {
            tangent = Vector3d.XAxis;
            if (curve == null) return false;
            try
            {
                Point3d closest = curve.GetClosestPointTo(ToXY(point), false);
                double param = curve.GetParameterAtPoint(closest);
                Vector3d derivative = ToXY(curve.GetFirstDerivative(param));
                if (derivative.Length <= 1e-9) return false;
                tangent = derivative.GetNormal();
                return true;
            }
            catch
            {
                try
                {
                    Vector3d chord = ToXY(curve.EndPoint - curve.StartPoint);
                    if (chord.Length <= 1e-9) return false;
                    tangent = chord.GetNormal();
                    return true;
                }
                catch { return false; }
            }
        }

        private bool IsValidTJunctionPolygon(
            Point3d center,
            List<Point3d> points,
            Vector3d branchDirection,
            Point3d farA,
            Point3d farB)
        {
            if (points == null || points.Count < 6) return false;
            if (!IsValidPolygon(center, points)) return false;

            Vector3d branch = SafeNormal(ToXY(branchDirection), Vector3d.YAxis);
            if ((farA - center).DotProduct(branch) >= -PointMergeTolerance) return false;
            if ((farB - center).DotProduct(branch) >= -PointMergeTolerance) return false;

            // Mép thẳng đối diện phải tạo được một cạnh có chiều dài hữu ích.
            if (farA.DistanceTo(farB) < 0.50) return false;
            return true;
        }

        private static bool IsAngleInsideMinorSector(double angle, double a, double b, double margin)
        {
            double delta = NormalizeSignedAngle(b - a);
            double rel = NormalizeSignedAngle(angle - a);
            if (Math.Abs(delta) <= margin * 2.0) return false;

            if (delta > 0.0)
                return rel > margin && rel < delta - margin;
            return rel < -margin && rel > delta + margin;
        }

        private static double MinorSectorMidAngle(double a, double b)
        {
            double delta = NormalizeSignedAngle(b - a);
            return NormalizeAngle(a + delta * 0.5);
        }

        private List<ThroatHit> CollectStableThroats(
            Point3d center,
            List<CurveProxy> tims,
            List<EdgeRef> edges)
        {
            var result = new List<ThroatHit>();
            foreach (CurveProxy tim in tims)
            {
                Curve curve = tim.Curve;
                try
                {
                    Point3d closest = ToXY(curve.GetClosestPointTo(center, false));
                    if (closest.DistanceTo(center) > TimAttachTolerance) continue;
                    double centerParam = curve.GetParameterAtPoint(curve.GetClosestPointTo(center, false));
                    double centerDistance = curve.GetDistanceAtParameter(centerParam);
                    double length = CurveLength(curve);
                    if (length <= 1e-6) continue;

                    double toStart = closest.DistanceTo(ToXY(curve.StartPoint));
                    double toEnd = closest.DistanceTo(ToXY(curve.EndPoint));
                    bool nearStart = toStart <= TimAttachTolerance * 1.5;
                    bool nearEnd = toEnd <= TimAttachTolerance * 1.5;

                    if (!nearEnd) AddThroatIfValid(result, FindStableThroat(curve, centerDistance, +1, edges));
                    if (!nearStart) AddThroatIfValid(result, FindStableThroat(curve, centerDistance, -1, edges));
                }
                catch { }
            }
            return result;
        }

        private static void AddThroatIfValid(List<ThroatHit> result, ThroatHit? hit)
        {
            if (hit == null) return;
            bool duplicate = result.Any(x =>
                x.LeftPoint.DistanceTo(hit.LeftPoint) <= PointMergeTolerance &&
                x.RightPoint.DistanceTo(hit.RightPoint) <= PointMergeTolerance);
            if (!duplicate) result.Add(hit);
        }

        private ThroatHit? FindStableThroat(Curve tim, double centerDistance, int directionSign, List<EdgeRef> edges)
        {
            double[] probes = { 2, 4, 6, 8, 10, 12, 15, 18, 22, 26, 32, 40, 50 };
            var candidates = new List<ThroatHit>();
            double length = CurveLength(tim);

            foreach (double fromCenter in probes)
            {
                double targetDistance = centerDistance + directionSign * fromCenter;
                if (targetDistance <= 0.05 || targetDistance >= length - 0.05) continue;

                try
                {
                    double param = tim.GetParameterAtDistance(targetDistance);
                    Point3d origin = ToXY(tim.GetPointAtParameter(param));
                    Vector3d tangent = ToXY(tim.GetFirstDerivative(param));
                    if (tangent.Length <= 1e-9) continue;
                    tangent = tangent.GetNormal();

                    Vector3d outward = directionSign > 0 ? tangent : tangent.Negate();
                    Vector3d leftDir = new Vector3d(-outward.Y, outward.X, 0.0);
                    RayHit? left = FindNearestRayHit(origin, leftDir, edges);
                    RayHit? right = FindNearestRayHit(origin, leftDir.Negate(), edges);
                    if (left == null || right == null) continue;

                    double width = left.Distance + right.Distance;
                    if (width < MinRoadWidth || width > MaxRoadWidth) continue;

                    candidates.Add(new ThroatHit
                    {
                        LeftPoint = left.Point,
                        RightPoint = right.Point,
                        LeftHandle = left.EdgeHandle,
                        RightHandle = right.EdgeHandle,
                        Width = width,
                        DistanceFromCenter = fromCenter
                    });
                }
                catch { }
            }

            if (candidates.Count == 0) return null;
            double minWidth = candidates.Min(x => x.Width);
            double allowed = Math.Max(0.75, minWidth * 0.15);
            return candidates
                .Where(x => x.Width <= minWidth + allowed)
                .OrderBy(x => x.DistanceFromCenter)
                .ThenBy(x => x.Width)
                .FirstOrDefault();
        }

        private RayHit? FindNearestRayHit(Point3d origin, Vector3d direction, List<EdgeRef> edges)
        {
            if (direction.Length <= 1e-9) return null;
            Vector3d dir = ToXY(direction).GetNormal();
            Point3d end = origin + dir * MaxRayLength;
            RayHit? best = null;

            using (var probe = new Line(origin, end))
            {
                foreach (EdgeRef edge in edges)
                {
                    using (var points = new Point3dCollection())
                    {
                        try { probe.IntersectWith(edge.Curve, Intersect.OnBothOperands, points, IntPtr.Zero, IntPtr.Zero); }
                        catch { continue; }

                        foreach (Point3d raw in points)
                        {
                            Point3d p = ToXY(raw);
                            Vector3d v = p - origin;
                            double forward = v.DotProduct(dir);
                            if (forward <= 1e-5 || forward > MaxRayLength) continue;
                            double lateral = Math.Abs(dir.X * v.Y - dir.Y * v.X);
                            if (lateral > 0.05) continue;
                            if (best == null || forward < best.Distance)
                                best = new RayHit { Point = p, Distance = forward, EdgeHandle = edge.Handle };
                        }
                    }
                }
            }
            return best;
        }

        private List<Point3d> PreparePolygonPoints(Point3d center, IEnumerable<Point3d> raw)
        {
            List<Point3d> cleaned = MergeNearbyPoints(raw.Select(ToXY).ToList(), PointMergeTolerance);
            return cleaned
                .OrderBy(p => NormalizeAngle(AngleFrom(center, p)))
                .ThenBy(p => p.DistanceTo(center))
                .ToList();
        }

        /// <summary>
        /// Xác định chính xác những cạnh polygon nào là biên đường thật. Mỗi cạnh được
        /// so khớp bằng nhiều điểm nội bộ với các MÉP đã chọn; vì kiểm tra toàn đoạn nên
        /// các cạnh nhân tạo nối cổ nút (chỉ chạm MÉP ở endpoint) sẽ tự bị loại.
        /// </summary>
        private List<BoundaryRun> BuildBoundaryRuns(PolygonDraft draft, List<EdgeRef> selectedEdges)
        {
            var runs = new List<BoundaryRun>();
            if (draft == null || draft.Points == null || draft.Points.Count < 2 ||
                selectedEdges == null || selectedEdges.Count == 0)
                return runs;

            var knownHandles = new HashSet<string>(
                (draft.EdgeHandles ?? new List<string>())
                    .Where(x => !string.IsNullOrWhiteSpace(x)),
                StringComparer.OrdinalIgnoreCase);

            List<EdgeRef> candidates = knownHandles.Count > 0
                ? selectedEdges.Where(x => x != null && knownHandles.Contains(x.Handle)).ToList()
                : selectedEdges.Where(x => x != null).ToList();

            if (candidates.Count == 0)
                candidates = selectedEdges.Where(x => x != null).ToList();

            var bullhornSet = new HashSet<string>(
                draft.BullhornHandles ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
            var oppositeSet = new HashSet<string>(
                draft.OppositeEdgeHandles ?? new List<string>(), StringComparer.OrdinalIgnoreCase);

            // Polygon được dựng từ giao/tiếp điểm CAD nhưng Polyline/Alignment proxy có thể
            // sai số xấp xỉ vài chục cm sau chuyển đổi. 0.50 m vẫn đủ nhỏ để không nhận cạnh
            // cổ nút vì các cạnh đó chỉ chạm MÉP tại endpoint, trong khi kiểm tra dùng 5 mẫu nội bộ.
            const double matchTolerance = 0.50;
            int count = draft.Points.Count;
            for (int i = 0; i < count; i++)
            {
                Point3d a = draft.Points[i];
                Point3d b = draft.Points[(i + 1) % count];
                double bulge = draft.Bulges != null && i < draft.Bulges.Count
                    ? draft.Bulges[i]
                    : 0.0;

                using (var boundary = new Polyline(2))
                {
                    boundary.AddVertexAt(0, new Point2d(a.X, a.Y), bulge, 0.0, 0.0);
                    boundary.AddVertexAt(1, new Point2d(b.X, b.Y), 0.0, 0.0, 0.0);

                    EdgeRef? best = null;
                    double bestMaxDistance = double.MaxValue;
                    double bestAverageDistance = double.MaxValue;

                    foreach (EdgeRef candidate in candidates)
                    {
                        if (candidate?.Curve == null || candidate.Curve.IsErased) continue;

                        double maxDistance = 0.0;
                        double sumDistance = 0.0;
                        int valid = 0;
                        bool failed = false;

                        foreach (double fraction in new[] { 0.12, 0.30, 0.50, 0.70, 0.88 })
                        {
                            try
                            {
                                Point3d sample = CurveGeometryHelper.PointAtFraction(boundary, fraction);
                                Point3d closest = candidate.Curve.GetClosestPointTo(sample, false);
                                double d = ToXY(sample).DistanceTo(ToXY(closest));
                                maxDistance = Math.Max(maxDistance, d);
                                sumDistance += d;
                                valid++;
                            }
                            catch
                            {
                                failed = true;
                                break;
                            }
                        }

                        if (failed || valid == 0 || maxDistance > matchTolerance)
                            continue;

                        double average = sumDistance / valid;
                        if (maxDistance < bestMaxDistance - 1e-9 ||
                            (Math.Abs(maxDistance - bestMaxDistance) <= 1e-9 && average < bestAverageDistance))
                        {
                            best = candidate;
                            bestMaxDistance = maxDistance;
                            bestAverageDistance = average;
                        }
                    }

                    if (best == null || string.IsNullOrWhiteSpace(best.Handle))
                        continue;

                    bool curvedBoundary = Math.Abs(bulge) > 1e-8;
                    bool isTJunction = string.Equals(
                        draft.Mode,
                        "AUTO_T_JUNCTION_2_BULLHORN_1_STRAIGHT",
                        StringComparison.OrdinalIgnoreCase);

                    string role = oppositeSet.Contains(best.Handle)
                        ? "OPPOSITE_EDGE"
                        : bullhornSet.Contains(best.Handle) || curvedBoundary
                            ? "BULLHORN_EDGE"
                            : isTJunction
                                // Ở topology T chuyên biệt, cạnh thẳng nào match MÉP CAD trên
                                // toàn chiều dài chính là mép tuyến chính phía đối diện. Ba cạnh
                                // còn lại là mặt cắt đóng polygon và không pass kiểm tra 5 mẫu.
                                ? "OPPOSITE_EDGE"
                                : "BOUNDARY_EDGE";

                    runs.Add(new BoundaryRun
                    {
                        SegmentIndex = i,
                        SourceHandle = best.Handle,
                        SourceSegmentIndex = FindBestSourceSegmentIndex(best.Curve, boundary),
                        Role = role
                    });
                }
            }

            // Một cạnh polygon chỉ được sinh một lần. Giữ thứ tự quanh polygon để việc
            // chẩn đoán và GenerationKey ổn định giữa các lần chạy.
            return runs
                .GroupBy(x => x.SegmentIndex)
                .Select(g => g.First())
                .OrderBy(x => x.SegmentIndex)
                .ToList();
        }

        private static string SerializeBoundaryRuns(IEnumerable<BoundaryRun> runs)
        {
            return string.Join(";", (runs ?? Enumerable.Empty<BoundaryRun>())
                .Where(x => x != null && !string.IsNullOrWhiteSpace(x.SourceHandle))
                .OrderBy(x => x.SegmentIndex)
                .Select(x => string.Join("|",
                    x.SegmentIndex.ToString(CultureInfo.InvariantCulture),
                    x.Role ?? "BOUNDARY_EDGE",
                    x.SourceHandle.Trim())));
        }

        private static string SerializeBoundaryRunsV3(IEnumerable<BoundaryRun> runs)
        {
            return string.Join(";", (runs ?? Enumerable.Empty<BoundaryRun>())
                .Where(x => x != null && !string.IsNullOrWhiteSpace(x.SourceHandle))
                .OrderBy(x => x.SegmentIndex)
                .Select(x => string.Join("|",
                    x.SegmentIndex.ToString(CultureInfo.InvariantCulture),
                    x.Role ?? "BOUNDARY_EDGE",
                    x.SourceHandle.Trim(),
                    x.SourceSegmentIndex.ToString(CultureInfo.InvariantCulture))));
        }

        /// <summary>
        /// Trả về index segment của chính Polyline MÉP CAD khớp nhất với một cạnh polygon.
        /// Đây là khóa hình học cần thiết để Bước 4 lấy vertex ARC/BULGE -> LINE trực tiếp
        /// trên MÉP nguồn, thay vì suy đoán từ vertex/bulge của polygon helper.
        /// Line/Arc độc lập trả -1 vì toàn entity chính là segment nguồn.
        /// </summary>
        private static int FindBestSourceSegmentIndex(Curve source, Curve boundary)
        {
            if (!(source is Polyline pl) || pl.NumberOfVertices < 2 || boundary == null)
                return -1;

            int segmentCount = pl.Closed ? pl.NumberOfVertices : pl.NumberOfVertices - 1;
            if (segmentCount <= 0) return -1;

            int bestIndex = -1;
            double bestMax = double.MaxValue;
            double bestAverage = double.MaxValue;

            for (int i = 0; i < segmentCount; i++)
            {
                int j = (i + 1) % pl.NumberOfVertices;
                try
                {
                    Point3d a = pl.GetPoint3dAt(i);
                    Point3d b = pl.GetPoint3dAt(j);
                    double bulge = pl.GetBulgeAt(i);

                    using (var segment = new Polyline(2))
                    {
                        segment.AddVertexAt(0, new Point2d(a.X, a.Y), bulge, 0.0, 0.0);
                        segment.AddVertexAt(1, new Point2d(b.X, b.Y), 0.0, 0.0, 0.0);

                        double maxDistance = 0.0;
                        double sum = 0.0;
                        int valid = 0;
                        foreach (double fraction in new[] { 0.15, 0.35, 0.50, 0.65, 0.85 })
                        {
                            Point3d sample = CurveGeometryHelper.PointAtFraction(boundary, fraction);
                            Point3d closest = segment.GetClosestPointTo(sample, false);
                            double d = ToXY(sample).DistanceTo(ToXY(closest));
                            maxDistance = Math.Max(maxDistance, d);
                            sum += d;
                            valid++;
                        }

                        if (valid == 0) continue;
                        double average = sum / valid;
                        if (maxDistance < bestMax - 1e-9 ||
                            (Math.Abs(maxDistance - bestMax) <= 1e-9 && average < bestAverage))
                        {
                            bestIndex = i;
                            bestMax = maxDistance;
                            bestAverage = average;
                        }
                    }
                }
                catch { }
            }

            return bestIndex;
        }

        /// <summary>
        /// Gán bulge thật của sừng bò lên cạnh polygon có đúng hai đầu tiếp tuyến.
        /// Nhờ vậy polygon bám theo cung curb-return thay vì dùng dây cung thẳng.
        /// </summary>
        private List<double> BuildBulgesForOrderedPoints(
            IReadOnlyList<Point3d> points,
            IEnumerable<BullhornSegment> horns)
        {
            var result = Enumerable.Repeat(0.0, points?.Count ?? 0).ToList();
            if (points == null || points.Count < 2 || horns == null) return result;

            List<BullhornSegment> list = horns.Where(x => x != null).ToList();
            double tolerance = Math.Max(0.20, PointMergeTolerance * 3.0);

            for (int i = 0; i < points.Count; i++)
            {
                Point3d from = points[i];
                Point3d to = points[(i + 1) % points.Count];

                foreach (BullhornSegment horn in list)
                {
                    if (horn.Points == null || horn.Bulges == null || horn.Points.Count < 2) continue;
                    
                    bool found = false;
                    for (int k = 0; k < horn.Points.Count - 1; k++)
                    {
                        Point3d hFrom = horn.Points[k];
                        Point3d hTo = horn.Points[k + 1];
                        double hBulge = horn.Bulges[k];

                        if (from.DistanceTo(hFrom) <= tolerance && to.DistanceTo(hTo) <= tolerance)
                        {
                            result[i] = hBulge;
                            found = true;
                            break;
                        }

                        if (from.DistanceTo(hTo) <= tolerance && to.DistanceTo(hFrom) <= tolerance)
                        {
                            result[i] = -hBulge;
                            found = true;
                            break;
                        }
                    }
                    if (found) break;
                }
            }

            return result;
        }

        private List<string> FindBullhornHandlesForOrderedPoints(
            IReadOnlyList<Point3d> points,
            IEnumerable<BullhornSegment> horns)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (points == null || points.Count < 2 || horns == null) return result.ToList();

            double tolerance = Math.Max(0.20, PointMergeTolerance * 3.0);
            List<BullhornSegment> list = horns.Where(x => x != null).ToList();
            for (int i = 0; i < points.Count; i++)
            {
                Point3d from = points[i];
                Point3d to = points[(i + 1) % points.Count];
                foreach (BullhornSegment horn in list)
                {
                    bool direct = from.DistanceTo(horn.Start) <= tolerance &&
                                  to.DistanceTo(horn.End) <= tolerance;
                    bool reverse = from.DistanceTo(horn.End) <= tolerance &&
                                   to.DistanceTo(horn.Start) <= tolerance;
                    if ((direct || reverse) && !string.IsNullOrWhiteSpace(horn.SourceHandle))
                        result.Add(horn.SourceHandle);
                }
            }
            return result.ToList();
        }

        private bool IsValidPolygon(Point3d center, List<Point3d> points)
        {
            if (points == null || points.Count < 4) return false;
            if (Math.Abs(SignedArea(points)) < MinPolygonArea) return false;
            if (HasSelfIntersection(points)) return false;
            if (!PointInPolygon(center, points)) return false;

            for (int i = 0; i < points.Count; i++)
            {
                if (points[i].DistanceTo(points[(i + 1) % points.Count]) <= PointMergeTolerance)
                    return false;
            }
            return true;
        }

        private static bool HasSelfIntersection(IReadOnlyList<Point3d> points)
        {
            int n = points.Count;
            for (int i = 0; i < n; i++)
            {
                Point3d a1 = points[i];
                Point3d a2 = points[(i + 1) % n];
                for (int j = i + 1; j < n; j++)
                {
                    if (Math.Abs(i - j) <= 1) continue;
                    if (i == 0 && j == n - 1) continue;
                    Point3d b1 = points[j];
                    Point3d b2 = points[(j + 1) % n];
                    if (SegmentsIntersect(a1, a2, b1, b2)) return true;
                }
            }
            return false;
        }

        private static bool SegmentsIntersect(Point3d a, Point3d b, Point3d c, Point3d d)
        {
            double o1 = Orientation(a, b, c);
            double o2 = Orientation(a, b, d);
            double o3 = Orientation(c, d, a);
            double o4 = Orientation(c, d, b);
            const double eps = 1e-9;
            return ((o1 > eps && o2 < -eps) || (o1 < -eps && o2 > eps)) &&
                   ((o3 > eps && o4 < -eps) || (o3 < -eps && o4 > eps));
        }

        private static double Orientation(Point3d a, Point3d b, Point3d c)
        {
            return (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
        }

        private static bool PointInPolygon(Point3d p, IReadOnlyList<Point3d> polygon)
        {
            bool inside = false;
            for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
            {
                Point3d a = polygon[i];
                Point3d b = polygon[j];
                bool crosses = ((a.Y > p.Y) != (b.Y > p.Y)) &&
                    (p.X < (b.X - a.X) * (p.Y - a.Y) / ((b.Y - a.Y) == 0.0 ? 1e-12 : (b.Y - a.Y)) + a.X);
                if (crosses) inside = !inside;
            }
            return inside;
        }

        private void EraseCurrentPolygons(Database db, Transaction tr, ArmProjectState state)
        {
            // State của Palette có thể giữ handle của polygon đã bị xóa bằng tay hoặc từ lần chạy trước.
            // Không mở trực tiếp ObjectId stale bằng GetObject(openErased:false), vì AutoCAD sẽ ném eWasErased.
            foreach (string handle in (state.IntersectionPolygonHandles ?? new List<string>())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!TryOpenHandleEntity(db, tr, handle, OpenMode.ForWrite, out AcEntity? entity) || entity == null)
                    continue;

                try
                {
                    if (!entity.IsErased)
                        entity.Erase(true);
                }
                catch
                {
                    // Idempotent delete: polygon đã erased/xóa tay được coi như đã xóa thành công.
                }
            }

            state.IntersectionPolygonHandles = new List<string>();
        }

        /// <summary>
        /// Mở entity từ handle một cách an toàn. Handle stale/erased/invalid được bỏ qua thay vì
        /// làm hỏng toàn bộ workflow bằng eWasErased/eNullObjectId/eInvalidInput.
        /// </summary>
        private bool TryOpenHandleEntity<T>(
            Database db,
            Transaction tr,
            string? handle,
            OpenMode mode,
            out T? entity) where T : DBObject
        {
            entity = null;
            if (db == null || tr == null || string.IsNullOrWhiteSpace(handle))
                return false;

            ObjectId id;
            try
            {
                id = _geometry.FromHandle(db, handle!);
            }
            catch
            {
                return false;
            }

            return TryOpenObject(tr, id, mode, out entity);
        }

        private static bool TryOpenObject<T>(
            Transaction tr,
            ObjectId id,
            OpenMode mode,
            out T? entity) where T : DBObject
        {
            entity = null;
            if (tr == null || id.IsNull)
                return false;

            try
            {
                if (!id.IsValid || id.IsErased)
                    return false;

                entity = tr.GetObject(id, mode, false, true) as T;
                if (entity == null || entity.IsErased)
                {
                    entity = null;
                    return false;
                }

                return true;
            }
            catch
            {
                entity = null;
                return false;
            }
        }

        private static bool HasUsableCurvature(Curve curve)
        {
            if (curve is Arc) return true;
            if (curve is Polyline pl)
            {
                int count = pl.Closed ? pl.NumberOfVertices : Math.Max(0, pl.NumberOfVertices - 1);
                for (int i = 0; i < count; i++)
                {
                    try { if (Math.Abs(pl.GetBulgeAt(i)) > 1e-8) return true; }
                    catch { }
                }
                return false;
            }

            try
            {
                Vector3d a = ToXY(curve.GetFirstDerivative(curve.StartParam));
                Vector3d b = ToXY(curve.GetFirstDerivative(curve.EndParam));
                if (a.Length <= 1e-9 || b.Length <= 1e-9) return false;
                double diff = LineAngleDifference(AngleOf(a), AngleOf(b));
                return diff >= MinCurveTurnDeg * Math.PI / 180.0;
            }
            catch { return false; }
        }

        private static double DistanceToCurve(Curve curve, Point3d point)
        {
            try { return ToXY(curve.GetClosestPointTo(ToXY(point), false)).DistanceTo(ToXY(point)); }
            catch { return double.MaxValue; }
        }

        private static double CurveLength(Curve curve)
        {
            try { return Math.Abs(curve.GetDistanceAtParameter(curve.EndParam) - curve.GetDistanceAtParameter(curve.StartParam)); }
            catch { try { return ToXY(curve.StartPoint).DistanceTo(ToXY(curve.EndPoint)); } catch { return 0.0; } }
        }

        private static List<Point3d> MergeNearbyPoints(List<Point3d> points, double tolerance)
        {
            var result = new List<Point3d>();
            foreach (Point3d p in points)
            {
                int index = result.FindIndex(x => x.DistanceTo(p) <= tolerance);
                if (index < 0)
                    result.Add(p);
                else
                    result[index] = Midpoint(result[index], p);
            }
            return result;
        }

        private static void AddUniquePoint(List<Point3d> points, Point3d point, double tolerance)
        {
            if (!points.Any(x => x.DistanceTo(point) <= tolerance))
                points.Add(ToXY(point));
        }

        private static void AddUniqueAngle(List<double> angles, double angle, double toleranceDeg)
        {
            double a = NormalizeAngle(angle);
            double tol = toleranceDeg * Math.PI / 180.0;
            if (!angles.Any(x => CircularAngleDifference(x, a) <= tol)) angles.Add(a);
        }

        private static double SignedArea(IReadOnlyList<Point3d> points)
        {
            double area2 = 0.0;
            for (int i = 0; i < points.Count; i++)
            {
                Point3d a = points[i];
                Point3d b = points[(i + 1) % points.Count];
                area2 += a.X * b.Y - b.X * a.Y;
            }
            return area2 * 0.5;
        }

        private static bool SamePointPair(Point3d a1, Point3d a2, Point3d b1, Point3d b2, double tolerance)
        {
            return (a1.DistanceTo(b1) <= tolerance && a2.DistanceTo(b2) <= tolerance) ||
                   (a1.DistanceTo(b2) <= tolerance && a2.DistanceTo(b1) <= tolerance);
        }

        private static Vector3d SafeNormal(Vector3d vector, Vector3d fallback)
        {
            if (vector.Length > 1e-9) return vector.GetNormal();
            if (fallback.Length > 1e-9) return fallback.GetNormal();
            return Vector3d.XAxis;
        }

        private static Point3d Midpoint(Point3d a, Point3d b) =>
            new Point3d((a.X + b.X) * 0.5, (a.Y + b.Y) * 0.5, 0.0);

        private static double AngleFrom(Point3d center, Point3d point) =>
            Math.Atan2(point.Y - center.Y, point.X - center.X);

        private static double AngleOf(Vector3d v) => Math.Atan2(v.Y, v.X);

        private static double NormalizeAngle(double angle)
        {
            double twoPi = Math.PI * 2.0;
            while (angle < 0.0) angle += twoPi;
            while (angle >= twoPi) angle -= twoPi;
            return angle;
        }

        private static double NormalizeSignedAngle(double angle)
        {
            while (angle <= -Math.PI) angle += Math.PI * 2.0;
            while (angle > Math.PI) angle -= Math.PI * 2.0;
            return angle;
        }

        private static double CircularAngleDifference(double a, double b) =>
            Math.Abs(NormalizeSignedAngle(a - b));

        private static double LineAngleDifference(double a, double b)
        {
            double d = CircularAngleDifference(a, b);
            return Math.Min(d, Math.Abs(Math.PI - d));
        }

        private static double Median(IEnumerable<double> values)
        {
            double[] data = values.OrderBy(x => x).ToArray();
            if (data.Length == 0) return 0.0;
            int mid = data.Length / 2;
            return data.Length % 2 == 1 ? data[mid] : (data[mid - 1] + data[mid]) * 0.5;
        }

        private static double Clamp(double value, double min, double max) => Math.Max(min, Math.Min(max, value));
        private static Point3d ToXY(Point3d p) => new Point3d(p.X, p.Y, 0.0);
        private static Vector3d ToXY(Vector3d v) => new Vector3d(v.X, v.Y, 0.0);

        private sealed class CurveProxy : IDisposable
        {
            public CurveProxy(string handle, Curve curve) { Handle = handle; Curve = curve; }
            public string Handle { get; }
            public Curve Curve { get; }
            public void Dispose() { Curve.Dispose(); }
        }

        private sealed class EdgeRef
        {
            public EdgeRef(ObjectId id, string handle, Curve curve) { Id = id; Handle = handle; Curve = curve; }
            public ObjectId Id { get; }
            public string Handle { get; }
            public Curve Curve { get; }
        }

        private sealed class BullhornSegment
        {
            public BullhornSegment(string sourceHandle, List<Point3d> points, List<double> bulges, Point3d mid, Vector3d startTangent, Vector3d endTangent, double turnRadians)
            {
                SourceHandle = sourceHandle;
                Points = points;
                Bulges = bulges;
                Start = points.First();
                End = points.Last();
                Mid = mid;
                StartTangent = startTangent;
                EndTangent = endTangent;
                TurnRadians = turnRadians;
                Bulge = bulges.FirstOrDefault();
            }
            public string SourceHandle { get; }
            public List<Point3d> Points { get; }
            public List<double> Bulges { get; }
            public Point3d Start { get; }
            public Point3d End { get; }
            public Point3d Mid { get; }
            public Vector3d StartTangent { get; }
            public Vector3d EndTangent { get; }
            public double TurnRadians { get; }
            public double Bulge { get; }
        }

        private sealed class RayHit
        {
            public Point3d Point { get; set; }
            public double Distance { get; set; }
            public string EdgeHandle { get; set; } = string.Empty;
        }

        private sealed class ThroatHit
        {
            public Point3d LeftPoint { get; set; }
            public Point3d RightPoint { get; set; }
            public string LeftHandle { get; set; } = string.Empty;
            public string RightHandle { get; set; } = string.Empty;
            public double Width { get; set; }
            public double DistanceFromCenter { get; set; }
        }

        private sealed class BoundaryRun
        {
            public int SegmentIndex { get; set; }
            public string SourceHandle { get; set; } = string.Empty;
            public int SourceSegmentIndex { get; set; } = -1;
            public string Role { get; set; } = "BOUNDARY_EDGE";
        }

        private sealed class PolygonDraft
        {
            public Point3d Center { get; set; }
            public List<Point3d> Points { get; set; } = new List<Point3d>();
            public List<double> Bulges { get; set; } = new List<double>();
            public List<string> EdgeHandles { get; set; } = new List<string>();
            public List<string> BullhornHandles { get; set; } = new List<string>();
            public List<string> OppositeEdgeHandles { get; set; } = new List<string>();
            public string NodeKey { get; set; } = string.Empty;
            public List<string> NodeTimHandles { get; set; } = new List<string>();
            public string Mode { get; set; } = string.Empty;
            public int ApproachCount { get; set; }
        }
    }
}
