using System;

namespace Autoroadmarking_Pro.Application.Intersections
{
    /// <summary>
    /// Quy tắc thuần domain cho station của vạch tại nút giao.
    /// Không phụ thuộc Autodesk API để có thể regression-test độc lập.
    /// </summary>
    public sealed class MarkingPlacementPlanner
    {
        private const double StationTolerance = 1e-6;

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

        /// <summary>
        /// Khóa contract Bước 4:
        /// - anchorStation là giao TIM với cạnh polygon Bước 2 và đồng thời là TIM 7.3;
        /// - 7.3 trải đối xứng quanh anchor;
        /// - TIM 7.1 cách TIM 7.3 đúng requestedDistance theo outwardSign;
        /// - không clamp bất kỳ station nào về endpoint RoadAxis.
        /// </summary>
        public CrosswalkStopStationPlan PlanStopCrosswalk(
            double anchorStation,
            int outwardSign,
            double crossingLength,
            double requestedDistance,
            double axisStart,
            double axisEnd)
        {
            if (!IsFinite(anchorStation) || !IsFinite(crossingLength) ||
                !IsFinite(requestedDistance) || !IsFinite(axisStart) || !IsFinite(axisEnd))
            {
                return CrosswalkStopStationPlan.Invalid("Thông số station của 7.1/7.3 không hợp lệ.");
            }

            if (outwardSign != -1 && outwardSign != 1)
                return CrosswalkStopStationPlan.Invalid("outwardSign phải là -1 hoặc +1.");

            if (crossingLength <= 0.0)
                return CrosswalkStopStationPlan.Invalid("Chiều dài vùng 7.3 phải lớn hơn 0 m.");

            MarkingDistanceValidation distance = ValidateStopToCrosswalkDistance(requestedDistance);
            if (!distance.IsValid)
                return CrosswalkStopStationPlan.Invalid(distance.Error);

            NormalizeDomain(axisStart, axisEnd, out double domainStart, out double domainEnd);

            double halfLength = crossingLength * 0.5;
            double crosswalkStart = anchorStation - outwardSign * halfLength;
            double crosswalkEnd = anchorStation + outwardSign * halfLength;
            double stopStation = anchorStation + outwardSign * requestedDistance;

            if (!Inside(domainStart, domainEnd, anchorStation) ||
                !Inside(domainStart, domainEnd, crosswalkStart) ||
                !Inside(domainStart, domainEnd, crosswalkEnd) ||
                !Inside(domainStart, domainEnd, stopStation))
            {
                return CrosswalkStopStationPlan.Invalid(
                    "Vị trí 7.1 hoặc toàn bộ chiều dài 7.3 nằm ngoài miền station của RoadAxis.",
                    anchorStation,
                    crosswalkStart,
                    crosswalkEnd,
                    stopStation);
            }

            return CrosswalkStopStationPlan.Valid(
                anchorStation,
                crosswalkStart,
                crosswalkEnd,
                stopStation);
        }

