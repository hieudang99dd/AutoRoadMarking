using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autoroadmarking_Pro.CadHost.Cad.Geometry;
using Autoroadmarking_Pro.CadHost.Cad.Intersections;
using Autoroadmarking_Pro.CadHost.Cad.Layers;
using Autoroadmarking_Pro.CadHost.Cad.Metadata;
using Autoroadmarking_Pro.CadHost.Cad.State;

namespace Autoroadmarking_Pro.CadHost.Cad.Markings
{
    /// <summary>
    /// Sinh vạch mép trong polygon nút giao.
    /// Thử cả hai phía offset, chấm điểm THEO CẢ NHÓM curve, ưu tiên nhóm thực sự
    /// đi vào polygon rồi clip chính xác theo biên. Polygon tự động có metadata
    /// EdgeHandles nên generator chỉ xử lý các MÉP liên quan đến đúng nút khi có thể.
    /// </summary>
    public sealed class EdgeMarkingGenerator
    {
        private const double ParamTolerance = 1e-6;
        private const double MinPhysicalMarkingWidth = 0.05;
        private const double MaxPhysicalMarkingWidth = 0.30;
        private double _requestedMarkingWidth = 0.15;
        private double _effectiveMarkingWidth = 0.15;
        private readonly CadGeometryService _geometry = new CadGeometryService();
        private readonly EntityMetadataStore _metadata = new EntityMetadataStore();
        private readonly MarkingLayerSynchronizer _layers = new MarkingLayerSynchronizer();

