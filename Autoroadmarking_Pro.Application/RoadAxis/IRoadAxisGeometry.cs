using System.Collections.Generic;

namespace Autoroadmarking_Pro.Application.RoadAxis
{
    public sealed class RoadAxisPoint
    {
        public double X { get; set; }
        public double Y { get; set; }
        public double Z { get; set; }
    }

    public interface IRoadAxisGeometry
    {
        double StartStation { get; }
        double EndStation { get; }
        RoadAxisPoint PointAtStation(double station);
        double SignedOffset(RoadAxisPoint point);
        IReadOnlyList<RoadAxisPoint> Sample(double step);
    }
}
