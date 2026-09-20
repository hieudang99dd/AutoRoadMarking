# ACTION MATRIX · FINAL

Bảng này mô tả action WebView/CadHost chính thức. Các action chọn/pick được `CadActionExecutor.IsInteractive(...)` đánh dấu để Palette xử lý đúng vòng đời tương tác CAD.

| Nhóm | Action | Chức năng |
|---|---|---|
| Core | `Ping` | Handshake UI ↔ C# và trả state schema. |
| Tab 0 | `ReadCadAxisIdentitiesCAD` | Đọc Alignment + Polyline đã định danh. |
| Tab 0 | `SelectCadAxisForNaming` | Pick TIM/Polyline để định danh. |
| Tab 0 | `AssignCadAxisIdentity` | Persist RoadName/identity. |
| Tab 0 | `ZoomCadAxisIdentity` | Zoom tuyến. |
| Tab 0 | `RemoveCadAxisIdentity` | Gỡ semantic identity do Tab 0 quản lý. |
| Tab 1 | `ReadMarkingTemplates` | Đọc thư viện template. |
| Tab 1 | `ValidateMarkingTemplate` | Validate field typed + CustomProperties. |
| Tab 1 | `SaveMarkingTemplate` | Thêm/cập nhật template. |
| Tab 1 | `ImportMarkingTemplates` | Import CSV đã parse ở UI. |
| Tab 1 | `DeleteMarkingTemplates` | Xóa template. |
| Tab 1 | `SyncMarkingTemplateCAD` | Tạo/cập nhật một template CAD. |
| Tab 1 | `SyncLegacyMarkingLayers` | Migration/adapter dữ liệu layer cũ. |
| Tab 1 | `Tab1_UpdateLayerManagementSet` | **CẬP NHẬT THƯ VIỆN**: chuẩn hóa metadata template. |
| Tab 1 | `Tab1_ApplySharedLayerSet` | **ÁP DỤNG VÀ ĐỒNG BỘ**: normalize + CAD Layer/Linetype + broadcast. |
| Tab 2 | `ReadCrossSections` | Đọc library + active MCN set. |
| Tab 2 | `SaveCrossSection` | Persist MCN. |
| Tab 2 | `DeleteCrossSections` | Xóa MCN/profile liên quan và invalidate dependent results. |
| Tab 2 | `SyncSelectedCrossSections` | **ĐỒNG BỘ MẶT CẮT ĐÃ CHỌN**: persist active set cho Tab 3/4. |
| Tab 2 | `SyncLegacyCrossSections` | Migration/adapter MCN legacy. |
| Tab 3 | `SelectTimCAD` | Chọn TIM/RoadAxis. |
| Tab 3 | `SelectMepCAD` | Chọn mép đường. |
| Tab 3 | `SelectMepCauKienCAD` | Chọn mép cấu kiện. |
| Tab 3 | `RefreshPipelineStatus` | Đọc trạng thái pipeline. |
| Tab 3 | `ReadComparisonResults` | Đọc kết quả TIM–MÉP–MCN. |
| Tab 3 | `AutoMatchCAD` | Đối chiếu engineering với active MCN set. |
| Tab 3 | `ZoomComparisonResult` | Zoom một result. |
| Tab 3 | `ZoomComparisonIssues` | Zoom vùng cần xử lý. |
| Tab 3 | `DrawMarkingsCAD` | Gateway các subaction sinh/cắt vạch. |
| Tab 4 | `SelectSymbolLibraryFolder` | Chọn thư mục DWG block. |
| Tab 4 | `ScanSymbolBlockLibrary` | Scan catalog block. |
| Tab 4 | `ReadSymbolPlacementWorkspace` | Đọc Node/Approach/MCN/profile/proposal. |
| Tab 4 | `ReadSymbolProfiles` | Đọc profile bố trí theo MCN. |
| Tab 4 | `SaveSymbolProfile` | Lưu profile lane 7.6/9.3. |
| Tab 4 | `AnalyzeSymbolBlockPlacement` | Tính proposal block. |
| Tab 4 | `GenerateSymbolBlocksCAD` | Sinh/update BlockReference. |
| Tab 4 | `SaveSymbolPlacementDefaults` | Lưu default rule toàn DWG. |
| Tab 5 | `ReadSupplementaryWorkspace` | Đọc road/template/group/rule. |
| Tab 5 | `SelectSupplementaryBoundary` | Pick boundary. |
| Tab 5 | `SelectSupplementaryEntities` | Chọn entity vạch hiện hữu. |
| Tab 5 | `SelectSupplementaryBlocks` | Chọn BlockReference hiện hữu. |
| Tab 5 | `SelectSupplementaryStation` | Pick station theo RoadAxis. |
| Tab 5 | `DrawSupplementaryManualPolyline` | Vẽ polyline phát sinh. |
| Tab 5 | `PreviewSpeedHump` | Preview station GGT. |
| Tab 5 | `GenerateSpeedHump` | Sinh/update GGT. |
| Tab 5 | `RegisterSupplementaryEntities` | Gắn ARM metadata cho entity phát sinh. |
| Tab 5 | `ScanSupplementaryBlocks` | Scan block chưa/đã quản lý. |
| Tab 5 | `SaveSupplementaryBlockRule` | Lưu mapping rule block. |
| Tab 5 | `SyncSupplementaryBlocks` | Apply rule/metadata cho selected blocks. |
| Tab 5 | `SetManagedGroupLock` | Lock/unlock managed group. |
| Tab 5 | `RemoveManagedGroup` | Gỡ ARM management metadata group. |
| Tab 5 | `ZoomManagedGroup` | Zoom group. |
| Tab 5 | `ZoomRoadAxisByKey` | Zoom RoadAxis. |
| Tab 6 | `ReadMarkingQuantitiesCAD` | Quét/đo quantity từ CAD. |
| Tab 6 | `ZoomMarkingQuantity` | Zoom quantity record/entity. |
| Tab 6 | `ExportMarkingQuantitiesExcel` | Xuất XLSX. |

## `DrawMarkingsCAD` subactions

| SubAction | Chức năng |
|---|---|
| `Step1_VeVachDocTuyen` | Sinh vạch dọc tuyến từ comparison + MCN. |
| `Step2_VeDaGiacNut` | Tương tác vẽ polygon nút giao. |
| `Step2_CapNhatDaGiacNut` | Refresh polygon registry. |
| `Step2_CatVachTrongDaGiac` | Trim vạch dọc trong vùng nút. |
| `Step3_VeVachMep` | Sinh vạch mép theo template/offset. |
| `Step4_VeVachDungVaDiBo` | Sinh 7.1 + zebra 7.3 theo approach. |

## UI aliases

Giao diện chốt còn một số `window.goiAction('Tên_UI')`; `arm-host-bridge.js` là nơi duy nhất map các alias này sang action backend chính thức. Static QA kiểm tra mọi alias literal đều có mapping hoặc handler backend.
