using System;
using System.Collections.Generic;
using System.Linq;

using Autodesk.AutoCAD.DatabaseServices;

using Autoroadmarking_Pro.CadHost.Cad.Geometry;
using Autoroadmarking_Pro.CadHost.Cad.State;

using AcEntity = Autodesk.AutoCAD.DatabaseServices.Entity;
using CivilAlignment = Autodesk.Civil.DatabaseServices.Alignment;

namespace Autoroadmarking_Pro.CadHost.Cad.RoadAxis
{
    /// <summary>
    /// Danh mục RoadAxis hợp nhất của một DWG.
    ///
    /// - Polyline: chỉ xuất hiện khi đã được Tab 0 gắn ROAD_IDENTITY.
    /// - Civil Alignment: tự động xuất hiện bằng Alignment.Name; không ghi metadata
    ///   ROAD_IDENTITY và không bắt người dùng định danh lại.
    ///
    /// AxisKey là khóa kỹ thuật:
    ///   POLY:&lt;ROAD_RECORD_ID&gt;
    ///   ALN:&lt;AUTOCAD_HANDLE&gt;
    ///
    /// RoadName chỉ phục vụ hiển thị, layer và báo cáo. Vì vậy rename Alignment hoặc
    /// đổi tên Polyline ở Tab 0 không làm đổi khóa kỹ thuật của trục.
    /// </summary>
    public sealed class RoadAxisCatalogService
    {
        private readonly RoadIdentityCadService _identity =
            new RoadIdentityCadService();

        private readonly CadGeometryService _geometry =
            new CadGeometryService();

        private readonly StationOffsetService _station =
            new StationOffsetService();

