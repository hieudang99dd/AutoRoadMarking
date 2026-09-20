using System;
using System.Collections.Generic;
using System.Linq;

using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;

using Autoroadmarking_Pro.CadHost.Cad.Geometry;
using Autoroadmarking_Pro.CadHost.Cad.Layers;
using Autoroadmarking_Pro.CadHost.Cad.Metadata;
using Autoroadmarking_Pro.CadHost.Cad.Selection;
using Autoroadmarking_Pro.CadHost.Cad.State;

using AcEntity = Autodesk.AutoCAD.DatabaseServices.Entity;
using CivilAlignment = Autodesk.Civil.DatabaseServices.Alignment;

namespace Autoroadmarking_Pro.CadHost.Cad.RoadAxis
{
    /// <summary>
    /// Định danh TIM CAD / Alignment và lưu traceability trực tiếp trên entity.
    /// RoadKey là khóa ổn định; RoadName là tên hiển thị và dùng cho layer/report.
    /// </summary>
    public sealed class RoadIdentityCadService
    {
        private readonly CadSelectionService _selection = new CadSelectionService();
        private readonly EntityMetadataStore _metadata = new EntityMetadataStore();
        private readonly CadGeometryService _geometry = new CadGeometryService();
        private readonly StationOffsetService _station = new StationOffsetService();

        public object SelectAxis(Document doc, Transaction tr)
        {
            ObjectId id = _selection.SelectCadPolyline(
                doc.Editor,
                tr,
                "Chọn Polyline TIM CAD:");

            if (id.IsNull)
                throw new InvalidOperationException("Đã hủy chọn TIM.");

            var entity = (AcEntity)tr.GetObject(id, OpenMode.ForRead);
            ArmEntityMetadata? md = _metadata.Read(entity, tr);

            string roadName =
                md?.RoadName ??
                string.Empty;

            return new
            {
                selectionToken = Guid.NewGuid().ToString("N"),
                recordId = md?.RecordId ?? string.Empty,
                objectHandle = id.Handle.ToString(),
                entityType = entity.GetType().Name,
                currentLayer = entity.Layer,
                originalLayer =
                    md?.Extra != null &&
                    md.Extra.TryGetValue("OriginalLayer", out string? original)
                        ? original
                        : entity.Layer,
                roadName,
                roadKey = md?.AxisKey ?? md?.RoadKey ?? NormalizeRoadKey(roadName),
                axisKey = md?.AxisKey ?? md?.RoadKey ?? string.Empty,
                length = _station.Length(entity)
            };
        }

