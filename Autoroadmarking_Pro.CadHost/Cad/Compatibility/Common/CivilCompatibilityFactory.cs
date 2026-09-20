using System;

namespace Autoroadmarking_Pro.CadHost.Cad.Compatibility.Common
{
    public static class CivilCompatibilityFactory
    {
        public static ICivilCompatibility Create()
        {
#if C3D2024
            return new C3D2024.Civil2024Compatibility();
#elif C3D2025
            return new C3D2025.Civil2025Compatibility();
#elif C3D2026
            return new C3D2026.Civil2026Compatibility();
#else
            throw new NotSupportedException("Civil3DVersion chưa được hỗ trợ.");
#endif
        }
    }
}
