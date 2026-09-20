using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autoroadmarking_Pro.CadHost.Cad.Blocks;
using Autoroadmarking_Pro.CadHost.Cad.Geometry;
using Autoroadmarking_Pro.CadHost.Cad.Intersections;
using Autoroadmarking_Pro.CadHost.Cad.Layers;
using Autoroadmarking_Pro.CadHost.Cad.Markings;
using Autoroadmarking_Pro.CadHost.Cad.Metadata;
using Autoroadmarking_Pro.CadHost.Cad.Quantities;
using Autoroadmarking_Pro.CadHost.Cad.RoadAxis;
using Autoroadmarking_Pro.CadHost.Cad.Selection;
using Autoroadmarking_Pro.CadHost.Cad.State;
using Autoroadmarking_Pro.CadHost.Cad.Supplementary;
using Autoroadmarking_Pro.CadHost.UI;
using Autoroadmarking_Pro.Application.Intersections;
using Autoroadmarking_Pro.Application.Markings;

namespace Autoroadmarking_Pro.CadHost.Cad
{
    /// <summary>
    /// Application gateway giữa WebView2 UI 6.1 và AutoCAD/Civil 3D.
    /// Chỉ lớp này hiểu tên action của UI. Các thuật toán hình học nằm trong
    /// service chuyên trách; state bền vững nằm trong DWG NOD/XRecord.
    /// </summary>
    public sealed class CadActionExecutor
    {
        private readonly DwgProjectStateStore _stateStore = new DwgProjectStateStore();
        private readonly CadGeometryService _geometry = new CadGeometryService();
        private readonly JsonSerializerOptions _json = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        public static bool IsInteractive(string action)
        {
            if (string.IsNullOrWhiteSpace(action)) return false;
            switch (action)
            {
                case "SelectCadAxisForNaming":
                case "SelectTimCAD":
                case "SelectMepCAD":
                case "SelectMepCauKienCAD":
                case "SelectSymbolLibraryFolder":
                case "SelectSupplementaryBoundary":
                case "SelectSupplementaryEntities":
                case "SelectSupplementaryBlocks":
                case "SelectSupplementaryStation":
                case "DrawSupplementaryManualPolyline":
                    return true;
                default:
                    return false;
            }
        }

        public static bool IsInteractive(string action, JsonElement payload)
        {
            // Toàn bộ DrawMarkingsCAD hiện chạy tự động. Step2_VeDaGiacNut không còn
            // gọi Editor.GetPoint: backend phân sector theo các nhánh TIM, chọn sừng bò
            // theo tiếp tuyến và fallback sang cổ nút ổn định nếu dữ liệu cong không đủ.
            if (string.Equals(action, "DrawMarkingsCAD", StringComparison.OrdinalIgnoreCase))
                return false;

            return IsInteractive(action);
        }

        public WebResponse Execute(string action, JsonElement payload)
        {
            Document? doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return WebResponse.Fail(action, "Không có bản vẽ AutoCAD đang hoạt động.");
            Database db = doc.Database;

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                ArmProjectState state = _stateStore.Load(db, tr);
                WebResponse response;

                switch (action)
                {
                    case "Ping":
                        response = WebResponse.Ok(
                            action,
                            "CAD host sẵn sàng.",
                            new { schema = "ARM/6.1", stateSchema = state.SchemaVersion });
                        break;

                    // TAB 0 - ROAD IDENTITY
                    case "ReadCadAxisIdentitiesCAD": response = RoadRead(action, db, tr, state); break;
                    case "SelectCadAxisForNaming": response = WebResponse.Ok(action, "Đã chọn TIM.", new RoadIdentityCadService().SelectAxis(doc, tr)); break;
                    case "AssignCadAxisIdentity": response = RoadAssign(action, payload, db, tr, state); break;
                    case "ZoomCadAxisIdentity": response = RoadZoom(action, payload, db, tr, state, doc); break;
                    case "RemoveCadAxisIdentity": response = RoadRemove(action, payload, db, tr, state); break;

                    // TAB 1 - MARKING TEMPLATE LIBRARY
                    case "ReadMarkingTemplates": response = WebResponse.Ok(action, "Đã đọc thư viện vạch sơn.", new { templates = state.MarkingTemplates }); break;
                    case "ValidateMarkingTemplate": response = ValidateTemplate(action, payload); break;
                    case "SaveMarkingTemplate": response = SaveTemplate(action, payload, db, tr, state); break;
                    case "ImportMarkingTemplates": response = ImportTemplates(action, payload, db, tr, state); break;
                    case "DeleteMarkingTemplates": response = DeleteTemplates(action, payload, db, tr, state); break;
                    case "SyncMarkingTemplateCAD": response = SyncTemplate(action, payload, db, tr, state); break;
                    case "SyncLegacyMarkingLayers": response = SyncLegacyLayers(action, payload, db, tr, state); break;
                    case "Tab1_UpdateLayerManagementSet": response = UpdateLayerManagementSet(action, payload, db, tr, state); break;
                    case "Tab1_ApplySharedLayerSet": response = ApplySharedLayerSet(action, payload, db, tr, state); break;

                    // TAB 2 - CROSS SECTION LIBRARY
                    case "ReadCrossSections": response = ReadCrossSections(action, state); break;
                    case "SaveCrossSection": response = SaveCrossSection(action, payload, db, tr, state); break;
                    case "LoadDefaultCrossSectionLibrary": response = LoadDefaultCrossSectionLibrary(action, db, tr, state); break;
                    case "ImportCrossSections": response = ImportCrossSections(action, payload, db, tr, state); break;
                    case "DeleteCrossSections": response = DeleteCrossSections(action, payload, db, tr, state); break;
                    case "SyncSelectedCrossSections": response = SyncSelectedCrossSections(action, payload, db, tr, state); break;
                    case "SyncLegacyCrossSections": response = SyncLegacyCrossSections(action, payload, db, tr, state); break;

                    // TAB 3 - LONGITUDINAL / INTERSECTION PIPELINE
                    case "SelectTimCAD": response = SelectTim(action, doc, tr, db, state); break;
                    case "SelectMepCAD": response = SelectEdges(action, doc, tr, db, state); break;
                    case "SelectMepCauKienCAD": response = SelectComponentEdges(action, doc, tr, db, state); break;
                    case "RefreshPipelineStatus": response = JsonPayload.Bool(payload, "rescanCad", false)
                        ? RefreshPipelineData(action, db, tr, state)
                        : PipelineStatus(action, state); break;
                    // Action riêng cho nút LÀM MỚI DỮ LIỆU/CẬP NHẬT TỪ CAD. Không phụ thuộc
                    // payload bool để tránh router/WebView cũ làm mất cờ rescanCad.
                    case "RefreshPipelineFromCad": response = RefreshPipelineData(action, db, tr, state); break;
                    case "ResetPipelineSession": response = ResetPipelineSession(action, db, tr, state); break;
                    case "ReadComparisonResults": response = WebResponse.Ok(action, "Đã đọc kết quả đối chiếu.", new { results = state.ComparisonResults }); break;
                    case "AutoMatchCAD": response = AutoMatch(action, db, tr, state); break;
                    case "ZoomComparisonResult": response = ZoomComparison(action, payload, db, tr, state, doc); break;
                    case "ZoomComparisonIssues": response = ZoomIssues(action, db, tr, state, doc); break;
                    case "DrawMarkingsCAD": response = DrawMarkings(action, payload, doc, db, tr, state); break;
                    case "GenerateBatchApproachLinesCAD": response = GenerateBatchApproachLines(action, payload, db, tr, state); break;

                    // TAB 4 - 7.6 / 9.3 BLOCK PLACEMENT
                    case "SelectSymbolLibraryFolder": response = SelectBlockFolder(action, db, tr, state); break;
                    case "ScanSymbolBlockLibrary": response = ScanBlocks(action, payload, db, tr, state); break;
                    case "ReadSymbolPlacementWorkspace": response = ReadBlockWorkspace(action, db, tr, state); break;
                    case "ReadSymbolProfiles": response = WebResponse.Ok(action, "Đã đọc profile bố trí 7.6/9.3.", new { profiles = state.SymbolProfiles }); break;
                    case "SaveSymbolProfile": response = SaveSymbolProfile(action, payload, db, tr, state); break;
                    case "AnalyzeSymbolBlockPlacement": response = AnalyzeBlocks(action, payload, db, tr, state); break;
                    case "GenerateSymbolBlocksCAD": response = GenerateBlocks(action, payload, db, tr, state); break;
                    case "SaveSymbolPlacementDefaults": response = SaveBlockDefaults(action, payload, db, tr, state); break;

                    // TAB 5 - SUPPLEMENTARY / EXISTING OBJECT MANAGEMENT
                    case "ReadSupplementaryWorkspace": response = ReadSupplementaryWorkspace(action, db, tr, state); break;
                    case "SelectSupplementaryBoundary": response = WebResponse.Ok(action, "Đã chọn đường biên.", new SupplementaryMarkingCadService().SelectCurve(doc.Editor, tr)); break;
                    case "SelectSupplementaryEntities": response = WebResponse.Ok(action, "Đã chọn đối tượng phát sinh.", new SupplementaryMarkingCadService().SelectEntities(doc.Editor, tr)); break;
                    case "SelectSupplementaryBlocks": response = WebResponse.Ok(action, "Đã chọn Block hiện hữu.", new SupplementaryMarkingCadService().SelectEntities(doc.Editor, tr, true)); break;
                    case "SelectSupplementaryStation": response = SelectSupplementaryStation(action, payload, doc, db, tr, state); break;
                    case "DrawSupplementaryManualPolyline": response = DrawSupplementaryPolyline(action, payload, doc, db, tr, state); break;
                    case "PreviewSpeedHump": response = PreviewSpeedHump(action, payload, db, tr, state); break;
                    case "GenerateSpeedHump": response = GenerateSpeedHump(action, payload, db, tr, state); break;
                    case "RegisterSupplementaryEntities": response = RegisterSupplementary(action, payload, db, tr, state); break;
                    case "ScanSupplementaryBlocks": response = ScanSupplementaryBlocks(action, payload, db, tr, state); break;
                    case "SaveSupplementaryBlockRule": response = SaveSupplementaryBlockRule(action, payload, db, tr, state); break;
                    case "SyncSupplementaryBlocks": response = SyncSupplementaryBlocks(action, payload, db, tr, state); break;
                    case "SetManagedGroupLock": response = SetManagedGroupLock(action, payload, db, tr, state); break;
                    case "RemoveManagedGroup": response = RemoveManagedGroup(action, payload, db, tr, state); break;
                    case "ZoomManagedGroup": response = ZoomManagedGroup(action, payload, db, tr, state, doc); break;
                    case "ZoomRoadAxisByKey": response = ZoomRoadByKey(action, payload, db, tr, state, doc); break;

                    // TAB 6 - QUANTITY SNAPSHOT / EXPORT
                    case "ReadMarkingQuantitiesCAD": response = ReadQuantities(action, db, tr, state); break;
                    case "ZoomMarkingQuantity": response = ZoomQuantity(action, payload, db, tr, doc); break;
                    case "ExportMarkingQuantitiesExcel": response = ExportExcel(action, payload, db, tr, state); break;

                    default: response = WebResponse.Fail(action, "Action chưa được triển khai: " + action); break;
                }

                tr.Commit();
                return response;
            }
        }

        // ------------------------------------------------------------------
        // TAB 0
        // ------------------------------------------------------------------
        private WebResponse RoadRead(string action, Database db, Transaction tr, ArmProjectState state)
        {
            List<ArmRoadAxisState> rows = new RoadIdentityCadService().ReadAll(db, tr, state);
            _stateStore.Save(db, tr, state);
            return WebResponse.Ok(action, "Đã đọc Road Identity từ DWG.", new { roadAxes = rows });
        }

        private WebResponse RoadAssign(string action, JsonElement p, Database db, Transaction tr, ArmProjectState state)
        {
            string roadName = JsonPayload.String(p, "roadName").Trim();
            if (roadName.Length == 0) return WebResponse.Fail(action, "Tên tuyến không được để trống.");
            string roadKey = JsonPayload.String(p, "roadKey").Trim();
            if (string.IsNullOrWhiteSpace(roadKey)) roadKey = roadName.ToUpperInvariant();
            ArmRoadAxisState result = new RoadIdentityCadService().Assign(db, tr, state,
                JsonPayload.String(p, "objectHandle"), roadName, roadKey, JsonPayload.String(p, "targetLayer"));
            state.QuantitySnapshotDirty = true;
            _stateStore.Save(db, tr, state);
            return WebResponse.Ok(action, "Đã gắn Road Identity.", new { roadAxis = result });
        }

