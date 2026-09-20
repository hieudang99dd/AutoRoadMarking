using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autoroadmarking_Pro.Application.Supplementary;
using Autoroadmarking_Pro.CadHost.Cad.Geometry;
using Autoroadmarking_Pro.CadHost.Cad.Metadata;
using Autoroadmarking_Pro.CadHost.Cad.Layers;
using Autoroadmarking_Pro.CadHost.Cad.RoadAxis;
using Autoroadmarking_Pro.CadHost.Cad.State;

namespace Autoroadmarking_Pro.CadHost.Cad.Supplementary
{
    /// <summary>
    /// CAD adapter cho Tab 5. Lập lịch station nằm ở Application; lớp này chỉ biến
    /// station thành hình học thực, ghi ARM metadata và bảo đảm sinh lại idempotent.
    /// </summary>
    public sealed class SupplementaryMarkingCadService
    {
        private readonly StationOffsetService _station = new StationOffsetService();
        private readonly EntityMetadataStore _metadata = new EntityMetadataStore();
        private readonly ArmMetadataMapper _mapper = new ArmMetadataMapper();
        private readonly StationDistributionPlanner _planner = new StationDistributionPlanner();
        private readonly RoadAxisCatalogService _axisCatalog = new RoadAxisCatalogService();
        private readonly MarkingLayerSynchronizer _layers = new MarkingLayerSynchronizer();

        public object SelectCurve(Editor editor, Transaction tr)
        {
            var options = new PromptEntityOptions("\nChọn đường biên: ");
            options.SetRejectMessage("\nĐối tượng phải là Curve.");
            options.AddAllowedClass(typeof(Curve), false);

            PromptEntityResult result = editor.GetEntity(options);
            if (result.Status != PromptStatus.OK)
                throw new InvalidOperationException("Đã hủy chọn đường biên.");

            var entity = (Entity)tr.GetObject(result.ObjectId, OpenMode.ForRead);
            return new
            {
                handle = result.ObjectId.Handle.ToString(),
                layer = entity.Layer,
                type = entity.GetType().Name
            };
        }

        public object SelectEntities(Editor editor, Transaction tr, bool blocksOnly = false)
        {
            PromptSelectionResult result = editor.GetSelection();
            if (result.Status != PromptStatus.OK)
                throw new InvalidOperationException("Đã hủy chọn đối tượng.");

            var list = new List<object>();
            foreach (ObjectId id in result.Value.GetObjectIds())
            {
                if (!(tr.GetObject(id, OpenMode.ForRead, false) is Entity entity))
                    continue;
                if (blocksOnly && !(entity is BlockReference))
                    continue;

                ArmEntityMetadata? md = _metadata.Read(entity, tr);
                list.Add(new
                {
                    handle = id.Handle.ToString(),
                    layer = entity.Layer,
                    type = entity.GetType().Name,
                    blockName = ReadBlockName(entity, tr),
                    isManaged = _mapper.IsValidManagedRecord(md),
                    armEntityId = md?.RecordId ?? string.Empty,
                    groupId = ReadExtra(md, "GroupId")
                });
            }

            return new { count = list.Count, items = list };
        }

        public object SelectStation(
            Editor editor,
            Database db,
            Transaction tr,
            ArmProjectState state,
            string roadKey,
            string prompt)
        {
            Entity axis = ResolveAxis(db, tr, state, roadKey);
            PromptPointResult pointResult = editor.GetPoint("\n" + (string.IsNullOrWhiteSpace(prompt) ? "Chọn vị trí trên tuyến: " : prompt));
            if (pointResult.Status != PromptStatus.OK)
                throw new InvalidOperationException("Đã hủy chọn lý trình.");

            if (!_station.TryStationOffset(axis, pointResult.Value, out double station, out double offset))
                throw new InvalidOperationException("Không chiếu được điểm chọn lên RoadAxis.");

            return new
            {
                station,
                offset,
                text = FormatStation(station),
                x = pointResult.Value.X,
                y = pointResult.Value.Y
            };
        }

