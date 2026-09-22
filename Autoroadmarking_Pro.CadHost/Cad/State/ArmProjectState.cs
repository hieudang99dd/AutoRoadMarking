using System;
using System.Collections.Generic;
using System.Linq;
using Autoroadmarking_Pro.Domain.Quantities;

namespace Autoroadmarking_Pro.CadHost.Cad.State
{
    /// <summary>
    /// Dữ liệu nghiệp vụ gắn với từng DWG. Đây là state bền vững của plugin, không phải
    /// state trình bày của WebView. Từ UI 6.1 trở đi mọi dữ liệu có ảnh hưởng đến việc
    /// sinh lại hình học đều phải nằm ở đây hoặc trong ARM metadata trên entity.
    /// </summary>
    public sealed class ArmProjectState
    {
        public int SchemaVersion { get; set; } = 11;

        public List<ArmRoadAxisState> RoadAxes { get; set; } = new List<ArmRoadAxisState>();
        public List<ArmMarkingTemplateState> MarkingTemplates { get; set; } = new List<ArmMarkingTemplateState>();
        public List<ArmCrossSectionState> CrossSections { get; set; } = new List<ArmCrossSectionState>();

        // Tab 2 · tập MCN đã "ĐỒNG BỘ MẶT CẮT ĐÃ CHỌN" và được phép tham gia
        // đối chiếu/sinh vạch/Tab 4. Danh sách rỗng giữ tương thích DWG cũ: dùng toàn bộ thư viện.
        public List<string> ActiveCrossSectionIds { get; set; } = new List<string>();

        public List<ArmCrossSectionState> GetEffectiveCrossSections()
        {
            if (CrossSections == null || CrossSections.Count == 0)
                return new List<ArmCrossSectionState>();
            if (ActiveCrossSectionIds == null || ActiveCrossSectionIds.Count == 0)
                return CrossSections.ToList();

            var ids = new HashSet<string>(ActiveCrossSectionIds, StringComparer.OrdinalIgnoreCase);
            List<ArmCrossSectionState> selected = CrossSections
                .Where(x => !string.IsNullOrWhiteSpace(x.Id) && ids.Contains(x.Id))
                .ToList();

            // Nếu state cũ/stale chỉ còn ID đã bị xóa, không khóa pipeline vào tập rỗng.
            return selected.Count > 0 ? selected : CrossSections.ToList();
        }

        public List<string> SelectedTimHandles { get; set; } = new List<string>();
        public List<string> SelectedEdgeHandles { get; set; } = new List<string>();
        public List<string> SelectedComponentEdgeHandles { get; set; } = new List<string>();
        public List<ArmComparisonState> ComparisonResults { get; set; } = new List<ArmComparisonState>();
        public List<string> IntersectionPolygonHandles { get; set; } = new List<string>();

        public string BlockLibraryPath { get; set; } = string.Empty;
        public List<ArmBlockCatalogState> BlockCatalog { get; set; } = new List<ArmBlockCatalogState>();
        public List<ArmBlockProposalState> BlockProposals { get; set; } = new List<ArmBlockProposalState>();
        public List<ArmSymbolProfileState> SymbolProfiles { get; set; } = new List<ArmSymbolProfileState>();

        public List<ManualLayerRule> ManualLayerRules { get; set; } = new List<ManualLayerRule>();
        public List<ArmBlockManagementRuleState> BlockManagementRules { get; set; } = new List<ArmBlockManagementRuleState>();
        public List<ArmManagedGroupState> ManagedGroups { get; set; } = new List<ArmManagedGroupState>();

        // Tab 4 - tham số toàn dự án. Các giá trị này chỉ là mặc định; profile MCN có
        // quyền bật/tắt 7.6/9.3 theo từng làn.
        public double BlockDistance76 { get; set; } = 30.0;
        public bool BlockInboundOnly76 { get; set; } = true;
        public string BlockAnchor93 { get; set; } = "71";
        public double BlockFirstDistance93 { get; set; } = 20.0;
        public int BlockClusterCount93 { get; set; } = 3;
        public double BlockClusterSpacing93 { get; set; } = 25.0;
        public double BlockOutboundDistance93 { get; set; } = 15.0;
        public bool BlockRotateWithTraffic { get; set; } = true;

