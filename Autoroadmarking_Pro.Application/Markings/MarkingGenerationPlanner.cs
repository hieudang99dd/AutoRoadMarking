using System;

namespace Autoroadmarking_Pro.Application.Markings
{
    public sealed class MarkingGenerationPlanner
    {
        public string BuildGenerationKey(string roadKey, string ownerId, string markingCode, int sequence)
        {
            return string.Join("|",
                roadKey ?? string.Empty,
                ownerId ?? string.Empty,
                markingCode ?? string.Empty,
                sequence.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
    }
}