        public object DrawManualPolyline(
            Editor editor,
            Database db,
            Transaction tr,
            string targetLayer)
        {
            var points = new List<Point3d>();
            PromptPointResult first = editor.GetPoint("\nĐiểm đầu vạch phát sinh: ");
            if (first.Status != PromptStatus.OK)
                throw new InvalidOperationException("Đã hủy vẽ vạch phát sinh.");
            points.Add(first.Value);

            while (true)
            {
                var options = new PromptPointOptions("\nĐiểm tiếp theo <Enter để kết thúc>: ")
                {
                    UseBasePoint = true,
                    BasePoint = points[points.Count - 1],
                    AllowNone = true
                };
                PromptPointResult next = editor.GetPoint(options);
                if (next.Status == PromptStatus.None)
                    break;
                if (next.Status != PromptStatus.OK)
                    throw new InvalidOperationException("Đã hủy vẽ vạch phát sinh.");
                points.Add(next.Value);
            }

            if (points.Count < 2)
                throw new InvalidOperationException("Cần ít nhất 2 điểm để tạo vạch.");

            BlockTableRecord modelSpace = (BlockTableRecord)tr.GetObject(
                SymbolUtilityServices.GetBlockModelSpaceId(db),
                OpenMode.ForWrite);

            var polyline = new Polyline(points.Count);
            for (int i = 0; i < points.Count; i++)
                polyline.AddVertexAt(i, new Point2d(points[i].X, points[i].Y), 0.0, 0.0, 0.0);

            if (!string.IsNullOrWhiteSpace(targetLayer))
                polyline.Layer = targetLayer;

            modelSpace.AppendEntity(polyline);
            tr.AddNewlyCreatedDBObject(polyline, true);

            return new
            {
                handle = polyline.ObjectId.Handle.ToString(),
                layer = polyline.Layer,
                type = polyline.GetType().Name,
                length = CurveGeometryHelper.Length(polyline)
            };
        }

        public object PreviewSpeedHump(
            Database db,
            Transaction tr,
            ArmProjectState state,
            string roadKey,
            string boundary1,
            string boundary2,
            string mode,
            double startStation,
            double endStation,
            double spacing,
            bool balanceRemainder,
            double anchorStation,
            int clusterDirectionSign,
            int clusterCount,
            double clusterOffset,
            double clusterSpacing,
            int barsPerCluster,
            double barSpacing,
            double stripWidth)
        {
            Entity axis = ResolveAxis(db, tr, state, roadKey);
            Curve firstBoundary = ResolveCurve(db, tr, boundary1);
            Curve secondBoundary = ResolveCurve(db, tr, boundary2);

            StationDistributionResult distribution = BuildStations(
                axis,
                mode,
                startStation,
                endStation,
                spacing,
                balanceRemainder,
                anchorStation,
                clusterDirectionSign,
                clusterCount,
                clusterOffset,
                clusterSpacing,
                barsPerCluster,
                barSpacing);

            double totalLength = 0.0;
            int valid = 0;
            var rejected = new List<double>(distribution.RejectedStations);

            foreach (double station in distribution.Stations)
            {
                if (TryCrossSection(axis, firstBoundary, secondBoundary, station, out Point3d a, out Point3d b))
                {
                    totalLength += a.DistanceTo(b);
                    valid++;
                }
                else
                {
                    rejected.Add(station);
                }
            }

            return new
            {
                stations = distribution.Stations,
                rejectedStations = rejected,
                count = valid,
                totalLength,
                totalArea = totalLength * Math.Max(0.0, stripWidth),
                effectiveSpacing = distribution.EffectiveSpacing,
                startMargin = distribution.StartMargin,
                endMargin = distribution.EndMargin,
                workingStart = distribution.WorkingStart,
                workingEnd = distribution.WorkingEnd,
                clippedToAxis = distribution.WasClippedToAxis,
                shortSegmentPolicyApplied = distribution.UsedShortSegmentPolicy,
                range = distribution.Stations.Count == 0
                    ? "—"
                    : $"{distribution.Stations.Min():0.00} → {distribution.Stations.Max():0.00}"
            };
        }

