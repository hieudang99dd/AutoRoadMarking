using Autoroadmarking_Pro.Domain.Quantities;

namespace Autoroadmarking_Pro.Application.Quantities
{
    public sealed class QuantityRecognitionService
    {
        public bool IsManaged(string? metadataRecordId)
        {
            return !string.IsNullOrWhiteSpace(metadataRecordId);
        }

        public bool MatchesManualRule(string layerName, ManualLayerRule rule)
        {
            return rule != null
                && rule.Enabled
                && string.Equals(layerName, rule.LayerName, System.StringComparison.OrdinalIgnoreCase);
        }
    }
}
