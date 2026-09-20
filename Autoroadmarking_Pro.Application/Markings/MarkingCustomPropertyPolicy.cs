using System;
using System.Collections.Generic;
using System.Linq;

namespace Autoroadmarking_Pro.Application.Markings
{
    /// <summary>
    /// Phân tách metadata mở rộng của dự án khỏi các trường kỹ thuật có kiểu dữ liệu rõ ràng.
    ///
    /// CustomProperties chỉ dành cho metadata bổ sung (ví dụ PaintType, Manufacturer,
    /// ProjectNote...). Không cho phép dùng key/value tự do để ghi đè tham số hình học,
    /// nhận dạng ARM, tham chiếu tiêu chuẩn hoặc thuộc tính CAD cốt lõi.
    ///
    /// Chính sách được đặt tại Application layer để UI và CadHost cùng tuân theo một
    /// nguyên tắc nghiệp vụ; CadHost vẫn là lớp thực thi kiểm tra có thẩm quyền cuối cùng.
    /// </summary>
    public static class MarkingCustomPropertyPolicy
    {
        private static readonly HashSet<string> Reserved = new HashSet<string>(
            new[]
            {
                // MarkingTemplate typed geometry/pattern fields.
                "Code", "MarkingCode", "Name", "Description", "Category", "Geometry",
                "Layer", "LayerName", "Width", "Pattern", "Linetype", "LinetypeName",
                "LinetypeScale", "Scale", "DashLength", "GapLength",
                "CustomDash1", "CustomGap1", "CustomDash2", "CustomGap2",
                "CustomPhase", "CustomCycle", "Cycle", "PaintRatio",
                "Color", "ColorHex", "Rgb", "RGB",

                // Engineering provenance/reference fields.
                "Reference", "StandardRef", "StandardReference", "StandardSource",
                "StandardClause", "VerificationStatus", "Verified",

                // ARM identity / persistence / CAD system fields.
                "Id", "TemplateId", "EntityId", "GroupId", "Handle", "LayerHandle",
                "ManagementState", "QuantityMethod", "ManagementSchema",
                "ManagementVersion", "ManagementUpdatedAt",
                "RoadIdentity", "RoadName", "RoadNameSnapshot", "RoadKey",
                "AxisKey", "AxisHandle", "AxisType", "NodeId", "NodeKey",
                "ApproachId", "ApproachKey", "OwnerId", "OwnerType",
                "GenerationMode", "Quantity", "Length", "Area", "Count"
            },
            StringComparer.OrdinalIgnoreCase);

        public static IReadOnlyCollection<string> ReservedKeys =>
            Reserved.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();

        public static bool IsReserved(string? key)
        {
            // Tách bước chuẩn hóa để compiler nullable-flow của C#/.NET Framework
            // không coi key là nullable tại thời điểm gọi Trim().
            string normalizedKey = (key ?? string.Empty).Trim();
            return normalizedKey.Length > 0 && Reserved.Contains(normalizedKey);
        }

        public static IReadOnlyList<string> Validate(IDictionary<string, string>? properties)
        {
            var errors = new List<string>();
            if (properties == null || properties.Count == 0) return errors;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, string> item in properties)
            {
                string key = (item.Key ?? string.Empty).Trim();
                if (key.Length == 0)
                {
                    errors.Add("CustomProperties có tên thuộc tính rỗng.");
                    continue;
                }
                if (!seen.Add(key))
                    errors.Add($"Thuộc tính mở rộng '{key}' bị trùng (không phân biệt hoa/thường).");
                if (key.Length > 64)
                    errors.Add($"Thuộc tính mở rộng '{key}' vượt quá 64 ký tự.");
                if (IsReserved(key))
                    errors.Add($"'{key}' là thuộc tính hệ thống; hãy chỉnh tại trường kỹ thuật tương ứng thay vì CustomProperties.");
            }

            return errors;
        }

        /// <summary>
        /// Chuẩn hóa key/value trước khi persist. Không tự xóa reserved key; reserved key
        /// phải bị Validate từ chối để người dùng nhận biết xung đột thay vì mất dữ liệu âm thầm.
        /// </summary>
        public static Dictionary<string, string> Normalize(IDictionary<string, string>? properties)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (properties == null) return result;

            foreach (KeyValuePair<string, string> item in properties)
            {
                string key = (item.Key ?? string.Empty).Trim();
                if (key.Length == 0) continue;
                result[key] = (item.Value ?? string.Empty).Trim();
            }
            return result;
        }
    }
}
