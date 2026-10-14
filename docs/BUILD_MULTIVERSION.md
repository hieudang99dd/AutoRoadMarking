# AutoRoadMarking Pro — Build Civil 3D 2024 / 2025 / 2026

Cập nhật: 2026-10-14. Phần này mô tả **cấu hình source**, không khẳng định đã compile hay NETLOAD trên Autodesk Civil 3D.

## A. Chọn cấu hình ngay trong Visual Studio

1. `git pull --ff-only origin main` tại thư mục repo trên máy PC (GitHub không tự đẩy code xuống PC).
2. Mở `AutoRoadMarking_Pro.slnx` trên Visual Studio phiên bản hỗ trợ SLNX.
3. Trong thanh `Solution Configurations`, chọn `C3D2024`, `C3D2025`, hoặc `C3D2026`.
4. Chọn platform `x64`.
5. Menu **Build → Build Solution**. Kiểm tra Build Output; kiểm tra DLL tại `Autoroadmarking_Pro.CadHost/bin/Release/C3D<version>/`.

Các profile C3D có `Optimize=true` và dùng chung tên thư mục **Release** để phù hợp script `build-release.ps1`. Những cấu hình `Debug` và `Release` truyền thống vẫn mặc định C3D2024.

| Chọn trong Visual Studio | Target Framework CadHost | DLL |
|---|---|---|
| `C3D2024` | `net48` | `C3D2024/Autoroadmarking_Pro.CadHost.C3D2024.dll` |
| `C3D2025` | `net8.0-windows` | `C3D2025/Autoroadmarking_Pro.CadHost.C3D2025.dll` |
| `C3D2026` | `net8.0-windows` | `C3D2026/Autoroadmarking_Pro.CadHost.C3D2026.dll` |

Cần cài Civil 3D đích và .NET SDK đúng phiên bản. Nếu máy thiếu Autodesk assemblies, MSBuild sẽ dừng ở `ValidateCivil3DVersion`; **không thay DLL Autodesk của 2024 cho 2025/2026**.

## B. Build All — không cần sửa project hoặc gõ lại tham số

Trên Windows, chạy `build-all.cmd` (có thể mở PowerShell từ thư mục project):

```powershell
./build-all.ps1
./build-all.ps1 -Versions 2025,2026 -Configuration Release
```

Chỉ một phiên bản:

```powershell
./build-release.ps1 -Civil3DVersion 2025 -Configuration Release
```

Có 4 launcher tại thư mục root: `build-2024.cmd`, `build-2025.cmd`, `build-2026.cmd`, `build-all.cmd`. Build All gọi `build-release.ps1` **tuần tự**, báo PASS/FAIL theo từng version, và trả lỗi tổng nếu bất kỳ version nào thất bại. Không có DLL build sẵn trong git.

Nếu muốn chạy Build All từ menu Visual Studio, vào **Tools → External Tools → Add**:
- Title: `Build All Civil 3D`
- Command: `cmd.exe`
- Arguments: `/c "$(SolutionDir)build-all.cmd"`
- Initial directory: `$(SolutionDir)`

Trong Solution Explorer, folder `Build Helpers` liên kết các file batch/scripts để dễ tìm. Click đúp `.cmd` trong Solution Explorer thường mở editor, **không đồng nghĩa chạy script**; hãy dùng External Tools hoặc PowerShell để thực thi.

## C. Vì sao tách obj/C3D2025 và obj/C3D2026?

C3D2025 và C3D2026 cùng target .NET 8 nhưng reference assemblies Autodesk khác nhau. `Directory.Build.props` chọn `Civil3DVersion` theo Solution Configuration và đặt `BaseIntermediateOutputPath=obj/C3D<version>/` trong cả 4 project để tránh tái sử dụng artifacts/NuGet assets sai phiên bản. Override `-p:Civil3DVersion=2025` khi gọi MSBuild vẫn được ưu tiên hơn giá trị mặc định.

## D. Chỗ kiểm tra khi không thấy DLL 2025

- Xem cấu hình VS có đúng **C3D2025** (không phải `Release`) và **x64**.
- Kiểm tra `C:\Program Files\Autodesk\AutoCAD 2025\AcMgd.dll`, `AcDbMgd.dll`, `AcCoreMgd.dll` và `C3D\AeccDbMgd.dll` (hoặc custom `AutoCadRoot`/`Civil3DRoot`).
- Tìm đúng `Autoroadmarking_Pro.CadHost/bin/Release/C3D2025/Autoroadmarking_Pro.CadHost.C3D2025.dll`.
- Kiểm tra phiên bản Runtime. VS 2026 là môi trường IDE, không có nghĩa đổi plugin Civil 3D sang .NET 10.
- Chỉ ghi PASS build/runtime sau khi build thật, NETLOAD và thử `ARM_TEST`, `HDV_VACHSON` trên Civil 3D 2025.

## E. Kiểm thử và giới hạn

`python tools/verify-build-profiles.py`: XML profile, mapping version/framework/output, script và QA integration.

`python tools/qa-static.py`: kiểm thử hợp đồng hiện có, không thay thế compile.

GitHub Actions chỉ chạy kiểm thử tĩnh trên Ubuntu. Muốn kiểm thử plugin thật phải dùng máy Windows có cài Autodesk Civil 3D tương ứng. Trạng thái runtime: **chưa xác nhận**.
