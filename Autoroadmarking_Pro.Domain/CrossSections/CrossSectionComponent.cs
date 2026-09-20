namespace Autoroadmarking_Pro.Domain.CrossSections
{
    public sealed class CrossSectionComponent
    {
        public int Order { get; set; }
        public string Name { get; set; } = string.Empty;
        public ComponentRole Role { get; set; } = ComponentRole.Other;
        public string Side { get; set; } = string.Empty;
        public double Width { get; set; }
        public bool OccupiesWidth { get; set; } = true;
        public string? MarkingTemplateId { get; set; }
    }
}
