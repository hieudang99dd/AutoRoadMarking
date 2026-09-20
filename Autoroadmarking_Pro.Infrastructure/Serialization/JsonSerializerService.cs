using System.Text.Json;

namespace Autoroadmarking_Pro.Infrastructure.Serialization
{
    public sealed class JsonSerializerService
    {
        private readonly JsonSerializerOptions _options = JsonOptionsFactory.Create();

        public string Serialize<T>(T value)
        {
            return JsonSerializer.Serialize(value, _options);
        }

        public T? Deserialize<T>(string json)
        {
            return JsonSerializer.Deserialize<T>(json, _options);
        }
    }
}