        // Tab 3 - khoảng cách TIM vạch 7.3 đến TIM vạch dừng 7.1.
        // Đây là tham số thiết kế tùy biến theo dự án; không áp trần cứng 3 m.
        public double StopToCrosswalkDistance { get; set; } = 2.0;

        // Tab 6 - chỉ dùng để thông báo snapshot đã cũ. Khối lượng thật luôn được đọc
        // lại từ ModelSpace/metadata khi Refresh.
        public DateTime? LastQuantitySnapshotUtc { get; set; }
        public bool QuantitySnapshotDirty { get; set; } = true;
    }

    /// <summary>
    /// Mô tả một trục tuyến dùng xuyên suốt Tab 3/4/5/6.
    ///
    /// RoadAxes trong state chỉ persist các Polyline đã được định danh ở Tab 0.
    /// Alignment được RoadAxisCatalogService khám phá trực tiếp từ DWG vì Alignment
    /// đã có tên native trong Civil 3D và tuyệt đối không cần qua Tab 0.
    ///
    /// AxisKey là khóa kỹ thuật ổn định. RoadName là tên nghiệp vụ/hiển thị và có
    /// thể thay đổi mà không làm đứt liên kết metadata đã sinh.
    /// </summary>
    public sealed class ArmRoadAxisState
    {
        public string RecordId { get; set; } = string.Empty;

        public string AxisKey { get; set; } = string.Empty;
        public string RoadName { get; set; } = string.Empty;

        /// <summary>
        /// Alias tương thích UI/state cũ. Từ schema 7, RoadKey luôn được normalize
        /// về AxisKey khi service tạo/cập nhật dữ liệu mới.
        /// </summary>
        public string RoadKey { get; set; } = string.Empty;

        public string LegacyRoadKey { get; set; } = string.Empty;
        public string Handle { get; set; } = string.Empty;
        public string EntityType { get; set; } = string.Empty;
        public string AxisType { get; set; } = string.Empty; // POLYLINE | ALIGNMENT
        public string IdentitySource { get; set; } = string.Empty; // TAB0_METADATA | CIVIL_ALIGNMENT
        public bool IsNativeNamed { get; set; }

        public string Layer { get; set; } = string.Empty;
        public string OriginalLayer { get; set; } = string.Empty;
        public double Length { get; set; }
        public double StartStation { get; set; }
        public double EndStation { get; set; }
        public int OrientationSign { get; set; } = 1;

        public string EffectiveAxisKey =>
            !string.IsNullOrWhiteSpace(AxisKey)
                ? AxisKey
                : RoadKey;
    }

    public sealed class ArmMarkingTemplateState
    {
        public string Id { get; set; } = string.Empty;
        public string Code { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
        public string Layer { get; set; } = string.Empty;
        public string Geometry { get; set; } = "line";
        public double Width { get; set; }
        public double LinetypeScale { get; set; } = 1.0;
        public string Pattern { get; set; } = "CUSTOM_REAL";
        public double DashLength { get; set; }
        public double GapLength { get; set; }
        public double CustomDash1 { get; set; }
        public double CustomGap1 { get; set; }
        public double CustomDash2 { get; set; }
        public double CustomGap2 { get; set; }
        public double CustomPhase { get; set; }
        public string Color { get; set; } = "#ffffff";
        public string Rgb { get; set; } = "255,255,255";
        public string Reference { get; set; } = string.Empty;
        public string StandardClause { get; set; } = string.Empty;
        public string Note { get; set; } = string.Empty;

        // Metadata cấp template phục vụ luồng "CẬP NHẬT THƯ VIỆN" / "ÁP DỤNG VÀ ĐỒNG BỘ".
        // Các trường này KHÔNG phải identity của entity CAD.
        public string ManagementState { get; set; } = "DIRTY"; // NEW | DIRTY | READY | ERROR
        public string QuantityMethod { get; set; } = "LENGTH"; // LENGTH | AREA | COUNT | GENERATED
        public string ManagementSchema { get; set; } = "ARM_QTY_TEMPLATE";
        public int ManagementVersion { get; set; } = 1;
        public DateTime? ManagementUpdatedAt { get; set; }
        public string LayerHandle { get; set; } = string.Empty;

        /// <summary>
        /// Metadata mở rộng riêng của dự án. Không dùng để ghi đè Width/Pattern/DashLength/
        /// GapLength/StandardReference/RoadIdentity/...; các trường kỹ thuật đó có property
        /// typed riêng và được MarkingCustomPropertyPolicy bảo vệ tại Application/CadHost.
        /// </summary>
        public Dictionary<string, string> CustomProperties { get; set; } = new Dictionary<string, string>();
        public bool Verified { get; set; } = true;

        public double PaintRatio
        {
            get
            {
                string p = (Pattern ?? string.Empty).Trim().ToUpperInvariant();
                if (p == "CONTINUOUS" || p == "SOLID") return 1.0;

                if (p == "CUSTOM_REAL")
                {
                    double painted = Math.Max(0.0, CustomDash1) + Math.Max(0.0, CustomDash2);
                    double cycle = painted + Math.Max(0.0, CustomGap1) + Math.Max(0.0, CustomGap2);
                    return cycle > 1e-9 ? Math.Max(0.0, Math.Min(1.0, painted / cycle)) : 1.0;
                }

                double dash = Math.Max(0.0, DashLength);
                double gap = Math.Max(0.0, GapLength);
                return dash + gap > 1e-9 ? Math.Max(0.0, Math.Min(1.0, dash / (dash + gap))) : 1.0;
            }
        }
    }

