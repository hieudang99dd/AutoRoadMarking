using System;
using System.Collections.Generic;
using System.Linq;
using Autoroadmarking_Pro.Domain.CrossSections;

namespace Autoroadmarking_Pro.Application.CrossSections
{
    public sealed class LaneGeometryService
    {
        public List<LaneGeometry> BuildFromComponents(IEnumerable<CrossSectionComponent> components)
        {
            if (components == null) return new List<LaneGeometry>();

            var result = new List<LaneGeometry>();
            double left = 0.0;
            double right = 0.0;
            int laneIndex = 1;

            foreach (var item in components.OrderBy(x => x.Order))
            {
                if (item.Role != ComponentRole.Lane || item.Width <= 0.0)
                    continue;

                if (string.Equals(item.Side, "Left", StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(new LaneGeometry
                    {
                        LaneIndex = laneIndex++,
                        Side = "Left",
                        Width = item.Width,
                        CenterOffset = -(left + item.Width / 2.0),
                        Direction = LaneDirection.Unspecified
                    });
                    left += item.Width;
                }
                else if (string.Equals(item.Side, "Right", StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(new LaneGeometry
                    {
                        LaneIndex = laneIndex++,
                        Side = "Right",
                        Width = item.Width,
                        CenterOffset = right + item.Width / 2.0,
                        Direction = LaneDirection.Unspecified
                    });
                    right += item.Width;
                }
            }

            return result;
        }
    }
}
