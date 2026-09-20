using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Autodesk.AutoCAD.DatabaseServices;
using Autoroadmarking_Pro.Domain.Metadata;
using Autoroadmarking_Pro.Domain.Quantities;

namespace Autoroadmarking_Pro.CadHost.Cad.State
{
    /// <summary>
    /// Project state theo DWG, lưu trong Named Object Dictionary.
    /// Không seed kích thước QCVN trong C#: Tab 1 là nguồn template duy nhất.
    /// </summary>
    public sealed class DwgProjectStateStore
    {
        private readonly NamedObjectDictionaryService _nod =
            new NamedObjectDictionaryService();

        private readonly JsonSerializerOptions _options =
            new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                WriteIndented = false
            };

        public ArmProjectState Load(
            Database db,
            Transaction tr)
        {
            string json =
                _nod.ReadText(
                    db,
                    tr,
                    ArmMetadataKeys.ProjectStateKey);

            if (string.IsNullOrWhiteSpace(json))
                return CreateDefault();

            try
            {
                ArmProjectState state =
                    JsonSerializer.Deserialize<ArmProjectState>(
                        json,
                        _options)
                    ?? CreateDefault();

                EnsureCollections(state);
                return state;
            }
            catch
            {
                // State hỏng không làm hỏng DWG; trả state rỗng để người dùng
                // đồng bộ lại Tab 1 / Tab 2. Không tự bịa template.
                return CreateDefault();
            }
        }

        public void Save(
            Database db,
            Transaction tr,
            ArmProjectState state)
        {
            EnsureCollections(state);

            string json =
                JsonSerializer.Serialize(
                    state,
                    _options);

            _nod.WriteText(
                db,
                tr,
                ArmMetadataKeys.ProjectStateKey,
                json);
        }

        public static ArmProjectState CreateDefault()
        {
            var state =
                new ArmProjectState();

            EnsureCollections(state);
            return state;
        }