    public sealed class ArmCrossSectionState
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public List<ArmCrossSectionPartState> Components { get; set; } = new List<ArmCrossSectionPartState>();

        public double TotalLaneWidth => SumWidth("Lane");
        public double LeftWidth => Components.FindAll(IsLeftOccupying).Sum(x => Math.Max(0.0, x.Width));
        public double RightWidth => Components.FindAll(IsRightOccupying).Sum(x => Math.Max(0.0, x.Width));
        public int LeftLaneCount => Components.FindAll(x => IsRole(x, "Lane") && IsSide(x, "Left")).Count;
        public int RightLaneCount => Components.FindAll(x => IsRole(x, "Lane") && IsSide(x, "Right")).Count;

        /// <summary>
        /// Bề rộng chiếm chỗ của MCN. Marking/Centerline là hình học ký hiệu nên không
        /// làm tăng bề rộng mặt đường dùng cho bước đối chiếu TIM-MÉP.
        /// </summary>
        public double TotalSectionWidth
        {
            get
            {
                double total = 0.0;
                foreach (ArmCrossSectionPartState c in Components)
                {
                    if (c.Width <= 0.0 || !c.OccupiesWidth) continue;
                    if (IsRole(c, "Marking") || IsRole(c, "Centerline")) continue;
                    total += c.Width;
                }
                return total > 0.0 ? total : TotalLaneWidth;
            }
        }

        private double SumWidth(string role)
        {
            double total = 0.0;
            foreach (ArmCrossSectionPartState c in Components)
                if (IsRole(c, role) && c.Width > 0.0) total += c.Width;
            return total;
        }

        private static bool IsLeftOccupying(ArmCrossSectionPartState c) => c.OccupiesWidth && c.Width > 0.0 && IsSide(c, "Left") && !IsRole(c, "Marking") && !IsRole(c, "Centerline");
        private static bool IsRightOccupying(ArmCrossSectionPartState c) => c.OccupiesWidth && c.Width > 0.0 && IsSide(c, "Right") && !IsRole(c, "Marking") && !IsRole(c, "Centerline");
        private static bool IsRole(ArmCrossSectionPartState c, string role) => string.Equals(c.Role, role, StringComparison.OrdinalIgnoreCase);
        private static bool IsSide(ArmCrossSectionPartState c, string side) => string.Equals(c.Side, side, StringComparison.OrdinalIgnoreCase);
    }

    public sealed class ArmCrossSectionPartState
    {
        public string Id { get; set; } = string.Empty;
        public int Order { get; set; }
        public string Side { get; set; } = string.Empty;
        public string Role { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public double Width { get; set; }
        public double Offset { get; set; }
        public bool OccupiesWidth { get; set; } = true;
        public string Template { get; set; } = string.Empty;
    }

