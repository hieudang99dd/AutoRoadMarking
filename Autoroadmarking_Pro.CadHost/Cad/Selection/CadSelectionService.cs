using System.Collections.Generic;

using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;

using CivilAlignment = Autodesk.Civil.DatabaseServices.Alignment;

namespace Autoroadmarking_Pro.CadHost.Cad.Selection
{
    /// <summary>
    /// Selection adapter của CadHost. Chỉ trả ObjectId; không đưa Autodesk types ra Application.
    /// </summary>
    public sealed class CadSelectionService
    {
        /// <summary>
        /// TAB 0 chỉ cho phép định danh tim CAD dạng Polyline.
        /// Alignment Civil 3D dùng identity của chính Alignment, không gắn Road Identity kiểu CAD Curve.
        /// </summary>
        public ObjectId SelectCadPolyline(
            Editor editor,
            Transaction transaction,
            string message)
        {
            var options = new PromptEntityOptions("\n" + message);
            options.SetRejectMessage("\nChỉ chọn Polyline TIM CAD.");
            options.AddAllowedClass(typeof(Polyline), false);
            options.AddAllowedClass(typeof(Polyline2d), false);
            options.AddAllowedClass(typeof(Polyline3d), false);

            PromptEntityResult result = editor.GetEntity(options);
            return result.Status == PromptStatus.OK
                ? result.ObjectId
                : ObjectId.Null;
        }

        public ObjectId SelectRoadAxis(
            Editor editor,
            Transaction transaction,
            string message)
        {
            var options =
                new PromptEntityOptions(
                    "\n" + message);

            options.SetRejectMessage(
                "\nChỉ chọn Curve AutoCAD hoặc Civil 3D Alignment.");

            options.AddAllowedClass(
                typeof(Curve),
                false);

            options.AddAllowedClass(
                typeof(CivilAlignment),
                false);

            PromptEntityResult result =
                editor.GetEntity(options);

            return result.Status == PromptStatus.OK
                ? result.ObjectId
                : ObjectId.Null;
        }

        public List<ObjectId> SelectCurves(
            Editor editor,
            Transaction transaction,
            string message,
            bool allowAlignment)
        {
            editor.WriteMessage(
                "\n" + message);

            PromptSelectionResult result =
                editor.GetSelection();

            var ids =
                new List<ObjectId>();

            if (result.Status != PromptStatus.OK)
                return ids;

            foreach (ObjectId id in result.Value.GetObjectIds())
            {
                DBObject obj =
                    transaction.GetObject(
                        id,
                        OpenMode.ForRead,
                        false);

                if (obj is CivilAlignment)
                {
                    if (allowAlignment)
                        ids.Add(id);

                    continue;
                }

                if (obj is Curve)
                    ids.Add(id);
            }

            return ids;
        }
    }
}
