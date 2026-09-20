using System;

namespace Autoroadmarking_Pro.Application.Markings
{
    public sealed class LayerNamingService
    {
        public string BuildGeneratedLayer(string roadName, string templateLayer)
        {
            return Build(roadName, templateLayer, false);
        }

        public string BuildUserLayer(string roadName, string templateLayer)
        {
            return Build(roadName, templateLayer, true);
        }

        private static string Build(string roadName, string templateLayer, bool user)
        {
            if (string.IsNullOrWhiteSpace(roadName))
                throw new ArgumentException("RoadName không hợp lệ.", nameof(roadName));
            if (string.IsNullOrWhiteSpace(templateLayer))
                throw new ArgumentException("TemplateLayer không hợp lệ.", nameof(templateLayer));

            return user
                ? roadName.Trim() + "__USER__" + templateLayer.Trim()
                : roadName.Trim() + "__" + templateLayer.Trim();
        }
    }
}