        public object GenerateSpeedHump(
            Database db,
            Transaction tr,
            ArmProjectState state,
            string roadKey,
            string roadName,
            string boundary1,
            string boundary2,
            string mode,
            double startStation,
            double endStation,
            double spacing,
            bool balanceRemainder,
            double anchorStation,
            int clusterDirectionSign,
            int clusterCount,
            double clusterOffset,
            double clusterSpacing,
            int barsPerCluster,
            double barSpacing,
            double stripWidth,
            string markingCode,
            string templateId,
            string targetLayer,
            string groupId,
            double paintRatio)
        {
            ArmRoadAxisState axisInfo =
                _axisCatalog.ResolveDescriptor(db, tr, state, roadKey);

            roadKey = axisInfo.EffectiveAxisKey;
            roadName = axisInfo.RoadName;

            Entity axis =
                _axisCatalog.ResolveEntity(db, tr, state, roadKey);

            Curve firstBoundary = ResolveCurve(db, tr, boundary1);
            Curve secondBoundary = ResolveCurve(db, tr, boundary2);

            StationDistributionResult distribution = BuildStations(
                axis,
                mode,
                startStation,
                endStation,
                spacing,
                balanceRemainder,
                anchorStation,
                clusterDirectionSign,
                clusterCount,
                clusterOffset,
                clusterSpacing,
                barsPerCluster,
                barSpacing);

            EraseManagedGroup(db, tr, groupId, generatedOnly: true);

            BlockTableRecord modelSpace = (BlockTableRecord)tr.GetObject(
                SymbolUtilityServices.GetBlockModelSpaceId(db),
                OpenMode.ForWrite);

            int created = 0;
            double totalLength = 0.0;
            var handles = new List<string>();

            ArmMarkingTemplateState? template =
                state.MarkingTemplates.FirstOrDefault(x =>
                    string.Equals(x.Id, templateId, StringComparison.OrdinalIgnoreCase));

            string resolvedLayer = targetLayer;
            if (template != null)
            {
                ObjectId layerId =
                    _layers.EnsureGeneratedLayer(
                        db,
                        tr,
                        roadName,
                        template,
                        user: false);

                resolvedLayer =
                    ((LayerTableRecord)tr.GetObject(layerId, OpenMode.ForRead)).Name;
            }

            foreach (double station in distribution.Stations)
            {
                if (!TryCrossSection(axis, firstBoundary, secondBoundary, station, out Point3d p1, out Point3d p2))
                    continue;

                var line = new Line(p1, p2);
                if (!string.IsNullOrWhiteSpace(resolvedLayer))
                    line.Layer = resolvedLayer;

                modelSpace.AppendEntity(line);
                tr.AddNewlyCreatedDBObject(line, true);

                ArmEntityMetadata meta = _mapper.CreateGeneratedMarking(
                    line,
                    $"SPEED_HUMP|{roadKey}|{groupId}|{station:0.###}",
                    "SUPPLEMENTARY_SPEED_HUMP",
                    roadName,
                    roadKey,
                    "ROAD",
                    groupId,
                    string.Empty,
                    markingCode,
                    templateId,
                    template?.Layer ?? targetLayer,
                    stripWidth,
                    station,
                    0.0);

                _mapper.BindAxis(meta, axisInfo);
                meta.CadLayer = line.Layer;

                meta.Extra["GroupId"] = groupId;
                meta.Extra["SupplementaryType"] = "speed_hump";
                meta.Extra["QuantityMode"] = "AREA";
                meta.Extra["PaintRatio"] = Clamp01(paintRatio).ToString("0.########", CultureInfo.InvariantCulture);
                _metadata.Write(line, tr, meta);

                created++;
                totalLength += p1.DistanceTo(p2);
                handles.Add(line.ObjectId.Handle.ToString());
            }

            double paintedLength = totalLength * Clamp01(paintRatio);
            double area = paintedLength * Math.Max(0.0, stripWidth);
            return new
            {
                count = created,
                totalLength,
                paintedLength,
                totalArea = area,
                groupId,
                handles,
                stations = distribution.Stations,
                rejectedStations = distribution.RejectedStations,
                workingStart = distribution.WorkingStart,
                workingEnd = distribution.WorkingEnd,
                shortSegmentPolicyApplied = distribution.UsedShortSegmentPolicy
            };
        }

