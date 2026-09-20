using System;
using System.Collections.Generic;
using System.Linq;

using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

using Autoroadmarking_Pro.Application.CrossSections;
using Autoroadmarking_Pro.CadHost.Cad.Geometry;
using Autoroadmarking_Pro.CadHost.Cad.Metadata;
using Autoroadmarking_Pro.CadHost.Cad.RoadAxis;
using Autoroadmarking_Pro.CadHost.Cad.State;

using AcEntity = Autodesk.AutoCAD.DatabaseServices.Entity;

namespace Autoroadmarking_Pro.CadHost.Cad.Intersections
{
    /// <summary>
    /// Dò cặp MÉP trái/phải cho mỗi TIM và đối chiếu thư viện MCN.
    /// Thuật toán dùng nhiều mẫu dọc tuyến và median/MAD để giảm ghép nhầm
    /// do đường mép ngắn, đường phụ hoặc hình học lân cận.
    /// </summary>
    public sealed class IntersectionCadService
    {
        private readonly CadGeometryService _geometry =
            new CadGeometryService();

        private readonly StationOffsetService _station =
            new StationOffsetService();

        public List<ArmComparisonState> AutoMatch(
            Database db,
            Transaction tr,
            ArmProjectState state,
            double tolerance = 0.30)
        {
            var results =
                new List<ArmComparisonState>();

            var edgeEntities =
                state.SelectedEdgeHandles
                    .Select(handle => new
                    {
                        Handle = handle,
                        Id = _geometry.FromHandle(
                            db,
                            handle)
                    })
                    .Where(x => !x.Id.IsNull)
                    .Select(x => new
                    {
                        x.Handle,
                        Curve =
                            tr.GetObject(
                                x.Id,
                                OpenMode.ForRead,
                                false)
                            as Curve
                    })
                    .Where(x => x.Curve != null)
                    .ToList();

            List<ArmRoadAxisState> axisCatalog =
                new RoadAxisCatalogService()
                    .Build(
                        db,
                        tr,
                        state,
                        refreshPolylineRegistry: true);

            foreach (string timHandle in state.SelectedTimHandles)
            {
                ObjectId timId =
                    _geometry.FromHandle(
                        db,
                        timHandle);

                if (timId.IsNull ||
                    !(tr.GetObject(
                        timId,
                        OpenMode.ForRead,
                        false) is AcEntity axis))
                {
                    continue;
                }

                ArmRoadAxisState? axisDescriptor =
                    axisCatalog.FirstOrDefault(x =>
                        string.Equals(
                            x.Handle,
                            timHandle,
                            StringComparison.OrdinalIgnoreCase));

                // Alignment có tên native và luôn vào catalog. Polyline bắt buộc phải
                // được định danh ở Tab 0 để mọi Tab sau có RoadIdentity duy nhất.
                if (axisDescriptor == null)
                {
                    results.Add(
                        new ArmComparisonState
                        {
                            Id = "CMP_" + Guid.NewGuid().ToString("N"),
                            Road = "CHƯA ĐỊNH DANH",
                            RoadKey = "UNIDENTIFIED:" + timHandle,
                            Tim = axis.GetType().Name + " " + timHandle,
                            TimHandle = timHandle,
                            Mcn = "—",
                            Status = "action",
                            Reason = "TIM Polyline chưa có RoadIdentity. Hãy gắn tên tuyến ở Tab 0 rồi đối chiếu lại."
                        });

                    continue;
                }

                string roadName =
                    axisDescriptor.RoadName;

                string roadKey =
                    axisDescriptor.EffectiveAxisKey;

                Curve? proxy =
                    _station.CreatePolylineProxy(
                        axis);

                if (proxy == null)
                    continue;

                try
                {
                    var candidates =
                        new List<EdgeOffsetCandidate>();

                    foreach (var edge in edgeEntities)
                    {
                        if (edge.Curve == null)
                            continue;

                        if (TryEstimateOffset(
                            proxy,
                            edge.Curve,
                            out double medianOffset,
                            out double dispersion,
                            out int sampleCount))
                        {
                            candidates.Add(
                                new EdgeOffsetCandidate
                                {
                                    Handle =
                                        edge.Handle,
                                    Offset =
                                        medianOffset,
                                    Dispersion =
                                        dispersion,
                                    SampleCount =
                                        sampleCount
                                });
                        }
                    }

                    EdgeOffsetCandidate? left =
                        candidates
                            .Where(x => x.Offset < 0.0)
                            .OrderBy(x => Math.Abs(x.Offset))
                            .ThenBy(x => x.Dispersion)
                            .FirstOrDefault();

                    EdgeOffsetCandidate? right =
                        candidates
                            .Where(x => x.Offset > 0.0)
                            .OrderBy(x => Math.Abs(x.Offset))
                            .ThenBy(x => x.Dispersion)
                            .FirstOrDefault();

                    bool hasLeft =
                        left != null;

                    bool hasRight =
                        right != null;

                    double width =
                        hasLeft && hasRight
                            ? Math.Abs(left!.Offset) +
                              Math.Abs(right!.Offset)
                            : 0.0;

                    ArmCrossSectionState? match = null;
                    double? difference = null;
                    double score = double.MaxValue;
                    string reason = string.Empty;
                    string status;

                    if (hasLeft && hasRight)
                    {
                        var measurement = new CrossSectionMeasurement
                        {
                            LeftWidth = Math.Abs(left!.Offset),
                            RightWidth = Math.Abs(right!.Offset),
                            Dispersion = Math.Max(left.Dispersion, right.Dispersion),
                            SampleCount = Math.Min(left.SampleCount, right.SampleCount)
                        };

                        List<ArmCrossSectionState> activeCrossSections = state.GetEffectiveCrossSections();
                        CrossSectionEngineeringMatch? engineeringMatch =
                            new CrossSectionEngineeringMatcher().Match(
                                activeCrossSections.Select(x => new CrossSectionSignature
                                {
                                    Id = x.Id,
                                    LeftWidth = x.LeftWidth,
                                    RightWidth = x.RightWidth,
                                    LeftLaneCount = x.LeftLaneCount,
                                    RightLaneCount = x.RightLaneCount
                                }),
                                measurement,
                                widthTolerance: tolerance,
                                sideTolerance: Math.Max(tolerance, 0.35));

                        match = engineeringMatch == null
                            ? null
                            : activeCrossSections.FirstOrDefault(x =>
                                string.Equals(x.Id, engineeringMatch.Id, StringComparison.OrdinalIgnoreCase));
                        difference = engineeringMatch?.TotalDifference;
                        score = engineeringMatch?.Score ?? double.MaxValue;
                        reason = engineeringMatch?.Reason ?? "Không có MCN ứng viên.";
                        status = engineeringMatch != null && engineeringMatch.IsWithinTolerance
                            ? "matched"
                            : "action";
                    }
                    else
                    {
                        status = "unpaired";
                        reason = !hasLeft && !hasRight
                            ? "Không ghép được mép trái và mép phải."
                            : "Chỉ ghép được một phía mép đường.";
                    }

                    results.Add(
                        new ArmComparisonState
                        {
                            Id =
                                "CMP_" +
                                Guid.NewGuid()
                                    .ToString("N"),
                            Road = roadName,
                            RoadKey = roadKey,
                            Tim =
                                axis.GetType().Name +
                                " " +
                                timHandle,
                            TimHandle = timHandle,
                            LeftEdgeHandle =
                                left?.Handle ??
                                string.Empty,
                            RightEdgeHandle =
                                right?.Handle ??
                                string.Empty,
                            Edges =
                                hasLeft && hasRight
                                    ? "MÉP L + MÉP R"
                                    : hasLeft
                                        ? "Chỉ có MÉP L"
                                        : hasRight
                                            ? "Chỉ có MÉP R"
                                            : "Chưa ghép mép",
                            LeftWidth = left == null ? 0.0 : Math.Abs(left.Offset),
                            RightWidth = right == null ? 0.0 : Math.Abs(right.Offset),
                            Width = width,
                            EdgeDispersion = Math.Max(left?.Dispersion ?? 0.0, right?.Dispersion ?? 0.0),
                            SampleCount = Math.Min(left?.SampleCount ?? 0, right?.SampleCount ?? 0),
                            Mcn = match?.Id ?? "—",
                            Diff = difference,
                            Score = double.IsInfinity(score) || double.IsNaN(score) || score == double.MaxValue ? 0.0 : score,
                            Reason = reason,
                            Status = status
                        });
                }
                finally
                {
                    proxy.Dispose();
                }
            }

            state.ComparisonResults = results;
            return results;
        }

