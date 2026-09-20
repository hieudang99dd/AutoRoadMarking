# ARCHITECTURE FINAL

## 1. Nguyên tắc nguồn sự thật

- UI WebView2: state trình bày/transient.
- DWG NOD/XRecord (`ArmProjectState`): cấu hình và thư viện nghiệp vụ bền vững theo bản vẽ.
- ARM entity metadata: identity, generation key, owner, source, template và dữ liệu quản lý của entity CAD.
- Geometry CAD hiện tại: nguồn sự thật cho chiều dài/diện tích khi tính khối lượng.

Không lưu một thuộc tính kỹ thuật ở hai nguồn có thẩm quyền song song.

## 2. Pipeline giữa các tab

`Tab 0 RoadAxis → Tab 1 Template/Layer → Tab 2 Active MCN → Tab 3 Comparison + Marking Geometry → Tab 4 Symbol Blocks → Tab 5 Supplementary → Tab 6 Quantity`

Các thư viện được bridge phát lại sau khi backend xác nhận persist thành công.

## 3. Tab 1

CSV = technical template only. `Tab1_UpdateLayerManagementSet` chuẩn hóa metadata template. `Tab1_ApplySharedLayerSet` tạo/cập nhật CAD layer/linetype và trả full library để UI phát cho Tab 2–6.

`CUSTOM_REAL + gap=0` được tạo thành linetype liên tục ở CAD nhưng vẫn giữ pattern ARM là CUSTOM_REAL. 7.3/GGT là geometry-driven nên không dùng paint-ratio linetype để giả lập hình học.

## 4. Tab 2 Active MCN

`CrossSections` là toàn bộ thư viện. `ActiveCrossSectionIds` là tập đã bấm **ĐỒNG BỘ MẶT CẮT ĐÃ CHỌN**.

`GetEffectiveCrossSections()`:

- Active list rỗng → toàn bộ library (compatibility mode).
- Active list có dữ liệu → chỉ các MCN hợp lệ trong active set.
- stale IDs được prune khi load/save state.

Thay đổi tập active hoặc nội dung library invalidates `ComparisonResults` và `BlockProposals`.

## 5. Idempotency

Generator dùng generation key/metadata để tìm và cập nhật/thay thế entity ARM hiện hữu. Mục tiêu là bấm Generate/Update nhiều lần không tích lũy bản sao không kiểm soát.

## 6. Quantity

Tab 6 không dùng CSV/template làm số lượng. Quantity engine quét ModelSpace/metadata, đo geometry hiện tại, áp paint ratio một lần cho linear pattern và Count cho Block.

## 7. Compatibility

- C3D 2024: net48.
- C3D 2025/2026: net8.0-windows.
- Web UI được vừa copy ra output vừa embedded resource.
- Tên action UI legacy được bridge map sang backend action để giữ tương thích giao diện chốt.
