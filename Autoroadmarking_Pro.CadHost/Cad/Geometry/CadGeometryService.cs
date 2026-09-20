using System;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.EditorInput;

namespace Autoroadmarking_Pro.CadHost.Cad.Geometry
{
    public sealed class CadGeometryService
    {
        public ObjectId FromHandle(Database db, string handleText)
        {
            if (string.IsNullOrWhiteSpace(handleText)) return ObjectId.Null;
            try
            {
                long value = Convert.ToInt64(handleText, 16);
                return db.GetObjectId(false, new Handle(value), 0);
            }
            catch { return ObjectId.Null; }
        }

        public void ZoomToEntity(Editor ed, Entity entity, double margin = 1.25)
        {
            try
            {
                Extents3d ext = entity.GeometricExtents;
                ZoomToExtents(ed, ext, margin);
            }
            catch { }
        }

        public void ZoomToExtents(Editor ed, Extents3d ext, double margin = 1.25)
        {
            var min = ext.MinPoint; var max = ext.MaxPoint;
            double w = Math.Max(1.0, (max.X-min.X)*margin);
            double h = Math.Max(1.0, (max.Y-min.Y)*margin);
            using (var view = ed.GetCurrentView())
            {
                view.CenterPoint = new Point2d((min.X+max.X)*0.5, (min.Y+max.Y)*0.5);
                double aspect = view.Width / Math.Max(view.Height, 1e-9);
                if (w/h > aspect) h = w/aspect; else w = h*aspect;
                view.Width = w; view.Height = h;
                ed.SetCurrentView(view);
            }
            ed.Regen();
        }
    }
}
