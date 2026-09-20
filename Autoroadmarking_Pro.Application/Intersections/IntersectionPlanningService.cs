namespace Autoroadmarking_Pro.Application.Intersections
{
    public sealed class IntersectionPlan
    {
        public string Id { get; set; } = string.Empty;
        public string RoadKey { get; set; } = string.Empty;
        public bool CreatePolygon { get; set; } = true;
        public bool TrimLongitudinalMarkings { get; set; } = true;
        public bool CreateEdgeMarkings { get; set; } = true;
        public bool CreateStopAndCrosswalk { get; set; } = true;
    }

    public sealed class IntersectionPlanningService
    {
        public IntersectionPlan CreateDefault(string roadKey)
        {
            return new IntersectionPlan { RoadKey = roadKey ?? string.Empty };
        }
    }
}
