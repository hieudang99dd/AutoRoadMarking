using System;
using System.Collections.Generic;
using System.Linq;

namespace Autoroadmarking_Pro.Application.CrossSections
{
    /// <summary>
    /// Chữ ký hình học MCN dùng cho đối chiếu với TIM/MÉP. Tách khỏi AutoCAD để
    /// thuật toán có thể kiểm thử độc lập và không phụ thuộc API Civil 3D.
    /// </summary>
    public sealed class CrossSectionSignature
    {
        public string Id { get; set; } = string.Empty;
        public double LeftWidth { get; set; }
        public double RightWidth { get; set; }
        public int LeftLaneCount { get; set; }
        public int RightLaneCount { get; set; }

        public double TotalWidth => Math.Max(0.0, LeftWidth) + Math.Max(0.0, RightWidth);
    }

    public sealed class CrossSectionMeasurement
    {
        public double LeftWidth { get; set; }
        public double RightWidth { get; set; }
        public double Dispersion { get; set; }
        public int SampleCount { get; set; }

        public double TotalWidth => Math.Max(0.0, LeftWidth) + Math.Max(0.0, RightWidth);
    }

    public sealed class CrossSectionEngineeringMatch
    {
        public string Id { get; set; } = string.Empty;
        public double TotalDifference { get; set; }
        public double LeftDifference { get; set; }
        public double RightDifference { get; set; }
        public double AsymmetryDifference { get; set; }
        public double Score { get; set; }
        public bool IsWithinTolerance { get; set; }
        public string Reason { get; set; } = string.Empty;
    }

    /// <summary>
    /// Matcher đa tiêu chí. Không chỉ so tổng bề rộng vì hai MCN có thể bằng tổng
    /// nhưng lệch hẳn phân bố trái/phải so với TIM. Score càng nhỏ càng tốt.
    /// </summary>
    public sealed class CrossSectionEngineeringMatcher
    {
        public CrossSectionEngineeringMatch? Match(
            IEnumerable<CrossSectionSignature> candidates,
            CrossSectionMeasurement measured,
            double widthTolerance = 0.30,
            double sideTolerance = 0.35,
            int minimumSamples = 5)
        {
            if (candidates == null)
                return null;

            if (measured == null)
                throw new ArgumentNullException(nameof(measured));

            widthTolerance = Math.Max(0.01, widthTolerance);
            sideTolerance = Math.Max(widthTolerance, sideTolerance);

            List<CrossSectionEngineeringMatch> ranked = candidates
                .Where(x => x != null && !string.IsNullOrWhiteSpace(x.Id) && x.TotalWidth > 0.0)
                .Select(x => Evaluate(x, measured, widthTolerance, sideTolerance, minimumSamples))
                .OrderBy(x => x.Score)
                .ThenBy(x => x.TotalDifference)
                .ThenBy(x => x.LeftDifference + x.RightDifference)
                .ToList();

            return ranked.FirstOrDefault();
        }

        private static CrossSectionEngineeringMatch Evaluate(
            CrossSectionSignature candidate,
            CrossSectionMeasurement measured,
            double widthTolerance,
            double sideTolerance,
            int minimumSamples)
        {
            double totalDiff = Math.Abs(candidate.TotalWidth - measured.TotalWidth);
            double leftDiff = Math.Abs(Math.Max(0.0, candidate.LeftWidth) - Math.Max(0.0, measured.LeftWidth));
            double rightDiff = Math.Abs(Math.Max(0.0, candidate.RightWidth) - Math.Max(0.0, measured.RightWidth));
            double candidateAsymmetry = candidate.RightWidth - candidate.LeftWidth;
            double measuredAsymmetry = measured.RightWidth - measured.LeftWidth;
            double asymmetryDiff = Math.Abs(candidateAsymmetry - measuredAsymmetry);

            // Chuẩn hóa theo tolerance để score có ý nghĩa ổn định giữa dự án.
            double score =
                0.50 * totalDiff / widthTolerance +
                0.20 * leftDiff / sideTolerance +
                0.20 * rightDiff / sideTolerance +
                0.10 * asymmetryDiff / Math.Max(0.01, sideTolerance * 2.0);

            // Đường mép dao động mạnh hoặc quá ít mẫu làm giảm độ tin cậy.
            if (measured.Dispersion > 0.15)
                score += Math.Min(2.0, measured.Dispersion / 0.15) * 0.15;
            if (measured.SampleCount < minimumSamples)
                score += 0.50;

            bool totalOk = totalDiff <= widthTolerance;
            bool sidesOk = leftDiff <= sideTolerance && rightDiff <= sideTolerance;
            bool samplesOk = measured.SampleCount >= Math.Max(2, minimumSamples / 2);
            bool ok = totalOk && sidesOk && samplesOk;

            string reason;
            if (ok)
            {
                reason = "Khớp tổng bề rộng và phân bố trái/phải.";
            }
            else if (!samplesOk)
            {
                reason = "Không đủ mẫu hình học ổn định để tự động chấp nhận.";
            }
            else if (!totalOk)
            {
                reason = "Sai khác tổng bề rộng vượt dung sai.";
            }
            else
            {
                reason = "Tổng bề rộng gần đúng nhưng phân bố trái/phải lệch MCN.";
            }

            return new CrossSectionEngineeringMatch
            {
                Id = candidate.Id,
                TotalDifference = totalDiff,
                LeftDifference = leftDiff,
                RightDifference = rightDiff,
                AsymmetryDifference = asymmetryDiff,
                Score = score,
                IsWithinTolerance = ok,
                Reason = reason
            };
        }
    }
}
