# DEVELOPMENT PROGRESS — AutoRoadMarking

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
