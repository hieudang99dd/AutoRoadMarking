namespace Autoroadmarking_Pro.Domain.Markings
{
    public sealed class MarkingTemplate
    {
        public string Id { get; set; } = string.Empty;
        public string Code { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string LayerName { get; set; } = string.Empty;
        public string LinetypeName { get; set; } = string.Empty;
        public double LinetypeScale { get; set; } = 1.0;
        public double Width { get; set; }
        public string StandardSource { get; set; } = string.Empty;
        public string StandardReference { get; set; } = string.Empty;
        public MarkingVerificationStatus VerificationStatus { get; set; } =
            MarkingVerificationStatus.Draft;

        public bool IsVerified => VerificationStatus == MarkingVerificationStatus.Verified;
    }
}