        /// <summary>
        /// Khóa contract Bước 5 hiện hành: vạch tiếp cận là một đoạn có chiều dài đúng
        /// requestedDistance và kết thúc tại 7.1 theo hướng xe vào nút.
        /// Nếu RoadAxis không đủ chiều dài thì reject; tuyệt đối không silent-clamp.
        /// </summary>
        public ApproachSegmentPlan PlanApproachSegment(
            double stopStation,
            string approachDirection,
            double requestedDistance,
            double axisStart,
            double axisEnd)
        {
            if (!IsFinite(stopStation) || !IsFinite(requestedDistance) ||
                !IsFinite(axisStart) || !IsFinite(axisEnd))
            {
                return ApproachSegmentPlan.Invalid("Thông số station của vạch tiếp cận không hợp lệ.");
            }

            if (requestedDistance <= 0.0)
                return ApproachSegmentPlan.Invalid("Khoảng cách lùi từ vạch dừng phải lớn hơn 0 m.");

            string direction = (approachDirection ?? string.Empty).Trim().ToUpperInvariant();
            if (direction != "FORWARD" && direction != "REVERSE")
                return ApproachSegmentPlan.Invalid("ApproachDirection phải là FORWARD hoặc REVERSE.");

            double startStation;
            double endStation;

            if (direction == "FORWARD")
            {
                startStation = stopStation - requestedDistance;
                endStation = stopStation;
            }
            else
            {
                startStation = stopStation;
                endStation = stopStation + requestedDistance;
            }

            NormalizeDomain(axisStart, axisEnd, out double domainStart, out double domainEnd);

            if (!Inside(domainStart, domainEnd, stopStation) ||
                !Inside(domainStart, domainEnd, startStation) ||
                !Inside(domainStart, domainEnd, endStation))
            {
                double available = direction == "FORWARD"
                    ? Math.Max(0.0, stopStation - domainStart)
                    : Math.Max(0.0, domainEnd - stopStation);

                return ApproachSegmentPlan.Invalid(
                    "RoadAxis không đủ chiều dài để bố trí đủ " +
                    requestedDistance.ToString("0.###") + " m; khả dụng " +
                    available.ToString("0.###") + " m. Candidate bị reject, không clamp.",
                    startStation,
                    endStation);
            }

            return ApproachSegmentPlan.Valid(startStation, endStation);
        }

        private static bool IsFinite(double value) =>
            !double.IsNaN(value) && !double.IsInfinity(value);

        private static void NormalizeDomain(
            double a,
            double b,
            out double start,
            out double end)
        {
            start = Math.Min(a, b);
            end = Math.Max(a, b);
        }

        private static bool Inside(double start, double end, double station) =>
            station >= start - StationTolerance && station <= end + StationTolerance;
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

    public sealed class CrosswalkStopStationPlan
    {
        public bool IsValid { get; private set; }
        public string Error { get; private set; } = string.Empty;
        public double AnchorStation { get; private set; }
        public double CrosswalkStartStation { get; private set; }
        public double CrosswalkEndStation { get; private set; }
        public double StopStation { get; private set; }

        public static CrosswalkStopStationPlan Valid(
            double anchorStation,
            double crosswalkStartStation,
            double crosswalkEndStation,
            double stopStation) =>
            new CrosswalkStopStationPlan
            {
                IsValid = true,
                AnchorStation = anchorStation,
                CrosswalkStartStation = crosswalkStartStation,
                CrosswalkEndStation = crosswalkEndStation,
                StopStation = stopStation
            };

        public static CrosswalkStopStationPlan Invalid(
            string error,
            double anchorStation = 0.0,
            double crosswalkStartStation = 0.0,
            double crosswalkEndStation = 0.0,
            double stopStation = 0.0) =>
            new CrosswalkStopStationPlan
            {
                IsValid = false,
                Error = error ?? string.Empty,
                AnchorStation = anchorStation,
                CrosswalkStartStation = crosswalkStartStation,
                CrosswalkEndStation = crosswalkEndStation,
                StopStation = stopStation
            };
    }

    public sealed class ApproachSegmentPlan
    {
        public bool IsValid { get; private set; }
        public string Error { get; private set; } = string.Empty;
        public double StartStation { get; private set; }
        public double EndStation { get; private set; }

        public double Length => Math.Abs(EndStation - StartStation);

        public static ApproachSegmentPlan Valid(double startStation, double endStation) =>
            new ApproachSegmentPlan
            {
                IsValid = true,
                StartStation = startStation,
                EndStation = endStation
            };

        public static ApproachSegmentPlan Invalid(
            string error,
            double startStation = 0.0,
            double endStation = 0.0) =>
            new ApproachSegmentPlan
            {
                IsValid = false,
                Error = error ?? string.Empty,
                StartStation = startStation,
                EndStation = endStation
            };
    }
}
