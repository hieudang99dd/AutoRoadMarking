using System;
using System.Collections.Generic;
using System.Linq;
using Autoroadmarking_Pro.Domain.CrossSections;

namespace Autoroadmarking_Pro.Application.CrossSections
{
    public sealed class CrossSectionMatchingService
    {
        public CrossSectionDefinition? FindClosestByTotalLaneWidth(
            IEnumerable<CrossSectionDefinition> library,
            double measuredWidth,
            double tolerance)
        {
            if (library == null) return null;

            return library
                .Select(x => new
                {
                    Item = x,
                    Difference = Math.Abs(x.Lanes.Sum(l => l.Width) - measuredWidth)
                })
                .Where(x => x.Difference <= tolerance)
                .OrderBy(x => x.Difference)
                .Select(x => x.Item)
                .FirstOrDefault();
        }
    }
}
