using System;
using Autodesk.AutoCAD.DatabaseServices;

namespace Autoroadmarking_Pro.CadHost.Cad.Blocks
{
    public sealed class BlockDefinitionService
    {
        public ObjectId EnsureDefinition(Database db, Transaction tr, string blockName, string sourceDwgPath)
        {
            var bt=(BlockTable)tr.GetObject(db.BlockTableId,OpenMode.ForRead);if(bt.Has(blockName))return bt[blockName];
            if(string.IsNullOrWhiteSpace(sourceDwgPath)||!System.IO.File.Exists(sourceDwgPath))throw new InvalidOperationException("Không tìm thấy DWG block: "+sourceDwgPath);
            using(var source=new Database(false,true))
            {
                source.ReadDwgFile(sourceDwgPath,FileOpenMode.OpenForReadAndAllShare,true,string.Empty);
                return db.Insert(blockName,source,false);
            }
        }
    }
}
