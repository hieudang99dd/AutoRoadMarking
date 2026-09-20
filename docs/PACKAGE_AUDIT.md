# PACKAGE AUDIT · 2026-08-21

- Source files (excluding manifest): **136**
- C# files: **104**
- UI JavaScript modules: **8**
- UI CSS modules: **6**
- HTML ids: **360**, unique: **360**
- Literal WebView `post(...)` actions: **51**
- Bundled marking templates: **15**
- DWG state schema: **11**
- Static QA: **PASS**

## Finalized critical contracts

- `Tab1_UpdateLayerManagementSet`
- `Tab1_ApplySharedLayerSet`
- `SyncSelectedCrossSections`
- `DrawMarkingsCAD` + 6 marking subactions
- `AnalyzeSymbolBlockPlacement` / `GenerateSymbolBlocksCAD`
- Supplementary/GGT/manual/block management actions
- `ReadMarkingQuantitiesCAD` / `ExportMarkingQuantitiesExcel`

## Runtime qualification still required

This package has not been compiled or NETLOAD-tested in the packaging environment because `dotnet/MSBuild` and Autodesk managed assemblies are unavailable. Use `DEPLOYMENT_CHECKLIST.md` on the target Civil 3D workstation.
