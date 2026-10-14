# Tab 3 / Sinh vạch — xử lý triệt để tràn chữ khỏi card nguồn

Ngày 2026-10-15. Chỉ sửa giao diện; không đụng C# và API Civil 3D.

## Vấn đề và nguyên nhân

Ảnh lỗi cho thấy card **Đường TIM: 0 / Chưa chọn** có viền trái màu tím; nhãn và trạng thái dồn/đè mép phải. HTML hiện hành ép bốn cột bằng CSS `!important` ngay trong `<section id="tabVach2D">`. Đồng thời `design-system.css` ép bốn cột lần hai. Mỗi card bị đặt thành một flex row cùng nút chọn CAD, khiến chiều ngang dành cho nhãn quá nhỏ. Quy tắc responsive cũ chỉ dùng viewport `@media`; cửa sổ host Civil 3D có thể hẹp hơn kích thước màn hình lớn.

Status pill mặc định là `height:22px;white-space:nowrap`, dẫn đến tràn chiều ngang và có thể tràn chiều đứng khi cưỡng ép xuống dòng.

## Thay đổi

1. Bỏ 9 dòng CSS inline cố định của Tab 3 trong `UI/web/index.html`, không đổi cấu trúc và onclick nghiệp vụ.
2. Nạp `UI/web/assets/css/tab3-source-layout.css` làm stylesheet cuối.
3. Đặt `.t3-dashboard` làm CSS query container (CSS `container-type:inline-size`); nhóm card chọn số cột theo panel:
   - rộng hơn 1350 CSS px: 4 cột
   - từ 711 đến 1350 CSS px: 2 cột
   - tối đa 710 CSS px: 1 cột
4. Card dùng `flex-flow:row wrap`, nội dung `min-width:0`, cho nhãn/status/button xuống dòng; badge đặt `height:auto` và giới hạn `max-width:100%`; nút giữ tối thiểu 30px chiều cao.
5. Fallback `@supports not (container-type:inline-size)` dùng media query cho runtime WebView2 cũ.
6. Xóa rule bốn cột của Tab 3 trong `design-system.css`, loại bỏ hai orphan CSS block ở `foundation.css`.

**Không dùng** `transform:scale(...)`, không ẩn/cắt text để che bug, không sửa nội dung dữ liệu và không ảnh hưởng thao tác CAD.

## QA

- Fixture mô phỏng bốn card đúng cấu trúc HTML: Chromium headless; 12 chiều rộng 290–1740 px × ba mức zoom 100%,125%,150% = 36 trường hợp. So sánh bounding rectangles của nhãn, status badge và nút so với card; không có tràn, horizontal scroll overflow. Test local PASS.
- Static test tự động trên GitHub: `python tools/qa-tab3-source-layout.py`; kiểm tra thứ tự stylesheet, các card/status ID, container queries, flex wrap và không tái xuất hiện inline CSS.
- Giao diện thật vẫn cần test trên Civil 3D/WebView2: panel 320, 600, 900, 1400 CSS px; Windows 100/125/150%; đổi trạng thái card từ `Chưa chọn` thành `Đã chọn ...`; nút CAD phải có thể click và không bị che; Tab 0/1/2/4/5/6 hoạt động như trước.
- Tính năng backend và DLL runtime chưa được chứng nhận chỉ bằng bài kiểm tra trình duyệt.
