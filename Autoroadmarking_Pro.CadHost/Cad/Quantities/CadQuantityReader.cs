using System.Collections.Generic;using Autodesk.AutoCAD.DatabaseServices;
namespace Autoroadmarking_Pro.CadHost.Cad.Quantities{public sealed class CadQuantityReader{private readonly ModelSpaceQuantityScanner _scanner=new ModelSpaceQuantityScanner();public List<CadQuantityRow> Read(Database db,Transaction tr)=>_scanner.Scan(db,tr);}}
