using System;
using System.Collections.Generic;
using System.Linq;

namespace Autoroadmarking_Pro.Application.Supplementary
{
    public enum StationDistributionMode
    {
        Uniform,
        Cluster
    }

    /// <summary>
    /// Cách xử lý khi đoạn hợp lệ ngắn hơn spacing yêu cầu.
    /// CenterSingle là mặc định an toàn cho đối tượng lặp theo lý trình: tạo đúng
    /// một vị trí ở giữa miền làm việc thay vì kéo về đầu/cuối tuyến.
    /// </summary>
    public enum ShortSegmentPolicy
    {
        None,
        CenterSingle,
        StartSingle,
        EndSingle
    }

    public sealed class StationDistributionRequest
    {
        public StationDistributionMode Mode { get; set; } = StationDistributionMode.Uniform;

        // Miền station thật của RoadAxis.
        public double AxisStart { get; set; }
        public double AxisEnd { get; set; }

        // Miền làm việc của lệnh. Với Uniform, Start/End là bắt buộc về mặt logic;
        // End <= 0 được hiểu là hết tuyến để tương thích payload UI cũ.
        public double Start { get; set; }
        public double End { get; set; }

        public double Spacing { get; set; } = 5.0;
        public bool BalanceRemainder { get; set; } = true;
        public ShortSegmentPolicy ShortSegmentPolicy { get; set; } = ShortSegmentPolicy.CenterSingle;

        // Cluster mode.
        public double Anchor { get; set; }
        public int DirectionSign { get; set; } = 1;
        public int ClusterCount { get; set; } = 1;
        public double ClusterOffset { get; set; }
        public double ClusterSpacing { get; set; } = 10.0;
        public int BarsPerCluster { get; set; } = 3;
        public double BarSpacing { get; set; } = 0.5;

        /// <summary>
        /// Khoảng cách tối thiểu giữa hai station sau khi merge. 10 mm là đủ để
        /// tránh duplicate số học mà không xóa các vạch thiết kế thật.
        /// </summary>
        public double DuplicateTolerance { get; set; } = 0.01;
    }

    public sealed class StationDistributionResult
    {
        public List<double> Stations { get; set; } = new List<double>();
        public List<double> RejectedStations { get; set; } = new List<double>();
        public double EffectiveSpacing { get; set; }
        public double StartMargin { get; set; }
        public double EndMargin { get; set; }
        public double WorkingStart { get; set; }
        public double WorkingEnd { get; set; }
        public bool WasClippedToAxis { get; set; }
        public bool UsedShortSegmentPolicy { get; set; }
    }

    /// <summary>
    /// Bộ lập lịch lý trình thuần Application layer. Không phụ thuộc AutoCAD/Civil 3D,
    /// vì vậy có thể unit-test độc lập.
    ///
    /// Nguyên tắc:
    /// - Không bao giờ clamp từng candidate về endpoint; candidate ngoài miền bị reject.
    /// - Uniform ngắn hơn spacing dùng ShortSegmentPolicy thay vì sinh 0 hoặc 2 điểm sai.
    /// - BalanceRemainder chỉ chia phần dư ở hai đầu; spacing thiết kế không bị phóng đại.
    /// - Cluster giữ đúng anchor/direction và loại candidate ngoài miền.
    /// </summary>
    public sealed class StationDistributionPlanner
    {
        private const double Epsilon = 1e-9;

