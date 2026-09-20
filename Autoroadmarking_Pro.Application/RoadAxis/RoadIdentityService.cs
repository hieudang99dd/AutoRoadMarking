using System;
using Autoroadmarking_Pro.Domain.RoadIdentity;

namespace Autoroadmarking_Pro.Application.RoadAxis
{
    /// <summary>
    /// Factory thuần nghiệp vụ cho RoadIdentity. Không phụ thuộc AutoCAD.
    /// </summary>
    public sealed class RoadIdentityService
    {
        public RoadIdentity CreatePolyline(
            string roadName,
            string recordId,
            string handle)
        {
            string name = RequireName(roadName);
            string id = string.IsNullOrWhiteSpace(recordId)
                ? Guid.NewGuid().ToString("N")
                : recordId.Trim();

            return new RoadIdentity
            {
                Id = id,
                AxisKey = "POLY:" + id,
                RoadName = name,
                AxisHandle = (handle ?? string.Empty).Trim(),
                AxisType = "POLYLINE",
                IdentitySource = "TAB0_METADATA",
                IsNativeNamed = false,
                OrientationSign = 1
            };
        }

        public RoadIdentity CreateAlignment(
            string alignmentName,
            string handle)
        {
            string name = RequireName(alignmentName);
            string axisHandle = (handle ?? string.Empty).Trim();

            if (axisHandle.Length == 0)
                throw new ArgumentException(
                    "Alignment phải có Handle để tạo AxisKey ổn định.",
                    nameof(handle));

            return new RoadIdentity
            {
                Id = "ALIGNMENT_" + axisHandle,
                AxisKey = "ALN:" + axisHandle,
                RoadName = name,
                AxisHandle = axisHandle,
                AxisType = "ALIGNMENT",
                IdentitySource = "CIVIL_ALIGNMENT",
                IsNativeNamed = true,
                AlignmentName = name,
                OrientationSign = 1
            };
        }

        /// <summary>
        /// API tương thích code cũ. Dữ liệu mới nên dùng CreatePolyline/CreateAlignment.
        /// </summary>
        public RoadIdentity Create(
            string roadName,
            string axisType,
            string? alignmentName = null)
        {
            string type = (axisType ?? string.Empty).Trim().ToUpperInvariant();
            string id = Guid.NewGuid().ToString("N");

            if (type == "ALIGNMENT")
            {
                // Không có Handle ở API cũ nên chỉ tạo identity tạm; CadHost sẽ
                // canonicalize về ALN:<Handle> khi đọc DWG.
                return new RoadIdentity
                {
                    Id = id,
                    AxisKey = "TEMP:" + id,
                    RoadName = RequireName(
                        string.IsNullOrWhiteSpace(alignmentName)
                            ? roadName
                            : alignmentName!),
                    AxisType = "ALIGNMENT",
                    IdentitySource = "CIVIL_ALIGNMENT",
                    IsNativeNamed = true,
                    AlignmentName = alignmentName,
                    OrientationSign = 1
                };
            }

            return CreatePolyline(
                roadName,
                id,
                string.Empty);
        }

        public string NormalizeKey(string value)
        {
            return (value ?? string.Empty)
                .Trim()
                .ToUpperInvariant()
                .Replace(" ", "_");
        }

        private static string RequireName(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                throw new ArgumentException(
                    "Tên tuyến không được để trống.",
                    nameof(value));

            return value.Trim();
        }
    }
}
