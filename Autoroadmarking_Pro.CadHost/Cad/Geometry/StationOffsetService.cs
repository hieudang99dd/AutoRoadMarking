using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

using AcEntity = Autodesk.AutoCAD.DatabaseServices.Entity;
using CivilAlignment = Autodesk.Civil.DatabaseServices.Alignment;

namespace Autoroadmarking_Pro.CadHost.Cad.Geometry
{
    public sealed class StationOffsetService
    {
        public double Length(AcEntity axis)
        {
            if (axis is CivilAlignment al)
                return al.Length;

            if (axis is Curve c)
                return CurveGeometryHelper.Length(c);

            return 0.0;
        }

        public double StartStation(AcEntity axis)
        {
            if (axis is CivilAlignment al)
                return al.StartingStation;

            return 0.0;
        }

        public double EndStation(AcEntity axis)
        {
            if (axis is CivilAlignment al)
                return al.EndingStation;

            return Length(axis);
        }

        public Point3d PointAtStationOffset(
            AcEntity axis,
            double station,
            double signedOffset,
            out Vector3d tangent)
        {
            if (axis is CivilAlignment al)
            {
                double easting = 0.0;
                double northing = 0.0;
                double bearing = 0.0;

                al.PointLocation(
                    station,
                    signedOffset,
                    0.001,
                    ref easting,
                    ref northing,
                    ref bearing);

                Point3d point =
                    new Point3d(
                        easting,
                        northing,
                        0.0);

                double delta =
                    Math.Min(
                        0.10,
                        Math.Max(
                            0.01,
                            al.Length * 1e-5));

                double station2 =
                    Math.Min(
                        al.EndingStation,
                        station + delta);

                if (Math.Abs(station2 - station) < 1e-9)
                {
                    station2 =
                        Math.Max(
                            al.StartingStation,
                            station - delta);
                }

                double easting2 = 0.0;
                double northing2 = 0.0;
                double bearing2 = 0.0;

                al.PointLocation(
                    station2,
                    signedOffset,
                    0.001,
                    ref easting2,
                    ref northing2,
                    ref bearing2);

                tangent =
                    new Point3d(
                        easting2,
                        northing2,
                        0.0)
                    - point;

                if (station2 < station)
                    tangent = -tangent;

                tangent =
                    tangent.Length <= 1e-9
                        ? Vector3d.XAxis
                        : tangent.GetNormal();

                return point;
            }

            if (axis is Curve curve)
            {
                double distance =
                    Math.Max(
                        0.0,
                        Math.Min(
                            CurveGeometryHelper.Length(curve),
                            station));

                Point3d center =
                    curve.GetPointAtDist(
                        distance);

                tangent =
                    CurveGeometryHelper
                        .TangentAtPoint(
                            curve,
                            center);

                return CurveGeometryHelper
                    .OffsetPoint(
                        center,
                        tangent,
                        signedOffset);
            }

            tangent = Vector3d.XAxis;
            return Point3d.Origin;
        }

        public bool TryStationOffset(
            AcEntity axis,
            Point3d point,
            out double station,
            out double offset)
        {
            station = 0.0;
            offset = 0.0;

            try
            {
                if (axis is CivilAlignment al)
                {
                    double s = 0.0;
                    double o = 0.0;

                    al.StationOffset(
                        point.X,
                        point.Y,
                        ref s,
                        ref o);

                    station = s;
                    offset = o;

                    return true;
                }

                if (axis is Curve curve)
                {
                    Point3d closestPoint =
                        curve.GetClosestPointTo(
                            point,
                            false);

                    double parameter =
                        curve.GetParameterAtPoint(
                            closestPoint);

                    station =
                        curve.GetDistanceAtParameter(
                            parameter);

                    offset =
                        CurveGeometryHelper
                            .SignedOffset(
                                curve,
                                point);

                    return true;
                }
            }
            catch
            {
            }

            return false;
        }