    public sealed class ArmComparisonState
    {
        public string Id { get; set; } = string.Empty;
        public string Road { get; set; } = string.Empty;
        public string RoadKey { get; set; } = string.Empty;
        public string Tim { get; set; } = string.Empty;
        public string TimHandle { get; set; } = string.Empty;
        public string LeftEdgeHandle { get; set; } = string.Empty;
        public string RightEdgeHandle { get; set; } = string.Empty;
        public string Edges { get; set; } = string.Empty;
        public double LeftWidth { get; set; }
        public double RightWidth { get; set; }
        public double Width { get; set; }
        public double EdgeDispersion { get; set; }
        public int SampleCount { get; set; }
        public string Mcn { get; set; } = string.Empty;
        public double? Diff { get; set; }
        public double Score { get; set; }
        public string Reason { get; set; } = string.Empty;
        public string Status { get; set; } = "unpaired";
    }

    public sealed class ArmBlockCatalogState
    {
        public string Code { get; set; } = string.Empty;
        public string Variant { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string SourceDwgPath { get; set; } = string.Empty;
    }

    public sealed class ArmBlockProposalState
    {
        public string Id { get; set; } = string.Empty;
        public bool Selected { get; set; } = true;
        public string Road { get; set; } = string.Empty;
        public string RoadKey { get; set; } = string.Empty;
        public string AxisHandle { get; set; } = string.Empty;
        public string Approach { get; set; } = string.Empty;
        public string NodeId { get; set; } = string.Empty;
        public string Direction { get; set; } = string.Empty;
        public string Lane { get; set; } = string.Empty;
        public int LaneIndex { get; set; }
        public int Cluster { get; set; }
        public string Code { get; set; } = string.Empty;
        public string Movement { get; set; } = string.Empty;
        public string Block { get; set; } = string.Empty;
        public double StationValue { get; set; }
        public string Station { get; set; } = string.Empty;
        public double OffsetValue { get; set; }
        public string Offset { get; set; } = string.Empty;
        public double Rotation { get; set; }
        public string PlacementKey { get; set; } = string.Empty;
        public string Status { get; set; } = "MỚI";
        public bool IsOverride { get; set; }
        public bool IsLocked { get; set; }
    }

    public sealed class ArmSymbolProfileState
    {
        public string Id { get; set; } = string.Empty;
        public string AssemblyId { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
        public List<ArmSymbolLaneProfileState> Lanes { get; set; } = new List<ArmSymbolLaneProfileState>();
    }

    public sealed class ArmSymbolLaneProfileState
    {
        public string LaneId { get; set; } = string.Empty;
        public string Side { get; set; } = string.Empty;
        public int LaneIndex { get; set; }
        public double Offset { get; set; }
        public bool Enable76 { get; set; } = true;
        public bool Enable93 { get; set; } = true;
        public string Movement { get; set; } = "STRAIGHT";

        /// <summary>
        /// Vai trò giao thông của nhóm làn trong profile MCN: INBOUND hoặc OUTBOUND.
        /// Profile là dữ liệu dùng lại cho cả hai approach của một nút, nên không thể
        /// lưu FORWARD/REVERSE tuyệt đối tại đây.
        /// </summary>
        public string TrafficRole { get; set; } = string.Empty;

        /// <summary>
        /// Trường tương thích schema cũ. UI trước schema 9 đã dùng FORWARD để biểu diễn
        /// nhóm INBOUND và REVERSE để biểu diễn nhóm OUTBOUND. DwgProjectStateStore
        /// tự migrate sang TrafficRole khi load.
        /// </summary>
        public string Direction { get; set; } = string.Empty;
    }

    public sealed class ArmBlockManagementRuleState
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string BlockName { get; set; } = string.Empty;
        public string MarkingCode { get; set; } = string.Empty;
        public string LayerTemplateId { get; set; } = string.Empty;
        public string TargetLayer { get; set; } = string.Empty;
        public bool Enabled { get; set; } = true;
    }

    public sealed class ArmManagedGroupState
    {
        public string GroupId { get; set; } = string.Empty;
        public string RoadKey { get; set; } = string.Empty;
        public string RoadIdentity { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public string MarkingCode { get; set; } = string.Empty;
        public string GenerationMode { get; set; } = string.Empty;
        public string Layer { get; set; } = string.Empty;
        public int EntityCount { get; set; }
        public double TotalLength { get; set; }
        public double TotalArea { get; set; }
        public string Status { get; set; } = "active";
        public bool IsLocked { get; set; }
        public bool IsOverride { get; set; }
        public List<string> EntityHandles { get; set; } = new List<string>();
    }
}
