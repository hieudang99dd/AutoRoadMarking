# QA REPORT · AutoRoadMarking Pro FINAL SOURCE

## Kết quả static QA trong môi trường đóng gói

`python tools/qa-static.py` hiện PASS với:

- **104** file C#: delimiter/token-state scan pass.
- XML `.csproj/.slnx/.xaml`: parse pass.
- `index.html`: **360 id unique**, không duplicate.
- **8** JavaScript module: `node --check` pass.
- UI event `window.*`: không có function literal bị thiếu definition.
- **51 WebView `post(...)` action literal**: đều có backend handler.
- **27 `goiAction` aliases**: đều được bridge/backend xử lý.
- Không đóng gói `bin`, `obj`, `.vs`.
- DWG state schema: **11**.

## Kiểm tra nghiệp vụ/contract quan trọng

1. Tab 1 typed pattern/reference tách khỏi `CustomProperties`.
2. Reserved metadata keys được backend policy chặn, bao gồm management fields mới.
3. Bundled CSV có 15 template unique, toàn `CUSTOM_REAL`, có 1.3/7.3/GGT, không GTT.
4. GGT không còn lặp `code` trong `CustomProperties`.
5. `CUSTOM_REAL` có CAD linetype service thật; geometry-driven templates được nhận diện riêng.
6. 1.3 có `RequiresDoublePresentation`/`DUPLICATE_ON_CROSS_SECTION` trong generator.
7. 7.3 có `CreateCrosswalkZebra`, geometry type `PEDESTRIAN_CROSSWALK_ZEBRA`, stripe metadata paint ratio 1.
8. 7.1↔7.3 không có hard max 3 m; planner dùng optional warningMaximum và không clamp.
9. Tab 2 `SyncSelectedCrossSections` là backend action thật; active set persist bằng `ActiveCrossSectionIds` và `GetEffectiveCrossSections()`.
10. Thay đổi MCN/active set invalidates comparison/block proposal phụ thuộc MCN.
11. Migration nâng state lên schema 11, GTT→GGT, dọn reserved legacy code và quantity method 7.3/GGT.

## Scenario code-path đã rà soát

- Alignment native name / Polyline Tab 0 identity.
- Stable AxisKey khi đổi RoadName.
- Multi-node + FORWARD/REVERSE approach.
- Active MCN selection → engineering matcher.
- Candidate station ngoài domain reject, không clamp.
- Uniform/cluster station distribution và short segment behavior.
- Idempotent ARM generation keys.
- 1.3 double geometry từ single template.
- 7.3 zebra generated geometry.
- GGT generated supplementary geometry.
- Symbol placement 7.6/9.3 từ Node/Approach anchors.
- Quantity linear/area/count và dirty snapshot.

## Giới hạn kiểm chứng

Môi trường hiện tại có Node/Python nhưng **không có `dotnet`, `msbuild`, `csc` và Autodesk managed assemblies**. Vì vậy QA này không thay thế compile/link/NETLOAD thật.

Bước nghiệm thu bắt buộc trên máy Civil 3D được mô tả tại `docs/DEPLOYMENT_CHECKLIST.md`.
