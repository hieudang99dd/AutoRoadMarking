using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autoroadmarking_Pro.CadHost.Cad.Geometry;
using Autoroadmarking_Pro.CadHost.Cad.Metadata;
using Autoroadmarking_Pro.CadHost.Cad.State;

namespace Autoroadmarking_Pro.CadHost.Cad.Intersections
{
    /// <summary>
    /// Xén vạch dọc bên trong tập polygon nút giao.
    ///
    /// Thuật toán xử lý toàn bộ polygon trong một lần split cho mỗi curve, tránh lỗi
    /// phiên bản cũ chỉ xén được polygon đầu tiên rồi erase curve gốc. Metadata được
    /// sao chép sang từng đoạn còn lại với RecordId mới nhưng giữ GenerationKey để
    /// lần sinh lại tiếp theo vẫn idempotent.
    /// </summary>
    public sealed class IntersectionTrimService
    {
        private readonly CadGeometryService _geometry = new CadGeometryService();
        private readonly EntityMetadataStore _metadata = new EntityMetadataStore();

        public int TrimLongitudinalInsidePolygons(
            Database db,
            Transaction tr,
            ArmProjectState state)
        {
            if (db == null) throw new ArgumentNullException(nameof(db));
            if (tr == null) throw new ArgumentNullException(nameof(tr));
            if (state == null) throw new ArgumentNullException(nameof(state));

            List<Polyline> polygons = LoadPolygons(db, tr, state);
            if (polygons.Count == 0)
                return 0;

            BlockTableRecord modelSpace = (BlockTableRecord)tr.GetObject(
                SymbolUtilityServices.GetBlockModelSpaceId(db),
                OpenMode.ForRead);

            var targets = new List<ObjectId>();
            foreach (ObjectId id in modelSpace)
            {
                if (!(tr.GetObject(id, OpenMode.ForRead, false) is Curve curve))
                    continue;

                ArmEntityMetadata? metadata = _metadata.Read((Entity)curve, tr);
                if (metadata != null &&
                    string.Equals(metadata.Source, "AUTO_LONGITUDINAL", StringComparison.OrdinalIgnoreCase))
                {
                    targets.Add(id);
                }
            }

            int affected = 0;
            foreach (ObjectId id in targets)
            {
                if (!(tr.GetObject(id, OpenMode.ForWrite, false) is Curve curve) || curve.IsErased)
                    continue;

                ArmEntityMetadata? metadata = _metadata.Read((Entity)curve, tr);
                if (metadata == null)
                    continue;

                if (ClipOutsideAll(tr, curve, polygons, metadata))
                    affected++;
            }

            return affected;
        }

        private List<Polyline> LoadPolygons(
            Database db,
            Transaction tr,
            ArmProjectState state)
        {
            // Đồng bộ Handle trước để polygon chỉnh grip vẫn là nguồn hình học hiện tại.
            new IntersectionPolygonService().RefreshPolygons(db, tr, state);

            return state.IntersectionPolygonHandles
                .Select(handle => _geometry.FromHandle(db, handle))
                .Where(id => !id.IsNull)
                .Select(id => tr.GetObject(id, OpenMode.ForRead, false) as Polyline)
                .Where(p => p != null && p.Closed && p.NumberOfVertices >= 3)
                .Cast<Polyline>()
                .ToList();
        }

