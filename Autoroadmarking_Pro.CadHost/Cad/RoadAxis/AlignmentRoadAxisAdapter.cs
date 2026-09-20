using System;
using System.Collections.Generic;

using Autodesk.AutoCAD.Geometry;
using Autoroadmarking_Pro.Application.RoadAxis;

using CivilAlignment = Autodesk.Civil.DatabaseServices.Alignment;

namespace Autoroadmarking_Pro.CadHost.Cad.RoadAxis
{
    /// <summary>
    /// IRoadAxisGeometry adapter cho Civil 3D Alignment.
    /// Station của Alignment được giữ nguyên theo Civil 3D.
    /// ARM convention: LEFT &lt; 0, RIGHT &gt; 0.
    /// </summary>
    public sealed class AlignmentRoadAxisAdapter : IRoadAxisGeometry
    {
        private readonly CivilAlignment _alignment;

        public AlignmentRoadAxisAdapter(
            CivilAlignment alignment)
        {
            _alignment =
                alignment ??
                throw new ArgumentNullException(
                    nameof(alignment));
        }

        public double StartStation =>
            _alignment.StartingStation;

        public double EndStation =>
            _alignment.EndingStation;

        public RoadAxisPoint PointAtStation(
            double station)
        {
            double s = ClampStation(station);

            double easting = 0.0;
            double northing = 0.0;

            _alignment.PointLocation(
                s,
                0.0,
                ref easting,
                ref northing);

            return new RoadAxisPoint
            {
                X = easting,
                Y = northing,
                Z = 0.0
            };
        }

        public double SignedOffset(
            RoadAxisPoint point)
        {
            if (point == null)
                throw new ArgumentNullException(
                    nameof(point));

            double station = 0.0;
            double civilOffset = 0.0;

            _alignment.StationOffset(
                point.X,
                point.Y,
                ref station,
                ref civilOffset);

            station = ClampStation(station);

            RoadAxisPoint center =
                PointAtStation(station);

            double deltaStation =
                Math.Max(
                    0.01,
                    Math.Min(
                        0.25,
                        Math.Abs(
                            EndStation -
                            StartStation) *
                        1e-5));

            double station2 =
                Math.Min(
                    EndStation,
                    station + deltaStation);

            bool reversedSample = false;

            if (Math.Abs(station2 - station) < 1e-9)
            {
                station2 =
                    Math.Max(
                        StartStation,
                        station - deltaStation);

                reversedSample = true;
            }

            RoadAxisPoint forward =
                PointAtStation(station2);

            var tangent =
                new Vector3d(
                    forward.X - center.X,
                    forward.Y - center.Y,
                    0.0);

            if (reversedSample)
                tangent = -tangent;

            if (tangent.Length <= 1e-9)
            {
                // Civil API đã trả offset; dùng làm fallback.
                return civilOffset;
            }

            tangent =
                tangent.GetNormal();

            var delta =
                new Vector3d(
                    point.X - center.X,
                    point.Y - center.Y,
                    0.0);

            double distance =
                Math.Sqrt(
                    delta.X * delta.X +
                    delta.Y * delta.Y);

            if (distance <= 1e-9)
                return 0.0;

            double cross =
                tangent.X * delta.Y -
                tangent.Y * delta.X;

            // ARM: left negative, right positive.
            return cross >= 0.0
                ? -distance
                : distance;
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

            double start =
                StartStation;

            double end =
                EndStation;

            if (end < start)
            {
                double temp = start;
                start = end;
                end = temp;
            }

            for (double s = start;
                 s < end;
                 s += step)
            {
                result.Add(
                    PointAtStation(s));
            }

            result.Add(
                PointAtStation(end));

            return result;
        }

        private double ClampStation(
            double station)
        {
            double min =
                Math.Min(
                    StartStation,
                    EndStation);

            double max =
                Math.Max(
                    StartStation,
                    EndStation);

            return Math.Max(
                min,
                Math.Min(
                    max,
                    station));
        }
    }
}