        public int Generate(Database db, Transaction tr, ArmProjectState state, string templateId, double offset, double markingWidthOverride = 0.0)
        {
            string requested = (templateId ?? string.Empty).Trim();
            ArmMarkingTemplateState? template = state.MarkingTemplates.FirstOrDefault(x =>
                    !string.IsNullOrWhiteSpace(requested) &&
                    (x.Id.Equals(requested, StringComparison.OrdinalIgnoreCase) ||
                     x.Code.Equals(requested, StringComparison.OrdinalIgnoreCase) ||
                     x.Layer.Equals(requested, StringComparison.OrdinalIgnoreCase)))
                ?? state.MarkingTemplates.FirstOrDefault(x => x.Code.Equals("3.1A", StringComparison.OrdinalIgnoreCase))
                ?? state.MarkingTemplates.FirstOrDefault(x => x.Code.StartsWith("3.1", StringComparison.OrdinalIgnoreCase))
                ?? state.MarkingTemplates.FirstOrDefault(x => x.Code.StartsWith("3.", StringComparison.OrdinalIgnoreCase));

            if (template == null)
                throw new InvalidOperationException("Không tìm thấy template vạch mép.");

            // Bề rộng vật lý của vạch mép được kiểm soát độc lập với offset.
            // UI có thể override Width từ Tab 1, nhưng backend luôn clamp để một template
            // sai dữ liệu không thể tạo Polyline quá dày trên mặt bằng.
            double requestedWidth = markingWidthOverride > 1e-9 ? markingWidthOverride : template.Width;
            if (requestedWidth <= 1e-9)
                throw new InvalidOperationException($"Template vạch mép {template.Code} đang có Width = 0 và UI chưa nhập bề rộng vạch.");

            _requestedMarkingWidth = requestedWidth;
            _effectiveMarkingWidth = Math.Max(
                MinPhysicalMarkingWidth,
                Math.Min(MaxPhysicalMarkingWidth, requestedWidth));

            double offsetDistance = Math.Abs(offset);
            if (offsetDistance <= 1e-9)
                throw new InvalidOperationException("Offset vạch mép phải lớn hơn 0.");

            BlockTableRecord modelSpace = (BlockTableRecord)tr.GetObject(
                SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForWrite);

            int createdCount = 0;
            int validPolygonCount = 0;
            int openedEdgeCount = 0;
            int offsetCandidateCount = 0;

            List<string> polygonHandles = (state.IntersectionPolygonHandles ?? new List<string>())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (polygonHandles.Count == 0)
                throw new InvalidOperationException("Bước 3 chưa có polygon nút giao. Hãy chạy BƯỚC 2 · VẼ ĐA GIÁC trước.");

            var clearedNodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var processedGenerationKeys = new HashSet<string>(StringComparer.Ordinal);
            int sourceSegmentCount = 0;

            foreach (string polygonHandle in polygonHandles)
            {
                ObjectId polygonId = _geometry.FromHandle(db, polygonHandle);
                if (polygonId.IsNull ||
                    !(tr.GetObject(polygonId, OpenMode.ForRead, false) is Polyline polygon) ||
                    !polygon.Closed || polygon.NumberOfVertices < 3)
                    continue;

                validPolygonCount++;
                Point3d polygonCenter = PolygonCentroid(polygon);
                string nodeKey = ResolveNodeKey(polygon, tr, polygonHandle);

                // QUAN TRỌNG: chỉ xóa vạch cũ một lần cho mỗi NodeKey. Trước đây lệnh xóa
                // nằm trong vòng lặp polygon; nếu DWG còn 2 polygon cùng một nút, polygon sau
                // có thể xóa chính vạch vừa sinh ở polygon trước nhưng createdCount vẫn > 0.
                if (clearedNodes.Add(nodeKey))
                    ErasePreviousPolygonEdgeMarkings(db, tr, nodeKey, polygonHandle);

                HashSet<string> associatedHandles = ReadMetadataHandles(polygon, tr, "EdgeHandles");
                HashSet<string> bullhornHandles = ReadMetadataHandles(polygon, tr, "BullhornHandles");
                HashSet<string> oppositeEdgeHandles = ReadMetadataHandles(polygon, tr, "OppositeEdgeHandles");
                List<string> selectedHandles = (state.SelectedEdgeHandles ?? new List<string>())
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                // Contract V2: Bước 2 đã xác định chính xác cạnh nào của polygon là MÉP CAD thật.
                // Đây là đường sinh ưu tiên tuyệt đối vì không còn phải suy đoán từ Handle của
                // cả Polyline dài. Mỗi run dùng đúng SegmentIndex của polygon, role nghiệp vụ và
                // SourceHandle để truy vết/khối lượng. Cạnh thẳng được kéo dài nhẹ trước offset,
                // sau đó hard-clip lại polygon để đoạn OPPOSITE_EDGE luôn chạm đúng hai cổ nút.
                List<BoundaryRunRef> boundaryRuns = ReadBoundaryRuns(polygon, tr);

                // Không coi BoundaryRunsV2 trong metadata là nguồn dữ liệu duy nhất. Một số DWG đã
                // tạo polygon bằng bản DLL cũ hoặc polygon grip-edit có thể thiếu OPPOSITE_EDGE dù
                // hình học CAD vẫn đầy đủ. Mỗi lần sinh vạch, tự đối chiếu từng cạnh polygon với
                // các MÉP CAD thật để bổ sung run còn thiếu. Các cạnh đóng polygon nhân tạo chỉ
                // chạm MÉP ở endpoint nên không vượt qua kiểm tra nhiều mẫu trên toàn cạnh.
                boundaryRuns = MergeBoundaryRuns(
                    boundaryRuns,
                    DiscoverBoundaryRunsFromCad(db, tr, state, polygon));

                string topologyV2 = ReadMetadataText(polygon, tr, "Topology");
                bool isTJunctionV2 = topologyV2.IndexOf("T_JUNCTION", StringComparison.OrdinalIgnoreCase) >= 0;
                bool expectsOppositeV2 = boundaryRuns.Any(x =>
                    string.Equals(x.Role, "OPPOSITE_EDGE", StringComparison.OrdinalIgnoreCase));

                // Chỉ dùng contract V2 khi đủ topology cần thiết. Nếu polygon chữ T cũ chưa thể
                // suy ra OPPOSITE_EDGE, không dừng lệnh ở đây: rơi xuống cơ chế Handle fallback
                // bên dưới để vẫn có cơ hội sinh từ MÉP thật đã chọn thay vì báo lỗi giả.
                bool useBoundaryRunsV2 = boundaryRuns.Count > 0 && (!isTJunctionV2 || expectsOppositeV2);
                if (useBoundaryRunsV2)
                {
                    int polygonOppositeCreatedV2 = 0;

                    foreach (BoundaryRunRef run in boundaryRuns)
                    {
                        using (Curve? sourceRun = BuildBoundaryRunCurve(
                            polygon, run.SegmentIndex, Math.Max(2.0, offsetDistance * 4.0)))
                        {
                            if (sourceRun == null)
                                continue;

                            sourceSegmentCount++;
                            openedEdgeCount++; // run đã được Bước 2 xác nhận từ MÉP CAD thật

                            ArmComparisonState comparison = ResolveRoadContext(
                                db, tr, state, run.SourceHandle, polygonCenter);
                            string roadName = !string.IsNullOrWhiteSpace(comparison.Road)
                                ? comparison.Road
                                : "GIAO_NUT";
                            string quantityRole = NormalizeBoundaryRole(run.Role);
                            string generationKey = string.Join(
                                "|", nodeKey, "EDGE", quantityRole, run.SourceHandle,
                                "RUN" + run.SegmentIndex.ToString(CultureInfo.InvariantCulture),
                                offsetDistance.ToString("0.###", CultureInfo.InvariantCulture), template.Id);

                            if (!processedGenerationKeys.Add(generationKey))
                                continue;

                            EraseByGeneration(db, tr, generationKey);
                            OffsetGroup? best = PickOffsetGroupTowardPolygon(
                                sourceRun, polygon, offsetDistance);
                            if (best == null)
                                continue;

                            offsetCandidateCount++;
                            try
                            {
                                ObjectId layerId = _layers.EnsureGeneratedLayer(db, tr, roadName, template);
                                EnsureLayerVisible(tr, layerId);

                                foreach (Curve candidate in best.Curves)
                                {
                                    int added = AppendInsidePolygon(
                                        tr, modelSpace, candidate, polygon, layerId,
                                        comparison, template, nodeKey, polygonHandle, generationKey,
                                        run.SourceHandle, quantityRole);
                                    createdCount += added;
                                    if (added > 0 && string.Equals(
                                        quantityRole, "OPPOSITE_EDGE", StringComparison.OrdinalIgnoreCase))
                                        polygonOppositeCreatedV2 += added;
                                }
                                best.Curves.Clear();
                            }
                            finally
                            {
                                best.Dispose();
                            }
                        }
                    }

                    if (expectsOppositeV2 && polygonOppositeCreatedV2 == 0)
                    {
                        throw new InvalidOperationException(
                            "Polygon đã xác định MÉP ĐỐI DIỆN nhưng hard-clip không tạo được đoạn vạch. " +
                            "Hãy tạo lại polygon Bước 2 để cập nhật BoundaryRunsV2; nếu vẫn lỗi, kiểm tra Offset có lớn hơn bề rộng vùng nút hay không.");
                    }

                    // Có contract V2 thì không chạy lại cơ chế Handle cũ để tránh sinh trùng hoặc
                    // bỏ sót do một Polyline nguồn chứa nhiều segment khác nhau.
                    continue;
                }

                // Không còn coi "vạch mép nút" = chỉ Bullhorn. Một polygon nút có thể có:
                //   - BULLHORN_EDGE: cung/đoạn cong sừng bò;
                //   - OPPOSITE_EDGE: mép thẳng hoặc cong phía đối diện (đặc biệt ngã ba T);
                //   - BOUNDARY_EDGE: mép thật dùng trong fallback ổn định.
                // EdgeHandles do Bước 2 ghi chỉ chứa MÉP CAD thật đã tham gia dựng polygon; các cạnh
                // đóng polygon nhân tạo (mặt cắt cổ nút) không có handle nên sẽ không bị sinh vạch.
                var edgeHandleSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string h in associatedHandles) edgeHandleSet.Add(h);
                foreach (string h in bullhornHandles) edgeHandleSet.Add(h);
                foreach (string h in oppositeEdgeHandles) edgeHandleSet.Add(h);

                // Luôn bổ sung MÉP đang chọn có tiếp xúc với polygon. Metadata của polygon cũ
                // có thể thiếu OppositeEdgeHandles; chỉ fallback khi set rỗng sẽ bỏ sót đúng mép
                // tuyến chính của ngã ba T. Segment extraction + hard-clip phía dưới sẽ loại các
                // phần không thực sự thuộc biên nút, nên bổ sung này an toàn hơn việc tin tuyệt đối
                // vào metadata stale.
                foreach (string h in selectedHandles.Where(h => HandleTouchesPolygon(db, tr, h, polygon)))
                    edgeHandleSet.Add(h);

                List<string> edgeHandles = edgeHandleSet.ToList();
                bool polygonHasExplicitBullhorns = bullhornHandles.Count > 0;
                int polygonOppositeCreated = 0;

                foreach (string edgeHandle in edgeHandles)
                {
                    ObjectId edgeId = _geometry.FromHandle(db, edgeHandle);
                    if (edgeId.IsNull || !(tr.GetObject(edgeId, OpenMode.ForRead, false) is Curve edge) || edge.IsErased)
                        continue;
                    openedEdgeCount++;

                    ArmComparisonState comparison = ResolveRoadContext(db, tr, state, edgeHandle, polygonCenter);
                    string roadName = !string.IsNullOrWhiteSpace(comparison.Road)
                        ? comparison.Road
                        : "GIAO_NUT";

                    // BullhornHandles chỉ xác định entity nguồn. Nếu nguồn là Polyline, entity đó
                    // thường gồm cả đoạn thẳng dài và đoạn bulge sừng bò. Không offset toàn entity:
                    // tách đúng từng segment cong gần biên polygon rồi offset riêng từng segment.
                    bool handleIsBullhorn = bullhornHandles.Contains(edgeHandle);
                    bool handleIsOpposite = oppositeEdgeHandles.Contains(edgeHandle);
                    // Chỉ ép "curved only" khi handle thuần sừng bò. Nếu cùng một entity vừa chứa
                    // sừng bò vừa chứa mép đối diện thì phải xét mọi segment và phân vai theo segment.
                    bool curvedOnly = polygonHasExplicitBullhorns && handleIsBullhorn && !handleIsOpposite;

                    using (GenerationSourceCollection sources = ExtractGenerationSources(
                        edge,
                        polygon,
                        curvedOnly,
                        offsetDistance))
                    {
                        foreach (GenerationSource sourceSegment in sources.Items)
                        {
                            sourceSegmentCount++;
                            string quantityRole = ResolveBoundaryRole(
                                sourceSegment.IsCurved,
                                handleIsBullhorn,
                                handleIsOpposite);
                            string generationKey = string.Join(
                                "|", nodeKey, "EDGE", quantityRole, edgeHandle, sourceSegment.Key,
                                offsetDistance.ToString("0.###", CultureInfo.InvariantCulture), template.Id);

                            if (!processedGenerationKeys.Add(generationKey))
                                continue;

                            EraseByGeneration(db, tr, generationKey);

                            OffsetGroup? best = PickOffsetGroupTowardPolygon(
                                sourceSegment.Curve,
                                polygon,
                                offsetDistance);
                            if (best == null)
                                continue;

                            offsetCandidateCount++;
                            try
                            {
                                ObjectId layerId = _layers.EnsureGeneratedLayer(db, tr, roadName, template);
                                EnsureLayerVisible(tr, layerId);

                                foreach (Curve candidate in best.Curves)
                                {
                                    int added = AppendInsidePolygon(
                                        tr, modelSpace, candidate, polygon, layerId,
                                        comparison, template, nodeKey, polygonHandle, generationKey,
                                        edgeHandle, quantityRole);
                                    createdCount += added;
                                    if (added > 0)
                                    {
                                        if (handleIsOpposite)
                                            polygonOppositeCreated += added;
                                    }
                                }
                                best.Curves.Clear(); // ownership đã chuyển/dispose trong AppendInsidePolygon
                            }
                            finally
                            {
                                best.Dispose();
                            }
                        }
                    }
                }

                // Với ngã ba T, mép đối diện là một phần bắt buộc của biên xe chạy. Không được
                // báo thành công một phần khi chỉ sinh được hai sừng bò. Nếu metadata đã chỉ ra
                // OppositeEdgeHandles mà không tạo được đoạn nào, dừng với lỗi chẩn đoán rõ ràng.
                if (oppositeEdgeHandles.Count > 0 && polygonOppositeCreated == 0)
                {
                    throw new InvalidOperationException(
                        "Đã sinh/đọc được sừng bò nhưng chưa sinh được MÉP ĐỐI DIỆN của nút chữ T. " +
                        "Kiểm tra EdgeHandles/OppositeEdgeHandles của polygon hoặc tạo lại polygon Bước 2.");
                }
            }

