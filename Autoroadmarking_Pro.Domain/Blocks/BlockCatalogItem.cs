namespace Autoroadmarking_Pro.Domain.Blocks
{
    public sealed class BlockCatalogItem
    {
        public string Id { get; set; } = string.Empty;
        public string BlockName { get; set; } = string.Empty;
        public string MarkingCode { get; set; } = string.Empty;
        public string SourceDwgPath { get; set; } = string.Empty;
        public LaneMovement Movement { get; set; } = LaneMovement.None;
    }
}
