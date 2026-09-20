using System;
using System.Text.Json;
using System.Threading.Tasks;
using Autoroadmarking_Pro.CadHost.Commands;

namespace Autoroadmarking_Pro.CadHost.UI
{
    /// <summary>
    /// Bridge tương thích hai contract:
    /// 1) Mới: { action: "...", payload: { ... } }
    /// 2) UI đã chốt trước đây: { action: "...", field1: ... }
    ///    hoặc chuỗi JSON do postMessage(JSON.stringify(...)).
    ///
    /// Ngoài ra giữ tương thích hai payload đồng bộ thư viện cũ:
    /// { Layers:[...], Mode:"UPSERT" }
    /// { Layers:[...], Assemblies:[...], Mode:"UPSERT" }
    /// </summary>
    public sealed class WebMessageRouter
    {
        public Task<WebResponse> RouteAsync(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return Task.FromResult(WebResponse.Fail(string.Empty, "Web message rỗng."));

            try
            {
                using (JsonDocument document = ParseMessageDocument(json))
                {
                    JsonElement root = document.RootElement;
                    if (root.ValueKind != JsonValueKind.Object)
                        return Task.FromResult(WebResponse.Fail(string.Empty, "Web message phải là JSON object."));

                    string action = GetStringIgnoreCase(root, "action");

                    // Contract thư viện cũ của Tab 1 / Tab 2 không có trường action.
                    if (string.IsNullOrWhiteSpace(action))
                    {
                        bool hasLayers = TryGetPropertyIgnoreCase(root, "Layers", out _);
                        bool hasAssemblies = TryGetPropertyIgnoreCase(root, "Assemblies", out _);

                        if (hasAssemblies)
                            action = "SyncLegacyCrossSections";
                        else if (hasLayers)
                            action = "SyncLegacyMarkingLayers";
                    }

                    if (string.IsNullOrWhiteSpace(action))
                        return Task.FromResult(WebResponse.Fail(string.Empty, "Web message thiếu action."));

                    if (string.Equals(action, "Ping", StringComparison.OrdinalIgnoreCase))
                    {
                        return Task.FromResult(WebResponse.Ok("Ping", "Kết nối WebView2 và C# thành công.", new
                        {
                            source = "Autoroadmarking_Pro.CadHost",
                            functionalBackend = true,
                            legacyUiCompatible = true
                        }));
                    }

                    JsonElement payload;
                    if (TryGetPropertyIgnoreCase(root, "payload", out JsonElement nestedPayload) &&
                        nestedPayload.ValueKind != JsonValueKind.Undefined &&
                        nestedPayload.ValueKind != JsonValueKind.Null)
                    {
                        payload = nestedPayload.Clone();
                    }
                    else
                    {
                        // UI cũ đặt các field cùng cấp với action. Executor đọc trực tiếp root.
                        payload = root.Clone();
                    }

                    if (!CadCommandQueue.Enqueue(action, payload))
                        return Task.FromResult(WebResponse.Fail(action, "Không có bản vẽ AutoCAD đang hoạt động."));

                    return Task.FromResult(WebResponse.Ok(action, string.Empty, new { queued = true }));
                }
            }
            catch (JsonException ex)
            {
                return Task.FromResult(WebResponse.Fail(string.Empty, "JSON không hợp lệ: " + ex.Message));
            }
            catch (Exception ex)
            {
                return Task.FromResult(WebResponse.Fail(string.Empty, ex.Message));
            }
        }

        private static JsonDocument ParseMessageDocument(string json)
        {
            JsonDocument first = JsonDocument.Parse(json);

            if (first.RootElement.ValueKind != JsonValueKind.String)
                return first;

            string inner = first.RootElement.GetString() ?? string.Empty;
            first.Dispose();

            if (string.IsNullOrWhiteSpace(inner))
                return JsonDocument.Parse("{}");

            return JsonDocument.Parse(inner);
        }

        private static string GetStringIgnoreCase(JsonElement obj, string name)
        {
            return TryGetPropertyIgnoreCase(obj, name, out JsonElement value) &&
                   value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? string.Empty
                : string.Empty;
        }

        private static bool TryGetPropertyIgnoreCase(JsonElement obj, string name, out JsonElement value)
        {
            if (obj.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty property in obj.EnumerateObject())
                {
                    if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                    {
                        value = property.Value;
                        return true;
                    }
                }
            }

            value = default;
            return false;
        }
    }
}
