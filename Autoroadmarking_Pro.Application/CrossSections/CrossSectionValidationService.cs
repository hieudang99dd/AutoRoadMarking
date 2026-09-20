using System.Collections.Generic;
using Autoroadmarking_Pro.Domain.CrossSections;

namespace Autoroadmarking_Pro.Application.CrossSections
{
    public sealed class CrossSectionValidationService
    {
        public IReadOnlyList<string> Validate(CrossSectionDefinition definition)
        {
            var errors = new List<string>();

            if (definition == null)
            {
                errors.Add("Mặt cắt không tồn tại.");
                return errors;
            }

            if (string.IsNullOrWhiteSpace(definition.Name))
                errors.Add("Mặt cắt chưa có tên.");

            foreach (var lane in definition.Lanes)
            {
                if (lane.Width <= 0.0)
                    errors.Add("Có làn xe có bề rộng không hợp lệ.");
            }

            return errors;
        }
    }
}