        public object RegisterExisting(
            Database db,
            Transaction tr,
            ArmProjectState state,
            IEnumerable<string> handles,
            string roadKey,
            string roadName,
            string type,
            string markingCode,
            string templateId,
            double width,
            string groupId,
            double paintRatio,
            bool removeManagement = false)
        {
            ArmRoadAxisState axisInfo =
                _axisCatalog.ResolveDescriptor(db, tr, state, roadKey);

            roadKey = axisInfo.EffectiveAxisKey;
            roadName = axisInfo.RoadName;

            ArmMarkingTemplateState? template =
                state.MarkingTemplates.FirstOrDefault(x =>
                    string.Equals(x.Id, templateId, StringComparison.OrdinalIgnoreCase));

            int updated = 0;
            double totalLength = 0.0;
            double totalArea = 0.0;
            var managedHandles = new List<string>();

            foreach (string handle in handles ?? Enumerable.Empty<string>())
            {
                if (!TryGetObjectId(db, handle, out ObjectId id))
                    continue;
                if (!(tr.GetObject(id, OpenMode.ForWrite, false) is Entity entity))
                    continue;

                if (removeManagement)
                {
                    _metadata.Remove(entity, tr);
                    updated++;
                    continue;
                }

                if (!(entity is BlockReference) && template != null)
                {
                    ObjectId layerId =
                        _layers.EnsureGeneratedLayer(
                            db,
                            tr,
                            roadName,
                            template,
                            user: true);

                    entity.LayerId = layerId;
                }

                ArmEntityMetadata meta = _mapper.CreateGeneratedMarking(
                    entity,
                    $"SUPPLEMENTARY|{roadKey}|{groupId}|{handle}",
                    type.Equals("manual_block", StringComparison.OrdinalIgnoreCase)
                        ? "SUPPLEMENTARY_EXTERNAL_BLOCK"
                        : "SUPPLEMENTARY_MANUAL",
                    roadName,
                    roadKey,
                    "ROAD",
                    groupId,
                    string.Empty,
                    markingCode,
                    templateId,
                    template?.Layer ?? entity.Layer,
                    width,
                    0.0,
                    0.0);

                _mapper.BindAxis(meta, axisInfo);
                meta.CadLayer = entity.Layer;

                meta.BlockName = ReadBlockName(entity, tr);
                meta.Extra["GroupId"] = groupId;
                meta.Extra["SupplementaryType"] = type;
                meta.Extra["QuantityMode"] = entity is BlockReference ? "COUNT" : "GEOMETRY";
                meta.Extra["PaintRatio"] = Clamp01(paintRatio).ToString("0.########", CultureInfo.InvariantCulture);
                if (entity is BlockReference)
                {
                    meta.QuantityCount = 1;
                    meta.Extra["QuantityCount"] = "1";
                }

                _metadata.Write(entity, tr, meta);
                updated++;
                managedHandles.Add(handle);

                if (entity is Polyline polyline && polyline.Closed)
                {
                    // Closed Polyline đại diện vùng sơn: diện tích CAD là authoritative;
                    // không được lấy chu vi * width như vạch tuyến tính.
                    try
                    {
                        totalArea += Math.Abs(polyline.Area);
                    }
                    catch
                    {
                        double fallbackLength = Math.Max(0.0, CurveGeometryHelper.Length(polyline));
                        totalLength += fallbackLength;
                        totalArea += fallbackLength * Math.Max(0.0, width) * Clamp01(paintRatio);
                    }
                }
                else if (entity is Curve curve)
                {
                    double length = Math.Max(0.0, CurveGeometryHelper.Length(curve));
                    totalLength += length;
                    totalArea += length * Math.Max(0.0, width) * Clamp01(paintRatio);
                }
            }

            return new
            {
                count = updated,
                groupId,
                totalLength,
                totalArea,
                handles = managedHandles
            };
        }

