using System;
using System.Collections.Generic;
using System.Linq;

namespace Autoroadmarking_Pro.Application.Intersections
{
    public sealed class RoadPairCandidate
    {
        public string CenterlineId { get; set; } = string.Empty;
        public List<string> EdgeIds { get; set; } = new List<string>();
        public double Width { get; set; }
    }

    public sealed class RoadPairingService
    {
        public IReadOnlyList<RoadPairCandidate> OrderByWidth(IEnumerable<RoadPairCandidate> items)
        {
            return items == null
                ? new List<RoadPairCandidate>()
                : items.OrderBy(x => x.Width).ToList();
        }
    }
}