        private bool ClipOutsideAll(
            Transaction tr,
            Curve curve,
            IReadOnlyList<Polyline> polygons,
            ArmEntityMetadata metadata)
        {
            List<double> parameters = CollectSplitParameters(curve, polygons);

            if (parameters.Count == 0)
            {
                Point3d midpoint = CurveGeometryHelper.PointAtFraction(curve, 0.5);
                if (!polygons.Any(p => PointInPolygon(p, midpoint)))
                    return false;

                curve.Erase(true);
                return true;
            }

            var splitParameters = new DoubleCollection();
            foreach (double parameter in parameters)
                splitParameters.Add(parameter);

            DBObjectCollection parts;
            try
            {
                parts = curve.GetSplitCurves(splitParameters);
            }
            catch
            {
                return false;
            }

            BlockTableRecord owner = (BlockTableRecord)tr.GetObject(
                curve.OwnerId,
                OpenMode.ForWrite);

            string parentRecordId = metadata.RecordId;
            int keptIndex = 0;

            foreach (DBObject obj in parts)
            {
                if (!(obj is Curve part))
                {
                    obj.Dispose();
                    continue;
                }

                Point3d midpoint = CurveGeometryHelper.PointAtFraction(part, 0.5);
                if (polygons.Any(p => PointInPolygon(p, midpoint)))
                {
                    part.Dispose();
                    continue;
                }

                Entity entity = (Entity)part;
                entity.LayerId = ((Entity)curve).LayerId;
                owner.AppendEntity(entity);
                tr.AddNewlyCreatedDBObject(entity, true);

                ArmEntityMetadata splitMetadata = CloneForSplit(
                    metadata,
                    parentRecordId,
                    ++keptIndex,
                    entity.Layer);

                _metadata.Write(entity, tr, splitMetadata);
            }

            curve.Erase(true);
            return true;
        }

        private static List<double> CollectSplitParameters(
            Curve curve,
            IReadOnlyList<Polyline> polygons)
        {
            var parameters = new List<double>();

            foreach (Polyline polygon in polygons)
            {
                var points = new Point3dCollection();
                try
                {
                    curve.IntersectWith(
                        polygon,
                        Intersect.OnBothOperands,
                        points,
                        IntPtr.Zero,
                        IntPtr.Zero);
                }
                catch
                {
                    continue;
                }

                foreach (Point3d point in points)
                {
                    try
                    {
                        Point3d onCurve = curve.GetClosestPointTo(point, false);
                        double parameter = curve.GetParameterAtPoint(onCurve);
                        if (parameter > curve.StartParam + 1e-8 &&
                            parameter < curve.EndParam - 1e-8)
                        {
                            parameters.Add(parameter);
                        }
                    }
                    catch
                    {
                    }
                }
            }

            return parameters
                .OrderBy(x => x)
                .Aggregate(
                    new List<double>(),
                    (list, value) =>
                    {
                        if (list.Count == 0 || Math.Abs(list[list.Count - 1] - value) > 1e-7)
                            list.Add(value);
                        return list;
                    });
        }

        private static ArmEntityMetadata CloneForSplit(
            ArmEntityMetadata source,
            string parentRecordId,
            int sequence,
            string cadLayer)
        {
            var extra = source.Extra == null
                ? new Dictionary<string, string>()
                : new Dictionary<string, string>(source.Extra, StringComparer.OrdinalIgnoreCase);

            extra["Trimmed"] = "true";
            extra["ParentRecordId"] = parentRecordId ?? string.Empty;
            extra["TrimSequence"] = sequence.ToString(System.Globalization.CultureInfo.InvariantCulture);

            return new ArmEntityMetadata
            {
                Schema = source.Schema,
                RecordId = "MRK_" + Guid.NewGuid().ToString("N"),
                GenerationKey = source.GenerationKey,
                Source = source.Source,
                RoadName = source.RoadName,
                AxisKey = source.AxisKey,
                AxisHandle = source.AxisHandle,
                AxisType = source.AxisType,
                RoadKey = source.RoadKey,
                OwnerType = source.OwnerType,
                OwnerId = source.OwnerId,
                McnId = source.McnId,
                MarkingCode = source.MarkingCode,
                TemplateId = source.TemplateId,
                TemplateLayer = source.TemplateLayer,
                CadLayer = cadLayer ?? source.CadLayer,
                BlockName = source.BlockName,
                LaneIndex = source.LaneIndex,
                ClusterIndex = source.ClusterIndex,
                Station = source.Station,
                Offset = source.Offset,
                Width = source.Width,
                QuantityCount = source.QuantityCount,
                PaintedLengthOverride = 0.0,
                PaintedAreaOverride = 0.0,
                Extra = extra
            };
        }

