using System.Text.Json;

namespace Autoroadmarking_Pro.CadHost.UI
{
    public sealed class WebMessage
    {
        public string Action { get; set; } =
            string.Empty;

        public JsonElement Payload { get; set; }
    }
}
