using System;

namespace Autoroadmarking_Pro.Domain.Blocks
{
    [Flags]
    public enum LaneMovement
    {
        None = 0,
        Left = 1,
        Straight = 2,
        Right = 4
    }
}
