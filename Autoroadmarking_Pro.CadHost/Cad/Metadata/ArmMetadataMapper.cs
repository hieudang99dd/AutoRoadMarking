using System;
using Autodesk.AutoCAD.DatabaseServices;
using Autoroadmarking_Pro.CadHost.Cad.State;

namespace Autoroadmarking_Pro.CadHost.Cad.Metadata
{
    /// <summary>
    /// Chuẩn hóa và kiểm tra metadata AUTOROADMARKING_PRO trước khi ghi/đọc.
    /// Metadata hợp lệ là nguồn authoritative cho nhận dạng và khối lượng.
    /// </summary>
    public sealed class ArmMetadataMapper
    {
        public bool IsValidManagedRecord(ArmEntityMetadata? metadata)
        {
            return metadata != null
                && !string.IsNullOrWhiteSpace(metadata.RecordId)
                && !string.IsNullOrWhiteSpace(metadata.MarkingCode)
                && !string.IsNullOrWhiteSpace(metadata.RoadKey);
        }

        public ArmEntityMetadata Normalize(
            ArmEntityMetadata metadata,
            Entity entity)
        {
            if (metadata == null)
                throw new ArgumentNullException(nameof(metadata));

            if (entity == null)
                throw new ArgumentNullException(nameof(entity));

            metadata.Schema =
                string.IsNullOrWhiteSpace(metadata.Schema)
                    ? "2.0"
                    : metadata.Schema.Trim();

            metadata.RecordId =
                string.IsNullOrWhiteSpace(metadata.RecordId)
                    ? "ARM_" + Guid.NewGuid().ToString("N")
                    : metadata.RecordId.Trim();

            metadata.GenerationKey =
                (metadata.GenerationKey ?? string.Empty).Trim();

            metadata.Source =
                string.IsNullOrWhiteSpace(metadata.Source)
                    ? "AUTO"
                    : metadata.Source.Trim();

            metadata.RoadName =
                (metadata.RoadName ?? string.Empty).Trim();

            metadata.AxisKey =
                (metadata.AxisKey ?? string.Empty).Trim();

            metadata.AxisHandle =
                (metadata.AxisHandle ?? string.Empty).Trim();

            metadata.AxisType =
                (metadata.AxisType ?? string.Empty).Trim().ToUpperInvariant();

            // Schema <= 1 chỉ có RoadKey. Giữ khả năng đọc ngược nhưng dữ liệu mới
            // luôn truyền AxisKey ổn định từ RoadAxisCatalogService.
            if (string.IsNullOrWhiteSpace(metadata.AxisKey))
                metadata.AxisKey = (metadata.RoadKey ?? string.Empty).Trim();

            if (string.IsNullOrWhiteSpace(metadata.AxisKey))
                metadata.AxisKey = NormalizeKey(metadata.RoadName);

            metadata.RoadKey = metadata.AxisKey;

            metadata.OwnerType =
                string.IsNullOrWhiteSpace(metadata.OwnerType)
                    ? "ROAD"
                    : metadata.OwnerType.Trim().ToUpperInvariant();

            metadata.OwnerId =
                string.IsNullOrWhiteSpace(metadata.OwnerId)
                    ? metadata.RoadKey
                    : metadata.OwnerId.Trim();

            metadata.MarkingCode =
                (metadata.MarkingCode ?? string.Empty).Trim();

            metadata.TemplateId =
                (metadata.TemplateId ?? string.Empty).Trim();

            metadata.TemplateLayer =
                (metadata.TemplateLayer ?? string.Empty).Trim();

            metadata.CadLayer =
                string.IsNullOrWhiteSpace(metadata.CadLayer)
                    ? entity.Layer
                    : metadata.CadLayer.Trim();

            metadata.BlockName =
                (metadata.BlockName ?? string.Empty).Trim();

            if (metadata.Width < 0.0)
                metadata.Width = Math.Abs(metadata.Width);

            return metadata;
        }

        public ArmEntityMetadata CreateGeneratedMarking(
            Entity entity,
            string generationKey,
            string source,
            string roadName,
            string roadKey,
            string ownerType,
            string ownerId,
            string mcnId,
            string markingCode,
            string templateId,
            string templateLayer,
            double width,
            double station = 0.0,
            double offset = 0.0)
        {
            var metadata = new ArmEntityMetadata
            {
                RecordId = "MRK_" + Guid.NewGuid().ToString("N"),
                GenerationKey = generationKey ?? string.Empty,
                Source = source ?? "AUTO",
                RoadName = roadName ?? string.Empty,
                RoadKey = roadKey ?? string.Empty,
                OwnerType = ownerType ?? "ROAD",
                OwnerId = ownerId ?? string.Empty,
                McnId = mcnId ?? string.Empty,
                MarkingCode = markingCode ?? string.Empty,
                TemplateId = templateId ?? string.Empty,
                TemplateLayer = templateLayer ?? string.Empty,
                CadLayer = entity.Layer,
                Width = Math.Max(0.0, width),
                Station = station,
                Offset = offset
            };

            return Normalize(metadata, entity);
        }

        public ArmEntityMetadata BindAxis(
            ArmEntityMetadata metadata,
            ArmRoadAxisState axis)
        {
            if (metadata == null)
                throw new ArgumentNullException(nameof(metadata));

            if (axis == null)
                throw new ArgumentNullException(nameof(axis));

            metadata.RoadName = axis.RoadName ?? string.Empty;
            metadata.AxisKey = axis.EffectiveAxisKey ?? string.Empty;
            metadata.RoadKey = metadata.AxisKey;
            metadata.AxisHandle = axis.Handle ?? string.Empty;
            metadata.AxisType = axis.AxisType ?? string.Empty;
            return metadata;
        }

        public static string NormalizeKey(string? value)
        {
            if (value == null)
                return string.Empty;

            value = value.Trim();

            if (value.Length == 0)
                return string.Empty;

            string normalized =
                value
                    .ToUpperInvariant()
                    .Replace('Đ', 'D');

            var chars = new char[normalized.Length];
            int index = 0;
            bool previousSeparator = false;

            foreach (char c in normalized)
            {
                bool valid =
                    char.IsLetterOrDigit(c);

                if (valid)
                {
                    chars[index++] = c;
                    previousSeparator = false;
                    continue;
                }

                if (!previousSeparator && index > 0)
                {
                    chars[index++] = '_';
                    previousSeparator = true;
                }
            }

            while (index > 0 && chars[index - 1] == '_')
                index--;

            return new string(chars, 0, index);
        }
    }
}
