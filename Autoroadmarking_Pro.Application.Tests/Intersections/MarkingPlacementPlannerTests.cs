using Autoroadmarking_Pro.Application.Intersections;
using Xunit;

namespace Autoroadmarking_Pro.Application.Tests.Intersections
{
    public sealed class MarkingPlacementPlannerTests
    {
        private readonly MarkingPlacementPlanner _planner = new MarkingPlacementPlanner();

        [Fact]
        public void Crosswalk_UsesStep2AnchorAsCenter_AndKeepsRequestedDistance()
        {
            CrosswalkStopStationPlan plan = _planner.PlanStopCrosswalk(
                anchorStation: 100.0,
                outwardSign: 1,
                crossingLength: 4.0,
                requestedDistance: 3.5,
                axisStart: 0.0,
                axisEnd: 200.0);

            Assert.True(plan.IsValid, plan.Error);
            Assert.Equal(100.0, plan.AnchorStation, 6);
            Assert.Equal(98.0, plan.CrosswalkStartStation, 6);
            Assert.Equal(102.0, plan.CrosswalkEndStation, 6);
            Assert.Equal(103.5, plan.StopStation, 6);
            Assert.Equal(3.5, plan.StopStation - plan.AnchorStation, 6);
        }

        [Fact]
        public void Crosswalk_ReverseDirection_KeepsRequestedDistance()
        {
            CrosswalkStopStationPlan plan = _planner.PlanStopCrosswalk(
                anchorStation: 100.0,
                outwardSign: -1,
                crossingLength: 4.0,
                requestedDistance: 3.5,
                axisStart: 0.0,
                axisEnd: 200.0);

            Assert.True(plan.IsValid, plan.Error);
            Assert.Equal(102.0, plan.CrosswalkStartStation, 6);
            Assert.Equal(98.0, plan.CrosswalkEndStation, 6);
            Assert.Equal(96.5, plan.StopStation, 6);
            Assert.Equal(3.5, plan.AnchorStation - plan.StopStation, 6);
        }

        [Fact]
        public void Crosswalk_RejectsWhenAnyRequiredStationIsOutsideAxis()
        {
            CrosswalkStopStationPlan plan = _planner.PlanStopCrosswalk(
                anchorStation: 1.0,
                outwardSign: 1,
                crossingLength: 4.0,
                requestedDistance: 3.5,
                axisStart: 0.0,
                axisEnd: 200.0);

            Assert.False(plan.IsValid);
            Assert.Contains("ngoài miền station", plan.Error);
        }

        [Fact]
        public void ApproachForward_CreatesFullRequestedLength()
        {
            ApproachSegmentPlan plan = _planner.PlanApproachSegment(
                stopStation: 100.0,
                approachDirection: "FORWARD",
                requestedDistance: 20.0,
                axisStart: 0.0,
                axisEnd: 200.0);

            Assert.True(plan.IsValid, plan.Error);
            Assert.Equal(80.0, plan.StartStation, 6);
            Assert.Equal(100.0, plan.EndStation, 6);
            Assert.Equal(20.0, plan.Length, 6);
        }

        [Fact]
        public void ApproachReverse_CreatesFullRequestedLength()
        {
            ApproachSegmentPlan plan = _planner.PlanApproachSegment(
                stopStation: 100.0,
                approachDirection: "REVERSE",
                requestedDistance: 20.0,
                axisStart: 0.0,
                axisEnd: 200.0);

            Assert.True(plan.IsValid, plan.Error);
            Assert.Equal(100.0, plan.StartStation, 6);
            Assert.Equal(120.0, plan.EndStation, 6);
            Assert.Equal(20.0, plan.Length, 6);
        }

        [Fact]
        public void Approach_DoesNotClamp_WhenAxisIsTooShort()
        {
            ApproachSegmentPlan plan = _planner.PlanApproachSegment(
                stopStation: 10.0,
                approachDirection: "FORWARD",
                requestedDistance: 20.0,
                axisStart: 0.0,
                axisEnd: 200.0);

            Assert.False(plan.IsValid);
            Assert.Equal(-10.0, plan.StartStation, 6);
            Assert.Equal(10.0, plan.EndStation, 6);
            Assert.Contains("không clamp", plan.Error);
        }

        [Fact]
        public void Approach_RejectsUnknownDirection()
        {
            ApproachSegmentPlan plan = _planner.PlanApproachSegment(
                stopStation: 100.0,
                approachDirection: "",
                requestedDistance: 20.0,
                axisStart: 0.0,
                axisEnd: 200.0);

            Assert.False(plan.IsValid);
            Assert.Contains("FORWARD hoặc REVERSE", plan.Error);
        }

        [Fact]
        public void DistanceWarning_DoesNotClampValue()
        {
            MarkingDistanceValidation result =
                _planner.ValidateStopToCrosswalkDistance(
                    requested: 8.0,
                    minimum: 0.1,
                    warningMaximum: 5.0);

            Assert.True(result.IsValid);
            Assert.Equal(8.0, result.Value, 6);
            Assert.NotEmpty(result.Warning);
        }
    }
}
