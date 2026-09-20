using System;

namespace Autoroadmarking_Pro.Application.Intersections
{
    /// <summary>
    /// Validation thuần domain cho khoảng cách bố trí vạch ngang.
    /// Không hard-code trần 3 m: khoảng cách 7.1 ↔ 7.3 là tham số thiết kế của dự án.
    /// Nếu cần kiểm soát theo một tiêu chuẩn/hồ sơ riêng, caller có thể truyền
    /// warningMaximum để cảnh báo nhưng không tự ý sửa giá trị người dùng.
    /// </summary>
    public sealed class MarkingPlacementPlanner
    {
        public MarkingDistanceValidation ValidateStopToCrosswalkDistance(
            double requested,
            double minimum = 0.1,
            double? warningMaximum = null)
        {
            if (double.IsNaN(requested) || double.IsInfinity(requested))
                return MarkingDistanceValidation.Invalid("Khoảng cách 7.1 ↔ 7.3 không phải số hợp lệ.");

            if (requested < minimum)
                return MarkingDistanceValidation.Invalid(
                    "Khoảng cách 7.1 ↔ 7.3 phải ≥ " + minimum.ToString("0.###") + " m.");

            if (warningMaximum.HasValue && requested > warningMaximum.Value)
                return MarkingDistanceValidation.ValidWithWarning(
                    requested,
                    "Khoảng cách vượt giới hạn cảnh báo " + warningMaximum.Value.ToString("0.###") +
                    " m của cấu hình hiện hành; giá trị vẫn được giữ nguyên.");

            return MarkingDistanceValidation.Valid(requested);
        }
    }

    public sealed class MarkingDistanceValidation
    {
        public bool IsValid { get; private set; }
        public double Value { get; private set; }
        public string Error { get; private set; } = string.Empty;
        public string Warning { get; private set; } = string.Empty;

        public static MarkingDistanceValidation Valid(double value) =>
            new MarkingDistanceValidation { IsValid = true, Value = value };

        public static MarkingDistanceValidation ValidWithWarning(double value, string warning) =>
            new MarkingDistanceValidation { IsValid = true, Value = value, Warning = warning ?? string.Empty };

        public static MarkingDistanceValidation Invalid(string error) =>
            new MarkingDistanceValidation { IsValid = false, Error = error ?? string.Empty };
    }
}