        public StationDistributionResult Build(StationDistributionRequest request)
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));

            if (!IsFinite(request.AxisStart) || !IsFinite(request.AxisEnd))
                throw new ArgumentException("Miền station của RoadAxis không hợp lệ.", nameof(request));

            double axisStart = Math.Min(request.AxisStart, request.AxisEnd);
            double axisEnd = Math.Max(request.AxisStart, request.AxisEnd);

            if (axisEnd - axisStart <= Epsilon)
            {
                return new StationDistributionResult
                {
                    WorkingStart = axisStart,
                    WorkingEnd = axisEnd
                };
            }

            return request.Mode == StationDistributionMode.Cluster
                ? BuildCluster(request, axisStart, axisEnd)
                : BuildUniform(request, axisStart, axisEnd);
        }

        private static StationDistributionResult BuildUniform(
            StationDistributionRequest request,
            double axisStart,
            double axisEnd)
        {
            double rawStart = IsFinite(request.Start) ? request.Start : axisStart;
            double rawEnd = (!IsFinite(request.End) || request.End <= 0.0) ? axisEnd : request.End;

            if (rawEnd < rawStart)
            {
                double t = rawStart;
                rawStart = rawEnd;
                rawEnd = t;
            }

            // Chỉ clip miền làm việc một lần. Candidate sau đó được validate với đúng
            // working range, không phải toàn bộ axis.
            double start = Math.Max(axisStart, rawStart);
            double end = Math.Min(axisEnd, rawEnd);
            bool clipped = Math.Abs(start - rawStart) > Epsilon || Math.Abs(end - rawEnd) > Epsilon;

            var result = new StationDistributionResult
            {
                WorkingStart = start,
                WorkingEnd = end,
                WasClippedToAxis = clipped
            };

            if (end < start - Epsilon)
                return result;

            double length = Math.Max(0.0, end - start);
            double spacing = PositiveOrDefault(request.Spacing, 0.01);
            double tolerance = PositiveOrDefault(request.DuplicateTolerance, 0.01);

            if (length <= Epsilon)
            {
                result.Stations.Add(start);
                result.EffectiveSpacing = 0.0;
                result.UsedShortSegmentPolicy = true;
                return result;
            }

            // Edge case quan trọng: L < spacing. Không tạo hai điểm cách spacing rồi
            // để một điểm lọt ra ngoài đoạn; cũng không mặc định kéo về start.
            if (length + Epsilon < spacing)
            {
                double? single = ResolveShortSegmentStation(request.ShortSegmentPolicy, start, end);
                if (single.HasValue)
                    result.Stations.Add(single.Value);

                result.EffectiveSpacing = 0.0;
                result.StartMargin = single.HasValue ? single.Value - start : length;
                result.EndMargin = single.HasValue ? end - single.Value : length;
                result.UsedShortSegmentPolicy = true;
                return result;
            }

            if (request.BalanceRemainder)
            {
                int intervalCount = Math.Max(1, (int)Math.Floor((length + Epsilon) / spacing));
                double occupied = intervalCount * spacing;

                // Do floor + floating point, occupied có thể nhỉnh hơn L vài epsilon.
                if (occupied > length && occupied - length < 1e-7)
                    occupied = length;

                double margin = Math.Max(0.0, (length - occupied) * 0.5);
                var candidates = new List<double>(intervalCount + 1);
                for (int i = 0; i <= intervalCount; i++)
                    candidates.Add(start + margin + i * spacing);

                MergeCandidates(candidates, start, end, tolerance, result);
                result.EffectiveSpacing = spacing;
                result.StartMargin = result.Stations.Count > 0 ? result.Stations[0] - start : length;
                result.EndMargin = result.Stations.Count > 0 ? end - result.Stations[result.Stations.Count - 1] : length;
                return result;
            }

            var anchored = new List<double>();
            for (double station = start; station <= end + Epsilon; station += spacing)
                anchored.Add(station);

            MergeCandidates(anchored, start, end, tolerance, result);
            result.EffectiveSpacing = spacing;
            result.StartMargin = result.Stations.Count > 0 ? result.Stations[0] - start : length;
            result.EndMargin = result.Stations.Count > 0 ? end - result.Stations[result.Stations.Count - 1] : length;
            return result;
        }

        private static StationDistributionResult BuildCluster(
            StationDistributionRequest request,
            double axisStart,
            double axisEnd)
        {
            double tolerance = PositiveOrDefault(request.DuplicateTolerance, 0.01);
            int clusters = Math.Max(1, Math.Min(100, request.ClusterCount));
            int bars = Math.Max(1, Math.Min(100, request.BarsPerCluster));
            int sign = request.DirectionSign < 0 ? -1 : 1;
            double clusterSpacing = Math.Max(0.0, FiniteOrZero(request.ClusterSpacing));
            double barSpacing = Math.Max(0.0, FiniteOrZero(request.BarSpacing));

            // Cluster mặc định dùng toàn bộ axis. Nếu caller truyền Start/End hợp lệ,
            // chúng được dùng làm working range để hỗ trợ cluster trong một phân đoạn.
            double start = axisStart;
            double end = axisEnd;
            if (IsFinite(request.Start) && IsFinite(request.End) && request.End > 0.0 && Math.Abs(request.End - request.Start) > Epsilon)
            {
                double a = Math.Min(request.Start, request.End);
                double b = Math.Max(request.Start, request.End);
                start = Math.Max(axisStart, a);
                end = Math.Min(axisEnd, b);
            }

            var result = new StationDistributionResult
            {
                WorkingStart = start,
                WorkingEnd = end,
                WasClippedToAxis = start > Math.Min(request.Start, request.End) + Epsilon ||
                                   (request.End > 0.0 && end < Math.Max(request.Start, request.End) - Epsilon),
                EffectiveSpacing = barSpacing
            };

            if (end < start - Epsilon)
                return result;

            double anchor = IsFinite(request.Anchor) ? request.Anchor : start;
            double first = anchor + sign * FiniteOrZero(request.ClusterOffset);

            var candidates = new List<double>(clusters * bars);
            for (int c = 0; c < clusters; c++)
            {
                double clusterAnchor = first + sign * c * clusterSpacing;
                for (int b = 0; b < bars; b++)
                    candidates.Add(clusterAnchor + sign * b * barSpacing);
            }

            MergeCandidates(candidates, start, end, tolerance, result);
            return result;
        }

        private static void MergeCandidates(
            IEnumerable<double> candidates,
            double min,
            double max,
            double tolerance,
            StationDistributionResult result)
        {
            foreach (double raw in candidates.Where(IsFinite).OrderBy(x => x))
            {
                if (raw < min - tolerance || raw > max + tolerance)
                {
                    result.RejectedStations.Add(raw);
                    continue;
                }

                // Chỉ snap sai số số học rất nhỏ về biên; đây không phải clamp nghiệp vụ.
                double value = raw < min ? min : (raw > max ? max : raw);
                if (result.Stations.Count == 0 || Math.Abs(result.Stations[result.Stations.Count - 1] - value) > tolerance)
                    result.Stations.Add(value);
            }
        }

        private static double? ResolveShortSegmentStation(
            ShortSegmentPolicy policy,
            double start,
            double end)
        {
            switch (policy)
            {
                case ShortSegmentPolicy.None:
                    return null;
                case ShortSegmentPolicy.StartSingle:
                    return start;
                case ShortSegmentPolicy.EndSingle:
                    return end;
                default:
                    return (start + end) * 0.5;
            }
        }

        private static bool IsFinite(double value) =>
            !double.IsNaN(value) && !double.IsInfinity(value);

        private static double FiniteOrZero(double value) =>
            IsFinite(value) ? value : 0.0;

        private static double PositiveOrDefault(double value, double fallback) =>
            IsFinite(value) && value > Epsilon ? value : fallback;
    }
}
