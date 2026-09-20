using System.Collections.Generic;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autoroadmarking_Pro.CadHost.Cad.Geometry;
using Autoroadmarking_Pro.CadHost.Cad.State;

namespace Autoroadmarking_Pro.CadHost.Cad.Selection
{
    public sealed class RoadEdgeSelectionService
    {
        private readonly CadSelectionService _selection = new CadSelectionService();
        public List<string> Select(Document doc, Transaction tr, ArmProjectState state)
        {
            var ids = _selection.SelectCurves(doc.Editor, tr, "Quét chọn toàn bộ đường MÉP rồi nhấn Enter.", false);
            var handles = new List<string>();
            foreach (var id in ids) handles.Add(CurveGeometryHelper.Handle(id));
            state.SelectedEdgeHandles = handles;
            return handles;
        }
    }
}
