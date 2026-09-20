using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autoroadmarking_Pro.Application.RoadAxis;
using Autoroadmarking_Pro.CadHost.Cad.Geometry;

namespace Autoroadmarking_Pro.CadHost.Cad.RoadAxis
{
    /// <summary>
    /// IRoadAxisGeometry adapter cho AutoCAD Curve.
    /// orientationSign = +1: station tăng theo chiều Curve gốc.
    /// orientationSign = -1: station tăng theo chiều ngược Curve gốc.
    /// ARM convention: LEFT &lt; 0, RIGHT &gt; 0 theo chiều station nghiệp vụ.
    /// </summary>
    public sealed class CadCurveRoadAxisAdapter : IRoadAxisGeometry
    {
        private readonly Curve _curve;
        private readonly int _orientationSign;
        private readonly double _length;

        public CadCurveRoadAxisAdapter(
            Curve curve,
            int orientationSign = 1)
        {
            _curve =
                curve ??
                throw new ArgumentNullException(
                    nameof(curve));

            _orientationSign =
                orientationSign < 0
                    ? -1
                    : 1;

            _length =
                Math.Max(
                    0.0,
                    CurveGeometryHelper.Length(
                        _curve));
        }

        public double StartStation => 0.0;

        public double EndStation => _length;

        public RoadAxisPoint PointAtStation(
            double station)
        {
            double businessDistance =
                Math.Max(
                    0.0,
                    Math.Min(
                        _length,
                        station));

            double curveDistance =
                _orientationSign > 0
                    ? businessDistance
                    : _length - businessDistance;

            Point3d point =
                _curve.GetPointAtDist(
                    curveDistance);

            return new RoadAxisPoint
            {
                X = point.X,
                Y = point.Y,
                Z = point.Z
            };
        }

        public double SignedOffset(
            RoadAxisPoint point)
        {
            if (point == null)
                throw new ArgumentNullException(
                    nameof(point));

            // CurveGeometryHelper sử dụng chiều Curve gốc.
            // Đảo dấu khi chiều nghiệp vụ đảo ngược.
            double offset =
                CurveGeometryHelper
                    .SignedOffset(
                        _curve,
                        new Point3d(
                            point.X,
                            point.Y,
                            point.Z));

            return offset *
                   _orientationSign;
        }

        public IReadOnlyList<RoadAxisPoint> Sample(
            double step)
        {
            if (step <= 0.0)
                throw new ArgumentOutOfRangeException(
                    nameof(step),
                    "Bước lấy mẫu phải lớn hơn 0.");

            var result =
                new List<RoadAxisPoint>();

            for (double station = 0.0;
                 station < _length;
                 station += step)
            {
                result.Add(
                    PointAtStation(
                        station));
            }

            result.Add(
                PointAtStation(
                    _length));

            return result;
        }
    }
}
