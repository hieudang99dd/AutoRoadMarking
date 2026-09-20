namespace Autoroadmarking_Pro.Domain.Quantities
{
    public sealed class ManualLayerRule
    {
        public string Id { get; set; } = string.Empty;
        public string LayerName { get; set; } = string.Empty;
        public string RoadName { get; set; } = string.Empty;
        public string MarkingCode { get; set; } = string.Empty;
        public bool Enabled { get; set; } = true;
    }
}