        private static bool TryEstimateOffset(
            Curve axis,
            Curve edge,
            out double medianOffset,
            out double dispersion,
            out int validSampleCount)
        {
            medianOffset = 0.0;
            dispersion = double.MaxValue;
            validSampleCount = 0;

            var offsets = new List<double>();

            double axisLength = Math.Max(0.0, CurveGeometryHelper.Length(axis));
            if (axisLength <= 1e-6)
                return false;

            // Lấy mẫu đủ dày để vẫn nhận được MÉP chỉ phủ một phần TIM tại nút giao.
            // Trước đây chỉ có 19 mẫu trên toàn TIM; khi MÉP ngắn hơn TIM, các mẫu ngoài
            // vùng chồng lấn bị kéo về endpoint của MÉP và làm MAD tăng mạnh -> unpaired.
            int sampleIntervals = Math.Max(
                24,
                Math.Min(
                    96,
                    (int)Math.Ceiling(axisLength / 3.0)));

            double sampleSpacing = axisLength / sampleIntervals;
            double maxLongitudinalGap = Math.Max(2.0, Math.Min(5.0, sampleSpacing * 1.50));

            for (int i = 1; i < sampleIntervals; i++)
            {
                double fraction = i / (double)sampleIntervals;

                try
                {
                    Point3d axisPoint = CurveGeometryHelper.PointAtFraction(axis, fraction);
                    Point3d edgePoint = edge.GetClosestPointTo(axisPoint, false);

                    // Chỉ dùng mẫu thực sự nằm trong vùng chồng lấn dọc tuyến.
                    // Nếu closest point là endpoint của một MÉP ngắn, hình chiếu ngược lên
                    // TIM sẽ lệch xa station đang lấy mẫu; mẫu đó phải bị bỏ qua.
                    Point3d projectedAxisPoint = axis.GetClosestPointTo(edgePoint, false);
                    double sampleDistance = axis.GetDistAtPoint(axisPoint);
                    double projectedDistance = axis.GetDistAtPoint(projectedAxisPoint);
                    if (Math.Abs(projectedDistance - sampleDistance) > maxLongitudinalGap)
                        continue;

                    double offset = CurveGeometryHelper.SignedOffset(axis, edgePoint);

                    if (double.IsNaN(offset) ||
                        double.IsInfinity(offset) ||
                        Math.Abs(offset) <= 1e-6)
                    {
                        continue;
                    }

                    offsets.Add(offset);
                }
                catch
                {
                    // Một sample lỗi không làm hỏng toàn bộ edge candidate.
                }
            }

            validSampleCount = offsets.Count;

            // 5 mẫu tương ứng khoảng >= 12-15 m overlap với spacing mặc định 3 m.
            // Đủ để nhận mép nhánh/nút nhưng vẫn loại các curve chỉ lướt qua TIM.
            if (offsets.Count < 5)
                return false;

            double median = CurveGeometryHelper.Median(offsets);

            if (double.IsNaN(median) || Math.Abs(median) <= 1e-6)
                return false;

            List<double> sameSideOffsets = offsets
                .Where(x => Math.Sign(x) == Math.Sign(median))
                .ToList();

            double sameSideRatio = sameSideOffsets.Count / (double)offsets.Count;

            // Mép hợp lệ phải nằm nhất quán một phía của TIM. Nới nhẹ từ 0.80 xuống
            // 0.75 để không loại mép cong tại vùng nhập/tách nút chỉ vì vài mẫu biên.
            if (sameSideRatio < 0.75 || sameSideOffsets.Count < 5)
                return false;

            var absoluteDeviations = sameSideOffsets
                .Select(x => Math.Abs(x - median))
                .ToList();

            double mad = CurveGeometryHelper.Median(absoluteDeviations);

            // Dung sai hình học theo bề rộng đường. 4 m mỗi phía cho phép MAD tối đa
            // 0.8 m nhưng matcher MCN phía sau vẫn kiểm tra widthTolerance chặt hơn.
            double allowedDispersion = Math.Max(0.35, Math.Abs(median) * 0.20);

            if (mad > allowedDispersion)
                return false;

            medianOffset = CurveGeometryHelper.Median(sameSideOffsets);
            dispersion = mad;
            validSampleCount = sameSideOffsets.Count;
            return true;
        }

        private sealed class EdgeOffsetCandidate
        {
            public string Handle { get; set; } =
                string.Empty;

            public double Offset { get; set; }

            public double Dispersion { get; set; }

            public int SampleCount { get; set; }
        }
    }
}
