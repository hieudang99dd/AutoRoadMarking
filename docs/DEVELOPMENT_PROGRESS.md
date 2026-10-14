# DEVELOPMENT PROGRESS — AutoRoadMarking

## 2026-10-15 · Tab 3 source cards — eliminate overflowing text

**Phát hiện**: tab Sinh vạch hiển thị nhóm nguồn TIM/MÉP/CẤU KIỆN/Thư viện bị bó trong thẻ nhỏ; nhãn và status badge "Chưa chọn" tràn/đè đường bao.

**Nguyên nhân kỹ thuật**:
- Inline `<style>` trong `UI/web/index.html` ép `repeat(4,1fr)!important` và bố cục nội dung ngang trên mọi panel.
- `design-system.css` cũng ép bốn cột; breakpoint chỉ quan sát viewport thay vì chiều rộng thực của WebView2.
- Flex child/badge và nút có `white-space:nowrap`, `height` cố định nên không thể co/xuống hàng trong card hẹp.
- `foundation.css` có bốn khối `{ ... }` không có selector, làm CSS không hợp lệ.

**Sửa đổi đã triển khai**:
- Bỏ `<style>` nội tuyến ở Tab 3; load `assets/css/tab3-source-layout.css` sau tất cả stylesheet còn lại.
- Dùng CSS container query trên `.t3-dashboard`: 4 cột khi rộng, 2 cột khi ≤1350px và 1 cột khi ≤710px **theo chiều rộng thực của panel**; fallback cho WebView2 cũ.
- Card flex wrap, text/badge/button được wrap mà không cắt nội dung; trạng thái tự tăng chiều cao khi cần.
- Xóa cấu hình nguồn bốn cột chồng chéo trong design-system và bốn orphan blocks của foundation.
- Thêm `tools/qa-tab3-source-layout.py` vào GitHub Actions; xem `docs/TAB3_OVERFLOW_FIX.md`.

**Kiểm thử**: 42 trường hợp Chromium headless trong fixture bốn card (14 kích thước 210–1740px × 100/125/150% zoom), kiểm tra box geometry của chữ/badge/nút không vượt card: **PASS**. Static CI: cần kiểm tra run. Kiểm thử plugin WebView2/NETLOAD trên Civil 3D thực tế **PENDING**.

**Phạm vi**: chỉ HTML/CSS bố cục Tab 3, không thay đổi thuật toán CAD, metadata, bridge hay các Tab 0/1/2/4/5/6.

## 2026-10-14 · Multi-version Visual Studio Build

**Yêu cầu**: Visual Studio chọn được build C3D2024/C3D2025/C3D2026 mà không sửa `.csproj`, có Build All nhanh, DLL ở folder riêng; cập nhật GitHub sau chỉnh sửa.

**Thay đổi source**:
- Solution `.slnx`: thêm solution BuildTypes `C3D2024`, `C3D2025`, `C3D2026` và solution folder `Build Helpers`.
- `Directory.Build.props`: ánh xạ lựa chọn IDE → `Civil3DVersion`, fallback 2024 cho Debug/Release cũ, optimization cho C3D profiles, tách `obj/C3D<version>`.
- CadHost `.csproj`: `ArmOutputConfiguration` giúp profile VS sinh DLL đúng `bin/Release/C3D<version>`.
- `build-all.ps1` và launcher `.cmd`: build tuần tự, báo lỗi rõ theo version, không ghi DLL vào Git.
- `tools/verify-build-profiles.py` và GitHub Actions: bổ sung static contract QA.
- `README.md`, `docs/BUILD_MULTIVERSION.md`, `docs/DEPLOYMENT_CHECKLIST.md`: hướng dẫn.

**Không thay đổi**: business rules, CAD model state/metadata, UI, WebView2 bridge.

**Chứng nhận**: kiểm tra cấu hình XML, script và hợp đồng tĩnh tự động hóa; compile/NETLOAD trên máy Civil 3D 2024/2025/2026 **PENDING**, không công bố DLL thành phẩm.

**Quy trình tiếp theo**: sau `git pull --ff-only` tại PC, chọn configuration C3D2025 và build; chạy NETLOAD, smoke test; ghi lỗi/tiến độ vào Markdown khi có dữ liệu kiểm thử. GitHub không tự đồng bộ lên PC nếu không có agent/runner local.
