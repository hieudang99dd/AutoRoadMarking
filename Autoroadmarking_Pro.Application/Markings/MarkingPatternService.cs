using System;

namespace Autoroadmarking_Pro.Application.Markings
{
    public sealed class MarkingPatternDefinition
    {
        public string Pattern { get; set; } = "CONTINUOUS";
        public double DashLength { get; set; }
        public double GapLength { get; set; }
        public double Dash1 { get; set; }
        public double Gap1 { get; set; }
        public double Dash2 { get; set; }
        public double Gap2 { get; set; }
    }

    public sealed class MarkingPatternService
    {
        public double PaintRatio(MarkingPatternDefinition pattern)
        {
            if (pattern == null) return 1.0;
            string name = (pattern.Pattern ?? string.Empty).Trim().ToUpperInvariant();
            if (name == "CONTINUOUS" || name == "SOLID" || name.Length == 0) return 1.0;

            if (name == "CUSTOM_REAL")
            {
                double painted = Positive(pattern.Dash1) + Positive(pattern.Dash2);
                double cycle = painted + Positive(pattern.Gap1) + Positive(pattern.Gap2);
                return cycle > 1e-9 ? Clamp01(painted / cycle) : 1.0;
            }

            double dash = Positive(pattern.DashLength);
            double gap = Positive(pattern.GapLength);
            return dash + gap > 1e-9 ? Clamp01(dash / (dash + gap)) : 1.0;
        }

        private static double Positive(double value) => Math.Max(0.0, value);
        private static double Clamp01(double value) => Math.Max(0.0, Math.Min(1.0, value));
    }
}
