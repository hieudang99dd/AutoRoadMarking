using Autodesk.AutoCAD.Runtime;

[assembly: ExtensionApplication(
    typeof(Autoroadmarking_Pro.CadHost.Commands.RoadMarkingCommands))]

[assembly: CommandClass(
    typeof(Autoroadmarking_Pro.CadHost.Commands.RoadMarkingCommands))]
