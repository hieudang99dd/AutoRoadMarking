# AutoRoadMarking Pro · FINAL SOURCE

Đây là source chính thức sau khi giao diện đã chốt và được tách thành HTML/CSS/JS modular. Backend C#/Civil 3D và WebView2 bridge được nối theo cùng contract; dữ liệu nghiệp vụ bền vững nằm trong DWG state/ARM entity metadata, không nằm trong state tạm của giao diện.

## Cấu trúc solution

- `Autoroadmarking_Pro.Domain` — model nghiệp vụ và metadata/quantity contracts.
- `Autoroadmarking_Pro.Application` — thuật toán/validation không phụ thuộc CAD khi có thể.
- `Autoroadmarking_Pro.Infrastructure` — serialization/configuration/migration.
- `Autoroadmarking_Pro.CadHost` — AutoCAD/Civil 3D adapters, DWG state, generators, quantities, WebView2 và UI modular.
- `Autoroadmarking_Pro.CadHost/UI/web` — giao diện chốt, CSS/JS đã tách module.
- `tools/qa-static.py` — static QA không cần Autodesk SDK.
- `docs/ACTION_MATRIX.md` — mapping tác vụ UI → backend.
- `docs/DEPLOYMENT_CHECKLIST.md` — checklist build/NETLOAD/nghiệm thu.
- `docs/ARCHITECTURE_FINAL.md` — nguyên tắc dữ liệu và luồng giữa các tab.

Solution: `AutoRoadMarking_Pro.slnx`.

## Luồng nghiệp vụ đã chốt

### Tab 0 · Định danh tuyến

- Civil 3D `Alignment`: dùng tên native, `AxisKey = ALN:<Handle>`.
- CAD Polyline: Tab 0 gắn `RoadName` + persistent identity, `AxisKey = POLY:<PersistentRoadId>`.
- Rename tên hiển thị không làm đổi khóa kỹ thuật của entity ARM.

### Tab 1 · Thư viện vạch sơn

Tab 1 là **nguồn thư viện Layer/template dùng chung**.

- CSV/template chỉ nạp thông số kỹ thuật.
- **CẬP NHẬT THƯ VIỆN** sinh/chuẩn hóa metadata cấp template: `TemplateId`, `MarkingCode`, `ManagementState`, `QuantityMethod`, schema/version/timestamp.
- **ÁP DỤNG VÀ ĐỒNG BỘ** tự chuẩn hóa các dòng đã chọn, tạo/cập nhật Layer + Linetype CAD và phát lại thư viện dùng chung cho Tab 2–6.
- Object identity/quantity (`RecordId`, `ObjectHandle`, Length/Area/Count...) chỉ xuất hiện khi có entity CAD thật.
- `CustomProperties` không được ghi đè field typed/reserved.

Tất cả template chuẩn đóng gói dùng `CUSTOM_REAL`. Backend tạo linetype CAD thật từ `CustomDash1/Gap1/Dash2/Gap2/Phase`. Template geometry-driven như 7.3/GGT dùng Continuous ở layer và hình học sơn do generator tạo.

Các quy tắc đặc biệt đã chốt:

- `1.3`: quản lý như **một template một nét**, nhưng generator/presentation tạo hai nét song song.
- `7.3`: zebra crossing thật; preset phổ thông stripe `0.40 m`, gap `0.60 m`, crossing width tối thiểu `3.0 m`.
- `GGT`: mã cuối cùng cho gờ giảm tốc; state/CSV cũ `GTT` được migrate thành `GGT`.

### Tab 2 · Thư viện mặt cắt

- MCN được persist trong DWG state.
- Nút **ĐỒNG BỘ MẶT CẮT ĐÃ CHỌN** lưu `ActiveCrossSectionIds` theo DWG.
- Khi danh sách này rỗng (DWG cũ/chưa từng sync), backend dùng toàn bộ thư viện để giữ tương thích.
- Sau khi đã sync, chỉ MCN trong tập active được Tab 3 đối chiếu và Tab 4 sử dụng.
- Save/Delete/Sync MCN làm invalid kết quả comparison/block proposal phụ thuộc MCN để tránh dữ liệu stale.

### Tab 3 · Sinh vạch 2D

