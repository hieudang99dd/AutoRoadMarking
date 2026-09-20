using System.Collections.Generic;

namespace Autoroadmarking_Pro.Domain.Quantities
{
    public sealed class QuantityGroup
    {
        public string Key { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public List<QuantityRecord> Records { get; set; } = new List<QuantityRecord>();
    }
}
