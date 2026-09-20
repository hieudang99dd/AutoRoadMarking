namespace Autoroadmarking_Pro.Domain.Quantities
{
    /// <summary>
    /// Bản ghi khối lượng độc lập CAD. AxisKey là identity kỹ thuật ổn định;
    /// RoadName là nhãn nghiệp vụ có thể thay đổi/rename theo Alignment hoặc Tab 0.
    /// </summary>
    public sealed class QuantityRecord
    {
        public string RecordId { get; set; } = string.Empty;
        public string AxisKey { get; set; } = string.Empty;
        public string RoadName { get; set; } = string.Empty;
        public string OwnerType { get; set; } = string.Empty;
        public string OwnerId { get; set; } = string.Empty;
        public string MarkingCode { get; set; } = string.Empty;
        public double GeometryLength { get; set; }
        public double PaintRatio { get; set; } = 1.0;
        public double PaintedLength { get; set; }
        public double PaintedArea { get; set; }
        public int Count { get; set; }

        // Compatibility aliases for callers using the original domain contract.
        public double Length
        {
            get => GeometryLength;
            set => GeometryLength = value;
        }

        public double Area
        {
            get => PaintedArea;
            set => PaintedArea = value;
        }
    }
}