        private static List<Point2d> SimplifyPolyline(
            IList<Point2d> points,
            double tolerance)
        {
            var result = new List<Point2d>();
            if (points == null || points.Count == 0)
                return result;

            if (points.Count <= 2 || tolerance <= 0.0)
            {
                for (int i = 0; i < points.Count; i++)
                    result.Add(points[i]);
                return result;
            }

            bool[] keep = new bool[points.Count];
            keep[0] = true;
            keep[points.Count - 1] = true;

            var stack = new Stack<Tuple<int, int>>();
            stack.Push(Tuple.Create(0, points.Count - 1));
            double toleranceSquared = tolerance * tolerance;

            while (stack.Count > 0)
            {
                Tuple<int, int> range = stack.Pop();
                int first = range.Item1;
                int last = range.Item2;
                if (last <= first + 1)
                    continue;

                double maxDistanceSquared = -1.0;
                int farthest = -1;

                for (int i = first + 1; i < last; i++)
                {
                    double distanceSquared = DistanceToSegmentSquared(
                        points[i],
                        points[first],
                        points[last]);

                    if (distanceSquared > maxDistanceSquared)
                    {
                        maxDistanceSquared = distanceSquared;
                        farthest = i;
                    }
                }

                if (farthest >= 0 && maxDistanceSquared > toleranceSquared)
                {
                    keep[farthest] = true;
                    stack.Push(Tuple.Create(first, farthest));
                    stack.Push(Tuple.Create(farthest, last));
                }
            }

            for (int i = 0; i < points.Count; i++)
            {
                if (keep[i])
                    result.Add(points[i]);
            }

            return result;
        }

        private static double DistanceToSegmentSquared(
            Point2d point,
            Point2d start,
            Point2d end)
        {
            double dx = end.X - start.X;
            double dy = end.Y - start.Y;
            double lengthSquared = dx * dx + dy * dy;

            if (lengthSquared <= 1e-18)
            {
                double px = point.X - start.X;
                double py = point.Y - start.Y;
                return px * px + py * py;
            }

            double t = ((point.X - start.X) * dx + (point.Y - start.Y) * dy) / lengthSquared;
            t = Math.Max(0.0, Math.Min(1.0, t));

            double cx = start.X + t * dx;
            double cy = start.Y + t * dy;
            double ex = point.X - cx;
            double ey = point.Y - cy;
            return ex * ex + ey * ey;
        }

        /// <summary>
        /// Tạo Curve proxy không DB-resident để thuật toán hình học dùng chung.
        /// Alignment được lấy mẫu bằng PointLocation.
        /// Curve AutoCAD được clone hoặc offset.
        /// Caller sở hữu Curve trả về và phải Dispose.
        /// </summary>
        public Curve? CreatePolylineProxy(
            AcEntity axis,
            double signedOffset = 0.0)
        {
            if (axis is CivilAlignment al)
            {
                double start = al.StartingStation;
                double end = al.EndingStation;
                double length = Math.Max(0.0, al.Length);

                // Lấy mẫu Civil Alignment đủ mịn rồi đơn giản hóa theo sai số hình học.
                // Bản cũ ghi thẳng 1 vertex / ~2 m, vì vậy tuyến 156 m sinh gần 80 grip
                // dù là đoạn thẳng. Douglas-Peucker giữ hình học trong sai số tối đa khoảng 5 cm:
                // đoạn thẳng thường chỉ còn 2 vertex, đoạn cong chỉ giữ điểm cần thiết.
                int segments = Math.Max(
                    24,
                    Math.Min(
                        5000,
                        (int)Math.Ceiling(length / 1.5)));

                var sampled = new List<Point2d>(segments + 1);

                for (int i = 0; i <= segments; i++)
                {
                    double station = i == segments
                        ? end
                        : start + (end - start) * i / segments;

                    double easting = 0.0;
                    double northing = 0.0;

                    al.PointLocation(
                        station,
                        signedOffset,
                        ref easting,
                        ref northing);

                    Point2d point = new Point2d(easting, northing);
                    if (sampled.Count == 0)
                    {
                        sampled.Add(point);
                    }
                    else
                    {
                        Point2d previous = sampled[sampled.Count - 1];
                        double dx = previous.X - point.X;
                        double dy = previous.Y - point.Y;
                        if (dx * dx + dy * dy > 1e-14)
                            sampled.Add(point);
                    }
                }

                List<Point2d> simplified = SimplifyPolyline(sampled, 0.05);
                if (simplified.Count < 2)
                    simplified = sampled;

                Polyline polyline = new Polyline(simplified.Count);
                for (int i = 0; i < simplified.Count; i++)
                {
                    polyline.AddVertexAt(
                        i,
                        simplified[i],
                        0.0,
                        0.0,
                        0.0);
                }

                return polyline;
            }

            if (axis is Curve curve)
            {
                Curve clone =
                    (Curve)curve.Clone();

                if (Math.Abs(signedOffset) <= 1e-9)
                    return clone;

                try
                {
                    DBObjectCollection offsets =
                        clone.GetOffsetCurves(
                            signedOffset);

                    clone.Dispose();

                    Curve? selected = null;

                    foreach (DBObject obj in offsets)
                    {
                        if (selected == null &&
                            obj is Curve offsetCurve)
                        {
                            selected = offsetCurve;
                            continue;
                        }

                        obj.Dispose();
                    }

                    return selected;
                }
                catch
                {
                    clone.Dispose();
                    throw;
                }
            }

            return null;
        }
    }
}