            if (validPolygonCount == 0)
                throw new InvalidOperationException("Không còn polygon nút giao hợp lệ trong DWG. Hãy bấm CẬP NHẬT ĐA GIÁC hoặc tạo lại ở Bước 2.");
            if (openedEdgeCount == 0)
                throw new InvalidOperationException("Không đọc được MÉP liên quan đến polygon. Hãy chọn lại đường MÉP rồi CẬP NHẬT ĐA GIÁC.");
            if (sourceSegmentCount == 0)
                throw new InvalidOperationException("Đã đọc được MÉP nhưng không tách được segment hình học thuộc biên nút. Hãy tạo lại polygon Bước 2 để cập nhật BullhornHandles.");
            if (offsetCandidateCount == 0)
                throw new InvalidOperationException("Đã đọc MÉP nhưng không tìm được phía offset đi vào polygon. Kiểm tra polygon có bao đúng phần xe chạy và Offset không quá lớn.");
            if (createdCount == 0)
                throw new InvalidOperationException("Đã tính được offset nhưng không có đoạn vạch nào nằm trong polygon. Kiểm tra biên polygon và vị trí MÉP tại nút giao.");

            int survivingCount = CountCurrentEdgeMarkings(db, tr, clearedNodes);
            if (survivingCount == 0)
                throw new InvalidOperationException("Generator đã tạo đối tượng tạm nhưng không còn vạch AUTO_EDGE nào tồn tại trước khi commit. Kiểm tra polygon trùng NodeKey hoặc thao tác xóa/sinh lại.");

            return survivingCount;
        }

        private GenerationSourceCollection ExtractGenerationSources(
            Curve edge,
            Polyline polygon,
            bool curvedOnly,
            double offsetDistance)
        {
            var result = new GenerationSourceCollection();
            double boundaryTolerance = Math.Max(0.25, Math.Min(1.50, offsetDistance * 1.5));

            if (edge is Polyline pl)
            {
                int n = pl.NumberOfVertices;
                int segmentCount = pl.Closed ? n : Math.Max(0, n - 1);
                for (int i = 0; i < segmentCount; i++)
                {
                    int j = (i + 1) % n;
                    double bulge = 0.0;
                    try { bulge = pl.GetBulgeAt(i); } catch { }
                    bool curved = Math.Abs(bulge) > 1e-8;
                    if (curvedOnly && !curved)
                        continue;

                    Polyline? segment = null;
                    try
                    {
                        segment = new Polyline(2)
                        {
                            Normal = pl.Normal,
                            Elevation = pl.Elevation
                        };
                        segment.AddVertexAt(0, pl.GetPoint2dAt(i), bulge, 0.0, 0.0);
                        segment.AddVertexAt(1, pl.GetPoint2dAt(j), 0.0, 0.0, 0.0);

                        if (!SourceCurveNearPolygon(segment, polygon, boundaryTolerance))
                            continue;

                        result.Items.Add(new GenerationSource(
                            curved ? "ARCSEG" + i.ToString(CultureInfo.InvariantCulture) : "LINESEG" + i.ToString(CultureInfo.InvariantCulture),
                            segment,
                            curved));
                        segment = null;
                    }
                    finally
                    {
                        segment?.Dispose();
                    }
                }

                // Polygon cũ có thể chỉ lưu handle mà không lưu đúng segment. Nếu không ở chế độ
                // bullhorn rõ ràng và không tách được segment nào, dùng clone toàn curve làm fallback.
                if (result.Items.Count == 0 && !curvedOnly)
                {
                    Curve? clone = edge.Clone() as Curve;
                    if (clone != null && SourceCurveNearPolygon(clone, polygon, boundaryTolerance))
                        result.Items.Add(new GenerationSource("WHOLE", clone, HandleCurveIsCurved(clone)));
                    else
                        clone?.Dispose();
                }

                return result;
            }

            Curve? sourceClone = edge.Clone() as Curve;
            if (sourceClone != null)
            {
                bool accept = SourceCurveNearPolygon(sourceClone, polygon, boundaryTolerance);
                if (accept)
                    result.Items.Add(new GenerationSource(edge is Arc ? "ARC" : "CURVE", sourceClone, HandleCurveIsCurved(sourceClone)));
                else
                    sourceClone.Dispose();
            }
            return result;
        }

        private static bool SourceCurveNearPolygon(Curve curve, Polyline polygon, double tolerance)
        {
            foreach (double fraction in new[] { 0.0, 0.25, 0.50, 0.75, 1.0 })
            {
                try
                {
                    Point3d point = CurveGeometryHelper.PointAtFraction(curve, fraction);
                    if (IntersectionTrimService.PointInPolygon(polygon, point))
                        return true;

                    Point3d onBoundary = polygon.GetClosestPointTo(point, false);
                    if (point.DistanceTo(onBoundary) <= tolerance)
                        return true;
                }
                catch { }
            }
            return false;
        }

