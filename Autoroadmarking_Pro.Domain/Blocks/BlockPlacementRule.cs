namespace Autoroadmarking_Pro.Domain.Blocks
{
    public sealed class BlockPlacementRule
    {
        public string Id { get; set; } = string.Empty;
        public string BlockCatalogItemId { get; set; } = string.Empty;
        public double FirstDistance { get; set; }
        public int ClusterCount { get; set; } = 1;
        public double ClusterSpacing { get; set; }
    }
}
