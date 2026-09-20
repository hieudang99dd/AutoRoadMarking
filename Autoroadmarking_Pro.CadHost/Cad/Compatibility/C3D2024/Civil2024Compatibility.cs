using Autoroadmarking_Pro.CadHost.Cad.Compatibility.Common;

namespace Autoroadmarking_Pro.CadHost.Cad.Compatibility.C3D2024
{
    public sealed class Civil2024Compatibility : ICivilCompatibility
    {
        public int Civil3DVersion => 2024;
        public string RuntimeFamily => "C3D_LEGACY_NET48";
    }
}