        private List<BoundaryRunRef> ReadBoundaryRuns(Polyline polygon, Transaction tr)
        {
            var result = new List<BoundaryRunRef>();
            ArmEntityMetadata? md = _metadata.Read(polygon, tr);
            if (md == null || md.Extra == null ||
                !md.Extra.TryGetValue("BoundaryRunsV2", out string? text) ||
                string.IsNullOrWhiteSpace(text))
                return result;

            foreach (string token in text.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string[] parts = token.Split('|');
                if (parts.Length < 3) continue;
                if (!int.TryParse(parts[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int segmentIndex))
                    continue;
                if (segmentIndex < 0 || segmentIndex >= polygon.NumberOfVertices)
                    continue;

                string role = NormalizeBoundaryRole(parts[1]);
                string handle = parts[2].Trim();
                if (string.IsNullOrWhiteSpace(handle)) continue;

                result.Add(new BoundaryRunRef
                {
                    SegmentIndex = segmentIndex,
                    Role = role,
                    SourceHandle = handle
                });
            }

            return result
                .GroupBy(x => x.SegmentIndex)
                .Select(g => g.First())
                .OrderBy(x => x.SegmentIndex)
                .ToList();
        }

        private List<BoundaryRunRef> DiscoverBoundaryRunsFromCad(
            Database db,
            Transaction tr,
            ArmProjectState state,
            Polyline polygon)
        {
            var result = new List<BoundaryRunRef>();
            if (db == null || tr == null || state == null || polygon == null ||
                polygon.NumberOfVertices < 2)
                return result;

            var bullhornHandles = ReadMetadataHandles(polygon, tr, "BullhornHandles");
            var oppositeHandles = ReadMetadataHandles(polygon, tr, "OppositeEdgeHandles");
            var candidateHandles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string h in state.SelectedEdgeHandles ?? new List<string>())
                if (!string.IsNullOrWhiteSpace(h)) candidateHandles.Add(h.Trim());
            foreach (string h in ReadMetadataHandles(polygon, tr, "EdgeHandles"))
                candidateHandles.Add(h);
            foreach (string h in bullhornHandles) candidateHandles.Add(h);
            foreach (string h in oppositeHandles) candidateHandles.Add(h);

            if (candidateHandles.Count == 0)
                return result;

            var candidates = new List<Tuple<string, Curve>>();
            foreach (string handle in candidateHandles)
            {
                try
                {
                    ObjectId id = _geometry.FromHandle(db, handle);
                    if (id.IsNull || !id.IsValid || id.IsErased) continue;
                    Curve? curve = tr.GetObject(id, OpenMode.ForRead, false, true) as Curve;
                    if (curve == null || curve.IsErased) continue;
                    candidates.Add(Tuple.Create(handle, curve));
                }
                catch { }
            }

            if (candidates.Count == 0)
                return result;

            string topology = ReadMetadataText(polygon, tr, "Topology");
            bool isTJunction = topology.IndexOf("T_JUNCTION", StringComparison.OrdinalIgnoreCase) >= 0;
            const double matchTolerance = 0.45;

            int segmentCount = polygon.Closed
                ? polygon.NumberOfVertices
                : Math.Max(0, polygon.NumberOfVertices - 1);

            for (int i = 0; i < segmentCount; i++)
            {
                using (Curve? boundary = BuildBoundaryRunCurve(polygon, i, 0.0))
                {
                    if (boundary == null) continue;

                    string bestHandle = string.Empty;
                    double bestMax = double.MaxValue;
                    double bestAvg = double.MaxValue;

                    foreach (Tuple<string, Curve> candidate in candidates)
                    {
                        if (!TryMeasureBoundaryMatch(
                            boundary,
                            candidate.Item2,
                            matchTolerance,
                            out double maxDistance,
                            out double averageDistance))
                            continue;

                        if (maxDistance < bestMax - 1e-9 ||
                            (Math.Abs(maxDistance - bestMax) <= 1e-9 && averageDistance < bestAvg))
                        {
                            bestHandle = candidate.Item1;
                            bestMax = maxDistance;
                            bestAvg = averageDistance;
                        }
                    }

                    if (string.IsNullOrWhiteSpace(bestHandle))
                        continue;

                    double bulge = 0.0;
                    try { bulge = polygon.GetBulgeAt(i); } catch { }
                    bool curved = Math.Abs(bulge) > 1e-8;

                    string role;
                    if (oppositeHandles.Contains(bestHandle))
                        role = "OPPOSITE_EDGE";
                    else if (bullhornHandles.Contains(bestHandle))
                        role = "BULLHORN_EDGE";
                    else if (isTJunction && !curved)
                        // Với T-junction, cạnh thẳng duy nhất bám MÉP CAD trên toàn chiều dài
                        // chính là mép tuyến chính phía đối diện. Các đường cổ nút là cạnh nhân
                        // tạo và không match được nhiều sample với một MÉP CAD.
                        role = "OPPOSITE_EDGE";
                    else if (curved)
                        role = "BULLHORN_EDGE";
                    else
                        role = "BOUNDARY_EDGE";

                    result.Add(new BoundaryRunRef
                    {
                        SegmentIndex = i,
                        SourceHandle = bestHandle,
                        Role = role
                    });
                }
            }

            return result;
        }

        private static bool TryMeasureBoundaryMatch(
            Curve boundary,
            Curve source,
            double tolerance,
            out double maxDistance,
            out double averageDistance)
        {
            maxDistance = double.MaxValue;
            averageDistance = double.MaxValue;
            if (boundary == null || source == null) return false;

            double max = 0.0;
            double sum = 0.0;
            int valid = 0;

            // Không lấy endpoint vì cạnh cổ nút thường chỉ chạm MÉP đúng tại endpoint.
            foreach (double fraction in new[] { 0.12, 0.28, 0.50, 0.72, 0.88 })
            {
                try
                {
                    Point3d sample = CurveGeometryHelper.PointAtFraction(boundary, fraction);
                    Point3d closest = source.GetClosestPointTo(sample, false);
                    double distance = new Point3d(sample.X, sample.Y, 0.0)
                        .DistanceTo(new Point3d(closest.X, closest.Y, 0.0));
                    if (distance > tolerance)
                        return false;

                    max = Math.Max(max, distance);
                    sum += distance;
                    valid++;
                }
                catch
                {
                    return false;
                }
            }

            if (valid == 0) return false;
            maxDistance = max;
            averageDistance = sum / valid;
            return true;
        }

