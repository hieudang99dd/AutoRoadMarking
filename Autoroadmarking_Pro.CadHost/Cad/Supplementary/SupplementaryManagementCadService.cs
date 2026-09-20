using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autoroadmarking_Pro.CadHost.Cad.Geometry;
using Autoroadmarking_Pro.CadHost.Cad.Metadata;
using Autoroadmarking_Pro.CadHost.Cad.RoadAxis;
using Autoroadmarking_Pro.CadHost.Cad.State;

namespace Autoroadmarking_Pro.CadHost.Cad.Supplementary
{
    /// <summary>
    /// Quản lý đối tượng phát sinh và Block có sẵn. Không đoán layer theo tên nếu đã
    /// có ARM metadata; metadata luôn là nguồn ưu tiên.
    /// </summary>
    public sealed class SupplementaryManagementCadService
    {
        private readonly EntityMetadataStore _metadata = new EntityMetadataStore();
        private readonly ArmMetadataMapper _mapper = new ArmMetadataMapper();
        private readonly CadGeometryService _geometry = new CadGeometryService();

        public List<object> ScanUnmanagedBlocks(Database db, Transaction tr, string scopeLayer = "")
        {
            BlockTableRecord modelSpace = (BlockTableRecord)tr.GetObject(
                SymbolUtilityServices.GetBlockModelSpaceId(db),
                OpenMode.ForRead);

            var result = new List<object>();
            foreach (ObjectId id in modelSpace)
            {
                if (!(tr.GetObject(id, OpenMode.ForRead, false) is BlockReference block))
                    continue;
                if (!string.IsNullOrWhiteSpace(scopeLayer) && !string.Equals(block.Layer, scopeLayer, StringComparison.OrdinalIgnoreCase))
                    continue;

                ArmEntityMetadata? md = _metadata.Read(block, tr);
                bool managed = _mapper.IsValidManagedRecord(md);
                result.Add(new
                {
                    handle = id.Handle.ToString(),
                    layer = block.Layer,
                    type = block.GetType().Name,
                    blockName = SupplementaryMarkingCadService.ReadBlockName(block, tr),
                    isManaged = managed,
                    armEntityId = md?.RecordId ?? string.Empty,
                    groupId = ReadExtra(md, "GroupId")
                });
            }
            return result;
        }

