using System;
using System.Collections.Generic;
using System.Linq;
using Autoroadmarking_Pro.Domain.Quantities;

namespace Autoroadmarking_Pro.Application.Quantities
{
    /// <summary>
    /// Tổng hợp báo cáo theo RoadName hiện hành (semantic identity mà người dùng nhìn thấy).
    /// AxisKey vẫn nằm trong từng record để truy vết kỹ thuật và rebind khi rename; nếu
    /// RoadName trống mới fallback về AxisKey. Cách này cho phép nhiều đoạn Polyline
    /// cùng được đặt một tên tuyến ở Tab 0 và vẫn cộng chung khối lượng theo tuyến.
    /// </summary>
    public sealed class QuantityAggregationService
    {
        public List<QuantityGroup> GroupByRoad(
            IEnumerable<QuantityRecord> records)
        {
            if (records == null)
                return new List<QuantityGroup>();

            return records
                .GroupBy(
                    RoadKey,
                    StringComparer.OrdinalIgnoreCase)
                .Select(group => new QuantityGroup
                {
                    Key = group.Key,
                    DisplayName = group
                        .Select(x => x.RoadName)
                        .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ??
                        group.Key,
                    Records = group.ToList()
                })
                .OrderBy(
                    x => x.DisplayName,
                    StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static string RoadKey(
            QuantityRecord record)
        {
            if (!string.IsNullOrWhiteSpace(record.RoadName))
                return record.RoadName.Trim();

            return (record.AxisKey ?? string.Empty).Trim();
        }
    }
}