        private static List<BoundaryRunRef> MergeBoundaryRuns(
            IEnumerable<BoundaryRunRef>? stored,
            IEnumerable<BoundaryRunRef>? discovered)
        {
            int RolePriority(string role)
            {
                string value = (role ?? string.Empty).Trim().ToUpperInvariant();
                if (value == "OPPOSITE_EDGE") return 3;
                if (value == "BULLHORN_EDGE") return 2;
                return 1;
            }

            return (stored ?? Enumerable.Empty<BoundaryRunRef>())
                .Concat(discovered ?? Enumerable.Empty<BoundaryRunRef>())
                .Where(x => x != null && x.SegmentIndex >= 0 && !string.IsNullOrWhiteSpace(x.SourceHandle))
                .GroupBy(x => x.SegmentIndex)
                .Select(g => g
                    .OrderByDescending(x => RolePriority(x.Role))
                    .ThenBy(x => x.SourceHandle, StringComparer.OrdinalIgnoreCase)
                    .First())
                .OrderBy(x => x.SegmentIndex)
                .ToList();
        }

        private static string NormalizeBoundaryRole(string? role)
        {
            string value = (role ?? string.Empty).Trim().ToUpperInvariant();
            if (value == "BULLHORN_EDGE") return "BULLHORN_EDGE";
            if (value == "OPPOSITE_EDGE") return "OPPOSITE_EDGE";
            return "BOUNDARY_EDGE";
        }

        /// <summary>
        /// Tạo curve nguồn đúng theo một cạnh polygon đã được Bước 2 chứng minh bám MÉP thật.
        /// Cạnh thẳng được kéo dài hai đầu trước khi offset để đường offset chắc chắn cắt hai
        /// cạnh cổ nút; AppendInsidePolygon sau đó hard-clip về đúng polygon. Cạnh cong giữ
        /// nguyên bulge thật để không biến sừng bò thành dây cung.
        /// </summary>
        private static Curve? BuildBoundaryRunCurve(Polyline polygon, int segmentIndex, double straightExtension)
        {
            if (polygon == null || polygon.NumberOfVertices < 2 ||
                segmentIndex < 0 || segmentIndex >= polygon.NumberOfVertices)
                return null;

            int next = (segmentIndex + 1) % polygon.NumberOfVertices;
            Point2d a = polygon.GetPoint2dAt(segmentIndex);
            Point2d b = polygon.GetPoint2dAt(next);
            double bulge = 0.0;
            try { bulge = polygon.GetBulgeAt(segmentIndex); } catch { }

            if (Math.Abs(bulge) <= 1e-8)
            {
                Vector2d raw = b - a;
                if (raw.Length <= 1e-9) return null;
                Vector2d dir = raw.GetNormal();
                double extension = Math.Max(0.0, straightExtension);
                Point2d start = a - dir * extension;
                Point2d end = b + dir * extension;
                var lineRun = new Polyline(2)
                {
                    Normal = polygon.Normal,
                    Elevation = polygon.Elevation
                };
                lineRun.AddVertexAt(0, start, 0.0, 0.0, 0.0);
                lineRun.AddVertexAt(1, end, 0.0, 0.0, 0.0);
                return lineRun;
            }

            var curvedRun = new Polyline(2)
            {
                Normal = polygon.Normal,
                Elevation = polygon.Elevation
            };
            curvedRun.AddVertexAt(0, a, bulge, 0.0, 0.0);
            curvedRun.AddVertexAt(1, b, 0.0, 0.0, 0.0);
            return curvedRun;
        }

        private static void EnsureLayerVisible(Transaction tr, ObjectId layerId)
        {
            if (layerId.IsNull) return;
            try
            {
                if (tr.GetObject(layerId, OpenMode.ForWrite, false) is LayerTableRecord layer)
                {
                    layer.IsOff = false;
                    layer.IsLocked = false;
                    try { layer.IsFrozen = false; } catch { }
                }
            }
            catch { }
        }

