namespace Autoroadmarking_Pro.Domain.CrossSections
{
    public sealed class LaneGeometry
    {
        public int LaneIndex { get; set; }
        public string Side { get; set; } = string.Empty;
        public double Width { get; set; }
        public double CenterOffset { get; set; }
        public LaneDirection Direction { get; set; } = LaneDirection.Unspecified;
    }
}