        public int EraseManagedGroup(Database db, Transaction tr, string groupId, bool generatedOnly)
        {
            if (string.IsNullOrWhiteSpace(groupId))
                return 0;

            BlockTableRecord modelSpace = (BlockTableRecord)tr.GetObject(
                SymbolUtilityServices.GetBlockModelSpaceId(db),
                OpenMode.ForRead);

            var ids = new List<ObjectId>();
            foreach (ObjectId id in modelSpace)
            {
                if (!(tr.GetObject(id, OpenMode.ForRead, false) is Entity entity))
                    continue;
                ArmEntityMetadata? md = _metadata.Read(entity, tr);
                if (md == null || !string.Equals(ReadExtra(md, "GroupId"), groupId, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (generatedOnly && !md.Source.StartsWith("SUPPLEMENTARY_SPEED_HUMP", StringComparison.OrdinalIgnoreCase))
                    continue;
                ids.Add(id);
            }

            foreach (ObjectId id in ids)
            {
                if (tr.GetObject(id, OpenMode.ForWrite, false) is Entity entity && !entity.IsErased)
                    entity.Erase(true);
            }
            return ids.Count;
        }

        private StationDistributionResult BuildStations(
            Entity axis,
            string mode,
            double start,
            double end,
            double spacing,
            bool balanceRemainder,
            double anchor,
            int directionSign,
            int clusters,
            double clusterOffset,
            double clusterSpacing,
            int bars,
            double barSpacing)
        {
            double axisStart = _station.StartStation(axis);
            double axisEnd = _station.EndStation(axis);

            return _planner.Build(new StationDistributionRequest
            {
                Mode = string.Equals(mode, "cluster", StringComparison.OrdinalIgnoreCase)
                    ? StationDistributionMode.Cluster
                    : StationDistributionMode.Uniform,
                AxisStart = axisStart,
                AxisEnd = axisEnd,
                Start = start,
                End = end,
                Spacing = spacing,
                BalanceRemainder = balanceRemainder,
                Anchor = anchor,
                DirectionSign = directionSign,
                ClusterCount = clusters,
                ClusterOffset = clusterOffset,
                ClusterSpacing = clusterSpacing,
                BarsPerCluster = bars,
                BarSpacing = barSpacing,
                ShortSegmentPolicy = ShortSegmentPolicy.CenterSingle,
                DuplicateTolerance = 0.01
            });
        }

        private bool TryCrossSection(Entity axis, Curve boundary1, Curve boundary2, double station, out Point3d p1, out Point3d p2)
        {
            p1 = Point3d.Origin;
            p2 = Point3d.Origin;

            Point3d center = _station.PointAtStationOffset(axis, station, 0.0, out Vector3d tangent);
            if (tangent.Length < 1e-9)
                return false;

            Vector3d normal = new Vector3d(-tangent.Y, tangent.X, 0.0).GetNormal();
            double span = Math.Max(1000.0, _station.Length(axis) * 2.0 + 100.0);

            using (var probe = new Line(center - normal * span, center + normal * span))
            {
                if (!TryIntersectNearest(probe, boundary1, center, out p1) ||
                    !TryIntersectNearest(probe, boundary2, center, out p2))
                    return false;
            }

            return p1.DistanceTo(p2) > 1e-6;
        }

        private static bool TryIntersectNearest(Curve probe, Curve boundary, Point3d center, out Point3d point)
        {
            point = Point3d.Origin;
            var points = new Point3dCollection();
            try
            {
                probe.IntersectWith(boundary, Intersect.OnBothOperands, points, IntPtr.Zero, IntPtr.Zero);
            }
            catch
            {
                return false;
            }

            if (points.Count == 0)
                return false;

            point = points.Cast<Point3d>().OrderBy(x => x.DistanceTo(center)).First();
            return true;
        }

        private Entity ResolveAxis(
            Database db,
            Transaction tr,
            ArmProjectState state,
            string roadKey)
        {
            return _axisCatalog.ResolveEntity(
                db,
                tr,
                state,
                roadKey);
        }

        private static Curve ResolveCurve(Database db, Transaction tr, string handle)
        {
            if (!TryGetObjectId(db, handle, out ObjectId id) ||
                !(tr.GetObject(id, OpenMode.ForRead, false) is Curve curve))
                throw new InvalidOperationException("Đường biên không hợp lệ: " + handle);

            return curve;
        }

        internal static bool TryGetObjectId(Database db, string handle, out ObjectId id)
        {
            id = ObjectId.Null;
            try
            {
                if (string.IsNullOrWhiteSpace(handle)) return false;
                long value = Convert.ToInt64(handle, 16);
                id = db.GetObjectId(false, new Handle(value), 0);
                return !id.IsNull;
            }
            catch
            {
                return false;
            }
        }

        internal static string ReadBlockName(Entity entity, Transaction tr)
        {
            if (!(entity is BlockReference blockReference))
                return string.Empty;
            try
            {
                ObjectId definitionId = blockReference.DynamicBlockTableRecord.IsNull
                    ? blockReference.BlockTableRecord
                    : blockReference.DynamicBlockTableRecord;
                var definition = (BlockTableRecord)tr.GetObject(definitionId, OpenMode.ForRead);
                return definition.Name;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string ReadExtra(ArmEntityMetadata? metadata, string key)
        {
            if (metadata?.Extra != null && metadata.Extra.TryGetValue(key, out string? value))
                return value ?? string.Empty;
            return string.Empty;
        }

        private static string FormatStation(double station)
        {
            int km = (int)Math.Floor(station / 1000.0);
            double metres = station - km * 1000.0;
            return km.ToString(CultureInfo.InvariantCulture) + "+" + metres.ToString("000.00", CultureInfo.InvariantCulture);
        }

        private static double Clamp01(double value) => Math.Max(0.0, Math.Min(1.0, value));
    }
}
