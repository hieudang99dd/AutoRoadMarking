using System.Text.Json;

namespace Autoroadmarking_Pro.Infrastructure.Serialization
{
    public static class JsonOptionsFactory
    {
        public static JsonSerializerOptions Create()
        {
            return new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                WriteIndented = true
            };
        }
    }
}
