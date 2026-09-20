using System.Text;
using Autodesk.AutoCAD.DatabaseServices;

namespace Autoroadmarking_Pro.CadHost.Cad.Metadata
{
    public sealed class XRecordService
    {
        public string Read(DBDictionary dictionary, Transaction tr, string key)
        {
            if (!dictionary.Contains(key)) return string.Empty;
            var xr = (Xrecord)tr.GetObject(dictionary.GetAt(key), OpenMode.ForRead);
            if (xr.Data == null) return string.Empty;
            var sb = new StringBuilder();
            foreach (TypedValue tv in xr.Data)
                if (tv.Value is string s) sb.Append(s);
            return sb.ToString();
        }

        public void Write(DBDictionary dictionary, Transaction tr, string key, string text)
        {
            Xrecord xr;
            if (dictionary.Contains(key)) xr = (Xrecord)tr.GetObject(dictionary.GetAt(key), OpenMode.ForWrite);
            else
            {
                if (!dictionary.IsWriteEnabled) dictionary.UpgradeOpen();
                xr = new Xrecord();
                dictionary.SetAt(key, xr);
                tr.AddNewlyCreatedDBObject(xr, true);
            }
            xr.Data = Build(text ?? string.Empty);
        }

        public void Remove(DBDictionary dictionary, Transaction tr, string key)
        {
            if (!dictionary.Contains(key)) return;
            if (!dictionary.IsWriteEnabled) dictionary.UpgradeOpen();
            var id = dictionary.GetAt(key);
            var obj = tr.GetObject(id, OpenMode.ForWrite);
            obj.Erase(true);
        }

        private static ResultBuffer Build(string text)
        {
            const int chunk = 1800;
            var values = new System.Collections.Generic.List<TypedValue>();
            for (int i=0;i<text.Length;i+=chunk)
                values.Add(new TypedValue((int)DxfCode.Text, text.Substring(i, System.Math.Min(chunk, text.Length-i))));
            if (values.Count==0) values.Add(new TypedValue((int)DxfCode.Text, string.Empty));
            return new ResultBuffer(values.ToArray());
        }
    }
}