        public ArmRoadAxisState Assign(
            Database db,
            Transaction tr,
            ArmProjectState state,
            string handle,
            string roadName,
            string roadKey,
            string targetLayer)
        {
            roadName = (roadName ?? string.Empty).Trim();

            if (string.IsNullOrWhiteSpace(roadName))
                throw new InvalidOperationException("Tên tuyến không được để trống.");

            if (string.IsNullOrWhiteSpace(roadKey))
                roadKey = NormalizeRoadKey(roadName);

            ObjectId id = _geometry.FromHandle(db, handle);

            if (id.IsNull)
                throw new InvalidOperationException(
                    "Không tìm thấy TIM theo Handle " + handle + ".");

            var entity = (AcEntity)tr.GetObject(id, OpenMode.ForWrite);

            if (!(entity is Polyline) &&
                !(entity is Polyline2d) &&
                !(entity is Polyline3d))
            {
                throw new InvalidOperationException(
                    "Tab 0 chỉ định danh TIM CAD dạng Polyline.");
            }

            ArmEntityMetadata? old = _metadata.Read(entity, tr);

            string originalLayer =
                old?.Extra != null &&
                old.Extra.TryGetValue("OriginalLayer", out string? storedLayer)
                    ? storedLayer
                    : entity.Layer;

            string recordId =
                string.IsNullOrWhiteSpace(old?.RecordId)
                    ? "ROAD_" + Guid.NewGuid().ToString("N")
                    : old!.RecordId;

            string legacyRoadKey =
                !string.IsNullOrWhiteSpace(old?.RoadKey)
                    ? old!.RoadKey
                    : (roadKey ?? string.Empty).Trim();

            string axisKey =
                !string.IsNullOrWhiteSpace(old?.AxisKey)
                    ? old!.AxisKey
                    : "POLY:" + recordId;

            string layerName =
                string.IsNullOrWhiteSpace(targetLayer)
                    ? roadName
                    : targetLayer.Trim();

            new CadLayerService().EnsureLayer(db, tr, layerName);
            entity.Layer = layerName;

            double axisLength = _station.Length(entity);

            var record = new ArmRoadAxisState
            {
                RecordId = recordId,
                AxisKey = axisKey,
                RoadName = roadName,
                RoadKey = axisKey,
                LegacyRoadKey = string.Equals(legacyRoadKey, axisKey, StringComparison.OrdinalIgnoreCase)
                    ? string.Empty
                    : legacyRoadKey,
                Handle = handle,
                EntityType = entity.GetType().Name,
                AxisType = "POLYLINE",
                IdentitySource = "TAB0_METADATA",
                IsNativeNamed = false,
                Layer = entity.Layer,
                OriginalLayer = originalLayer,
                Length = axisLength,
                StartStation = 0.0,
                EndStation = axisLength,
                OrientationSign = 1
            };

            var metadata = new ArmEntityMetadata
            {
                Schema = "2.0",
                RecordId = record.RecordId,
                Source = "ROAD_IDENTITY",
                RoadName = roadName,
                AxisKey = axisKey,
                AxisHandle = handle,
                AxisType = "POLYLINE",
                RoadKey = axisKey,
                OwnerType = "ROAD_AXIS",
                OwnerId = record.RecordId,
                CadLayer = entity.Layer
            };

            metadata.Extra["OriginalLayer"] = originalLayer;
            metadata.Extra["AxisType"] = record.EntityType;
            metadata.Extra["OrientationSign"] = record.OrientationSign.ToString();
            if (!string.IsNullOrWhiteSpace(record.LegacyRoadKey))
                metadata.Extra["LegacyRoadKey"] = record.LegacyRoadKey;

            _metadata.Write(entity, tr, metadata);

            state.RoadAxes.RemoveAll(x =>
                string.Equals(x.Handle, handle, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(x.RecordId, record.RecordId, StringComparison.OrdinalIgnoreCase));

            state.RoadAxes.Add(record);

            return record;
        }

        public List<ArmRoadAxisState> ReadAll(
            Database db,
            Transaction tr,
            ArmProjectState state)
        {
            var found = new List<ArmRoadAxisState>();

            var blockTable =
                (BlockTable)tr.GetObject(
                    db.BlockTableId,
                    OpenMode.ForRead);

            var modelSpace =
                (BlockTableRecord)tr.GetObject(
                    blockTable[BlockTableRecord.ModelSpace],
                    OpenMode.ForRead);

            foreach (ObjectId id in modelSpace)
            {
                if (!(tr.GetObject(id, OpenMode.ForRead, false) is AcEntity entity))
                    continue;

                ArmEntityMetadata? md = _metadata.Read(entity, tr);

                if (md == null ||
                    !string.Equals(
                        md.Source,
                        "ROAD_IDENTITY",
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                int orientationSign = 1;

                if (md.Extra != null &&
                    md.Extra.TryGetValue("OrientationSign", out string? signText) &&
                    int.TryParse(signText, out int parsedSign) &&
                    parsedSign != 0)
                {
                    orientationSign = parsedSign < 0 ? -1 : 1;
                }

                string handle = id.Handle.ToString();
                string axisKey = !string.IsNullOrWhiteSpace(md.AxisKey)
                    ? md.AxisKey
                    : "POLY:" + md.RecordId;
                string legacyRoadKey =
                    md.Extra != null &&
                    md.Extra.TryGetValue("LegacyRoadKey", out string? legacy)
                        ? legacy ?? string.Empty
                        : (!string.IsNullOrWhiteSpace(md.RoadKey) &&
                           !string.Equals(md.RoadKey, axisKey, StringComparison.OrdinalIgnoreCase)
                            ? md.RoadKey
                            : string.Empty);
                double axisLength = _station.Length(entity);

                found.Add(new ArmRoadAxisState
                {
                    RecordId = md.RecordId,
                    AxisKey = axisKey,
                    RoadName = md.RoadName,
                    RoadKey = axisKey,
                    LegacyRoadKey = legacyRoadKey,
                    Handle = handle,
                    EntityType = entity.GetType().Name,
                    AxisType = "POLYLINE",
                    IdentitySource = "TAB0_METADATA",
                    IsNativeNamed = false,
                    Layer = entity.Layer,
                    OriginalLayer =
                        md.Extra != null &&
                        md.Extra.TryGetValue("OriginalLayer", out string? original)
                            ? original
                            : entity.Layer,
                    Length = axisLength,
                    StartStation = 0.0,
                    EndStation = axisLength,
                    OrientationSign = orientationSign
                });
            }

            state.RoadAxes = found;

            return found
                .OrderBy(x => x.RoadName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public void Remove(
            Database db,
            Transaction tr,
            ArmProjectState state,
            string recordId,
            bool restoreLayer)
        {
            ArmRoadAxisState? item =
                state.RoadAxes.FirstOrDefault(x =>
                    string.Equals(
                        x.RecordId,
                        recordId,
                        StringComparison.OrdinalIgnoreCase));

            if (item == null)
                return;

            ObjectId id = _geometry.FromHandle(db, item.Handle);

            if (!id.IsNull &&
                tr.GetObject(id, OpenMode.ForWrite, false) is AcEntity entity)
            {
                if (restoreLayer &&
                    !string.IsNullOrWhiteSpace(item.OriginalLayer))
                {
                    new CadLayerService().EnsureLayer(
                        db,
                        tr,
                        item.OriginalLayer);

                    entity.Layer = item.OriginalLayer;
                }

                _metadata.Remove(entity, tr);
            }

            state.RoadAxes.RemoveAll(x =>
                string.Equals(
                    x.RecordId,
                    recordId,
                    StringComparison.OrdinalIgnoreCase));
        }

        public void Zoom(
            Database db,
            Transaction tr,
            Autodesk.AutoCAD.EditorInput.Editor editor,
            string recordId)
        {
            ObjectId id = _geometry.FromHandle(db, recordId);

            if (id.IsNull)
            {
                ArmProjectState state =
                    new DwgProjectStateStore()
                        .Load(db, tr);

                ArmRoadAxisState? item =
                    state.RoadAxes.FirstOrDefault(x =>
                        string.Equals(
                            x.RecordId,
                            recordId,
                            StringComparison.OrdinalIgnoreCase));

                if (item != null)
                    id = _geometry.FromHandle(db, item.Handle);
            }

            if (!id.IsNull &&
                tr.GetObject(id, OpenMode.ForRead, false) is AcEntity entity)
            {
                _geometry.ZoomToEntity(editor, entity);
            }
        }

        private static string NormalizeRoadKey(string value)
        {
            string key =
                (value ?? string.Empty)
                .Trim()
                .ToUpperInvariant()
                .Replace('Đ', 'D');

            while (key.Contains("  "))
                key = key.Replace("  ", " ");

            return key.Replace(" ", "_");
        }
    }
}
