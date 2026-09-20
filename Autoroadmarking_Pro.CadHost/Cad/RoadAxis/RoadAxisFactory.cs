using System;

using Autodesk.AutoCAD.DatabaseServices;
using Autoroadmarking_Pro.Application.RoadAxis;

using CivilAlignment = Autodesk.Civil.DatabaseServices.Alignment;

namespace Autoroadmarking_Pro.CadHost.Cad.RoadAxis
{
    public sealed class RoadAxisFactory
    {
        public IRoadAxisGeometry FromAlignment(
            CivilAlignment alignment)
        {
            return new AlignmentRoadAxisAdapter(
                alignment);
        }

        public IRoadAxisGeometry FromCadCurve(
            Curve curve,
            int orientationSign = 1)
        {
            return new CadCurveRoadAxisAdapter(
                curve,
                orientationSign);
        }

        public IRoadAxisGeometry FromEntity(
            Entity entity,
            int orientationSign = 1)
        {
            if (entity == null)
                throw new ArgumentNullException(
                    nameof(entity));

            if (entity is CivilAlignment alignment)
                return FromAlignment(alignment);

            if (entity is Curve curve)
                return FromCadCurve(
                    curve,
                    orientationSign);

            throw new NotSupportedException(
                "Đối tượng không phải Alignment hoặc Curve.");
        }
    }
}
