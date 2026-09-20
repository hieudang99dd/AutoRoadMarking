using Autoroadmarking_Pro.CadHost.Cad.Compatibility.Common;

namespace Autoroadmarking_Pro.CadHost.Cad.Compatibility.C3D2025
{
    public sealed class Civil2025Compatibility : ICivilCompatibility
    {
        public int Civil3DVersion => 2025;
        public string RuntimeFamily => "C3D_MODERN_NET8";
    }
}