        private static void EnsureCollections(
            ArmProjectState state)
        {
            if (state.RoadAxes == null)
                state.RoadAxes = new List<ArmRoadAxisState>();

            if (state.MarkingTemplates == null)
                state.MarkingTemplates = new List<ArmMarkingTemplateState>();

            if (state.CrossSections == null)
                state.CrossSections = new List<ArmCrossSectionState>();

            if (state.ActiveCrossSectionIds == null)
                state.ActiveCrossSectionIds = new List<string>();

            // Dọn ID MCN đã xóa/đổi tên để tập đồng bộ không chứa tham chiếu mồ côi.
            var existingCrossSectionIds = new HashSet<string>(
                state.CrossSections
                    .Where(x => !string.IsNullOrWhiteSpace(x.Id))
                    .Select(x => x.Id),
                System.StringComparer.OrdinalIgnoreCase);
            state.ActiveCrossSectionIds = state.ActiveCrossSectionIds
                .Where(x => !string.IsNullOrWhiteSpace(x) && existingCrossSectionIds.Contains(x))
                .Distinct(System.StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (state.SelectedTimHandles == null)
                state.SelectedTimHandles = new List<string>();

            if (state.SelectedEdgeHandles == null)
                state.SelectedEdgeHandles = new List<string>();

            if (state.SelectedComponentEdgeHandles == null)
                state.SelectedComponentEdgeHandles = new List<string>();

            if (state.ComparisonResults == null)
                state.ComparisonResults = new List<ArmComparisonState>();

            if (state.IntersectionPolygonHandles == null)
                state.IntersectionPolygonHandles = new List<string>();

            if (state.BlockCatalog == null)
                state.BlockCatalog = new List<ArmBlockCatalogState>();

            if (state.BlockProposals == null)
                state.BlockProposals = new List<ArmBlockProposalState>();

            if (state.SymbolProfiles == null)
                state.SymbolProfiles = new List<ArmSymbolProfileState>();

            if (state.ManualLayerRules == null)
                state.ManualLayerRules = new List<ManualLayerRule>();

            if (state.BlockManagementRules == null)
                state.BlockManagementRules = new List<ArmBlockManagementRuleState>();

            if (state.ManagedGroups == null)
                state.ManagedGroups = new List<ArmManagedGroupState>();

            NormalizeRoadAxes(state);
            NormalizeSymbolProfiles(state);
            NormalizeMarkingTemplates(state);

            if (state.StopToCrosswalkDistance < 0.10 ||
                double.IsNaN(state.StopToCrosswalkDistance) ||
                double.IsInfinity(state.StopToCrosswalkDistance))
                state.StopToCrosswalkDistance = 2.0;

            if (state.SchemaVersion < 11)
                state.SchemaVersion = 11;
        }

        private static void NormalizeMarkingTemplates(ArmProjectState state)
        {
            foreach (ArmMarkingTemplateState template in state.MarkingTemplates)
            {
                template.Code = (template.Code ?? string.Empty).Trim();
                template.Layer = (template.Layer ?? string.Empty).Trim();
                template.Pattern = string.IsNullOrWhiteSpace(template.Pattern)
                    ? "CUSTOM_REAL"
                    : template.Pattern.Trim().ToUpperInvariant();

                // Migration của tên mã đã chốt trong project: GTT -> GGT.
                if (template.Code.Equals("GTT", System.StringComparison.OrdinalIgnoreCase))
                    template.Code = "GGT";
                if (template.Layer.IndexOf("GTT", System.StringComparison.OrdinalIgnoreCase) >= 0)
                    template.Layer = System.Text.RegularExpressions.Regex.Replace(
                        template.Layer, "GTT", "GGT", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (string.Equals(template.Id, "ARM_GTT", System.StringComparison.OrdinalIgnoreCase))
                    template.Id = "ARM_GGT";

                if (template.CustomProperties == null)
                    template.CustomProperties = new Dictionary<string, string>();

                // CSV v6 cũ từng ghi lặp {"code":"GGT"} trong CustomProperties.
                // Sau khi Code trở thành field typed/reserved, chỉ xóa đúng key legacy
                // nếu giá trị trùng hoàn toàn với Code typed để không làm mất metadata dự án.
                string? redundantCodeKey = null;
                foreach (KeyValuePair<string, string> pair in template.CustomProperties)
                {
                    if (pair.Key.Equals("code", System.StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(pair.Value?.Trim(), template.Code, System.StringComparison.OrdinalIgnoreCase))
                    {
                        redundantCodeKey = pair.Key;
                        break;
                    }
                }

                // Fix CS8604: Provide an empty string fallback if null
                if (!string.IsNullOrWhiteSpace(redundantCodeKey))
                    template.CustomProperties.Remove(redundantCodeKey ?? string.Empty);

                if (string.IsNullOrWhiteSpace(template.ManagementSchema))
                    template.ManagementSchema = "ARM_QTY_TEMPLATE";
                if (template.ManagementVersion < 1)
                    template.ManagementVersion = 1;
                if (string.IsNullOrWhiteSpace(template.ManagementState))
                    template.ManagementState = "DIRTY";

                string normalizedCode = template.Code.ToUpperInvariant();
                if (normalizedCode == "7.3" || normalizedCode == "GGT")
                    template.QuantityMethod = "GENERATED";
                else if (string.IsNullOrWhiteSpace(template.QuantityMethod))
                    template.QuantityMethod = "LENGTH";
            }
        }

        private static void NormalizeSymbolProfiles(ArmProjectState state)
        {
            foreach (ArmSymbolProfileState profile in state.SymbolProfiles)
            {
                if (profile.Lanes == null)
                    profile.Lanes = new List<ArmSymbolLaneProfileState>();

                foreach (ArmSymbolLaneProfileState lane in profile.Lanes)
                {
                    string role = (lane.TrafficRole ?? string.Empty).Trim().ToUpperInvariant();
                    if (role != "INBOUND" && role != "OUTBOUND")
                    {
                        // Schema <= 8: FORWARD/REVERSE trong profile không phải hướng
                        // tuyệt đối của Alignment; UI dùng chúng để mã hóa IN/OUT.
                        string legacy = (lane.Direction ?? string.Empty).Trim().ToUpperInvariant();
                        role = legacy == "REVERSE" || legacy == "OUTBOUND"
                            ? "OUTBOUND"
                            : "INBOUND";
                    }

                    lane.TrafficRole = role;
                    // Không tiếp tục phát tán nghĩa cũ sang thuật toán mới.
                    lane.Direction = string.Empty;
                }
            }
        }

        private static void NormalizeRoadAxes(
            ArmProjectState state)
        {
            foreach (ArmRoadAxisState axis in state.RoadAxes)
            {
                axis.RecordId = (axis.RecordId ?? string.Empty).Trim();
                axis.RoadName = (axis.RoadName ?? string.Empty).Trim();
                axis.Handle = (axis.Handle ?? string.Empty).Trim();

                string previousRoadKey = (axis.RoadKey ?? string.Empty).Trim();

                if (string.IsNullOrWhiteSpace(axis.AxisKey))
                {
                    string stablePart = !string.IsNullOrWhiteSpace(axis.RecordId)
                        ? axis.RecordId
                        : axis.Handle;

                    axis.AxisKey = "POLY:" + stablePart;
                }

                if (string.IsNullOrWhiteSpace(axis.LegacyRoadKey) &&
                    !string.IsNullOrWhiteSpace(previousRoadKey) &&
                    !string.Equals(previousRoadKey, axis.AxisKey, System.StringComparison.OrdinalIgnoreCase))
                {
                    axis.LegacyRoadKey = previousRoadKey;
                }

                axis.RoadKey = axis.AxisKey;

                if (string.IsNullOrWhiteSpace(axis.AxisType))
                    axis.AxisType = "POLYLINE";

                if (string.IsNullOrWhiteSpace(axis.IdentitySource))
                    axis.IdentitySource = "TAB0_METADATA";

                axis.IsNativeNamed = false;

                if (axis.EndStation <= axis.StartStation)
                {
                    axis.StartStation = 0.0;
                    axis.EndStation = System.Math.Max(0.0, axis.Length);
                }
            }
        }
    }
}