        private int CountCurrentEdgeMarkings(Database db, Transaction tr, HashSet<string> nodeKeys)
        {
            if (nodeKeys == null || nodeKeys.Count == 0) return 0;
            BlockTableRecord modelSpace = (BlockTableRecord)tr.GetObject(
                SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForRead);
            int count = 0;
            foreach (ObjectId id in modelSpace)
            {
                if (!(tr.GetObject(id, OpenMode.ForRead, false) is Entity entity) || entity.IsErased)
                    continue;
                ArmEntityMetadata? md = _metadata.Read(entity, tr);
                if (md == null || !string.Equals(md.Source, "AUTO_EDGE", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (nodeKeys.Contains(md.OwnerId ?? string.Empty))
                    count++;
            }
            return count;
        }

        private ArmComparisonState ResolveRoadContext(
            Database db,
            Transaction tr,
            ArmProjectState state,
            string edgeHandle,
            Point3d polygonCenter)
        {
            List<ArmComparisonState> rows = state.ComparisonResults ?? new List<ArmComparisonState>();

            ArmComparisonState? exactMatched = rows.FirstOrDefault(x =>
                string.Equals(x.Status, "matched", StringComparison.OrdinalIgnoreCase) &&
                (string.Equals(x.LeftEdgeHandle, edgeHandle, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(x.RightEdgeHandle, edgeHandle, StringComparison.OrdinalIgnoreCase)));
            if (exactMatched != null) return exactMatched;

            ArmComparisonState? exactAny = rows.FirstOrDefault(x =>
                string.Equals(x.LeftEdgeHandle, edgeHandle, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(x.RightEdgeHandle, edgeHandle, StringComparison.OrdinalIgnoreCase));
            if (exactAny != null) return exactAny;

            ArmComparisonState? nearest = null;
            double bestDistance = double.MaxValue;
            foreach (ArmComparisonState row in rows
                .OrderByDescending(x => string.Equals(x.Status, "matched", StringComparison.OrdinalIgnoreCase)))
            {
                if (string.IsNullOrWhiteSpace(row.TimHandle)) continue;
                ObjectId timId = _geometry.FromHandle(db, row.TimHandle);
                if (timId.IsNull || !(tr.GetObject(timId, OpenMode.ForRead, false) is Curve tim) || tim.IsErased)
                    continue;

                try
                {
                    Point3d closest = tim.GetClosestPointTo(polygonCenter, false);
                    double distance = new Point3d(closest.X, closest.Y, 0.0)
                        .DistanceTo(new Point3d(polygonCenter.X, polygonCenter.Y, 0.0));
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        nearest = row;
                    }
                }
                catch { }
            }

            if (nearest != null) return nearest;

            // Polygon vẫn có thể được dựng từ MÉP hợp lệ trước khi người dùng chạy lại đối chiếu.
            // Cho phép sinh vạch trên layer nút giao thay vì âm thầm bỏ qua toàn bộ Bước 3.
            return new ArmComparisonState
            {
                Id = "INTERSECTION_" + edgeHandle,
                Road = "GIAO_NUT",
                RoadKey = "INTERSECTION",
                TimHandle = string.Empty,
                Mcn = string.Empty,
                Status = "geometry"
            };
        }

        private OffsetGroup? PickOffsetGroupTowardPolygon(Curve edge, Polyline polygon, double distance)
        {
            var groups = new List<OffsetGroup>();
            foreach (double signed in new[] { distance, -distance })
            {
                OffsetGroup? group = BuildOffsetGroup(edge, signed, polygon);
                if (group != null) groups.Add(group);
            }

            OffsetGroup? best = groups
                .Where(x => x.InsideRatio > 0.0 || x.Crossings > 0)
                .OrderByDescending(x => x.Score)
                .FirstOrDefault();

            foreach (OffsetGroup group in groups)
                if (!ReferenceEquals(group, best)) group.Dispose();
            return best;
        }

        private OffsetGroup? BuildOffsetGroup(Curve edge, double signedDistance, Polyline polygon)
        {
            DBObjectCollection? offsets = GetOffsetSafely(edge, signedDistance);
            if (offsets == null || offsets.Count == 0)
            {
                offsets?.Dispose();
                return null;
            }

            var group = new OffsetGroup { SignedDistance = signedDistance };
            try
            {
                // GetOffsetCurves trả về DBObject chưa thuộc Database. Không giữ trực tiếp
                // reference của object nằm trong DBObjectCollection rồi dispose collection:
                // tùy phiên bản AutoCAD, vòng đời native object có thể không còn an toàn.
                // Clone curve ra ownership riêng trước khi giải phóng collection.
                foreach (DBObject obj in offsets)
                {
                    if (!(obj is Curve source))
                        continue;

                    Curve? clone = source.Clone() as Curve;
                    if (clone != null)
                        group.Curves.Add(clone);
                }
            }
            finally
            {
                foreach (DBObject obj in offsets)
                    obj?.Dispose();
                offsets.Dispose();
            }

            if (group.Curves.Count == 0)
            {
                group.Dispose();
                return null;
            }

            Point3d center = PolygonCentroid(polygon);
            group.InsideRatio = group.Curves.Max(c => CurveInsideRatio(c, polygon));
            group.Crossings = group.Curves.Sum(c => CountBoundaryIntersections(c, polygon));
            group.CenterDistance = group.Curves.Min(c => DistanceToPoint(c, center));
            group.Score = group.InsideRatio * 1000.0 + Math.Min(8, group.Crossings) * 25.0 - group.CenterDistance * 0.001;
            return group;
        }

        private static DBObjectCollection? GetOffsetSafely(Curve edge, double signedDistance)
        {
            try
            {
                return edge.GetOffsetCurves(signedDistance);
            }
            catch
            {
                if (!(edge is Line line)) return null;
                Vector3d raw = new Vector3d(line.EndPoint.X - line.StartPoint.X, line.EndPoint.Y - line.StartPoint.Y, 0.0);
                if (raw.Length <= 1e-9) return null;
                Vector3d dir = raw.GetNormal();
                Vector3d shift = new Vector3d(-dir.Y, dir.X, 0.0) * signedDistance;
                return new DBObjectCollection
                {
                    new Line(
                        new Point3d(line.StartPoint.X + shift.X, line.StartPoint.Y + shift.Y, 0.0),
                        new Point3d(line.EndPoint.X + shift.X, line.EndPoint.Y + shift.Y, 0.0))
                };
            }
        }

        private int AppendInsidePolygon(
            Transaction tr,
            BlockTableRecord modelSpace,
            Curve source,
            Polyline polygon,
            ObjectId layerId,
            ArmComparisonState comparison,
            ArmMarkingTemplateState template,
            string nodeKey,
            string polygonHandle,
            string generationKey,
            string sourceEdgeHandle,
            string quantityRole)
        {
            if (source == null)
                return 0;

            using (var intersections = new Point3dCollection())
            {
                bool intersectionSucceeded = false;
                try
                {
                    source.IntersectWith(
                        polygon,
                        Intersect.OnBothOperands,
                        intersections,
                        IntPtr.Zero,
                        IntPtr.Zero);
                    intersectionSucceeded = true;
                }
                catch
                {
                    // HARD CLIP: nếu AutoCAD không xác định được giao biên thì tuyệt đối
                    // không append nguyên offset curve. Điều này ngăn vạch của tuyến chính
                    // lọt ra ngoài polygon khi IntersectWith gặp tolerance/hình học khó.
                    source.Dispose();
                    return 0;
                }

                if (!intersectionSucceeded)
                {
                    source.Dispose();
                    return 0;
                }

                // Không giao biên: curve liên tục chỉ được giữ nếu toàn bộ các mẫu kiểm tra
                // đều nằm trong polygon. Không còn fallback >= 50%.
                if (intersections.Count == 0)
                {
                    if (CurveIsStrictlyInside(source, polygon))
                        return AppendOne(
                            tr, modelSpace, source, layerId,
                            comparison, template, nodeKey, polygonHandle,
                            generationKey, sourceEdgeHandle, quantityRole);

                    source.Dispose();
                    return 0;
                }

                List<double> parameters = new List<double>();
                foreach (Point3d p in intersections)
                {
                    try
                    {
                        Point3d closest = source.GetClosestPointTo(p, false);
                        double param = source.GetParameterAtPoint(closest);
                        if (param > source.StartParam + ParamTolerance &&
                            param < source.EndParam - ParamTolerance)
                        {
                            parameters.Add(param);
                        }
                    }
                    catch { }
                }

                parameters = parameters
                    .OrderBy(x => x)
                    .Aggregate(new List<double>(), (list, value) =>
                    {
                        if (list.Count == 0 ||
                            Math.Abs(list[list.Count - 1] - value) > ParamTolerance)
                        {
                            list.Add(value);
                        }
                        return list;
                    });

                // Trường hợp giao điểm chỉ nằm đúng Start/End của source: không thể split,
                // nhưng vẫn có thể chứng minh source đã nằm hoàn toàn trong polygon.
                if (parameters.Count == 0)
                {
                    if (CurveIsStrictlyInside(source, polygon))
                        return AppendOne(
                            tr, modelSpace, source, layerId,
                            comparison, template, nodeKey, polygonHandle,
                            generationKey, sourceEdgeHandle, quantityRole);

                    source.Dispose();
                    return 0;
                }

                DBObjectCollection pieces;
                try
                {
                    pieces = source.GetSplitCurves(
                        new DoubleCollection(parameters.ToArray()));
                }
                catch
                {
                    // HARD CLIP: split thất bại thì không giữ nguyên source.
                    source.Dispose();
                    return 0;
                }
                source.Dispose();

                int count = 0;
                try
                {
                    foreach (DBObject obj in pieces)
                    {
                        if (!(obj is Curve piece))
                        {
                            obj.Dispose();
                            continue;
                        }

                        // Sau khi split tại toàn bộ giao điểm polygon, chỉ giữ mảnh được
                        // xác nhận nằm hoàn toàn trong vùng nút. Mảnh ngoài bị dispose.
                        if (!SegmentIsStrictlyInside(piece, polygon))
                        {
                            piece.Dispose();
                            continue;
                        }

                        count += AppendOne(
                            tr, modelSpace, piece, layerId,
                            comparison, template, nodeKey, polygonHandle,
                            generationKey, sourceEdgeHandle, quantityRole);
                    }
                }
                finally
                {
                    pieces.Dispose();
                }

                return count;
            }
        }

        private static bool SegmentIsStrictlyInside(Curve curve, Polyline polygon)
        {
            // Không lấy đúng endpoint vì endpoint sau split thường nằm trên biên polygon.
            // Tất cả các mẫu nội bộ phải nằm trong polygon; chỉ một mẫu ngoài là loại mảnh.
            double[] fractions = { 0.10, 0.25, 0.50, 0.75, 0.90 };
            int valid = 0;

            foreach (double fraction in fractions)
            {
                try
                {
                    Point3d p = CurveGeometryHelper.PointAtFraction(curve, fraction);
                    if (!IntersectionTrimService.PointInPolygon(polygon, p))
                        return false;
                    valid++;
                }
                catch
                {
                    return false;
                }
            }

            return valid == fractions.Length;
        }

        private static bool CurveIsStrictlyInside(Curve curve, Polyline polygon)
        {
            // Chỉ được gọi sau khi IntersectWith đã chạy thành công và trả 0 giao điểm.
            // Với một curve liên tục, nếu không cắt biên thì các mẫu nội bộ đồng nhất phía.
            // Dùng nhiều mẫu để bảo vệ thêm trước sai số native/tolerance.
            double[] fractions = { 0.05, 0.15, 0.30, 0.50, 0.70, 0.85, 0.95 };
            int valid = 0;

            foreach (double fraction in fractions)
            {
                try
                {
                    Point3d p = CurveGeometryHelper.PointAtFraction(curve, fraction);
                    if (!IntersectionTrimService.PointInPolygon(polygon, p))
                        return false;
                    valid++;
                }
                catch
                {
                    return false;
                }
            }

            return valid == fractions.Length;
        }

        private double CurveInsideRatio(Curve curve, Polyline polygon)
        {
            double length = CurveLength(curve);
            int samples = Math.Max(9, Math.Min(81, (int)Math.Ceiling(length / 1.0) + 1));
            int inside = 0;
            int valid = 0;
            for (int i = 0; i < samples; i++)
            {
                double fraction = samples == 1 ? 0.5 : (double)i / (samples - 1);
                try
                {
                    Point3d p = CurveGeometryHelper.PointAtFraction(curve, fraction);
                    if (IntersectionTrimService.PointInPolygon(polygon, p)) inside++;
                    valid++;
                }
                catch { }
            }
            return valid == 0 ? 0.0 : (double)inside / valid;
        }

        private static int CountBoundaryIntersections(Curve curve, Polyline polygon)
        {
            using (var points = new Point3dCollection())
            {
                try
                {
                    curve.IntersectWith(polygon, Intersect.OnBothOperands, points, IntPtr.Zero, IntPtr.Zero);
                    return points.Count;
                }
                catch { return 0; }
            }
        }

        private static double DistanceToPoint(Curve curve, Point3d point)
        {
            try { return curve.GetClosestPointTo(point, false).DistanceTo(point); }
            catch { return double.MaxValue / 4.0; }
        }

        private int AppendOne(
            Transaction tr,
            BlockTableRecord modelSpace,
            Curve source,
            ObjectId layerId,
            ArmComparisonState comparison,
            ArmMarkingTemplateState template,
            string nodeKey,
            string polygonHandle,
            string generationKey,
            string sourceEdgeHandle,
            string quantityRole)
        {
            Curve curve = NormalizeToWidthCapableCurve(source);
            ApplyPhysicalWidth(curve, _effectiveMarkingWidth);
            Entity entity = (Entity)curve;
            entity.LayerId = layerId;
            curve.LinetypeScale = template.LinetypeScale > 0.0 ? template.LinetypeScale : 1.0;

            modelSpace.AppendEntity(entity);
            tr.AddNewlyCreatedDBObject(entity, true);

            var metadata = new ArmEntityMetadata
            {
                RecordId = "MRK_" + Guid.NewGuid().ToString("N"),
                GenerationKey = generationKey,
                Source = "AUTO_EDGE",
                RoadName = comparison.Road,
                AxisKey = comparison.RoadKey,
                AxisHandle = comparison.TimHandle,
                RoadKey = comparison.RoadKey,
                OwnerType = "INTERSECTION",
                OwnerId = nodeKey,
                McnId = comparison.Mcn,
                MarkingCode = template.Code,
                TemplateId = template.Id,
                TemplateLayer = template.Layer,
                CadLayer = entity.Layer,
                Width = _effectiveMarkingWidth,
                Extra = new Dictionary<string, string>
                {
                    ["PaintRatio"] = template.PaintRatio.ToString("0.########", CultureInfo.InvariantCulture),
                    ["Pattern"] = template.Pattern ?? string.Empty,
                    ["QuantityCategory"] = "INTERSECTION_EDGE",
                    ["QuantityGroup"] = "VACH_MEP_NUT",
                    ["QuantityRole"] = quantityRole,
                    ["QuantityScope"] = "INTERSECTION",
                    ["NodeKey"] = nodeKey,
                    ["PolygonHandle"] = polygonHandle,
                    ["SourceEdgeHandle"] = sourceEdgeHandle ?? string.Empty,
                    ["BoundaryRole"] = quantityRole,
                    ["TemplateWidth"] = template.Width.ToString("0.########", CultureInfo.InvariantCulture),
                    ["RequestedMarkingWidth"] = _requestedMarkingWidth.ToString("0.########", CultureInfo.InvariantCulture),
                    ["EffectiveMarkingWidth"] = _effectiveMarkingWidth.ToString("0.########", CultureInfo.InvariantCulture)
                }
            };
            _metadata.Write(entity, tr, metadata);
            return 1;
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
                double total = Math.Abs(arc.TotalAngle);
                double bulge = Math.Tan(total / 4.0);
                if (arc.Normal.Z < 0.0) bulge = -bulge;
                var pl = new Polyline(2);
                pl.AddVertexAt(0, new Point2d(arc.StartPoint.X, arc.StartPoint.Y), bulge, 0.0, 0.0);
                pl.AddVertexAt(1, new Point2d(arc.EndPoint.X, arc.EndPoint.Y), 0.0, 0.0, 0.0);
                source.Dispose();
                return pl;
            }

            return source;
        }

        private static void ApplyPhysicalWidth(Curve curve, double width)
        {
            if (width <= 1e-9) return;
            if (curve is Polyline pl)
            {
                pl.ConstantWidth = width;
                pl.Plinegen = true;
            }
            else if (curve is Polyline2d pl2)
            {
                pl2.ConstantWidth = width;
                pl2.LinetypeGenerationOn = true;
            }
        }

        private static string ResolveBoundaryRole(bool segmentIsCurved, bool handleIsBullhorn, bool handleIsOpposite)
        {
            if (handleIsBullhorn && segmentIsCurved)
                return "BULLHORN_EDGE";
            if (handleIsOpposite)
                return "OPPOSITE_EDGE";
            if (handleIsBullhorn)
                return "BULLHORN_EDGE";
            return "BOUNDARY_EDGE";
        }

        private static bool HandleCurveIsCurved(Curve curve)
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
                Vector3d a = curve.GetFirstDerivative(curve.StartParam);
                Vector3d b = curve.GetFirstDerivative(curve.EndParam);
                if (a.Length <= 1e-9 || b.Length <= 1e-9) return false;
                double dot = Math.Max(-1.0, Math.Min(1.0, a.GetNormal().DotProduct(b.GetNormal())));
                return Math.Acos(Math.Abs(dot)) >= 3.0 * Math.PI / 180.0;
            }
            catch { return false; }
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

        private string ReadMetadataText(Polyline polygon, Transaction tr, string key)
        {
            ArmEntityMetadata? md = _metadata.Read(polygon, tr);
            if (md == null || md.Extra == null ||
                !md.Extra.TryGetValue(key, out string? text) || string.IsNullOrWhiteSpace(text))
                return string.Empty;
            return text.Trim();
        }

        private HashSet<string> ReadMetadataHandles(Polyline polygon, Transaction tr, string key)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            ArmEntityMetadata? md = _metadata.Read(polygon, tr);
            if (md == null || md.Extra == null ||
                !md.Extra.TryGetValue(key, out string? text) || string.IsNullOrWhiteSpace(text))
                return result;

            foreach (string value in text.Split(new[] { ',', ';', '|' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string handle = value.Trim();
                if (!string.IsNullOrWhiteSpace(handle)) result.Add(handle);
            }
            return result;
        }

        private bool HandleHasCurvature(Database db, Transaction tr, string handle)
        {
            ObjectId id = _geometry.FromHandle(db, handle);
            if (id.IsNull || !(tr.GetObject(id, OpenMode.ForRead, false) is Curve curve) || curve.IsErased)
                return false;

            if (curve is Arc) return true;
            if (curve is Polyline pl)
            {
                int segments = pl.Closed ? pl.NumberOfVertices : Math.Max(0, pl.NumberOfVertices - 1);
                for (int i = 0; i < segments; i++)
                {
                    try { if (Math.Abs(pl.GetBulgeAt(i)) > 1e-8) return true; }
                    catch { }
                }
                return false;
            }

            try
            {
                Vector3d a = curve.GetFirstDerivative(curve.StartParam);
                Vector3d b = curve.GetFirstDerivative(curve.EndParam);
                if (a.Length <= 1e-9 || b.Length <= 1e-9) return false;
                double dot = Math.Max(-1.0, Math.Min(1.0, a.GetNormal().DotProduct(b.GetNormal())));
                return Math.Acos(Math.Abs(dot)) >= 3.0 * Math.PI / 180.0;
            }
            catch { return false; }
        }

        private bool HandleTouchesPolygon(Database db, Transaction tr, string handle, Polyline polygon)
        {
            ObjectId id = _geometry.FromHandle(db, handle);
            if (id.IsNull || !(tr.GetObject(id, OpenMode.ForRead, false) is Curve curve) || curve.IsErased)
                return false;

            using (var points = new Point3dCollection())
            {
                try
                {
                    curve.IntersectWith(polygon, Intersect.OnBothOperands, points, IntPtr.Zero, IntPtr.Zero);
                    if (points.Count > 0) return true;
                }
                catch { }
            }

            try
            {
                Point3d p = curve.GetClosestPointTo(PolygonCentroid(polygon), false);
                return IntersectionTrimService.PointInPolygon(polygon, p);
            }
            catch { return false; }
        }

        private void ErasePreviousPolygonEdgeMarkings(Database db, Transaction tr, string nodeKey, string legacyPolygonHandle)
        {
            BlockTableRecord modelSpace = (BlockTableRecord)tr.GetObject(
                SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForRead);
            var ids = new List<ObjectId>();
            foreach (ObjectId id in modelSpace)
            {
                if (!(tr.GetObject(id, OpenMode.ForRead, false) is Entity entity) || entity.IsErased) continue;
                ArmEntityMetadata? md = _metadata.Read(entity, tr);
                if (md == null) continue;
                if (!string.Equals(md.Source, "AUTO_EDGE", StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.Equals(md.OwnerType, "INTERSECTION", StringComparison.OrdinalIgnoreCase)) continue;
                bool sameNode = string.Equals(md.OwnerId, nodeKey, StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(md.OwnerId, legacyPolygonHandle, StringComparison.OrdinalIgnoreCase);
                if (!sameNode) continue;
                ids.Add(id);
            }

            foreach (ObjectId id in ids)
            {
                if (tr.GetObject(id, OpenMode.ForWrite, false) is Entity entity && !entity.IsErased)
                    entity.Erase(true);
            }
        }

        private void EraseByGeneration(Database db, Transaction tr, string generationKey)
        {
            BlockTableRecord modelSpace = (BlockTableRecord)tr.GetObject(
                SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForRead);
            var ids = new List<ObjectId>();
            foreach (ObjectId id in modelSpace)
            {
                if (!(tr.GetObject(id, OpenMode.ForRead, false) is Entity entity)) continue;
                ArmEntityMetadata? md = _metadata.Read(entity, tr);
                if (md != null && md.GenerationKey.Equals(generationKey, StringComparison.Ordinal)) ids.Add(id);
            }
            foreach (ObjectId id in ids)
            {
                Entity entity = (Entity)tr.GetObject(id, OpenMode.ForWrite, false);
                entity.Erase(true);
            }
        }

        private static Point3d PolygonCentroid(Polyline polygon)
        {
            double area2 = 0.0, cx = 0.0, cy = 0.0;
            int count = polygon.NumberOfVertices;
            for (int i = 0; i < count; i++)
            {
                Point2d a = polygon.GetPoint2dAt(i);
                Point2d b = polygon.GetPoint2dAt((i + 1) % count);
                double f = a.X * b.Y - b.X * a.Y;
                area2 += f;
                cx += (a.X + b.X) * f;
                cy += (a.Y + b.Y) * f;
            }
            if (Math.Abs(area2) < 1e-12)
                return count > 0 ? polygon.GetPoint3dAt(0) : Point3d.Origin;
            return new Point3d(cx / (3.0 * area2), cy / (3.0 * area2), 0.0);
        }

        private static double CurveLength(Curve curve)
        {
            try { return Math.Abs(curve.GetDistanceAtParameter(curve.EndParam) - curve.GetDistanceAtParameter(curve.StartParam)); }
            catch { try { return curve.StartPoint.DistanceTo(curve.EndPoint); } catch { return 0.0; } }
        }

        private sealed class BoundaryRunRef
        {
            public int SegmentIndex { get; set; }
            public string SourceHandle { get; set; } = string.Empty;
            public string Role { get; set; } = "BOUNDARY_EDGE";
        }

        private sealed class GenerationSource : IDisposable
        {
            public GenerationSource(string key, Curve curve, bool isCurved)
            {
                Key = key ?? string.Empty;
                Curve = curve ?? throw new ArgumentNullException(nameof(curve));
                IsCurved = isCurved;
            }
            public string Key { get; }
            public Curve Curve { get; }
            public bool IsCurved { get; }
            public void Dispose() { Curve.Dispose(); }
        }

        private sealed class GenerationSourceCollection : IDisposable
        {
            public List<GenerationSource> Items { get; } = new List<GenerationSource>();
            public void Dispose()
            {
                foreach (GenerationSource item in Items) item.Dispose();
                Items.Clear();
            }
        }

        private sealed class OffsetGroup : IDisposable
        {
            public double SignedDistance { get; set; }
            public double InsideRatio { get; set; }
            public int Crossings { get; set; }
            public double CenterDistance { get; set; }
            public double Score { get; set; }
            public List<Curve> Curves { get; } = new List<Curve>();
            public void Dispose()
            {
                foreach (Curve curve in Curves) curve?.Dispose();
                Curves.Clear();
            }
        }
    }
}
