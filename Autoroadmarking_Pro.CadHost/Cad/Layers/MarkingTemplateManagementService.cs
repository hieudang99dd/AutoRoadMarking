using System;
using System.Globalization;
using System.Linq;
using System.Text;
using Autoroadmarking_Pro.CadHost.Cad.State;

namespace Autoroadmarking_Pro.CadHost.Cad.Layers
{
    /// <summary>
    /// Chuẩn hóa metadata cấp template. Service này không tạo RecordId/EntityId cho
    /// đối tượng CAD; identity đối tượng chỉ được sinh khi generate/adopt geometry.
    /// </summary>
    public sealed class MarkingTemplateManagementService
    {
        public ArmMarkingTemplateState Normalize(ArmMarkingTemplateState template, bool markReady)
        {
            if (template == null) throw new ArgumentNullException(nameof(template));

            template.Code = (template.Code ?? string.Empty).Trim();
            template.Name = string.IsNullOrWhiteSpace(template.Name) ? template.Code : template.Name.Trim();
            template.Layer = (template.Layer ?? string.Empty).Trim();
            template.Pattern = string.IsNullOrWhiteSpace(template.Pattern)
                ? "CUSTOM_REAL"
                : template.Pattern.Trim().ToUpperInvariant();

            if (string.IsNullOrWhiteSpace(template.Id))
                template.Id = BuildTemplateId(template.Code, template.Layer);

            template.QuantityMethod = ResolveQuantityMethod(template);
            template.ManagementSchema = "ARM_QTY_TEMPLATE";
            template.ManagementVersion = Math.Max(1, template.ManagementVersion);
            template.ManagementState = markReady ? "READY" : "DIRTY";
            if (markReady) template.ManagementUpdatedAt = DateTime.UtcNow;
            return template;
        }

        public string BuildTemplateId(string code, string layer)
        {
            string normalizedCode = NormalizeKey(code);
            if (normalizedCode == "GGT") return "ARM_GGT";
            if (!string.IsNullOrWhiteSpace(normalizedCode)) return "QCVN41_" + normalizedCode;

            string layerKey = NormalizeKey(layer);
            return !string.IsNullOrWhiteSpace(layerKey)
                ? "ARM_LAYER_" + layerKey
                : "ARM_TEMPLATE_" + Guid.NewGuid().ToString("N");
        }

        public string ResolveQuantityMethod(ArmMarkingTemplateState template)
        {
            string code = (template.Code ?? string.Empty).Trim().ToUpperInvariant();
            if (code == "7.3" || code == "GGT") return "GENERATED";

            // QuantityMethod là field typed cấp template, không lấy từ CustomProperties.
            // Giữ cấu hình typed hợp lệ nếu một dự án đã chỉ định rõ AREA/COUNT/GENERATED.
            string configured = (template.QuantityMethod ?? string.Empty).Trim().ToUpperInvariant();
            if (configured == "AREA" || configured == "COUNT" || configured == "GENERATED")
                return configured;

            if (ReadCustom(template, "geometryType", out string? geometryType))
            {
                string geometry = (geometryType ?? string.Empty).Trim().ToUpperInvariant();
                if (geometry.Contains("CROSSWALK") || geometry.Contains("SPEED_HUMP"))
                    return "GENERATED";
            }

            return "LENGTH";
        }

        public static bool TryReadDouble(ArmMarkingTemplateState template, string key, out double value)
        {
            value = 0.0;
            if (!ReadCustom(template, key, out string? text) || string.IsNullOrWhiteSpace(text)) return false;
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) ||
                   double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value);
        }

        public static bool ReadCustom(ArmMarkingTemplateState template, string key, out string? value)
        {
            value = null;
            if (template.CustomProperties == null) return false;
            foreach (var pair in template.CustomProperties)
            {
                if (pair.Key.Equals(key, StringComparison.OrdinalIgnoreCase))
                {
                    value = pair.Value;
                    return true;
                }
            }
            return false;
        }

        private static string NormalizeKey(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            var sb = new StringBuilder();

            // Fix CS8602: Safely handle null reference before Trim()
            foreach (char c in (value ?? string.Empty).Trim().ToUpperInvariant())
            {
                if (char.IsLetterOrDigit(c)) sb.Append(c);
                else if (sb.Length > 0 && sb[sb.Length - 1] != '_') sb.Append('_');
            }
            return sb.ToString().Trim('_');
        }
    }
}