- Chọn TIM/MÉP/MÉP CẤU KIỆN từ CAD.
- AutoMatch TIM–MÉP–MCN dùng tập MCN active của Tab 2.
- Sinh vạch dọc tuyến idempotent.
- Vẽ/cập nhật polygon nút giao; cắt vạch dọc trong polygon.
- Sinh vạch mép.
- Sinh 7.1 + 7.3 theo approach; khoảng cách 7.1↔7.3 là tham số dự án, chỉ yêu cầu `>= 0.10 m`, không hard-cap 3 m.
- Candidate ngoài station domain bị reject, không clamp về đầu/cuối tuyến.

### Tab 4 · 7.6 / 9.3

- Workspace lấy MCN active từ Tab 2, kết quả matching từ Tab 3 và anchor 7.1/7.3 từ ARM metadata.
- Mô hình `Node + Approach`; anchor khóa theo `AxisKey + NodeId + InboundDirection`.
- Profile lane dùng `TrafficRole` INBOUND/OUTBOUND và được persist theo AssemblyId.
- Phân tích proposal và Generate Block CAD là các tác vụ backend thật.

### Tab 5 · Phát sinh

- GGT theo uniform/cluster station distribution.
- Vạch phát sinh vẽ mới hoặc tiếp nhận entity hiện hữu.
- Block có sẵn được scan, map rule, attach ARM metadata và đồng bộ quản lý.
- Managed Group hỗ trợ zoom/lock/unmanage/update idempotent.

### Tab 6 · Khối lượng

- Nguồn sự thật là hình học ModelSpace hiện tại + ARM metadata.
- Linear marking: geometry length × paint ratio đúng một lần.
- Area/generated geometry: dùng hình học đã tạo/metadata tương ứng.
- Block: Count, đơn vị `Cái`.
- Report gom theo Road/Intersection nhưng vẫn giữ AxisKey/OwnerId để truy vết.
- Export Excel gồm các sheet tổng hợp/chi tiết theo implementation hiện tại.

## DWG state

`ArmProjectState.SchemaVersion = 11`.

Migration hiện xử lý tối thiểu:

- collection null/default;
- RoadAxis identity cũ;
- symbol profile normalization;
- template `GTT → GGT`;
- xóa legacy `CustomProperties.code` trùng typed `Code`;
- ép `QuantityMethod=GENERATED` cho 7.3/GGT;
- dọn `ActiveCrossSectionIds` mồ côi.

## Build

### Civil 3D 2024

Target `.NET Framework 4.8`:

```powershell
./build-release.ps1 -Civil3DVersion 2024 -Configuration Release
```

### Civil 3D 2025 / 2026

Target `.NET 8.0 Windows`:

```powershell
./build-release.ps1 -Civil3DVersion 2025 -Configuration Release
./build-release.ps1 -Civil3DVersion 2026 -Configuration Release
```

Nếu Autodesk cài ở đường dẫn khác:

```powershell
./build-release.ps1 -Civil3DVersion 2026 `
  -AutoCadRoot 'D:\Autodesk\AutoCAD 2026' `
  -Civil3DRoot 'D:\Autodesk\AutoCAD 2026\C3D'
```

Build script kiểm tra `AcCoreMgd/AcDbMgd/AcMgd/AeccDbMgd`, chạy static QA, restore và build đúng target.

DLL đầu ra:

`Autoroadmarking_Pro.CadHost/bin/Release/C3D<version>/Autoroadmarking_Pro.CadHost.C3D<version>.dll`

Sau `NETLOAD`:

- `ARM_TEST` — kiểm tra command registration.
- `HDV_VACHSON` — mở giao diện chính.

## Static QA

```bash
python tools/qa-static.py
```

QA hiện kiểm tra C# delimiter/lexical state, XML, duplicate HTML id, JavaScript syntax bằng Node, UI event function definitions, WebView action coverage, `goiAction` alias coverage, template reserved-property policy, CSV QCVN/GGT, 1.3/7.3 CUSTOM_REAL generator contracts, Tab 2 active MCN synchronization, state schema và build/cache hygiene.

Chi tiết: `QA_REPORT.md`.

## Giới hạn kiểm chứng của gói nguồn

Môi trường đóng gói hiện tại không có .NET SDK/MSBuild và Autodesk Civil 3D managed assemblies, nên **không thể chứng nhận compile/NETLOAD runtime tại đây**. Source đã static-QA; nghiệm thu cuối phải chạy `build-release.ps1` và test trên máy có Civil 3D đúng version. Xem `docs/DEPLOYMENT_CHECKLIST.md`.