        private WebResponse RoadZoom(string action, JsonElement p, Database db, Transaction tr, ArmProjectState state, Document doc)
        {
            new RoadIdentityCadService().Zoom(db, tr, doc.Editor, JsonPayload.String(p, "recordId"));
            return WebResponse.Ok(action, "Đã zoom tới TIM.");
        }

        private WebResponse RoadRemove(string action, JsonElement p, Database db, Transaction tr, ArmProjectState state)
        {
            new RoadIdentityCadService().Remove(db, tr, state, JsonPayload.String(p, "recordId"), JsonPayload.Bool(p, "restoreOriginalLayer", true));
            state.QuantitySnapshotDirty = true;
            _stateStore.Save(db, tr, state);
            return WebResponse.Ok(action, "Đã gỡ Road Identity.");
        }

        // ------------------------------------------------------------------
        // TAB 1
        // ------------------------------------------------------------------
        private WebResponse ValidateTemplate(string action, JsonElement p)
        {
            if (!JsonPayload.TryGet(p, "template", out JsonElement row) || row.ValueKind != JsonValueKind.Object)
                return WebResponse.Fail(action, "Không đọc được template.");
            ArmMarkingTemplateState t = ReadTemplateRow(row);

            List<string> errors = GetTemplateValidationErrors(t);
            return errors.Count == 0
                ? WebResponse.Ok(action, "Template hợp lệ.", new { valid = true, paintRatio = t.PaintRatio })
                : WebResponse.Fail(action, string.Join("; ", errors));
        }

        private WebResponse SaveTemplate(string action, JsonElement p, Database db, Transaction tr, ArmProjectState state)
        {
            if (!JsonPayload.TryGet(p, "template", out JsonElement row) || row.ValueKind != JsonValueKind.Object)
                return WebResponse.Fail(action, "Không đọc được template.");
            ArmMarkingTemplateState t = ReadTemplateRow(row);

            // Canonical hóa text trước khi validate, nhưng không clamp tham số kỹ thuật.
            // Giá trị hình học sai phải bị từ chối rõ ràng thay vì âm thầm sửa thiết kế.
            t.Code = (t.Code ?? string.Empty).Trim();
            t.Id = string.IsNullOrWhiteSpace(t.Id)
                ? new MarkingTemplateManagementService().BuildTemplateId(t.Code, t.Layer)
                : t.Id.Trim();
            t.Name = string.IsNullOrWhiteSpace(t.Name) ? t.Code : t.Name.Trim();
            t.Layer = (t.Layer ?? string.Empty).Trim();
            t.Pattern = string.IsNullOrWhiteSpace(t.Pattern) ? "CUSTOM_REAL" : t.Pattern.Trim().ToUpperInvariant();
            t.Reference = (t.Reference ?? string.Empty).Trim();
            t.StandardClause = (t.StandardClause ?? string.Empty).Trim();

            List<string> validationErrors = GetTemplateValidationErrors(t);
            if (validationErrors.Count > 0)
                return WebResponse.Fail(action, string.Join("; ", validationErrors));

            t.CustomProperties = MarkingCustomPropertyPolicy.Normalize(t.CustomProperties);

            // Một template chỉ có một nguồn tham số pattern có thẩm quyền. Các field không
            // thuộc pattern đang chọn được xóa trước khi persist để tránh hai nguồn sự thật.
            if (t.Pattern == "CONTINUOUS" || t.Pattern == "SOLID")
            {
                t.DashLength = 0.0;
                t.GapLength = 0.0;
                ClearCustomPattern(t);
            }
            else if (t.Pattern == "CUSTOM_REAL")
            {
                t.DashLength = 0.0;
                t.GapLength = 0.0;
            }
            else
            {
                ClearCustomPattern(t);
            }

            new MarkingTemplateManagementService().Normalize(t, markReady: false);
            state.MarkingTemplates.RemoveAll(x => x.Id.Equals(t.Id, StringComparison.OrdinalIgnoreCase) ||
                                                  (!string.IsNullOrWhiteSpace(t.Code) && x.Code.Equals(t.Code, StringComparison.OrdinalIgnoreCase)));
            state.MarkingTemplates.Add(t);
            _stateStore.Save(db, tr, state);
            return WebResponse.Ok(action, "Đã lưu template vào DWG state.", new { template = t, paintRatio = t.PaintRatio });
        }

        private WebResponse ImportTemplates(string action, JsonElement p, Database db, Transaction tr, ArmProjectState state)
        {
            if (!JsonPayload.TryGet(p, "templates", out JsonElement rows) || rows.ValueKind != JsonValueKind.Array)
                return WebResponse.Fail(action, "Không đọc được danh sách template import.");

            var manager = new MarkingTemplateManagementService();
            var errors = new List<string>();
            int imported = 0;
            int skipped = 0;

            foreach (JsonElement row in rows.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object)
                {
                    skipped++;
                    continue;
                }

                ArmMarkingTemplateState template = ReadTemplateRow(row);
                template.Code = (template.Code ?? string.Empty).Trim();
                template.Layer = (template.Layer ?? string.Empty).Trim();
                template.Pattern = string.IsNullOrWhiteSpace(template.Pattern)
                    ? "CUSTOM_REAL"
                    : template.Pattern.Trim().ToUpperInvariant();
                template.Id = string.IsNullOrWhiteSpace(template.Id)
                    ? manager.BuildTemplateId(template.Code, template.Layer)
                    : template.Id.Trim();
                template.CustomProperties = MarkingCustomPropertyPolicy.Normalize(template.CustomProperties);

                List<string> validation = GetTemplateValidationErrors(template);
                if (validation.Count > 0)
                {
                    skipped++;
                    errors.Add((string.IsNullOrWhiteSpace(template.Code) ? "?" : template.Code) + ": " + string.Join("; ", validation));
                    continue;
                }

                if (template.Pattern == "CUSTOM_REAL")
                {
                    template.DashLength = 0.0;
                    template.GapLength = 0.0;
                }
                else if (template.Pattern == "CONTINUOUS" || template.Pattern == "SOLID")
                {
                    template.DashLength = 0.0;
                    template.GapLength = 0.0;
                    ClearCustomPattern(template);
                }
                else
                {
                    ClearCustomPattern(template);
                }

                manager.Normalize(template, markReady: false);
                state.MarkingTemplates.RemoveAll(x =>
                    x.Id.Equals(template.Id, StringComparison.OrdinalIgnoreCase) ||
                    (!string.IsNullOrWhiteSpace(template.Code) && x.Code.Equals(template.Code, StringComparison.OrdinalIgnoreCase)));
                state.MarkingTemplates.Add(template);
                imported++;
            }

