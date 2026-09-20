using Autodesk.AutoCAD.DatabaseServices;
using Autoroadmarking_Pro.Application.Markings;
using Autoroadmarking_Pro.CadHost.Cad.State;

namespace Autoroadmarking_Pro.CadHost.Cad.Layers
{
    public sealed class MarkingLayerSynchronizer
    {
        private readonly CadLayerService _layers = new CadLayerService();
        private readonly CadLinetypeService _linetypes = new CadLinetypeService();
        private readonly LayerNamingService _naming = new LayerNamingService();

        public ObjectId EnsureTemplateLayer(Database db, Transaction tr, ArmMarkingTemplateState template)
        {
            ObjectId linetypeId = _linetypes.EnsureForTemplate(db, tr, template);
            return _layers.EnsureLayer(
                db,
                tr,
                template.Layer,
                linetypeId,
                string.IsNullOrWhiteSpace(template.Rgb) ? template.Color : template.Rgb);
        }

        public ObjectId EnsureGeneratedLayer(Database db, Transaction tr, string roadName, ArmMarkingTemplateState template, bool user = false)
        {
            string name = user
                ? _naming.BuildUserLayer(roadName, template.Layer)
                : _naming.BuildGeneratedLayer(roadName, template.Layer);

            ObjectId linetypeId = _linetypes.EnsureForTemplate(db, tr, template);
            return _layers.EnsureLayer(
                db,
                tr,
                name,
                linetypeId,
                string.IsNullOrWhiteSpace(template.Rgb) ? template.Color : template.Rgb);
        }
    }
}
