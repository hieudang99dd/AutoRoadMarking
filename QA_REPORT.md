# QA REPORT · AutoRoadMarking Pro

## Trạng thái hiện tại

Static QA/contract QA trên GitHub Actions đã **PASS** ngày 2026-09-22 cho source trên branch `main`.

Kết quả run đã xác nhận:

- **105** file C#: delimiter/token-state scan pass.
- `index.html`: **368 id unique**, không duplicate.
- **8** JavaScript module: `node --check` pass.
- **56 WebView `post(...)` action literal**: đều có backend handler.
- **28 `goiAction` aliases**: đều được bridge/backend xử lý.
- Tab 1 typed pattern/reference tách khỏi `CustomProperties`.
- Reserved metadata keys được backend policy validate.
- 7.1↔7.3 configurable, không hard-clamp 3 m.
- DWG state schema hiện tại: **11**.
- Không đóng gói `bin`, `obj`, `.vs`.

## Contract đã khóa và đang được QA kiểm tra

1. **GGT** là mã chính thức cho gờ giảm tốc; legacy `GTT` được migrate sang `GGT`.
2. CSV bundled phải chứa `1.3`, `7.3`, `GGT` và không còn `GTT`.
3. 7.3 dùng trực tiếp mốc `STEP2_POLYGON_AXIS_INTERSECTION`.
4. `StopCrosswalkGenerator` không được khôi phục heuristic `ResolveBullhornEndStation`.
5. Step 5 hỗ trợ `1.1 / 1.2 / 2.1 / 2.2` với nhãn liền/đứt đúng template.
6. Step 5 không được silent-clamp candidate vào endpoint của RoadAxis; thiếu chiều dài yêu cầu phải reject.
7. Step 5 lưu metadata truy vết gồm stop-line anchor, requested distance và station đầu/cuối.
8. `SOURCE_MANIFEST.sha256` được sinh deterministic từ toàn bộ git-tracked source và được kiểm tra hash trên CI.

## Manifest / CI

Workflow:

`.github/workflows/qa.yml`

Manifest generator:

`tools/update-manifest.py`

Static QA:

`tools/qa-static.py`

CI tự tái tạo manifest trước khi QA. Nếu manifest thay đổi trên push, workflow commit lại manifest bằng bot với commit message `chore: refresh source manifest [skip ci]`.

## Những gì PASS này chưa chứng minh

Static QA **không thay thế** runtime qualification trong Autodesk Civil 3D.

Vẫn phải kiểm tra trên máy có Civil 3D:

- build CadHost với Autodesk/Civil references thật;
- NETLOAD;
- thao tác WebView2 ↔ command context;
- sinh polygon nút giao trên DWG thật;
- 7.3/7.1 ở nút vuông, xiên, T-junction;
- khoảng cách station thực tế;
- Step 5 trên Alignment và Polyline;
- save/close/reopen DWG rồi Sync;
- regenerate không duplicate;
- hiệu năng trên bản vẽ lớn.

**Trạng thái:** Static/contract QA PASS · Civil 3D runtime qualification PENDING.
