using System.Collections.Generic;

namespace Autoroadmarking_Pro.Domain.CrossSections
{
    public sealed class CrossSectionDefinition
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public List<CrossSectionComponent> Components { get; set; } = new List<CrossSectionComponent>();
        public List<LaneGeometry> Lanes { get; set; } = new List<LaneGeometry>();
    }
}