            _stateStore.Save(db, tr, state);
            return WebResponse.Ok(action,
                errors.Count == 0 ? "Đã nhập thư viện template." : "Đã nhập thư viện template với cảnh báo.",
                new { imported, skipped, errors, templates = state.MarkingTemplates });
        }

        private static List<string> GetTemplateValidationErrors(ArmMarkingTemplateState t)
        {
            var errors = new List<string>();
            if (string.IsNullOrWhiteSpace(t.Code)) errors.Add("Thiếu mã vạch");
            if (string.IsNullOrWhiteSpace(t.Layer)) errors.Add("Thiếu Template Layer");
            if (t.Width < 0.0) errors.Add("Bề rộng không được âm");
            if (t.LinetypeScale <= 0.0) errors.Add("Linetype scale phải lớn hơn 0");

            string pattern = (t.Pattern ?? string.Empty).Trim().ToUpperInvariant();
            bool standardPattern = pattern == "DASHED" || pattern == "DASHEDX2" || pattern == "HIDDEN";
            if (standardPattern)
            {
                if (t.DashLength <= 0.0) errors.Add("Kiểu nét đứt phải có Chiều dài nét > 0");
                if (t.GapLength < 0.0) errors.Add("Khoảng trống không được âm");
            }
            else if (pattern == "CUSTOM_REAL")
            {
                if (t.CustomDash1 < 0.0 || t.CustomDash2 < 0.0 ||
                    t.CustomGap1 < 0.0 || t.CustomGap2 < 0.0 || t.CustomPhase < 0.0)
                    errors.Add("Tham số CUSTOM_REAL không được âm");
                if (t.CustomDash1 + t.CustomDash2 <= 0.0)
                    errors.Add("CUSTOM_REAL phải có ít nhất một đoạn sơn có chiều dài > 0");
            }

            errors.AddRange(MarkingCustomPropertyPolicy.Validate(t.CustomProperties));
            return errors;
        }

        private static void ClearCustomPattern(ArmMarkingTemplateState t)
        {
            t.CustomDash1 = 0.0;
            t.CustomGap1 = 0.0;
            t.CustomDash2 = 0.0;
            t.CustomGap2 = 0.0;
            t.CustomPhase = 0.0;
        }

        private WebResponse DeleteTemplates(string action, JsonElement p, Database db, Transaction tr, ArmProjectState state)
        {
            List<string> ids = JsonPayload.List<string>(p, "ids", _json);
            int before = state.MarkingTemplates.Count;
            state.MarkingTemplates.RemoveAll(x => ids.Contains(x.Id, StringComparer.OrdinalIgnoreCase));
            _stateStore.Save(db, tr, state);
            return WebResponse.Ok(action, "Đã xóa template khỏi thư viện dự án.", new { count = before - state.MarkingTemplates.Count });
        }

        private WebResponse SyncTemplate(string action, JsonElement p, Database db, Transaction tr, ArmProjectState state)
        {
            ArmMarkingTemplateState? t = JsonPayload.Object<ArmMarkingTemplateState>(p, "template", _json);
            if (t == null)
            {
                string id = JsonPayload.String(p, "templateId");
                t = state.MarkingTemplates.FirstOrDefault(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            }
            if (t == null) return WebResponse.Fail(action, "Không tìm thấy template.");
            var manager = new MarkingTemplateManagementService();
            manager.Normalize(t, markReady: true);
            ObjectId layerId = new MarkingLayerSynchronizer().EnsureTemplateLayer(db, tr, t);
            t.LayerHandle = layerId.IsNull ? string.Empty : layerId.Handle.ToString();
            t.ManagementState = "READY";
            t.ManagementUpdatedAt = DateTime.UtcNow;
            _stateStore.Save(db, tr, state);
            return WebResponse.Ok(action, "Đã tạo/cập nhật Template Layer trong DWG.", new { layer = t.Layer, template = t });
        }

        private WebResponse SyncLegacyLayers(string action, JsonElement p, Database db, Transaction tr, ArmProjectState state)
        {
            int count = new LegacyUiStateAdapter().SyncLayers(db, tr, state, p);
            _stateStore.Save(db, tr, state);
            return WebResponse.Ok(action, "Đã đồng bộ " + count + " layer vạch sơn.", new { count });
        }

        private WebResponse UpdateLayerManagementSet(string action, JsonElement p, Database db, Transaction tr, ArmProjectState state)
        {
            var manager = new MarkingTemplateManagementService();
            List<ArmMarkingTemplateState> targets = ResolveRequestedTemplates(p, state, allowCreate: true);
            if (targets.Count == 0) targets = state.MarkingTemplates;

            int updated = 0;
            var errors = new List<string>();
            foreach (ArmMarkingTemplateState template in targets)
            {
                try
                {
                    List<string> validation = GetTemplateValidationErrors(template);
                    if (validation.Count > 0)
                    {
                        template.ManagementState = "ERROR";
                        errors.Add(template.Code + ": " + string.Join("; ", validation));
                        continue;
                    }

                    manager.Normalize(template, markReady: true);
                    updated++;
                }
                catch (Exception ex)
                {
                    template.ManagementState = "ERROR";
                    errors.Add(template.Code + ": " + ex.Message);
                }
            }

            _stateStore.Save(db, tr, state);
            return WebResponse.Ok(action,
                errors.Count == 0
                    ? "Đã cập nhật thư viện Layer."
                    : "Đã cập nhật thư viện Layer với " + errors.Count + " cảnh báo.",
                new
                {
                    state = errors.Count == 0 ? "ready" : "error",
                    total = targets.Count,
                    updated,
                    failed = errors.Count,
                    errors,
                    templates = state.MarkingTemplates
                });
        }

        private WebResponse ApplySharedLayerSet(string action, JsonElement p, Database db, Transaction tr, ArmProjectState state)
        {
            var manager = new MarkingTemplateManagementService();
            var synchronizer = new MarkingLayerSynchronizer();
            List<ArmMarkingTemplateState> targets = ResolveRequestedTemplates(p, state, allowCreate: true);
            if (targets.Count == 0)
                return WebResponse.Fail(action, "Chưa chọn Layer nào để áp dụng và đồng bộ.");

            LayerTable layerTable = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            int created = 0;
            int updated = 0;
            var errors = new List<string>();

            foreach (ArmMarkingTemplateState template in targets)
            {
                try
                {
                    List<string> validation = GetTemplateValidationErrors(template);
                    if (validation.Count > 0)
                    {
                        template.ManagementState = "ERROR";
                        errors.Add(template.Code + ": " + string.Join("; ", validation));
                        continue;
                    }

                    manager.Normalize(template, markReady: true);
                    bool existed = layerTable.Has(template.Layer);
                    ObjectId layerId = synchronizer.EnsureTemplateLayer(db, tr, template);
                    template.LayerHandle = layerId.IsNull ? string.Empty : layerId.Handle.ToString();
                    template.ManagementState = "READY";
                    template.ManagementUpdatedAt = DateTime.UtcNow;
                    if (existed) updated++; else created++;
                }
                catch (Exception ex)
                {
                    template.ManagementState = "ERROR";
                    errors.Add(template.Code + ": " + ex.Message);
                }
            }

            _stateStore.Save(db, tr, state);

            return WebResponse.Ok(action,
                errors.Count == 0
                    ? "Đã áp dụng Layer vào CAD và đồng bộ thư viện dùng chung."
                    : "Đã áp dụng một phần; có " + errors.Count + " Layer lỗi.",
                new
                {
                    success = errors.Count == 0,
                    state = errors.Count == 0 ? "ready" : "error",
                    layers = targets,
                    templates = state.MarkingTemplates,
                    appliedAt = DateTime.UtcNow,
                    cad = new
                    {
                        created,
                        updated,
                        failed = errors.Count
                    },
                    errors
                });
        }

        private List<ArmMarkingTemplateState> ResolveRequestedTemplates(JsonElement p, ArmProjectState state, bool allowCreate)
        {
            var result = new List<ArmMarkingTemplateState>();
            if (!JsonPayload.TryGet(p, "layers", out JsonElement layers) || layers.ValueKind != JsonValueKind.Array)
                return result;

            foreach (JsonElement row in layers.EnumerateArray())
            {
                string id = JsonPayload.String(row, "templateId");
                if (string.IsNullOrWhiteSpace(id)) id = JsonPayload.String(row, "TemplateId");
                if (string.IsNullOrWhiteSpace(id)) id = JsonPayload.String(row, "id");
                if (string.IsNullOrWhiteSpace(id)) id = JsonPayload.String(row, "Id");

                string code = JsonPayload.String(row, "code");
                if (string.IsNullOrWhiteSpace(code)) code = JsonPayload.String(row, "Code");
                if (string.IsNullOrWhiteSpace(code)) code = JsonPayload.String(row, "markingCode");
                if (string.IsNullOrWhiteSpace(code)) code = JsonPayload.String(row, "MarkingCode");

                ArmMarkingTemplateState? existing = state.MarkingTemplates.FirstOrDefault(x =>
                    (!string.IsNullOrWhiteSpace(id) && x.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) ||
                    (!string.IsNullOrWhiteSpace(code) && x.Code.Equals(code, StringComparison.OrdinalIgnoreCase)));

                if (existing != null)
                {
                    if (!result.Contains(existing)) result.Add(existing);
                    continue;
                }

                if (!allowCreate) continue;
                ArmMarkingTemplateState created = ReadTemplateRow(row);
                if (string.IsNullOrWhiteSpace(created.Code) || string.IsNullOrWhiteSpace(created.Layer))
                    continue;

                new MarkingTemplateManagementService().Normalize(created, markReady: false);
                created.CustomProperties = MarkingCustomPropertyPolicy.Normalize(created.CustomProperties);
                state.MarkingTemplates.Add(created);
                result.Add(created);
            }

            return result;
        }

        private ArmMarkingTemplateState ReadTemplateRow(JsonElement row)
        {
            var template = new ArmMarkingTemplateState
            {
                Id = FirstString(row, "TemplateId", "templateId", "Id", "id"),
                Code = FirstString(row, "Code", "code", "MarkingCode", "markingCode"),
                Name = FirstString(row, "Name", "name"),
                Description = FirstString(row, "Description", "description"),
                Category = FirstString(row, "Category", "category"),
                Layer = FirstString(row, "Layer", "layer", "LayerName", "layerName", "name"),
                Geometry = FirstString(row, "Geometry", "geometry"),
                Pattern = FirstString(row, "Pattern", "pattern"),
                Width = FirstDouble(row, 0.0, "Width", "width"),
                LinetypeScale = FirstDouble(row, 1.0, "LinetypeScale", "linetypeScale", "Scale", "scale"),
                Color = FirstString(row, "Color", "color", "ColorHex", "colorHex"),
                Rgb = FirstString(row, "RGB", "Rgb", "rgb"),
                DashLength = FirstDouble(row, 0.0, "DashLength", "dashLength", "Dash", "dash"),
                GapLength = FirstDouble(row, 0.0, "GapLength", "gapLength", "Gap", "gap"),
                CustomDash1 = FirstDouble(row, 0.0, "CustomDash1", "customDash1"),
                CustomGap1 = FirstDouble(row, 0.0, "CustomGap1", "customGap1"),
                CustomDash2 = FirstDouble(row, 0.0, "CustomDash2", "customDash2"),
                CustomGap2 = FirstDouble(row, 0.0, "CustomGap2", "customGap2"),
                CustomPhase = FirstDouble(row, 0.0, "CustomPhase", "customPhase"),
                Reference = FirstString(row, "Reference", "reference", "StandardRef", "standardRef"),
                StandardClause = FirstString(row, "StandardClause", "standardClause"),
                Note = FirstString(row, "Note", "note"),
                ManagementState = FirstString(row, "ManagementState", "managementState"),
                QuantityMethod = FirstString(row, "QuantityMethod", "quantityMethod"),
                ManagementSchema = FirstString(row, "ManagementSchema", "managementSchema"),
                ManagementVersion = (int)FirstDouble(row, 1.0, "ManagementVersion", "managementVersion"),
                LayerHandle = FirstString(row, "LayerHandle", "layerHandle", "Handle", "handle"),
                Verified = true
            };

            if (string.IsNullOrWhiteSpace(template.Name)) template.Name = template.Code;
            if (string.IsNullOrWhiteSpace(template.Geometry)) template.Geometry = "line";
            if (string.IsNullOrWhiteSpace(template.Pattern)) template.Pattern = "CUSTOM_REAL";
            if (string.IsNullOrWhiteSpace(template.ManagementState)) template.ManagementState = "DIRTY";
            if (string.IsNullOrWhiteSpace(template.ManagementSchema)) template.ManagementSchema = "ARM_QTY_TEMPLATE";
            if (string.IsNullOrWhiteSpace(template.Color)) template.Color = "#ffffff";
            if (string.IsNullOrWhiteSpace(template.Rgb)) template.Rgb = "255,255,255";

            if (JsonPayload.TryGet(row, "CustomProperties", out JsonElement custom) ||
                JsonPayload.TryGet(row, "customProperties", out custom))
            {
                template.CustomProperties = ReadStringDictionary(custom);
            }

            return template;
        }

        private static Dictionary<string, string> ReadStringDictionary(JsonElement custom)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (custom.ValueKind == JsonValueKind.String)
            {
                string raw = custom.GetString() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(raw)) return result;
                try
                {
                    using (JsonDocument doc = JsonDocument.Parse(raw))
                        return ReadStringDictionary(doc.RootElement);
                }
                catch
                {
                    return result;
                }
            }

            if (custom.ValueKind != JsonValueKind.Object) return result;
            foreach (JsonProperty property in custom.EnumerateObject())
            {
                string value = property.Value.ValueKind == JsonValueKind.String
                    ? property.Value.GetString() ?? string.Empty
                    : property.Value.GetRawText();
                result[property.Name] = value;
            }
            return result;
        }

        private static string FirstString(JsonElement element, params string[] names)
        {
            foreach (string name in names)
            {
                string value = JsonPayload.String(element, name);
                if (!string.IsNullOrWhiteSpace(value)) return value;
            }
            return string.Empty;
        }

        private static double FirstDouble(JsonElement element, double fallback, params string[] names)
        {
            foreach (string name in names)
            {
                if (JsonPayload.TryGet(element, name, out JsonElement value))
                {
                    if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double n)) return n;
                    if (value.ValueKind == JsonValueKind.String &&
                        double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out n)) return n;
                }
            }
            return fallback;
        }

        // ------------------------------------------------------------------
        // TAB 2
        // ------------------------------------------------------------------
        private static WebResponse ReadCrossSections(string action, ArmProjectState state)
        {
            List<ArmCrossSectionState> active = state.GetEffectiveCrossSections();
            return WebResponse.Ok(action, "Đã đọc thư viện mặt cắt.", new
            {
                crossSections = state.CrossSections,
                activeCrossSectionIds = state.ActiveCrossSectionIds,
                activeCrossSections = active,
                activeCount = active.Count
            });
        }

        private WebResponse SyncSelectedCrossSections(string action, JsonElement p, Database db, Transaction tr, ArmProjectState state)
        {
            List<string> requested = JsonPayload.List<string>(p, "ids", _json)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (requested.Count == 0)
                return WebResponse.Fail(action, "Hãy chọn ít nhất một mặt cắt để đồng bộ.");

            var existing = new HashSet<string>(
                state.CrossSections.Select(x => x.Id),
                StringComparer.OrdinalIgnoreCase);
            List<string> valid = requested.Where(existing.Contains).ToList();
            List<string> missing = requested.Where(x => !existing.Contains(x)).ToList();

            if (valid.Count == 0)
                return WebResponse.Fail(action, "Không có mặt cắt đã chọn nào còn tồn tại trong thư viện DWG.");

            state.ActiveCrossSectionIds = valid;

            // Tập MCN ứng viên thay đổi => kết quả match/proposal phụ thuộc MCN không còn tin cậy.
            state.ComparisonResults.Clear();
            state.BlockProposals.Clear();
            _stateStore.Save(db, tr, state);

            List<ArmCrossSectionState> active = state.GetEffectiveCrossSections();
            return WebResponse.Ok(action, "Đã đồng bộ " + active.Count + " mặt cắt cho các Tab sử dụng.", new
            {
                ids = valid,
                missingIds = missing,
                activeCrossSectionIds = state.ActiveCrossSectionIds,
                activeCrossSections = active,
                crossSections = state.CrossSections,
                activeCount = active.Count,
                comparisonInvalidated = true
            });
        }

        private WebResponse SaveCrossSection(string action, JsonElement p, Database db, Transaction tr, ArmProjectState state)
        {
            ArmCrossSectionState? section = JsonPayload.Object<ArmCrossSectionState>(p, "crossSection", _json);
            if (section == null)
            {
                string name = JsonPayload.String(p, "name", "MCN_NEW");
                section = new ArmCrossSectionState { Id = name, Name = name, Components = JsonPayload.List<ArmCrossSectionPartState>(p, "components", _json) };
            }
            section = new CrossSectionStateNormalizer().Normalize(section);
            if (string.IsNullOrWhiteSpace(section.Id)) return WebResponse.Fail(action, "Tên mặt cắt không hợp lệ.");
            state.CrossSections.RemoveAll(x => x.Id.Equals(section.Id, StringComparison.OrdinalIgnoreCase));
            state.CrossSections.Add(section);
            // Thay đổi hình học MCN làm kết quả đối chiếu/proposal phụ thuộc MCN bị cũ.
            state.ComparisonResults.Clear();
            state.BlockProposals.Clear();
            _stateStore.Save(db, tr, state);
            return WebResponse.Ok(action, "Đã lưu mặt cắt.", new { crossSection = section });
        }

        private WebResponse LoadDefaultCrossSectionLibrary(
            string action,
            Database db,
            Transaction tr,
            ArmProjectState state)
        {
            const string fileName = "ARM_CrossSections_LIBRARY_DEFAULT.json";
            const string expectedResourceName = "ARM_WEB/data/templates/ARM_CrossSections_LIBRARY_DEFAULT.json";

            string jsonText = string.Empty;
            string source = string.Empty;
            Assembly assembly = typeof(CadActionExecutor).Assembly;

            try
            {
                // 1) Ưu tiên đọc trực tiếp từ EmbeddedResource của chính CadHost.dll.
                //    Không đi qua WebView2/fetch nên không phụ thuộc file:// / virtual host.
                string? resourceName = assembly
                    .GetManifestResourceNames()
                    .FirstOrDefault(name =>
                        string.Equals(name, expectedResourceName, StringComparison.OrdinalIgnoreCase));

                if (string.IsNullOrWhiteSpace(resourceName))
                {
                    resourceName = assembly
                        .GetManifestResourceNames()
                        .FirstOrDefault(name =>
                            name.EndsWith("/" + fileName, StringComparison.OrdinalIgnoreCase) ||
                            name.EndsWith("\\" + fileName, StringComparison.OrdinalIgnoreCase) ||
                            name.EndsWith("." + fileName, StringComparison.OrdinalIgnoreCase));
                }

                if (!string.IsNullOrWhiteSpace(resourceName))
                {
                    using (Stream? input = assembly.GetManifestResourceStream(resourceName))
                    {
                        if (input != null)
                        {
                            using (var reader = new StreamReader(input, Encoding.UTF8, true))
                            {
                                jsonText = reader.ReadToEnd();
                                source = "EmbeddedResource: " + resourceName;
                            }
                        }
                    }
                }

                // 2) Fallback: WebUiContentProvider giải nén embedded UI vào LocalAppData.
                //    Nhánh này cũng hỗ trợ cache runtime của các build cũ.
                if (string.IsNullOrWhiteSpace(jsonText))
                {
                    try
                    {
                        string webRoot = WebUiContentProvider.PrepareWebRoot();
                        string runtimePath = Path.Combine(
                            webRoot,
                            "data",
                            "templates",
                            fileName);

                        if (File.Exists(runtimePath))
                        {
                            jsonText = File.ReadAllText(runtimePath, Encoding.UTF8);
                            source = runtimePath;
                        }
                    }
                    catch
                    {
                        // Tiếp tục thử file cạnh DLL. Lỗi thật sẽ được trả nếu mọi nguồn đều thiếu.
                    }
                }

                // 3) Fallback cuối: file UI copy cạnh output DLL khi build từ source.
                if (string.IsNullOrWhiteSpace(jsonText))
                {
                    string? assemblyDir = Path.GetDirectoryName(assembly.Location);
                    if (!string.IsNullOrWhiteSpace(assemblyDir))
                    {
                        string outputPath = Path.Combine(
                            assemblyDir,
                            "UI",
                            "web",
                            "data",
                            "templates",
                            fileName);

                        if (File.Exists(outputPath))
                        {
                            jsonText = File.ReadAllText(outputPath, Encoding.UTF8);
                            source = outputPath;
                        }
                    }
                }

                if (string.IsNullOrWhiteSpace(jsonText))
                {
                    return WebResponse.Fail(
                        action,
                        "Không tìm thấy " + fileName + " trong CadHost.dll hoặc thư mục UI runtime. " +
                        "Hãy đặt file tại UI\\web\\data\\templates rồi Clean/Rebuild CadHost để file được nhúng với LogicalName " +
                        expectedResourceName + ".");
                }

                List<ArmCrossSectionState> incoming;
                using (JsonDocument library = JsonDocument.Parse(jsonText))
                {
                    JsonElement root = library.RootElement;
                    if (root.ValueKind == JsonValueKind.Array)
                    {
                        incoming = JsonSerializer.Deserialize<List<ArmCrossSectionState>>(
                            root.GetRawText(),
                            _json) ?? new List<ArmCrossSectionState>();
                    }
                    else
                    {
                        incoming = JsonPayload.List<ArmCrossSectionState>(root, "crossSections", _json);
                        if (incoming.Count == 0)
                            incoming = JsonPayload.List<ArmCrossSectionState>(root, "assemblies", _json);
                    }
                }

                if (incoming.Count == 0)
                {
                    return WebResponse.Fail(
                        action,
                        "Đã đọc được file thư viện nhưng không tìm thấy mảng crossSections/assemblies hợp lệ. Nguồn: " + source);
                }

                return ImportCrossSectionStates(
                    action,
                    incoming,
                    db,
                    tr,
                    state,
                    "thư viện mặt cắt mặc định",
                    source);
            }
            catch (JsonException ex)
            {
                return WebResponse.Fail(
                    action,
                    "JSON thư viện mặt cắt không hợp lệ: " + ex.Message);
            }
            catch (Exception ex)
            {
                return WebResponse.Fail(
                    action,
                    "Không đọc được thư viện mặt cắt mặc định: " + ex.Message);
            }
        }

        private WebResponse ImportCrossSections(string action, JsonElement p, Database db, Transaction tr, ArmProjectState state)
        {
            List<ArmCrossSectionState> incoming = JsonPayload.List<ArmCrossSectionState>(p, "crossSections", _json);
            if (incoming.Count == 0)
                incoming = JsonPayload.List<ArmCrossSectionState>(p, "assemblies", _json);

            if (incoming.Count == 0)
                return WebResponse.Fail(action, "Thư viện JSON không có mặt cắt hợp lệ.");

            string source = JsonPayload.String(p, "source", "JSON");
            return ImportCrossSectionStates(action, incoming, db, tr, state, source, source);
        }

        private WebResponse ImportCrossSectionStates(
            string action,
            IEnumerable<ArmCrossSectionState> incomingSource,
            Database db,
            Transaction tr,
            ArmProjectState state,
            string sourceLabel,
            string source)
        {
            List<ArmCrossSectionState> incoming = incomingSource?.ToList() ?? new List<ArmCrossSectionState>();
            if (incoming.Count == 0)
                return WebResponse.Fail(action, "Thư viện không có mặt cắt hợp lệ.");

            var normalizer = new CrossSectionStateNormalizer();
            var importedIds = new List<string>();
            int skipped = 0;

            foreach (ArmCrossSectionState raw in incoming)
            {
                if (raw == null)
                {
                    skipped++;
                    continue;
                }

                ArmCrossSectionState section;
                try
                {
                    section = normalizer.Normalize(raw);
                }
                catch
                {
                    skipped++;
                    continue;
                }

                if (string.IsNullOrWhiteSpace(section.Id) || section.Components == null || section.Components.Count == 0)
                {
                    skipped++;
                    continue;
                }

                state.CrossSections.RemoveAll(x => x.Id.Equals(section.Id, StringComparison.OrdinalIgnoreCase));
                state.CrossSections.Add(section);
                importedIds.Add(section.Id);
            }

            if (importedIds.Count == 0)
                return WebResponse.Fail(action, "Không có mặt cắt nào đủ dữ liệu để nạp.");

            // Thư viện ứng viên thay đổi => kết quả đối chiếu/proposal cũ không còn tin cậy.
            state.ComparisonResults.Clear();
            state.BlockProposals.Clear();
            _stateStore.Save(db, tr, state);

            string label = string.IsNullOrWhiteSpace(sourceLabel) ? "thư viện" : sourceLabel.Trim();
            return WebResponse.Ok(action,
                "Đã nạp " + importedIds.Count + " mặt cắt từ " + label + " vào thư viện DWG." +
                (skipped > 0 ? " Bỏ qua " + skipped + " mục không hợp lệ." : string.Empty),
                new
                {
                    importedCount = importedIds.Count,
                    skippedCount = skipped,
                    importedIds,
                    source,
                    crossSections = state.CrossSections,
                    activeCrossSectionIds = state.ActiveCrossSectionIds
                });
        }

        private WebResponse DeleteCrossSections(string action, JsonElement p, Database db, Transaction tr, ArmProjectState state)
        {
            List<string> ids = JsonPayload.List<string>(p, "ids", _json);
            int before = state.CrossSections.Count;
            state.CrossSections.RemoveAll(x => ids.Contains(x.Id, StringComparer.OrdinalIgnoreCase));
            state.ActiveCrossSectionIds.RemoveAll(x => ids.Contains(x, StringComparer.OrdinalIgnoreCase));
            state.SymbolProfiles.RemoveAll(x => ids.Contains(x.AssemblyId, StringComparer.OrdinalIgnoreCase));
            // Candidate MCN thay đổi: mọi kết quả match cũ cần chạy lại, không chỉ dòng trỏ trực tiếp ID đã xóa.
            state.ComparisonResults.Clear();
            state.BlockProposals.Clear();
            _stateStore.Save(db, tr, state);
            return WebResponse.Ok(action, "Đã xóa mặt cắt.", new { count = before - state.CrossSections.Count });
        }

        private WebResponse SyncLegacyCrossSections(string action, JsonElement p, Database db, Transaction tr, ArmProjectState state)
        {
            int count = new LegacyUiStateAdapter().SyncCrossSections(db, tr, state, p);
            state.CrossSections = state.CrossSections.Select(x => new CrossSectionStateNormalizer().Normalize(x)).ToList();
            state.ComparisonResults.Clear();
            state.BlockProposals.Clear();
            _stateStore.Save(db, tr, state);
            return WebResponse.Ok(action, "Đã đồng bộ " + count + " mặt cắt.", new { count, crossSections = state.CrossSections });
        }

        // ------------------------------------------------------------------
        // TAB 3
        // ------------------------------------------------------------------
        private WebResponse SelectTim(string action, Document doc, Transaction tr, Database db, ArmProjectState state)
        {
            List<string> handles = new CenterlineSelectionService().Select(doc, tr, state);
            _stateStore.Save(db, tr, state);
            return WebResponse.Ok(action, "Đã nhận TIM từ CAD.", new { handles, count = handles.Count });
        }

        private WebResponse SelectEdges(string action, Document doc, Transaction tr, Database db, ArmProjectState state)
        {
            List<string> handles = new RoadEdgeSelectionService().Select(doc, tr, state);
            _stateStore.Save(db, tr, state);
            return WebResponse.Ok(action, "Đã nhận MÉP từ CAD.", new { handles, count = handles.Count });
        }

        private WebResponse SelectComponentEdges(string action, Document doc, Transaction tr, Database db, ArmProjectState state)
        {
            List<ObjectId> ids = new CadSelectionService().SelectCurves(doc.Editor, tr, "Quét chọn MÉP CẤU KIỆN rồi nhấn Enter.", false);
            state.SelectedComponentEdgeHandles = ids.Select(x => x.Handle.ToString()).ToList();
            _stateStore.Save(db, tr, state);
            return WebResponse.Ok(action, "Đã nhận mép cấu kiện.", new { handles = state.SelectedComponentEdgeHandles, count = state.SelectedComponentEdgeHandles.Count });
        }

        /// <summary>
        /// Đưa Tab 3 về trạng thái một phiên làm việc mới. Chỉ xóa dữ liệu workflow
        /// và polygon helper do ARM tạo; không xóa TIM/MÉP gốc, thư viện Tab 1/2
        /// hoặc các vạch CAD đã sinh trước đó.
        /// </summary>
        private WebResponse ResetPipelineSession(string action, Database db, Transaction tr, ArmProjectState state)
        {
            int erasedHelperPolygons = 0;
            var metadataStore = new EntityMetadataStore();
            var polygonIds = new List<ObjectId>();

            try
            {
                BlockTableRecord modelSpace = (BlockTableRecord)tr.GetObject(
                    SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForRead);

                foreach (ObjectId id in modelSpace)
                {
                    try
                    {
                        Entity? entity = tr.GetObject(id, OpenMode.ForRead, false, true) as Entity;
                        if (entity == null || entity.IsErased) continue;

                        ArmEntityMetadata? metadata = metadataStore.Read(entity, tr);
                        if (metadata != null &&
                            string.Equals(metadata.Source, "INTERSECTION_POLYGON", StringComparison.OrdinalIgnoreCase))
                        {
                            polygonIds.Add(id);
                        }
                    }
                    catch
                    {
                        // Object stale/erased không được phép làm hỏng thao tác reset.
                    }
                }

                foreach (ObjectId id in polygonIds.Distinct())
                {
                    try
                    {
                        Entity? entity = tr.GetObject(id, OpenMode.ForWrite, false, true) as Entity;
                        if (entity != null && !entity.IsErased)
                        {
                            entity.Erase(true);
                            erasedHelperPolygons++;
                        }
                    }
                    catch
                    {
                        // Reset phải idempotent: polygon đã bị xóa tay thì chỉ bỏ qua.
                    }
                }
            }
            catch
            {
                // Dù không quét/xóa được helper polygon, state Tab 3 vẫn phải reset sạch.
            }

            state.SelectedTimHandles = new List<string>();
            state.SelectedEdgeHandles = new List<string>();
            state.SelectedComponentEdgeHandles = new List<string>();
            state.ComparisonResults = new List<ArmComparisonState>();
            state.IntersectionPolygonHandles = new List<string>();
            state.BlockProposals = new List<ArmBlockProposalState>();
            state.StopToCrosswalkDistance = 2.0;
            state.QuantitySnapshotDirty = true;

            _stateStore.Save(db, tr, state);

            return WebResponse.Ok(action, "Đã làm mới Tab 3 về trạng thái ban đầu.", new
            {
                reset = true,
                erasedHelperPolygons,
                timCount = 0,
                edgeCount = 0,
                componentEdgeCount = 0,
                mcnCount = state.GetEffectiveCrossSections().Count,
                mcnLibraryCount = state.CrossSections.Count,
                activeCrossSectionIds = state.ActiveCrossSectionIds,
                polygonCount = 0,
                comparisonCount = 0,
                matchedCount = 0,
                warningCount = 0,
                stopToCrosswalkDistance = state.StopToCrosswalkDistance,
                results = Array.Empty<ArmComparisonState>()
            });
        }

        private static WebResponse PipelineStatus(string action, ArmProjectState state)
        {
            return WebResponse.Ok(action, "Đã đọc trạng thái pipeline.", new
            {
                timCount = state.SelectedTimHandles.Count,
                edgeCount = state.SelectedEdgeHandles.Count,
                componentEdgeCount = state.SelectedComponentEdgeHandles.Count,
                mcnCount = state.GetEffectiveCrossSections().Count,
                mcnLibraryCount = state.CrossSections.Count,
                activeCrossSectionIds = state.ActiveCrossSectionIds,
                polygonCount = state.IntersectionPolygonHandles.Count,
                comparisonCount = state.ComparisonResults.Count,
                matchedCount = state.ComparisonResults.Count(x => x.Status.Equals("matched", StringComparison.OrdinalIgnoreCase)),
                warningCount = state.ComparisonResults.Count(x => !x.Status.Equals("matched", StringComparison.OrdinalIgnoreCase)),
                stopToCrosswalkDistance = state.StopToCrosswalkDistance
            });
        }

        /// <summary>
        /// Làm mới Tab 3 từ dữ liệu CAD thật, không chỉ đọc các bộ đếm đã lưu trong state.
        /// Handle đã bị xóa/recreate sẽ được loại; polygon được quét lại từ metadata trong ModelSpace;
        /// kết quả đối chiếu được tính lại khi còn đủ TIM + MÉP + MCN.
        /// </summary>
        private WebResponse RefreshPipelineData(string action, Database db, Transaction tr, ArmProjectState state)
        {
            int beforeTim = state.SelectedTimHandles?.Count ?? 0;
            int beforeEdge = state.SelectedEdgeHandles?.Count ?? 0;
            int beforeComponent = state.SelectedComponentEdgeHandles?.Count ?? 0;
            int beforePolygon = state.IntersectionPolygonHandles?.Count ?? 0;

            state.SelectedTimHandles = FilterLiveHandles(db, tr, state.SelectedTimHandles, requireCurve: false);
            state.SelectedEdgeHandles = FilterLiveHandles(db, tr, state.SelectedEdgeHandles, requireCurve: true);
            state.SelectedComponentEdgeHandles = FilterLiveHandles(db, tr, state.SelectedComponentEdgeHandles, requireCurve: true);

            // RefreshPolygons vừa loại handle stale vừa tìm lại polygon ARM còn tồn tại trong ModelSpace.
            state.IntersectionPolygonHandles = new IntersectionPolygonService()
                .RefreshPolygons(db, tr, state)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            // Khôi phục selection từ chính dữ liệu CAD/metadata đang tồn tại. Đây là phần còn thiếu
            // của nút LÀM MỚI DỮ LIỆU trước đây: chỉ lọc handle cũ thì không thể phục hồi MÉP ĐỐI DIỆN
            // hoặc TIM đã có trong polygon khi state WebView/NOD bị stale.
            RecoverPipelineSelectionsFromCad(db, tr, state);
            state.SelectedTimHandles = FilterLiveHandles(db, tr, state.SelectedTimHandles, requireCurve: false);
            state.SelectedEdgeHandles = FilterLiveHandles(db, tr, state.SelectedEdgeHandles, requireCurve: true);
            state.SelectedComponentEdgeHandles = FilterLiveHandles(db, tr, state.SelectedComponentEdgeHandles, requireCurve: true);

            bool selectionsChanged =
                beforeTim != state.SelectedTimHandles.Count ||
                beforeEdge != state.SelectedEdgeHandles.Count ||
                beforeComponent != state.SelectedComponentEdgeHandles.Count;

            // Dữ liệu đối chiếu phải phản ánh hình học CAD hiện tại. Nếu đủ đầu vào thì tính lại,
            // nếu không đủ thì xóa kết quả stale để UI không tiếp tục hiển thị match cũ.
            if (state.SelectedTimHandles.Count > 0 &&
                state.SelectedEdgeHandles.Count > 0 &&
                state.GetEffectiveCrossSections().Count > 0)
            {
                try
                {
                    state.ComparisonResults = new IntersectionCadService().AutoMatch(db, tr, state);
                }
                catch
                {
                    state.ComparisonResults.Clear();
                }
            }
            else
            {
                state.ComparisonResults.Clear();
            }

            _stateStore.Save(db, tr, state);

            int removedTim = Math.Max(0, beforeTim - state.SelectedTimHandles.Count);
            int removedEdge = Math.Max(0, beforeEdge - state.SelectedEdgeHandles.Count);
            int removedComponent = Math.Max(0, beforeComponent - state.SelectedComponentEdgeHandles.Count);
            int polygonDelta = state.IntersectionPolygonHandles.Count - beforePolygon;

            string refreshMessage = string.Format(
                CultureInfo.InvariantCulture,
                "Đã làm mới từ CAD: {0} TIM · {1} MÉP · {2} polygon · {3} matched.",
                state.SelectedTimHandles.Count,
                state.SelectedEdgeHandles.Count,
                state.IntersectionPolygonHandles.Count,
                state.ComparisonResults.Count(x => x.Status.Equals("matched", StringComparison.OrdinalIgnoreCase)));

            return WebResponse.Ok(action, refreshMessage, new
            {
                refreshedFromCad = true,
                selectionsChanged,
                removedTim,
                removedEdge,
                removedComponentEdge = removedComponent,
                polygonDelta,
                timCount = state.SelectedTimHandles.Count,
                edgeCount = state.SelectedEdgeHandles.Count,
                componentEdgeCount = state.SelectedComponentEdgeHandles.Count,
                mcnCount = state.GetEffectiveCrossSections().Count,
                mcnLibraryCount = state.CrossSections.Count,
                activeCrossSectionIds = state.ActiveCrossSectionIds,
                polygonCount = state.IntersectionPolygonHandles.Count,
                comparisonCount = state.ComparisonResults.Count,
                matchedCount = state.ComparisonResults.Count(x => x.Status.Equals("matched", StringComparison.OrdinalIgnoreCase)),
                warningCount = state.ComparisonResults.Count(x => !x.Status.Equals("matched", StringComparison.OrdinalIgnoreCase)),
                stopToCrosswalkDistance = state.StopToCrosswalkDistance,
                results = state.ComparisonResults
            });
        }

        private List<string> FilterLiveHandles(
            Database db,
            Transaction tr,
            IEnumerable<string>? handles,
            bool requireCurve)
        {
            var result = new List<string>();
            foreach (string handle in (handles ?? Enumerable.Empty<string>())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                ObjectId id;
                try
                {
                    id = _geometry.FromHandle(db, handle);
                }
                catch
                {
                    continue;
                }

                try
                {
                    if (id.IsNull || !id.IsValid || id.IsErased)
                        continue;

                    DBObject? obj = tr.GetObject(id, OpenMode.ForRead, false, true);
                    if (obj == null || obj.IsErased || !(obj is Entity))
                        continue;
                    if (requireCurve && !(obj is Curve))
                        continue;

                    result.Add(id.Handle.ToString());
                }
                catch
                {
                    // Handle stale/eWasErased/eNullObjectId: bỏ khỏi state.
                }
            }

            return result;
        }

        private void RecoverPipelineSelectionsFromCad(
            Database db,
            Transaction tr,
            ArmProjectState state)
        {
            var timHandles = new HashSet<string>(
                state.SelectedTimHandles ?? new List<string>(),
                StringComparer.OrdinalIgnoreCase);
            var edgeHandles = new HashSet<string>(
                state.SelectedEdgeHandles ?? new List<string>(),
                StringComparer.OrdinalIgnoreCase);

            // Kết quả đối chiếu cũ vẫn là nguồn truy vết tốt trước khi tính lại.
            foreach (ArmComparisonState row in state.ComparisonResults ?? new List<ArmComparisonState>())
            {
                if (!string.IsNullOrWhiteSpace(row.TimHandle)) timHandles.Add(row.TimHandle.Trim());
                if (!string.IsNullOrWhiteSpace(row.LeftEdgeHandle)) edgeHandles.Add(row.LeftEdgeHandle.Trim());
                if (!string.IsNullOrWhiteSpace(row.RightEdgeHandle)) edgeHandles.Add(row.RightEdgeHandle.Trim());
            }

            var metadataStore = new EntityMetadataStore();
            foreach (string polygonHandle in state.IntersectionPolygonHandles ?? new List<string>())
            {
                ObjectId id;
                try { id = _geometry.FromHandle(db, polygonHandle); }
                catch { continue; }

                if (id.IsNull || !id.IsValid || id.IsErased) continue;

                Entity? polygonEntity = null;
                try { polygonEntity = tr.GetObject(id, OpenMode.ForRead, false, true) as Entity; }
                catch { }
                if (polygonEntity == null || polygonEntity.IsErased) continue;

                ArmEntityMetadata? md = null;
                try { md = metadataStore.Read(polygonEntity, tr); }
                catch { }
                if (md?.Extra == null) continue;

                AddCsvHandles(md.Extra, "NodeTimHandles", timHandles);
                AddCsvHandles(md.Extra, "EdgeHandles", edgeHandles);
                AddCsvHandles(md.Extra, "BullhornHandles", edgeHandles);
                AddCsvHandles(md.Extra, "OppositeEdgeHandles", edgeHandles);

                if (md.Extra.TryGetValue("BoundaryRunsV2", out string? runs) &&
                    !string.IsNullOrWhiteSpace(runs))
                {
                    foreach (string token in runs.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        string[] parts = token.Split('|');
                        if (parts.Length < 3) continue;
                        string handle = parts[2].Trim();
                        if (!string.IsNullOrWhiteSpace(handle)) edgeHandles.Add(handle);
                    }
                }
            }

            state.SelectedTimHandles = timHandles
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            state.SelectedEdgeHandles = edgeHandles
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static void AddCsvHandles(
            Dictionary<string, string> extra,
            string key,
            HashSet<string> target)
        {
            if (extra == null || target == null ||
                !extra.TryGetValue(key, out string? text) ||
                string.IsNullOrWhiteSpace(text))
                return;

            foreach (string token in text.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string value = token.Trim();
                if (!string.IsNullOrWhiteSpace(value)) target.Add(value);
            }
        }

        private WebResponse AutoMatch(string action, Database db, Transaction tr, ArmProjectState state)
        {
            state.ComparisonResults = new IntersectionCadService().AutoMatch(db, tr, state);
            _stateStore.Save(db, tr, state);
            return WebResponse.Ok(action, "Đối chiếu TIM/MÉP/MCN hoàn tất.", new { results = state.ComparisonResults });
        }

        private WebResponse ZoomComparison(string action, JsonElement p, Database db, Transaction tr, ArmProjectState state, Document doc)
        {
            string id = JsonPayload.String(p, "recordId");
            ArmComparisonState? cmp = state.ComparisonResults.FirstOrDefault(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (cmp == null) return WebResponse.Fail(action, "Không tìm thấy kết quả.");
            ObjectId oid = _geometry.FromHandle(db, cmp.TimHandle);
            if (!oid.IsNull && tr.GetObject(oid, OpenMode.ForRead, false) is Entity entity) _geometry.ZoomToEntity(doc.Editor, entity);
            return WebResponse.Ok(action, "Đã zoom kết quả.");
        }

        private WebResponse ZoomIssues(string action, Database db, Transaction tr, ArmProjectState state, Document doc)
        {
            var ids = state.ComparisonResults.Where(x => !x.Status.Equals("matched", StringComparison.OrdinalIgnoreCase))
                .Select(x => _geometry.FromHandle(db, x.TimHandle)).Where(x => !x.IsNull).ToList();
            Extents3d? ext = null;
            foreach (ObjectId id in ids)
            {
                if (!(tr.GetObject(id, OpenMode.ForRead, false) is Entity entity)) continue;
                try { ext = ext == null ? entity.GeometricExtents : Union(ext.Value, entity.GeometricExtents); } catch { }
            }
            if (ext != null) _geometry.ZoomToExtents(doc.Editor, ext.Value);
            return WebResponse.Ok(action, ids.Count == 0 ? "Không có lỗi cần zoom." : "Đã zoom vùng có kết quả cần xử lý.");
        }

        private WebResponse GenerateBatchApproachLines(string action, JsonElement p, Database db, Transaction tr, ArmProjectState state)
        {
            string markingCode = JsonPayload.String(p, "markingCode", "2.2");
            string templateId = JsonPayload.String(p, "templateId", string.Empty);
            double distance = JsonPayload.Double(p, "distance", 20.0);
            
            var generator = new Autoroadmarking_Pro.CadHost.Cad.Markings.BatchApproachLaneGenerator();
            var result = generator.Generate(db, tr, state, markingCode, templateId, distance);
            
            state.QuantitySnapshotDirty = true;
            return WebResponse.Ok(action, "Đã bố trí đồng loạt vạch " + markingCode + " trước vạch dừng.", result);
        }

        private WebResponse DrawMarkings(string action, JsonElement p, Document doc, Database db, Transaction tr, ArmProjectState state)
        {
            string sub = JsonPayload.String(p, "subAction");
            int count;
            object? detail = null;

            switch (sub)
            {
                case "Step1_VeVachDocTuyen":
                    count = new LongitudinalMarkingGenerator().Generate(db, tr, state);
                    break;

                case "Step2_VeDaGiacNut":
                {
                    List<string> polygonHandles = new IntersectionPolygonService().DrawPolygons(
                        db, tr, state, JsonPayload.String(p, "helperLayer", "_9.HOTRO_VUNG_NUT_GIAO"));
                    count = polygonHandles.Count;
                    detail = new
                    {
                        polygonHandles,
                        mode = "AUTO_SECTOR_BULLHORN_WITH_THROAT_FALLBACK"
                    };
                    break;
                }

                case "Step2_CapNhatDaGiacNut":
                    count = new IntersectionPolygonService().RefreshPolygons(db, tr, state).Count;
                    break;

                case "Step2_CatVachTrongDaGiac":
                    count = new IntersectionTrimService().TrimLongitudinalInsidePolygons(db, tr, state);
                    break;

                case "Step3_VeVachMep":
                    count = new EdgeMarkingGenerator().Generate(
                        db, tr, state,
                        JsonPayload.String(p, "templateId"),
                        JsonPayload.Double(p, "offset", 0.5),
                        JsonPayload.Double(p, "markingWidth", 0.0));
                    break;

                case "Step4_VeVachDungVaDiBo":
                {
                    double distance = JsonPayload.Double(p, "distance", state.StopToCrosswalkDistance);
                    MarkingDistanceValidation distanceValidation =
                        new MarkingPlacementPlanner().ValidateStopToCrosswalkDistance(distance, minimum: 0.10);

                    if (!distanceValidation.IsValid)
                        return WebResponse.Fail(action, distanceValidation.Error);

                    // Khoảng cách Bước 4 được lưu theo TIM 7.3 -> TIM 7.1.
                    state.StopToCrosswalkDistance = distanceValidation.Value;
                    distance = distanceValidation.Value;
                    double crossingWidth = JsonPayload.Double(p, "crossingWidth", 0.0);
                    double? crossingWidthOverride = crossingWidth >= 3.0 ? crossingWidth : (double?)null;

                    StopCrosswalkGenerationResult generated = new StopCrosswalkGenerator().Generate(
                        db, tr, state,
                        JsonPayload.String(p, "stopTemplateId"),
                        JsonPayload.String(p, "pedestrianTemplateId"),
                        distance,
                        crossingWidthOverride);

                    count = generated.CreatedCount;
                    detail = generated;
                    break;
                }

                default:
                    return WebResponse.Fail(action, "SubAction không hợp lệ: " + sub);
            }

            state.QuantitySnapshotDirty = true;
            _stateStore.Save(db, tr, state);
            return WebResponse.Ok(
                action,
                "Đã thực thi " + sub + ".",
                new
                {
                    subAction = sub,
                    count,
                    polygonCount = state.IntersectionPolygonHandles.Count,
                    stopToCrosswalkDistance = state.StopToCrosswalkDistance,
                    detail
                });
        }

        // ------------------------------------------------------------------
        // TAB 4
        // ------------------------------------------------------------------
        private WebResponse SelectBlockFolder(string action, Database db, Transaction tr, ArmProjectState state)
        {
            using (var dialog = new System.Windows.Forms.FolderBrowserDialog())
            {
                dialog.Description = "Chọn thư mục thư viện DWG Block 7.6 / 9.3";
                dialog.ShowNewFolderButton = false;
                if (!string.IsNullOrWhiteSpace(state.BlockLibraryPath) && System.IO.Directory.Exists(state.BlockLibraryPath)) dialog.SelectedPath = state.BlockLibraryPath;
                if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK)
                    return WebResponse.Ok(action, string.Empty, new { canceled = true, path = state.BlockLibraryPath });
                state.BlockLibraryPath = dialog.SelectedPath;
                _stateStore.Save(db, tr, state);
                return WebResponse.Ok(action, "Đã chọn thư mục thư viện.", new { path = state.BlockLibraryPath });
            }
        }

        private WebResponse ScanBlocks(string action, JsonElement p, Database db, Transaction tr, ArmProjectState state)
        {
            string path = JsonPayload.String(p, "libraryPath");
            if (string.IsNullOrWhiteSpace(path)) path = JsonPayload.String(p, "folderPath");
            if (string.IsNullOrWhiteSpace(path)) path = state.BlockLibraryPath;
            List<ArmBlockCatalogState> items = new DwgBlockLibraryScanner().Scan(path);
            state.BlockLibraryPath = path;
            state.BlockCatalog = items;
            _stateStore.Save(db, tr, state);
            var blocks = items.Select(x => new { code = x.Code, variant = x.Variant, blockName = x.Name, filePath = x.SourceDwgPath, exists = true }).ToList();
            return WebResponse.Ok(action, "Đã quét " + items.Count + " DWG block.", new { blocks, path, folderPath = path });
        }

        private WebResponse ReadBlockWorkspace(string action, Database db, Transaction tr, ArmProjectState state)
        {
            List<ArmRoadAxisState> roadAxes =
                new RoadAxisCatalogService()
                    .Build(
                        db,
                        tr,
                        state,
                        refreshPolylineRegistry: true);

            SymbolPlacementWorkspace workspace =
                new SymbolPlacementWorkspaceService()
                    .Build(
                        db,
                        tr,
                        state);

            _stateStore.Save(db, tr, state);

            return WebResponse.Ok(action, "Đã đọc workspace Block từ Tab 2/3.", new
            {
                workspace.LibraryPath,
                workspace.Blocks,
                workspace.Nodes,
                workspace.Proposals,
                roadAxes,
                crossSections = state.CrossSections,
                activeCrossSectionIds = state.ActiveCrossSectionIds,
                profiles = state.SymbolProfiles,
                rules = new
                {
                    distance76 = state.BlockDistance76,
                    inboundOnly76 = state.BlockInboundOnly76,
                    anchor93 = state.BlockAnchor93,
                    firstDistance93 = state.BlockFirstDistance93,
                    clusterCount93 = state.BlockClusterCount93,
                    clusterSpacing93 = state.BlockClusterSpacing93,
                    outDistance93 = state.BlockOutboundDistance93,
                    rotateWithTraffic = state.BlockRotateWithTraffic
                }
            });
        }

        private WebResponse SaveSymbolProfile(string action, JsonElement p, Database db, Transaction tr, ArmProjectState state)
        {
            ArmSymbolProfileState? profile = JsonPayload.Object<ArmSymbolProfileState>(p, "profile", _json);
            if (profile == null) return WebResponse.Fail(action, "Không đọc được profile MCN.");
            if (string.IsNullOrWhiteSpace(profile.AssemblyId)) return WebResponse.Fail(action, "Profile thiếu AssemblyId.");
            profile.Id = string.IsNullOrWhiteSpace(profile.Id) ? "SP_" + profile.AssemblyId : profile.Id;
            profile.UpdatedUtc = DateTime.UtcNow;
            state.SymbolProfiles.RemoveAll(x => x.AssemblyId.Equals(profile.AssemblyId, StringComparison.OrdinalIgnoreCase));
            state.SymbolProfiles.Add(profile);
            _stateStore.Save(db, tr, state);
            return WebResponse.Ok(action, "Đã lưu profile bố trí 7.6/9.3 theo MCN.", new { profile });
        }

        private WebResponse AnalyzeBlocks(string action, JsonElement p, Database db, Transaction tr, ArmProjectState state)
        {
            ApplyBlockPlacementRules(p, state);

            if (JsonPayload.Bool(p, "analyzeAll", false))
                return AnalyzeAllBlocks(action, db, tr, state);

            string approachId = JsonPayload.String(p, "approachId");
            string mcnId = JsonPayload.String(p, "mcnId");
            string direction = approachId.IndexOf("REVERSE", StringComparison.OrdinalIgnoreCase) >= 0 ? "REVERSE" : "FORWARD";
            if (string.IsNullOrWhiteSpace(mcnId) && !string.IsNullOrWhiteSpace(approachId))
            {
                string[] parts = approachId.Split('|');
                if (parts.Length >= 2) mcnId = parts[1];
            }

            ArmCrossSectionState? mcn = state.GetEffectiveCrossSections().FirstOrDefault(x =>
                x.Id.Equals(mcnId, StringComparison.OrdinalIgnoreCase) ||
                x.Name.Equals(mcnId, StringComparison.OrdinalIgnoreCase));
            if (mcn == null) return WebResponse.Fail(action, "Không xác định được MCN của nhánh. Hãy đối chiếu Tab 3 và lưu Tab 2 trước.");

            List<LaneMappingInput> maps = ReadLaneMappings(p, state, mcn, direction);
            if (maps.Count == 0)
                return WebResponse.Fail(action, "MCN chưa có lane/profile hợp lệ cho bố trí 7.6/9.3.");

            List<ArmBlockProposalState> proposals = new BlockPlacementCadService().Analyze(
                db, tr, state, mcn, approachId, maps,
                state.BlockFirstDistance93, state.BlockClusterCount93,
                state.BlockClusterSpacing93, state.BlockDistance76);

            RenumberProposals(proposals);
            state.BlockProposals = proposals;
            _stateStore.Save(db, tr, state);
            return WebResponse.Ok(action, "Đã phân tích " + proposals.Count + " vị trí Block.", new { proposals, warnings = Array.Empty<string>() });
        }

        private WebResponse AnalyzeAllBlocks(string action, Database db, Transaction tr, ArmProjectState state)
        {
            SymbolPlacementWorkspace workspace = new SymbolPlacementWorkspaceService().Build(db, tr, state);
            var all = new List<ArmBlockProposalState>();
            var warnings = new List<string>();
            var placement = new BlockPlacementCadService();

            foreach (SymbolWorkspaceNode node in workspace.Nodes)
            {
                foreach (SymbolWorkspaceApproach approach in node.Approaches)
                {
                    if (!approach.StopLineFound && !approach.CrosswalkFound)
                    {
                        warnings.Add(approach.Name + ": thiếu mốc 7.1/7.3 từ Tab 3, đã bỏ qua.");
                        continue;
                    }

                    ArmCrossSectionState? mcn = state.GetEffectiveCrossSections().FirstOrDefault(x =>
                        x.Id.Equals(approach.AssemblyId, StringComparison.OrdinalIgnoreCase) ||
                        x.Name.Equals(approach.AssemblyName, StringComparison.OrdinalIgnoreCase));
                    if (mcn == null)
                    {
                        warnings.Add(approach.Name + ": không tìm thấy MCN " + approach.AssemblyName + ".");
                        continue;
                    }

                    List<LaneMappingInput> maps = BuildMappingsForApproach(state, mcn, approach);
                    if (maps.Count == 0)
                    {
                        warnings.Add(approach.Name + ": chưa có lane/profile dùng được.");
                        continue;
                    }

                    try
                    {
                        List<ArmBlockProposalState> part = placement.Analyze(
                            db, tr, state, mcn, approach.Id, maps,
                            state.BlockFirstDistance93, state.BlockClusterCount93,
                            state.BlockClusterSpacing93, state.BlockDistance76);
                        all.AddRange(part);
                    }
                    catch (Exception ex)
                    {
                        warnings.Add(approach.Name + ": " + ex.Message);
                    }
                }
            }

            // PlacementKey là identity hình học; nếu dữ liệu workspace lặp, giữ một proposal duy nhất.
            all = all
                .GroupBy(x => x.PlacementKey, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .OrderBy(x => x.Road)
                .ThenBy(x => x.Approach)
                .ThenBy(x => x.StationValue)
                .ThenBy(x => x.OffsetValue)
                .ToList();
            RenumberProposals(all);
            state.BlockProposals = all;
            _stateStore.Save(db, tr, state);

            string message = "Đã phân tích toàn bộ: " + all.Count + " vị trí Block";
            if (warnings.Count > 0) message += ", " + warnings.Count + " cảnh báo";
            return WebResponse.Ok(action, message + ".", new { proposals = all, warnings });
        }

        private static void ApplyBlockPlacementRules(JsonElement p, ArmProjectState state)
        {
            JsonElement rules = p;
            if (JsonPayload.TryGet(p, "rules", out JsonElement nested) && nested.ValueKind == JsonValueKind.Object)
                rules = nested;

            state.BlockDistance76 = Math.Max(0.0, JsonPayload.Double(rules, "distance76", state.BlockDistance76));
            state.BlockInboundOnly76 = JsonPayload.Bool(rules, "inboundOnly76", state.BlockInboundOnly76);
            state.BlockAnchor93 = JsonPayload.String(rules, "anchor93", state.BlockAnchor93);
            state.BlockFirstDistance93 = Math.Max(0.0, JsonPayload.Double(rules, "firstDistance93", state.BlockFirstDistance93));
            state.BlockClusterCount93 = Math.Max(1, JsonPayload.Int(rules, "clusterCount93", state.BlockClusterCount93));
            state.BlockClusterSpacing93 = Math.Max(0.0, JsonPayload.Double(rules, "clusterSpacing93", state.BlockClusterSpacing93));
            state.BlockOutboundDistance93 = Math.Max(0.0, JsonPayload.Double(rules, "outDistance93", state.BlockOutboundDistance93));
            state.BlockRotateWithTraffic = JsonPayload.Bool(rules, "rotateWithTraffic", state.BlockRotateWithTraffic);
        }

        private List<LaneMappingInput> ReadLaneMappings(JsonElement p, ArmProjectState state, ArmCrossSectionState mcn, string direction)
        {
            List<LaneMappingInput> maps = JsonPayload.List<LaneMappingInput>(p, "laneMappings", _json);
            if (maps.Count == 0 && JsonPayload.TryGet(p, "lanes", out JsonElement laneArray) && laneArray.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement lane in laneArray.EnumerateArray())
                {
                    maps.Add(new LaneMappingInput
                    {
                        Direction = JsonPayload.String(lane, "direction", direction),
                        TrafficRole = NormalizeTrafficRole(JsonPayload.String(lane, "trafficRole", "INBOUND")),
                        Lane = string.IsNullOrWhiteSpace(JsonPayload.String(lane, "laneId")) ? JsonPayload.String(lane, "id") : JsonPayload.String(lane, "laneId"),
                        Movement = JsonPayload.String(lane, "movement", "STRAIGHT"),
                        Enable76 = JsonPayload.Bool(lane, "enable76", true),
                        Enable93 = JsonPayload.Bool(lane, "enable93", true)
                    });
                }
            }

            if (maps.Count == 0)
            {
                ArmSymbolProfileState? profile = state.SymbolProfiles.FirstOrDefault(x => x.AssemblyId.Equals(mcn.Id, StringComparison.OrdinalIgnoreCase));
                if (profile != null)
                    maps = profile.Lanes
                        .Select(x => ToLaneMapping(x, direction))
                        .Where(x => !string.IsNullOrWhiteSpace(x.Lane))
                        .ToList();
            }

            if (maps.Count == 0)
            {
                int li = 0, ri = 0;
                foreach (ArmCrossSectionPartState lane in mcn.Components
                             .Where(x => x.Role.Equals("Lane", StringComparison.OrdinalIgnoreCase))
                             .OrderBy(x => Math.Abs(x.Offset)))
                {
                    bool left = lane.Side.Equals("Left", StringComparison.OrdinalIgnoreCase);
                    maps.Add(new LaneMappingInput
                    {
                        Direction = direction,
                        TrafficRole = "INBOUND",
                        Lane = (left ? "L" + (++li) : "R" + (++ri)),
                        Movement = "STRAIGHT",
                        Enable76 = true,
                        Enable93 = true
                    });
                }
            }
            return maps;
        }

        private static List<LaneMappingInput> BuildMappingsForApproach(ArmProjectState state, ArmCrossSectionState mcn, SymbolWorkspaceApproach approach)
        {
            ArmSymbolProfileState? profile = state.SymbolProfiles.FirstOrDefault(x => x.AssemblyId.Equals(mcn.Id, StringComparison.OrdinalIgnoreCase));
            if (profile != null)
            {
                List<LaneMappingInput> configured = profile.Lanes
                    .Select(x => ToLaneMapping(x, approach.InboundDirection))
                    .Where(x => !string.IsNullOrWhiteSpace(x.Lane))
                    .ToList();
                if (configured.Count > 0) return configured;
            }

            return approach.Lanes.Select(l => new LaneMappingInput
            {
                Direction = approach.InboundDirection,
                TrafficRole = "INBOUND",
                Lane = l.Id,
                Movement = "STRAIGHT",
                Enable76 = true,
                Enable93 = true
            }).ToList();
        }

        private static LaneMappingInput ToLaneMapping(
            ArmSymbolLaneProfileState lane,
            string inboundDirection)
        {
            string role = NormalizeTrafficRole(
                !string.IsNullOrWhiteSpace(lane.TrafficRole)
                    ? lane.TrafficRole
                    : lane.Direction);

            string inbound = NormalizeAxisDirection(inboundDirection);
            string actualDirection = role == "OUTBOUND"
                ? OppositeAxisDirection(inbound)
                : inbound;

            return new LaneMappingInput
            {
                Direction = actualDirection,
                TrafficRole = role,
                Lane = lane.LaneId,
                Movement = string.IsNullOrWhiteSpace(lane.Movement) ? "STRAIGHT" : lane.Movement,
                Enable76 = lane.Enable76,
                Enable93 = lane.Enable93
            };
        }

        private static string NormalizeTrafficRole(string value)
        {
            string role = (value ?? string.Empty).Trim().ToUpperInvariant();
            if (role == "OUTBOUND" || role == "OUT" || role == "REVERSE")
                return "OUTBOUND";
            return "INBOUND";
        }

        private static string NormalizeAxisDirection(string value)
        {
            return string.Equals(value, "REVERSE", StringComparison.OrdinalIgnoreCase)
                ? "REVERSE"
                : "FORWARD";
        }

        private static string OppositeAxisDirection(string value)
        {
            return NormalizeAxisDirection(value) == "FORWARD"
                ? "REVERSE"
                : "FORWARD";
        }

        private static void RenumberProposals(List<ArmBlockProposalState> proposals)
        {
            for (int i = 0; i < proposals.Count; i++)
                proposals[i].Id = "P" + (i + 1).ToString("00000", CultureInfo.InvariantCulture);
        }

        private WebResponse GenerateBlocks(string action, JsonElement p, Database db, Transaction tr, ArmProjectState state)
        {
            List<ArmBlockProposalState> proposals = JsonPayload.List<ArmBlockProposalState>(p, "blockReferences", _json);
            if (proposals.Count == 0) proposals = JsonPayload.List<ArmBlockProposalState>(p, "proposals", _json);
            int count = new BlockPlacementCadService().Generate(db, tr, state, proposals);
            if (proposals.Count > 0) state.BlockProposals = proposals;
            state.QuantitySnapshotDirty = true;
            _stateStore.Save(db, tr, state);
            return WebResponse.Ok(action, "Đã sinh/cập nhật " + count + " Block.", new { count });
        }

        private WebResponse SaveBlockDefaults(string action, JsonElement p, Database db, Transaction tr, ArmProjectState state)
        {
            ApplyBlockPlacementRules(p, state);
            _stateStore.Save(db, tr, state);
            return WebResponse.Ok(action, "Đã lưu quy tắc bố trí Block mặc định.", new { rules = new { distance76 = state.BlockDistance76, inboundOnly76 = state.BlockInboundOnly76, anchor93 = state.BlockAnchor93, firstDistance93 = state.BlockFirstDistance93, clusterCount93 = state.BlockClusterCount93, clusterSpacing93 = state.BlockClusterSpacing93, outDistance93 = state.BlockOutboundDistance93, rotateWithTraffic = state.BlockRotateWithTraffic } });
        }

        // ------------------------------------------------------------------
        // TAB 5
        // ------------------------------------------------------------------
        private WebResponse ReadSupplementaryWorkspace(
            string action,
            Database db,
            Transaction tr,
            ArmProjectState state)
        {
            List<ArmRoadAxisState> catalog =
                new RoadAxisCatalogService()
                    .Build(
                        db,
                        tr,
                        state,
                        refreshPolylineRegistry: true);

            _stateStore.Save(db, tr, state);

            return WebResponse.Ok(action, "Đã đọc RoadAxis, thư viện và dữ liệu phát sinh.", new
            {
                roads = catalog,
                templates = state.MarkingTemplates,
                groups = state.ManagedGroups,
                blockRules = state.BlockManagementRules
            });
        }

        private static WebResponse SelectSupplementaryStation(string action, JsonElement p, Document doc, Database db, Transaction tr, ArmProjectState state)
        {
            object data = new SupplementaryMarkingCadService().SelectStation(doc.Editor, db, tr, state,
                JsonPayload.String(p, "roadKey"), JsonPayload.String(p, "prompt", "Chọn vị trí trên tuyến: "));
            return WebResponse.Ok(action, "Đã nhận lý trình.", data);
        }

        private WebResponse DrawSupplementaryPolyline(string action, JsonElement p, Document doc, Database db, Transaction tr, ArmProjectState state)
        {
            object data = new SupplementaryMarkingCadService().DrawManualPolyline(doc.Editor, db, tr, JsonPayload.String(p, "targetLayer"));
            state.QuantitySnapshotDirty = true;
            _stateStore.Save(db, tr, state);
            return WebResponse.Ok(action, "Đã vẽ hình học phát sinh.", data);
        }

        private static WebResponse PreviewSpeedHump(string action, JsonElement p, Database db, Transaction tr, ArmProjectState state)
        {
            var svc = new SupplementaryMarkingCadService();
            object data = svc.PreviewSpeedHump(db, tr, state,
                JsonPayload.String(p, "roadKey"), JsonPayload.String(p, "boundary1"), JsonPayload.String(p, "boundary2"),
                JsonPayload.String(p, "mode", "uniform"), JsonPayload.Double(p, "startStation"), JsonPayload.Double(p, "endStation"),
                JsonPayload.Double(p, "spacing", 5), JsonPayload.Bool(p, "balanceRemainder", true), JsonPayload.Double(p, "anchorStation"),
                JsonPayload.Int(p, "clusterDirectionSign", 1), JsonPayload.Int(p, "clusterCount", 1), JsonPayload.Double(p, "clusterOffset", 0),
                JsonPayload.Double(p, "clusterSpacing", 10), JsonPayload.Int(p, "barsPerCluster", 3), JsonPayload.Double(p, "barSpacing", 0.5),
                JsonPayload.Double(p, "stripWidth", 0.4));
            return WebResponse.Ok(action, "Preview gờ giảm tốc hoàn tất.", data);
        }

        private WebResponse GenerateSpeedHump(string action, JsonElement p, Database db, Transaction tr, ArmProjectState state)
        {
            string roadKey = JsonPayload.String(p, "roadKey");
            string roadName = JsonPayload.String(p, "roadName", roadKey);
            ArmRoadAxisState roadAxis = new RoadAxisCatalogService().ResolveDescriptor(db, tr, state, roadKey);
            roadKey = roadAxis.EffectiveAxisKey;
            roadName = roadAxis.RoadName;
            string groupId = JsonPayload.String(p, "groupId");
            if (string.IsNullOrWhiteSpace(groupId)) groupId = "SG_" + Guid.NewGuid().ToString("N").Substring(0, 10).ToUpperInvariant();
            string templateId = JsonPayload.String(p, "templateId");
            ArmMarkingTemplateState? template = state.MarkingTemplates.FirstOrDefault(x => x.Id.Equals(templateId, StringComparison.OrdinalIgnoreCase));
            double paintRatio = JsonPayload.Double(p, "paintRatio", template?.PaintRatio ?? 1.0);
            object data = new SupplementaryMarkingCadService().GenerateSpeedHump(db, tr, state,
                roadKey, roadName, JsonPayload.String(p, "boundary1"), JsonPayload.String(p, "boundary2"), JsonPayload.String(p, "mode", "uniform"),
                JsonPayload.Double(p, "startStation"), JsonPayload.Double(p, "endStation"), JsonPayload.Double(p, "spacing", 5),
                JsonPayload.Bool(p, "balanceRemainder", true), JsonPayload.Double(p, "anchorStation"), JsonPayload.Int(p, "clusterDirectionSign", 1),
                JsonPayload.Int(p, "clusterCount", 1), JsonPayload.Double(p, "clusterOffset", 0), JsonPayload.Double(p, "clusterSpacing", 10),
                JsonPayload.Int(p, "barsPerCluster", 3), JsonPayload.Double(p, "barSpacing", 0.5), JsonPayload.Double(p, "stripWidth", template?.Width ?? JsonPayload.Double(p, "stripWidth", 0.4)),
                JsonPayload.String(p, "markingCode", template?.Code ?? "GGT"), templateId,
                JsonPayload.String(p, "targetLayer", template?.Layer ?? string.Empty), groupId, paintRatio);

            UpsertSupplementaryGroupFromAnonymous(state, data, groupId, roadKey, roadName, "speed_hump", template?.Code ?? "GGT", "SEMI_AUTO", template?.Layer ?? string.Empty);
            state.QuantitySnapshotDirty = true;
            _stateStore.Save(db, tr, state);
            return WebResponse.Ok(action, "Đã tạo/cập nhật gờ giảm tốc.", data);
        }

        private WebResponse RegisterSupplementary(string action, JsonElement p, Database db, Transaction tr, ArmProjectState state)
        {
            List<string> handles = ReadStringArray(p, "handles");
            string roadKey = JsonPayload.String(p, "roadKey");
            string roadName = JsonPayload.String(p, "roadName", roadKey);
            ArmRoadAxisState roadAxis = new RoadAxisCatalogService().ResolveDescriptor(db, tr, state, roadKey);
            roadKey = roadAxis.EffectiveAxisKey;
            roadName = roadAxis.RoadName;
            string type = JsonPayload.String(p, "type", "manual_marking");
            string groupId = JsonPayload.String(p, "groupId");
            if (string.IsNullOrWhiteSpace(groupId)) groupId = "SG_" + Guid.NewGuid().ToString("N").Substring(0, 10).ToUpperInvariant();
            string templateId = JsonPayload.String(p, "templateId");
            ArmMarkingTemplateState? template = state.MarkingTemplates.FirstOrDefault(x => x.Id.Equals(templateId, StringComparison.OrdinalIgnoreCase));
            string code = JsonPayload.String(p, "markingCode", template?.Code ?? "SUPPLEMENTARY");
            double width = JsonPayload.Double(p, "width", template?.Width ?? 0.0);
            double ratio = JsonPayload.Double(p, "paintRatio", template?.PaintRatio ?? 1.0);
            object data = new SupplementaryMarkingCadService().RegisterExisting(db, tr, state, handles, roadKey, roadName, type, code, templateId, width, groupId, ratio);
            UpsertSupplementaryGroupFromAnonymous(state, data, groupId, roadKey, roadName, type, code,
                type.Equals("manual_block", StringComparison.OrdinalIgnoreCase) ? "EXTERNAL_EXISTING" : "MANUAL_PLAN", template?.Layer ?? string.Empty);
            state.QuantitySnapshotDirty = true;
            _stateStore.Save(db, tr, state);
            return WebResponse.Ok(action, "Đã đưa đối tượng phát sinh vào hệ thống quản lý.", data);
        }

        private WebResponse ScanSupplementaryBlocks(string action, JsonElement p, Database db, Transaction tr, ArmProjectState state)
        {
            List<object> items = new SupplementaryManagementCadService().ScanUnmanagedBlocks(db, tr, JsonPayload.String(p, "scopeLayer"));
            return WebResponse.Ok(action, "Đã quét Block trong ModelSpace.", new { items, count = items.Count });
        }

        private WebResponse SaveSupplementaryBlockRule(string action, JsonElement p, Database db, Transaction tr, ArmProjectState state)
        {
            ArmBlockManagementRuleState? rule = JsonPayload.Object<ArmBlockManagementRuleState>(p, "rule", _json);
            if (rule == null) return WebResponse.Fail(action, "Không đọc được rule quản lý Block.");
            if (string.IsNullOrWhiteSpace(rule.BlockName)) return WebResponse.Fail(action, "Rule phải có BlockName.");
            rule.Id = string.IsNullOrWhiteSpace(rule.Id) ? "BR_" + Guid.NewGuid().ToString("N") : rule.Id;
            state.BlockManagementRules.RemoveAll(x => x.Id.Equals(rule.Id, StringComparison.OrdinalIgnoreCase) || x.BlockName.Equals(rule.BlockName, StringComparison.OrdinalIgnoreCase));
            state.BlockManagementRules.Add(rule);
            _stateStore.Save(db, tr, state);
            return WebResponse.Ok(action, "Đã lưu rule ánh xạ Block.", new { rule, rules = state.BlockManagementRules });
        }

        private WebResponse SyncSupplementaryBlocks(string action, JsonElement p, Database db, Transaction tr, ArmProjectState state)
        {
            List<string> handles = ReadStringArray(p, "handles");
            string roadKey = JsonPayload.String(p, "roadKey");
            string roadName = JsonPayload.String(p, "roadName", roadKey);
            ArmRoadAxisState roadAxis = new RoadAxisCatalogService().ResolveDescriptor(db, tr, state, roadKey);
            roadKey = roadAxis.EffectiveAxisKey;
            roadName = roadAxis.RoadName;
            string groupId = JsonPayload.String(p, "groupId");
            if (string.IsNullOrWhiteSpace(groupId)) groupId = "BLK_" + Guid.NewGuid().ToString("N").Substring(0, 10).ToUpperInvariant();
            var svc = new SupplementaryManagementCadService();
            int count = svc.ApplyBlockRules(db, tr, state, handles, roadKey, roadName, groupId);
            svc.UpsertGroup(state, groupId, roadKey, roadName, "manual_block", "BLOCK", "EXTERNAL_EXISTING", string.Empty, count, 0, 0, handles);
            state.QuantitySnapshotDirty = true;
            _stateStore.Save(db, tr, state);
            return WebResponse.Ok(action, "Đã đồng bộ metadata cho " + count + " Block.", new { count, groupId, groups = state.ManagedGroups });
        }

        private WebResponse SetManagedGroupLock(string action, JsonElement p, Database db, Transaction tr, ArmProjectState state)
        {
            string groupId = JsonPayload.String(p, "groupId");
            bool locked = JsonPayload.Bool(p, "locked", true);
            int count = new SupplementaryManagementCadService().SetGroupLock(db, tr, state, groupId, locked);
            _stateStore.Save(db, tr, state);
            return WebResponse.Ok(action, locked ? "Đã khóa Group." : "Đã mở khóa Group.", new { count, groupId, locked, groups = state.ManagedGroups });
        }

        private WebResponse RemoveManagedGroup(string action, JsonElement p, Database db, Transaction tr, ArmProjectState state)
        {
            string groupId = JsonPayload.String(p, "groupId");
            int count = new SupplementaryManagementCadService().RemoveManagement(db, tr, state, groupId);
            state.QuantitySnapshotDirty = true;
            _stateStore.Save(db, tr, state);
            return WebResponse.Ok(action, "Đã gỡ quản lý ARM khỏi Group.", new { count, groupId, groups = state.ManagedGroups });
        }

        private WebResponse ZoomManagedGroup(string action, JsonElement p, Database db, Transaction tr, ArmProjectState state, Document doc)
        {
            bool ok = new SupplementaryManagementCadService().ZoomGroup(db, tr, doc.Editor, JsonPayload.String(p, "groupId"));
            return ok ? WebResponse.Ok(action, "Đã zoom Group.") : WebResponse.Fail(action, "Không tìm thấy hình học của Group.");
        }

        private WebResponse ZoomRoadByKey(string action, JsonElement p, Database db, Transaction tr, ArmProjectState state, Document doc)
        {
            string roadKey = JsonPayload.String(p, "roadKey");

            ArmRoadAxisState road =
                new RoadAxisCatalogService()
                    .ResolveDescriptor(
                        db,
                        tr,
                        state,
                        roadKey);

            ObjectId id = _geometry.FromHandle(db, road.Handle);
            if (id.IsNull || !(tr.GetObject(id, OpenMode.ForRead, false) is Entity entity))
                return WebResponse.Fail(action, "TIM/Alignment không còn tồn tại trong DWG.");

            _geometry.ZoomToEntity(doc.Editor, entity);
            return WebResponse.Ok(action, "Đã zoom " + road.RoadName + ".");
        }

        // ------------------------------------------------------------------
        // TAB 6
        // ------------------------------------------------------------------
        private WebResponse ReadQuantities(string action, Database db, Transaction tr, ArmProjectState state)
        {
            QuantityScanResult scan = new ModelSpaceQuantityScanner().ScanWithSummary(db, tr, state);
            state.LastQuantitySnapshotUtc = DateTime.UtcNow;
            state.QuantitySnapshotDirty = false;
            _stateStore.Save(db, tr, state);
            return WebResponse.Ok(action, "Đã quét " + scan.Rows.Count + " đối tượng được quản lý.", new
            {
                rows = scan.Rows,
                unmanagedCount = scan.UnmanagedCount,
                timestampUtc = state.LastQuantitySnapshotUtc,
                dirty = false
            });
        }

        private WebResponse ZoomQuantity(string action, JsonElement p, Database db, Transaction tr, Document doc)
        {
            string handle = JsonPayload.String(p, "handle");
            string recordId = JsonPayload.String(p, "recordId");
            ObjectId id = string.IsNullOrWhiteSpace(handle) ? ObjectId.Null : _geometry.FromHandle(db, handle);
            if (id.IsNull && !string.IsNullOrWhiteSpace(recordId))
            {
                var metadata = new EntityMetadataStore();
                BlockTableRecord ms = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForRead);
                foreach (ObjectId candidate in ms)
                {
                    if (!(tr.GetObject(candidate, OpenMode.ForRead, false) is Entity entity)) continue;
                    ArmEntityMetadata? md = metadata.Read(entity, tr);
                    if (md != null && md.RecordId.Equals(recordId, StringComparison.OrdinalIgnoreCase)) { id = candidate; break; }
                }
            }
            if (id.IsNull) return WebResponse.Fail(action, "Không tìm thấy đối tượng khối lượng.");
            if (tr.GetObject(id, OpenMode.ForRead, false) is Entity e) _geometry.ZoomToEntity(doc.Editor, e);
            return WebResponse.Ok(action, "Đã zoom đối tượng khối lượng.");
        }

        private WebResponse ExportExcel(string action, JsonElement p, Database db, Transaction tr, ArmProjectState state)
        {
            QuantityScanResult scan = new ModelSpaceQuantityScanner().ScanWithSummary(db, tr, state);
            List<CadQuantityRow> rows = scan.Rows;
            List<string> recordIds = JsonPayload.List<string>(p, "recordIds", _json);
            if (recordIds.Count > 0)
            {
                var selected = new HashSet<string>(recordIds, StringComparer.OrdinalIgnoreCase);
                rows = rows.Where(x => selected.Contains(x.RecordId)).ToList();
            }
            if (rows.Count == 0) return WebResponse.Fail(action, "Không có dữ liệu phù hợp để xuất Excel.");
            string path = new QuantityExcelExporter().Export(rows, JsonPayload.String(p, "fileName", "Khoi_luong_vach_son.xlsx"), db.Filename);
            return WebResponse.Ok(action, "Đã xuất Excel: " + path, new { path, count = rows.Count });
        }

        // ------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------
        private static List<string> ReadStringArray(JsonElement p, string name)
        {
            var result = new List<string>();
            if (!JsonPayload.TryGet(p, name, out JsonElement a) || a.ValueKind != JsonValueKind.Array) return result;
            foreach (JsonElement x in a.EnumerateArray()) if (x.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(x.GetString())) result.Add(x.GetString()!);
            return result;
        }

        private static void UpsertSupplementaryGroupFromAnonymous(ArmProjectState state, object data, string groupId, string roadKey, string roadName, string type, string code, string mode, string layer)
        {
            // Anonymous response is serialized once here instead of coupling CAD service to UI state DTO.
            using (JsonDocument doc = JsonDocument.Parse(JsonSerializer.Serialize(data)))
            {
                JsonElement root = doc.RootElement;
                int count = JsonPayload.Int(root, "count");
                double length = JsonPayload.Double(root, "totalLength");
                double area = JsonPayload.Double(root, "totalArea");
                List<string> handles = ReadStringArray(root, "handles");
                new SupplementaryManagementCadService().UpsertGroup(state, groupId, roadKey, roadName, type, code, mode, layer, count, length, area, handles);
            }
        }

        private static Extents3d Union(Extents3d a, Extents3d b) => new Extents3d(
            new Point3d(Math.Min(a.MinPoint.X, b.MinPoint.X), Math.Min(a.MinPoint.Y, b.MinPoint.Y), Math.Min(a.MinPoint.Z, b.MinPoint.Z)),
            new Point3d(Math.Max(a.MaxPoint.X, b.MaxPoint.X), Math.Max(a.MaxPoint.Y, b.MaxPoint.Y), Math.Max(a.MaxPoint.Z, b.MaxPoint.Z)));
    }
}
