using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace Autoroadmarking_Pro.CadHost.Cad.Geometry
{
    public static class CurveGeometryHelper
    {
        public static double Length(Curve curve)
        {
            try { return curve.GetDistanceAtParameter(curve.EndParam) - curve.GetDistanceAtParameter(curve.StartParam); }
            catch { return 0.0; }
        }

        public static Point3d PointAtFraction(Curve curve, double fraction)
        {
            fraction = Math.Max(0.0, Math.Min(1.0, fraction));
            double len = Length(curve);
            if (len <= 1e-9) return curve.StartPoint;
            try { return curve.GetPointAtDist(len * fraction); }
            catch { return curve.StartPoint; }
        }

        public static Vector3d TangentAtPoint(Curve curve, Point3d point)
        {
            try
            {
                var cp = curve.GetClosestPointTo(point, false);
                var v = curve.GetFirstDerivative(cp);
                if (v.Length > 1e-9) return v.GetNormal();
            }
            catch { }
            return Vector3d.XAxis;
        }

        public static double SignedOffset(Curve axis, Point3d point)
        {
            var cp = axis.GetClosestPointTo(point, false);
            var tangent = TangentAtPoint(axis, cp);
            var delta = point - cp;
            double cross = tangent.X * delta.Y - tangent.Y * delta.X;
            double d = cp.DistanceTo(point);
            if (d <= 1e-9) return 0.0;
            // Quy ước ARM: LEFT < 0, RIGHT > 0.
            return cross >= 0.0 ? -d : d;
        }

        public static Point3d OffsetPoint(Point3d center, Vector3d tangent, double signedOffset)
        {
            var t = tangent.Length > 1e-9 ? tangent.GetNormal() : Vector3d.XAxis;
            var right = new Vector3d(t.Y, -t.X, 0.0).GetNormal();
            return center + right * signedOffset;
        }

        public static double Median(IEnumerable<double> values)
        {
            var a = values.OrderBy(x=>x).ToArray();
            if (a.Length==0) return double.NaN;
            int m=a.Length/2;
            return a.Length%2==1 ? a[m] : (a[m-1]+a[m])*0.5;
        }

        public static string Handle(ObjectId id) => id.Handle.ToString();
    }
}
