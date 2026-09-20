namespace Autoroadmarking_Pro.Domain.RoadIdentity
{
    /// <summary>
    /// Định danh logic của một trục tuyến.
    /// AxisKey là khóa kỹ thuật ổn định; RoadName là tên nghiệp vụ có thể đổi.
    /// </summary>
    public sealed class RoadIdentity
    {
        public string Id { get; set; } = string.Empty;
        public string AxisKey { get; set; } = string.Empty;
        public string RoadName { get; set; } = string.Empty;

        // Alias tương thích code/schema cũ.
        public string RoadKey
        {
            get => AxisKey;
            set => AxisKey = value ?? string.Empty;
        }

        public string AxisHandle { get; set; } = string.Empty;
        public string AxisType { get; set; } = string.Empty;
        public string IdentitySource { get; set; } = string.Empty;
        public bool IsNativeNamed { get; set; }
        public string? AlignmentName { get; set; }
        public int OrientationSign { get; set; } = 1;
    }
}
