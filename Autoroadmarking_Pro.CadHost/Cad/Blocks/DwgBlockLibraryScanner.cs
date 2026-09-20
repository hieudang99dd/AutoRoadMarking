using System;
using System.Collections.Generic;
using System.IO;
using Autodesk.AutoCAD.DatabaseServices;
using Autoroadmarking_Pro.CadHost.Cad.State;

namespace Autoroadmarking_Pro.CadHost.Cad.Blocks
{
    public sealed class DwgBlockLibraryScanner
    {
        public List<ArmBlockCatalogState> Scan(string folder)
        {
            var result=new List<ArmBlockCatalogState>();if(string.IsNullOrWhiteSpace(folder)||!Directory.Exists(folder))return result;
            foreach(var file in Directory.GetFiles(folder,"*.dwg",SearchOption.TopDirectoryOnly))
            {
                string stem=Path.GetFileNameWithoutExtension(file);string code=stem.IndexOf("7_6",StringComparison.OrdinalIgnoreCase)>=0||stem.IndexOf("76",StringComparison.OrdinalIgnoreCase)>=0?"7.6":"9.3";
                result.Add(new ArmBlockCatalogState{Code=code,Variant=InferVariant(stem),Name=stem,SourceDwgPath=file});
            }
            return result;
        }

        private static string InferVariant(string name)
        {
            string n=name.ToUpperInvariant();
            if(n.Contains("LEFT_RIGHT")||n.Contains("TRAI_PHAI")||n.Contains("TRAIPHAI")||n.Contains("RETRAI_REPHAI"))return "LeftRight";
            if(n.Contains("STRAIGHT_LEFT")||n.Contains("DITHANGTRAI")||n.Contains("THANGTRAI"))return "StraightLeft";
            if(n.Contains("STRAIGHT_RIGHT")||n.Contains("DITHANGPHAI")||n.Contains("THANGPHAI"))return "StraightRight";
            if(n.Contains("DIAMOND")||n.Contains("7_6")||n.Contains("76"))return "Diamond";
            if(n.Contains("LEFT")||n.Contains("RETRAI")||n.EndsWith("TRAI"))return "Left";
            if(n.Contains("RIGHT")||n.Contains("REPHAI")||n.EndsWith("PHAI"))return "Right";
            return "Straight";
        }
    }
}
