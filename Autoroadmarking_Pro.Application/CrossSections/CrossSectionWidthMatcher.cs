using System;
using System.Collections.Generic;
using System.Linq;

namespace Autoroadmarking_Pro.Application.CrossSections
{
    public sealed class CrossSectionWidthCandidate
    {
        public string Id { get; set; } = string.Empty;
        public double Width { get; set; }
    }

    public sealed class CrossSectionWidthMatchResult
    {
        public string Id { get; set; } = string.Empty;
        public double Difference { get; set; }
        public bool IsWithinTolerance { get; set; }
    }

    public sealed class CrossSectionWidthMatcher
    {
        public CrossSectionWidthMatchResult? Match(IEnumerable<CrossSectionWidthCandidate> candidates, double measuredWidth, double tolerance)
        {
            if (candidates == null) return null;
            var best = candidates.Select(x => new { x.Id, Difference = Math.Abs(x.Width - measuredWidth) }).OrderBy(x => x.Difference).FirstOrDefault();
            if (best == null) return null;
            return new CrossSectionWidthMatchResult { Id = best.Id, Difference = best.Difference, IsWithinTolerance = best.Difference <= tolerance };
        }
    }
}
