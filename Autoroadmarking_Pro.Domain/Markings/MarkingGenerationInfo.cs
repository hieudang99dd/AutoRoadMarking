namespace Autoroadmarking_Pro.Domain.Markings
{
    public sealed class MarkingGenerationInfo
    {
        public string RecordId { get; set; } = string.Empty;
        public string GenerationKey { get; set; } = string.Empty;
        public string TemplateId { get; set; } = string.Empty;
        public string RoadKey { get; set; } = string.Empty;
        public string OwnerType { get; set; } = string.Empty;
        public string OwnerId { get; set; } = string.Empty;
    }
}
