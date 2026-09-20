namespace Autoroadmarking_Pro.Infrastructure.Configuration
{
    public sealed class GlobalSettings
    {
        public int SchemaVersion { get; set; } = 1;
        public string SymbolLibraryFolder { get; set; } = string.Empty;
        public string LastProjectName { get; set; } = string.Empty;
    }
}
