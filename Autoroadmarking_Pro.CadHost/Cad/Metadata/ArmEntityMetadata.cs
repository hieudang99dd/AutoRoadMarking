using System.Collections.Generic;

namespace Autoroadmarking_Pro.CadHost.Cad.Metadata
{
    public sealed class ArmEntityMetadata
    {
        public string Schema { get; set; } = "2.0";
        public string RecordId { get; set; } = string.Empty;
        public string GenerationKey { get; set; } = string.Empty;
        public string Source { get; set; } = string.Empty;
        public string RoadName { get; set; } = string.Empty;

        /// <summary>
        /// Khóa kỹ thuật của trục tuyến. ALIGNMENT dùng ALN:&lt;Handle&gt;;
        /// Polyline Tab 0 dùng POLY:&lt;RecordId&gt;. Không phụ thuộc RoadName.
        /// </summary>
        public string AxisKey { get; set; } = string.Empty;
        public string AxisHandle { get; set; } = string.Empty;
        public string AxisType { get; set; } = string.Empty;

        /// <summary>
        /// Tên cũ giữ để đọc project schema trước 7. Dữ liệu mới luôn đồng bộ
        /// RoadKey = AxisKey.
        /// </summary>
        public string RoadKey { get; set; } = string.Empty;
        public string OwnerType { get; set; } = string.Empty;
        public string OwnerId { get; set; } = string.Empty;
        public string McnId { get; set; } = string.Empty;
        public string MarkingCode { get; set; } = string.Empty;
        public string TemplateId { get; set; } = string.Empty;
        public string TemplateLayer { get; set; } = string.Empty;
        public string CadLayer { get; set; } = string.Empty;
        public string BlockName { get; set; } = string.Empty;
        public int LaneIndex { get; set; }
        public int ClusterIndex { get; set; }
        public double Station { get; set; }
        public double Offset { get; set; }
        public double Width { get; set; }
        public int QuantityCount { get; set; }
        public double PaintedLengthOverride { get; set; }
        public double PaintedAreaOverride { get; set; }
        public Dictionary<string,string> Extra { get; set; } = new Dictionary<string,string>();
    }
}