        public int ApplyBlockRules(
            Database db,
            Transaction tr,
            ArmProjectState state,
            IEnumerable<string> handles,
            string roadKey,
            string roadName,
            string groupId)
        {
            ArmRoadAxisState axisInfo =
                new RoadAxisCatalogService()
                    .ResolveDescriptor(
                        db,
                        tr,
                        state,
                        roadKey);

            roadKey = axisInfo.EffectiveAxisKey;
            roadName = axisInfo.RoadName;

            int count = 0;
            foreach (string handle in handles ?? Enumerable.Empty<string>())
            {
                if (!SupplementaryMarkingCadService.TryGetObjectId(db, handle, out ObjectId id))
                    continue;
                if (!(tr.GetObject(id, OpenMode.ForWrite, false) is BlockReference block))
                    continue;

                string blockName = SupplementaryMarkingCadService.ReadBlockName(block, tr);
                ArmBlockManagementRuleState? rule = state.BlockManagementRules.FirstOrDefault(x =>
                    x.Enabled && string.Equals(x.BlockName, blockName, StringComparison.OrdinalIgnoreCase));
                if (rule == null)
                    continue;

                ArmMarkingTemplateState? template = state.MarkingTemplates.FirstOrDefault(x =>
                    string.Equals(x.Id, rule.LayerTemplateId, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(x.Code, rule.MarkingCode, StringComparison.OrdinalIgnoreCase));

                string targetLayer = !string.IsNullOrWhiteSpace(rule.TargetLayer)
                    ? rule.TargetLayer
                    : template?.Layer ?? block.Layer;

                // BlockReference hiện hữu là dữ liệu thiết kế bên ngoài ARM.
                // Không đổi layer/hình học; chỉ gắn metadata quản lý và mapping rule.
                var md = new ArmEntityMetadata
                {
                    RecordId = "BLK_EXT_" + Guid.NewGuid().ToString("N"),
                    GenerationKey = $"EXTERNAL_BLOCK|{roadKey}|{groupId}|{handle}",
                    Source = "SUPPLEMENTARY_EXTERNAL_BLOCK",
                    RoadName = roadName,
                    AxisKey = roadKey,
                    AxisHandle = axisInfo.Handle,
                    AxisType = axisInfo.AxisType,
                    RoadKey = roadKey,
                    OwnerType = "ROAD",
                    OwnerId = groupId,
                    MarkingCode = rule.MarkingCode,
                    TemplateId = template?.Id ?? rule.LayerTemplateId,
                    TemplateLayer = template?.Layer ?? targetLayer,
                    CadLayer = block.Layer,
                    BlockName = blockName,
                    QuantityCount = 1
                };
                md.Extra["GroupId"] = groupId;
                md.Extra["SupplementaryType"] = "manual_block";
                md.Extra["QuantityMode"] = "COUNT";
                _metadata.Write(block, tr, _mapper.Normalize(md, block));
                count++;
            }
            return count;
        }

        public int SetGroupLock(Database db, Transaction tr, ArmProjectState state, string groupId, bool locked)
        {
            ArmManagedGroupState? group = state.ManagedGroups.FirstOrDefault(x =>
                string.Equals(x.GroupId, groupId, StringComparison.OrdinalIgnoreCase));
            if (group != null)
            {
                group.IsLocked = locked;
                group.Status = locked ? "locked" : "active";
            }

            int count = 0;
            foreach (Entity entity in EnumerateGroupEntities(db, tr, groupId, OpenMode.ForWrite))
            {
                ArmEntityMetadata? md = _metadata.Read(entity, tr);
                if (md == null) continue;
                md.Extra["Locked"] = locked ? "true" : "false";
                _metadata.Write(entity, tr, md);
                count++;
            }
            return count;
        }

        public int RemoveManagement(Database db, Transaction tr, ArmProjectState state, string groupId)
        {
            int count = 0;
            foreach (Entity entity in EnumerateGroupEntities(db, tr, groupId, OpenMode.ForWrite))
            {
                _metadata.Remove(entity, tr);
                count++;
            }
            state.ManagedGroups.RemoveAll(x => string.Equals(x.GroupId, groupId, StringComparison.OrdinalIgnoreCase));
            return count;
        }

        public bool ZoomGroup(Database db, Transaction tr, Autodesk.AutoCAD.EditorInput.Editor editor, string groupId)
        {
            Extents3d? extents = null;
            foreach (Entity entity in EnumerateGroupEntities(db, tr, groupId, OpenMode.ForRead))
            {
                try
                {
                    Extents3d current = entity.GeometricExtents;
                    extents = extents == null ? current : Union(extents.Value, current);
                }
                catch { }
            }
            if (extents == null) return false;
            _geometry.ZoomToExtents(editor, extents.Value);
            return true;
        }

        public ArmManagedGroupState UpsertGroup(
            ArmProjectState state,
            string groupId,
            string roadKey,
            string roadName,
            string type,
            string code,
            string mode,
            string layer,
            int count,
            double totalLength,
            double totalArea,
            IEnumerable<string> handles)
        {
            ArmManagedGroupState? group = state.ManagedGroups.FirstOrDefault(x =>
                string.Equals(x.GroupId, groupId, StringComparison.OrdinalIgnoreCase));
            if (group == null)
            {
                group = new ArmManagedGroupState { GroupId = groupId };
                state.ManagedGroups.Add(group);
            }

            group.RoadKey = roadKey;
            group.RoadIdentity = roadName;
            group.Type = type;
            group.MarkingCode = code;
            group.GenerationMode = mode;
            group.Layer = layer;
            group.EntityCount = count;
            group.TotalLength = totalLength;
            group.TotalArea = totalArea;
            group.EntityHandles = (handles ?? Enumerable.Empty<string>()).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            group.Status = group.IsLocked ? "locked" : "active";
            return group;
        }

        private IEnumerable<Entity> EnumerateGroupEntities(Database db, Transaction tr, string groupId, OpenMode mode)
        {
            if (string.IsNullOrWhiteSpace(groupId)) yield break;
            BlockTableRecord modelSpace = (BlockTableRecord)tr.GetObject(
                SymbolUtilityServices.GetBlockModelSpaceId(db),
                OpenMode.ForRead);
            foreach (ObjectId id in modelSpace)
            {
                if (!(tr.GetObject(id, OpenMode.ForRead, false) is Entity readEntity))
                    continue;
                ArmEntityMetadata? md = _metadata.Read(readEntity, tr);
                if (!string.Equals(ReadExtra(md, "GroupId"), groupId, StringComparison.OrdinalIgnoreCase))
                    continue;
                yield return mode == OpenMode.ForRead
                    ? readEntity
                    : (Entity)tr.GetObject(id, OpenMode.ForWrite, false);
            }
        }

        private static string ReadExtra(ArmEntityMetadata? metadata, string key)
        {
            if (metadata?.Extra != null && metadata.Extra.TryGetValue(key, out string? value))
                return value ?? string.Empty;
            return string.Empty;
        }

        private static Extents3d Union(Extents3d a, Extents3d b) => new Extents3d(
            new Autodesk.AutoCAD.Geometry.Point3d(
                Math.Min(a.MinPoint.X, b.MinPoint.X),
                Math.Min(a.MinPoint.Y, b.MinPoint.Y),
                Math.Min(a.MinPoint.Z, b.MinPoint.Z)),
            new Autodesk.AutoCAD.Geometry.Point3d(
                Math.Max(a.MaxPoint.X, b.MaxPoint.X),
                Math.Max(a.MaxPoint.Y, b.MaxPoint.Y),
                Math.Max(a.MaxPoint.Z, b.MaxPoint.Z)));
    }
}
