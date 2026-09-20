using Autodesk.AutoCAD.DatabaseServices;
using System.Text;

namespace Autoroadmarking_Pro.CadHost.Cad.State
{
    public sealed class NamedObjectDictionaryService
    {
        public string ReadText(Database db, Transaction tr, string key)
        {
            var nod = (DBDictionary)tr.GetObject(db.NamedObjectsDictionaryId, OpenMode.ForRead);
            if (!nod.Contains(key)) return string.Empty;
            var xr = (Xrecord)tr.GetObject(nod.GetAt(key), OpenMode.ForRead);
            var data = xr.Data;
            if (data == null) return string.Empty;
            var sb = new StringBuilder();
            foreach (TypedValue tv in data)
                if (tv.Value is string s) sb.Append(s);
            return sb.ToString();
        }

        public void WriteText(Database db, Transaction tr, string key, string text)
        {
            var nod = (DBDictionary)tr.GetObject(db.NamedObjectsDictionaryId, OpenMode.ForRead);
            Xrecord xr;
            if (nod.Contains(key))
            {
                xr = (Xrecord)tr.GetObject(nod.GetAt(key), OpenMode.ForWrite);
            }
            else
            {
                nod.UpgradeOpen();
                xr = new Xrecord();
                nod.SetAt(key, xr);
                tr.AddNewlyCreatedDBObject(xr, true);
            }

            xr.Data = BuildResultBuffer(text ?? string.Empty);
        }

        private static ResultBuffer BuildResultBuffer(string text)
        {
            const int chunkSize = 1800;
            var values = new System.Collections.Generic.List<TypedValue>();
            for (int i = 0; i < text.Length; i += chunkSize)
            {
                int length = System.Math.Min(chunkSize, text.Length - i);
                values.Add(new TypedValue((int)DxfCode.Text, text.Substring(i, length)));
            }
            if (values.Count == 0) values.Add(new TypedValue((int)DxfCode.Text, string.Empty));
            return new ResultBuffer(values.ToArray());
        }
    }
}
