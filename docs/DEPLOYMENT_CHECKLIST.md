# DEPLOYMENT / ACCEPTANCE CHECKLIST

## A. Trước build

- [ ] Dùng đúng source ZIP/manifest cuối.
- [ ] Cài .NET SDK phù hợp.
- [ ] Xác định Civil 3D version: 2024, 2025 hoặc 2026.
- [ ] Kiểm tra `AcCoreMgd.dll`, `AcDbMgd.dll`, `AcMgd.dll`, `AeccDbMgd.dll` đúng installation.
- [ ] Chạy `python tools/qa-static.py` → `STATIC QA: PASS`.

## B. Build

```powershell
./build-release.ps1 -Civil3DVersion 2026 -Configuration Release
```

Thay `2026` bằng version thực tế. Không copy Autodesk DLL vào package plugin.

- [ ] Build exit code = 0.
- [ ] Có `Autoroadmarking_Pro.CadHost.C3D<version>.dll` trong `bin/Release/C3D<version>/`.
- [ ] Thư mục `UI/web` được copy cạnh output hoặc embedded resource load thành công.

## C. Smoke test Civil 3D

- [ ] Mở **bản copy** DWG nghiệm thu.
- [ ] `NETLOAD` DLL.
- [ ] `ARM_TEST` chạy được.
- [ ] `HDV_VACHSON` mở Palette, status `CAD sẵn sàng`.
- [ ] Đóng/mở Palette lại không mất state DWG.

## D. Test theo tab

### Tab 0
- [ ] Alignment xuất hiện bằng tên native.
- [ ] Polyline định danh, rename RoadName và zoom/gỡ identity hoạt động.

### Tab 1
- [ ] Import CSV bundled đủ 15 code, không còn GTT.
- [ ] `CẬP NHẬT THƯ VIỆN` chuyển metadata về READY.
- [ ] `ÁP DỤNG VÀ ĐỒNG BỘ` tạo/cập nhật Layer + Linetype CAD.
- [ ] CUSTOM_REAL dashed nhìn đúng dash/gap.
- [ ] CUSTOM_REAL gap=0 tạo CAD linetype liên tục nhưng template ARM vẫn CUSTOM_REAL.

### Tab 2
- [ ] Tạo/sửa/xóa MCN.
- [ ] Chọn một số MCN → `ĐỒNG BỘ MẶT CẮT ĐÃ CHỌN`.
- [ ] Mở lại Palette/DWG: checkbox active set được phục hồi.
- [ ] Tab 3/4 chỉ dùng tập MCN đã sync.
- [ ] Thay đổi MCN làm comparison cũ bị invalid và yêu cầu match lại.

### Tab 3
- [ ] Select TIM/MÉP/MÉP CẤU KIỆN.
- [ ] AutoMatch dùng đúng MCN active.
- [ ] Sinh vạch dọc lần 2 không nhân bản ngoài ý muốn.
- [ ] Vạch 1.3 sinh hai nét nhưng giữ một TemplateId quản lý.
- [ ] Polygon: draw/refresh/trim.
- [ ] Vạch mép.
- [ ] 7.1/7.3: zebra nhiều stripe, khoảng cách thiết kế >3 m vẫn được nếu nằm trong station domain và >=0.10 m.

### Tab 4
- [ ] Chọn/scan thư viện block.
- [ ] Save profile MCN.
- [ ] Analyze proposals theo Node + Approach.
- [ ] Generate 7.6/9.3 đúng station/offset/rotation.
- [ ] Re-run không sinh duplicate ngoài generation key.

### Tab 5
- [ ] GGT uniform + cluster.
- [ ] Vẽ vạch phát sinh mới và adopt entity có sẵn.
- [ ] Scan/map/sync Block có sẵn.
- [ ] Zoom/lock/unmanage group.

### Tab 6
- [ ] Refresh sau mỗi thay đổi hình học.
- [ ] Kiểm tra linear paint ratio chỉ áp một lần.
- [ ] 7.3/GGT area/generated geometry không bị nhân paint ratio linetype sai.
- [ ] Block Count đúng.
- [ ] Export Excel mở được và tổng hợp khớp CAD kiểm chứng.

## E. Regression DWG cũ

- [ ] DWG có state schema cũ load được và được nâng lên schema 11 khi save.
- [ ] Template `GTT` được migrate `GGT`.
- [ ] Legacy `CustomProperties.code` trùng typed Code được dọn.
- [ ] DWG chưa có `ActiveCrossSectionIds` vẫn dùng toàn bộ MCN cho tới lần Sync đầu tiên.

## F. Chỉ ký nghiệm thu sau khi

- [ ] Build thật pass trên máy Civil 3D đích.
- [ ] NETLOAD pass.
- [ ] Một DWG có Alignment và một DWG dùng Polyline RoadAxis đều pass.
- [ ] Station/offset/rotation/quantity được đối chiếu với hồ sơ mẫu đã duyệt.
