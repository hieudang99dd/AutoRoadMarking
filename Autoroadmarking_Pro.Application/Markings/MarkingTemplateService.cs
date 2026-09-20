using System;
using System.Collections.Generic;
using System.Linq;
using Autoroadmarking_Pro.Domain.Markings;

namespace Autoroadmarking_Pro.Application.Markings
{
    public sealed class MarkingTemplateService
    {
        public MarkingTemplate? FindById(IEnumerable<MarkingTemplate> items, string id)
        {
            return items == null ? null : items.FirstOrDefault(x => x.Id == id);
        }

        public void EnsureCanGenerate(MarkingTemplate template)
        {
            if (template == null) throw new ArgumentNullException(nameof(template));
            if (!template.IsVerified)
                throw new InvalidOperationException("MarkingTemplate chưa được xác nhận.");
            if (string.IsNullOrWhiteSpace(template.LayerName))
                throw new InvalidOperationException("MarkingTemplate chưa có LayerName.");
        }
    }
}