        public static bool PointInPolygon(Polyline polygon, Point3d point)
        {
            if (polygon == null || !polygon.Closed || polygon.NumberOfVertices < 3)
                return false;

            // Polyline polygon của nút giao có thể chứa bulge thật của sừng bò.
            // Ray-cast trực tiếp chỉ trên vertex sẽ coi cung là dây cung và loại nhầm
            // phần offset nằm giữa cung thật với dây cung. Vì vậy tuyến tính hóa từng
            // đoạn bulge với bước góc nhỏ rồi mới thực hiện point-in-polygon.
            List<Point2d> boundary = TessellatePolylineBoundary(polygon);
            if (boundary.Count < 3) return false;

            Point2d p = new Point2d(point.X, point.Y);
            bool inside = false;
            for (int i = 0, j = boundary.Count - 1; i < boundary.Count; j = i++)
            {
                Point2d a = boundary[j];
                Point2d b = boundary[i];
                if (PointOnSegment2d(p, a, b, 1e-6)) return true;

                if ((a.Y > p.Y) == (b.Y > p.Y)) continue;
                double denominator = b.Y - a.Y;
                if (Math.Abs(denominator) <= 1e-20) continue;
                double x = a.X + (p.Y - a.Y) * (b.X - a.X) / denominator;
                if (p.X < x) inside = !inside;
            }

            return inside;
        }

        private static List<Point2d> TessellatePolylineBoundary(Polyline polygon)
        {
            var result = new List<Point2d>();
            int n = polygon.NumberOfVertices;
            int segmentCount = polygon.Closed ? n : Math.Max(0, n - 1);
            const double maxAngleStep = Math.PI / 36.0; // 5°

            for (int i = 0; i < segmentCount; i++)
            {
                Point2d start = polygon.GetPoint2dAt(i);
                Point2d end = polygon.GetPoint2dAt((i + 1) % n);
                AddBoundaryPoint(result, start);

                double bulge = 0.0;
                try { bulge = polygon.GetBulgeAt(i); } catch { }
                if (Math.Abs(bulge) <= 1e-10)
                {
                    AddBoundaryPoint(result, end);
                    continue;
                }

                double sweep = Math.Abs(4.0 * Math.Atan(bulge));
                int steps = Math.Max(2, Math.Min(144, (int)Math.Ceiling(sweep / maxAngleStep)));
                for (int step = 1; step < steps; step++)
                {
                    double fraction = (double)step / steps;
                    try
                    {
                        Point3d q = polygon.GetPointAtParameter(i + fraction);
                        AddBoundaryPoint(result, new Point2d(q.X, q.Y));
                    }
                    catch { }
                }
                AddBoundaryPoint(result, end);
            }

            if (result.Count > 1 && result[0].GetDistanceTo(result[result.Count - 1]) <= 1e-9)
                result.RemoveAt(result.Count - 1);
            return result;
        }

        private static void AddBoundaryPoint(List<Point2d> points, Point2d point)
        {
            if (points.Count == 0 || points[points.Count - 1].GetDistanceTo(point) > 1e-9)
                points.Add(point);
        }

        private static bool PointOnSegment2d(Point2d p, Point2d a, Point2d b, double tolerance)
        {
            double dx = b.X - a.X;
            double dy = b.Y - a.Y;
            double length = Math.Sqrt(dx * dx + dy * dy);
            if (length <= tolerance) return p.GetDistanceTo(a) <= tolerance;

            double cross = (p.X - a.X) * dy - (p.Y - a.Y) * dx;
            if (Math.Abs(cross) > tolerance * Math.Max(1.0, length)) return false;

            double dot = (p.X - a.X) * dx + (p.Y - a.Y) * dy;
            if (dot < -tolerance) return false;
            return dot <= length * length + tolerance;
        }
    }
}
