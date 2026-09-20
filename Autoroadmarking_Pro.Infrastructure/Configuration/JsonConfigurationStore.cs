using System;
using System.IO;
using Autoroadmarking_Pro.Infrastructure.Serialization;

namespace Autoroadmarking_Pro.Infrastructure.Configuration
{
    public sealed class JsonConfigurationStore
    {
        private readonly JsonSerializerService _serializer = new JsonSerializerService();

        public T? Load<T>(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return default;

            return _serializer.Deserialize<T>(File.ReadAllText(path));
        }

        public void Save<T>(string path, T value)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("Đường dẫn không hợp lệ.", nameof(path));

            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            File.WriteAllText(path, _serializer.Serialize(value));
        }
    }
}
