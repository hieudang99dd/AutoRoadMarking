using System;
using System.Collections.Generic;
using System.Linq;

namespace Autoroadmarking_Pro.CadHost.Cad.State
{
    /// <summary>
    /// Chuẩn hóa component Tab 2 và tính offset tâm component từ TIM. UI có thể gửi
    /// offset nhưng backend luôn tính lại từ thứ tự/bề rộng để tránh state sai sau edit.
    /// </summary>
    public sealed class CrossSectionStateNormalizer
    {
        public ArmCrossSectionState Normalize(ArmCrossSectionState section)
        {
            if (section == null) throw new ArgumentNullException(nameof(section));
            section.Id = string.IsNullOrWhiteSpace(section.Id) ? section.Name.Trim() : section.Id.Trim();
            section.Name = string.IsNullOrWhiteSpace(section.Name) ? section.Id : section.Name.Trim();
            section.Components = section.Components ?? new List<ArmCrossSectionPartState>();

            var ordered = section.Components
                .Select((item, index) => new { Item = item, Index = index })
                .OrderBy(x => x.Item.Order <= 0 ? x.Index + 1 : x.Item.Order)
                .ToList();

            double left = 0.0;
            double right = 0.0;
            int sequence = 0;

            foreach (var entry in ordered)
            {
                ArmCrossSectionPartState part = entry.Item;
                sequence++;
                part.Order = sequence;
                part.Id = string.IsNullOrWhiteSpace(part.Id) ? "P" + sequence.ToString("000") : part.Id.Trim();
                part.Role = NormalizeRole(part.Role);
                part.Side = NormalizeSide(part.Side, part.Role);
                part.Width = Math.Max(0.0, part.Width);
                if (part.Role == "Centerline") part.OccupiesWidth = false;
                if (part.Role == "Marking") part.OccupiesWidth = false;

                double occupied = part.OccupiesWidth ? part.Width : 0.0;
                if (part.Side == "Left")
                {
                    part.Offset = occupied > 0.0 ? -(left + occupied * 0.5) : -left;
                    left += occupied;
                }
                else if (part.Side == "Right")
                {
                    part.Offset = occupied > 0.0 ? right + occupied * 0.5 : right;
                    right += occupied;
                }
                else
                {
                    part.Offset = 0.0;
                }
            }

            section.Components = ordered.Select(x => x.Item).ToList();
            return section;
        }

        private static string NormalizeRole(string value)
        {
            string v = (value ?? string.Empty).Trim();
            if (v.Equals("TimTuyen", StringComparison.OrdinalIgnoreCase)) return "Centerline";
            if (v.Equals("LanXe", StringComparison.OrdinalIgnoreCase)) return "Lane";
            if (v.Equals("VachSon", StringComparison.OrdinalIgnoreCase)) return "Marking";
            if (v.Equals("DaiPhanCach", StringComparison.OrdinalIgnoreCase)) return "Median";
            if (v.Equals("ViaHe", StringComparison.OrdinalIgnoreCase)) return "Sidewalk";
            return string.IsNullOrWhiteSpace(v) ? "Component" : v;
        }

        private static string NormalizeSide(string value, string role)
        {
            if (role == "Centerline") return "Center";
            string v = (value ?? string.Empty).Trim();
            if (v.Equals("Trai", StringComparison.OrdinalIgnoreCase) || v.Equals("Trái", StringComparison.OrdinalIgnoreCase) || v.Equals("Left", StringComparison.OrdinalIgnoreCase)) return "Left";
            if (v.Equals("Phai", StringComparison.OrdinalIgnoreCase) || v.Equals("Phải", StringComparison.OrdinalIgnoreCase) || v.Equals("Right", StringComparison.OrdinalIgnoreCase)) return "Right";
            return "Center";
        }
    }
}
