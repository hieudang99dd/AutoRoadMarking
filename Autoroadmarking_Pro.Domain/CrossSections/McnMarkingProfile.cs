using System.Collections.Generic;

namespace Autoroadmarking_Pro.Domain.CrossSections
{
    public sealed class McnMarkingProfile
    {
        public string Id { get; set; } = string.Empty;
        public string CrossSectionId { get; set; } = string.Empty;
        public List<string> MarkingTemplateIds { get; set; } = new List<string>();
    }
}