        public List<ArmRoadAxisState> Build(
            Database db,
            Transaction tr,
            ArmProjectState state,
            bool refreshPolylineRegistry = true)
        {
            if (db == null)
                throw new ArgumentNullException(nameof(db));

            if (tr == null)
                throw new ArgumentNullException(nameof(tr));

            if (state == null)
                throw new ArgumentNullException(nameof(state));

            if (refreshPolylineRegistry)
                _identity.ReadAll(db, tr, state);

            var catalog =
                new List<ArmRoadAxisState>();

            // Persisted registry chỉ chứa Polyline Tab 0.
            foreach (ArmRoadAxisState source in state.RoadAxes)
            {
                ArmRoadAxisState? normalized =
                    NormalizePolyline(db, tr, source);

                if (normalized != null)
                    catalog.Add(normalized);
            }

            DiscoverAlignments(db, tr, catalog);
            RebindDependentState(state, catalog);

            return catalog
                .GroupBy(
                    x => x.EffectiveAxisKey,
                    StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .OrderBy(
                    x => x.RoadName,
                    StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.AxisType)
                .ThenBy(x => x.Handle)
                .ToList();
        }

        public ArmRoadAxisState ResolveDescriptor(
            Database db,
            Transaction tr,
            ArmProjectState state,
            string axisKeyOrName)
        {
            string query =
                (axisKeyOrName ?? string.Empty).Trim();

            if (query.Length == 0)
                throw new InvalidOperationException(
                    "Chưa chọn tuyến/RoadAxis.");

            List<ArmRoadAxisState> catalog =
                Build(
                    db,
                    tr,
                    state,
                    refreshPolylineRegistry: false);

            List<ArmRoadAxisState> exact =
                catalog.Where(x =>
                        string.Equals(
                            x.EffectiveAxisKey,
                            query,
                            StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(
                            x.RoadKey,
                            query,
                            StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(
                            x.RecordId,
                            query,
                            StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(
                            x.Handle,
                            query,
                            StringComparison.OrdinalIgnoreCase))
                    .ToList();

            if (exact.Count == 1)
                return exact[0];

            List<ArmRoadAxisState> legacy =
                catalog.Where(x =>
                        !string.IsNullOrWhiteSpace(x.LegacyRoadKey) &&
                        string.Equals(
                            x.LegacyRoadKey,
                            query,
                            StringComparison.OrdinalIgnoreCase))
                    .ToList();

            if (legacy.Count == 1)
                return legacy[0];

            List<ArmRoadAxisState> byName =
                catalog.Where(x =>
                        string.Equals(
                            x.RoadName,
                            query,
                            StringComparison.OrdinalIgnoreCase))
                    .ToList();

            if (byName.Count == 1)
                return byName[0];

            if (byName.Count > 1)
            {
                throw new InvalidOperationException(
                    "Tên tuyến '" +
                    query +
                    "' trùng trên nhiều RoadAxis. Hãy chọn theo AxisKey.");
            }

            throw new InvalidOperationException(
                "Không tìm thấy RoadAxis cho '" +
                query +
                "'. Polyline phải được định danh ở Tab 0; Alignment dùng trực tiếp tên Alignment.");
        }

        public AcEntity ResolveEntity(
            Database db,
            Transaction tr,
            ArmProjectState state,
            string axisKeyOrName,
            OpenMode mode = OpenMode.ForRead)
        {
            ArmRoadAxisState descriptor =
                ResolveDescriptor(
                    db,
                    tr,
                    state,
                    axisKeyOrName);

            ObjectId id =
                _geometry.FromHandle(
                    db,
                    descriptor.Handle);

            if (id.IsNull ||
                !(tr.GetObject(
                    id,
                    mode,
                    false) is AcEntity entity))
            {
                throw new InvalidOperationException(
                    "RoadAxis '" +
                    descriptor.RoadName +
                    "' không còn tồn tại trong DWG.");
            }

            return entity;
        }

        public string ResolveCurrentRoadName(
            Database db,
            Transaction tr,
            ArmProjectState state,
            string axisKey,
            string fallbackRoadName)
        {
            if (string.IsNullOrWhiteSpace(axisKey))
                return fallbackRoadName ?? string.Empty;

            try
            {
                return ResolveDescriptor(
                    db,
                    tr,
                    state,
                    axisKey).RoadName;
            }
            catch
            {
                // Quantity/traceability không được mất chỉ vì trục đã bị xóa.
                return fallbackRoadName ?? string.Empty;
            }
        }


        /// <summary>
        /// Rebind state phụ thuộc theo Handle/AxisKey. Đây là bước migration nhẹ,
        /// giúp dữ liệu cũ dùng RoadName làm khóa tiếp tục hoạt động và giúp rename
        /// Alignment không tạo một tuyến mới giả trong Tab 4/5/6.
        /// </summary>
        private static void RebindDependentState(
            ArmProjectState state,
            IReadOnlyList<ArmRoadAxisState> catalog)
        {
            foreach (ArmComparisonState comparison in state.ComparisonResults)
            {
                ArmRoadAxisState? axis = catalog.FirstOrDefault(x =>
                    !string.IsNullOrWhiteSpace(comparison.TimHandle) &&
                    string.Equals(x.Handle, comparison.TimHandle, StringComparison.OrdinalIgnoreCase));

                if (axis == null)
                    continue;

                comparison.RoadKey = axis.EffectiveAxisKey;
                comparison.Road = axis.RoadName;
            }

            foreach (ArmManagedGroupState group in state.ManagedGroups)
            {
                ArmRoadAxisState? axis = FindByAnyKey(catalog, group.RoadKey, group.RoadIdentity);
                if (axis == null)
                    continue;

                group.RoadKey = axis.EffectiveAxisKey;
                group.RoadIdentity = axis.RoadName;
            }

            foreach (ArmBlockProposalState proposal in state.BlockProposals)
            {
                ArmRoadAxisState? axis = catalog.FirstOrDefault(x =>
                    !string.IsNullOrWhiteSpace(proposal.AxisHandle) &&
                    string.Equals(x.Handle, proposal.AxisHandle, StringComparison.OrdinalIgnoreCase));

                if (axis == null)
                    axis = FindByAnyKey(catalog, proposal.RoadKey, proposal.Road);

                if (axis == null)
                    continue;

                proposal.RoadKey = axis.EffectiveAxisKey;
                proposal.Road = axis.RoadName;
            }
        }

        private static ArmRoadAxisState? FindByAnyKey(
            IReadOnlyList<ArmRoadAxisState> catalog,
            string key,
            string roadName)
        {
            List<ArmRoadAxisState> matches = catalog.Where(x =>
                    (!string.IsNullOrWhiteSpace(key) &&
                     (string.Equals(x.EffectiveAxisKey, key, StringComparison.OrdinalIgnoreCase) ||
                      string.Equals(x.LegacyRoadKey, key, StringComparison.OrdinalIgnoreCase))) ||
                    (!string.IsNullOrWhiteSpace(roadName) &&
                     string.Equals(x.RoadName, roadName, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            return matches.Count == 1 ? matches[0] : null;
        }

        private ArmRoadAxisState? NormalizePolyline(
            Database db,
            Transaction tr,
            ArmRoadAxisState source)
        {
            ObjectId id =
                _geometry.FromHandle(
                    db,
                    source.Handle);

            if (id.IsNull ||
                !(tr.GetObject(
                    id,
                    OpenMode.ForRead,
                    false) is AcEntity entity))
            {
                return null;
            }

            double length =
                _station.Length(entity);

            string axisKey =
                !string.IsNullOrWhiteSpace(source.AxisKey)
                    ? source.AxisKey
                    : (!string.IsNullOrWhiteSpace(source.RecordId)
                        ? "POLY:" + source.RecordId
                        : "POLY:" + source.Handle);

            return new ArmRoadAxisState
            {
                RecordId = source.RecordId,
                AxisKey = axisKey,
                RoadName = source.RoadName,
                RoadKey = axisKey,
                LegacyRoadKey = source.LegacyRoadKey,
                Handle = source.Handle,
                EntityType = entity.GetType().Name,
                AxisType = "POLYLINE",
                IdentitySource = "TAB0_METADATA",
                IsNativeNamed = false,
                Layer = entity.Layer,
                OriginalLayer = source.OriginalLayer,
                Length = length,
                StartStation = 0.0,
                EndStation = length,
                OrientationSign =
                    source.OrientationSign < 0
                        ? -1
                        : 1
            };
        }

        private void DiscoverAlignments(
            Database db,
            Transaction tr,
            List<ArmRoadAxisState> catalog)
        {
            BlockTableRecord modelSpace =
                (BlockTableRecord)tr.GetObject(
                    SymbolUtilityServices.GetBlockModelSpaceId(db),
                    OpenMode.ForRead);

            foreach (ObjectId id in modelSpace)
            {
                if (!(tr.GetObject(
                        id,
                        OpenMode.ForRead,
                        false) is CivilAlignment alignment))
                {
                    continue;
                }

                string handle =
                    id.Handle.ToString();

                string axisKey =
                    "ALN:" + handle;

                if (catalog.Any(x =>
                    string.Equals(
                        x.EffectiveAxisKey,
                        axisKey,
                        StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                double start =
                    alignment.StartingStation;

                double end =
                    alignment.EndingStation;

                catalog.Add(
                    new ArmRoadAxisState
                    {
                        RecordId = "ALIGNMENT_" + handle,
                        AxisKey = axisKey,
                        RoadName = alignment.Name ?? string.Empty,
                        RoadKey = axisKey,
                        LegacyRoadKey = string.Empty,
                        Handle = handle,
                        EntityType = alignment.GetType().Name,
                        AxisType = "ALIGNMENT",
                        IdentitySource = "CIVIL_ALIGNMENT",
                        IsNativeNamed = true,
                        Layer = alignment.Layer,
                        OriginalLayer = alignment.Layer,
                        Length = Math.Max(0.0, alignment.Length),
                        StartStation = start,
                        EndStation = end,
                        OrientationSign = 1
                    });
            }
        }
    }
}
