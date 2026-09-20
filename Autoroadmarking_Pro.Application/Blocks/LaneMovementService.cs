using Autoroadmarking_Pro.Domain.Blocks;

namespace Autoroadmarking_Pro.Application.Blocks
{
    public sealed class LaneMovementService
    {
        public string ResolveDefaultBlockName(LaneMovement movement)
        {
            switch (movement)
            {
                case LaneMovement.Left:
                    return "ARM_9_3_LEFT";
                case LaneMovement.Straight:
                    return "ARM_9_3_STRAIGHT";
                case LaneMovement.Right:
                    return "ARM_9_3_RIGHT";
                case LaneMovement.Left | LaneMovement.Straight:
                    return "ARM_9_3_STRAIGHT_LEFT";
                case LaneMovement.Straight | LaneMovement.Right:
                    return "ARM_9_3_STRAIGHT_RIGHT";
                default:
                    return string.Empty;
            }
        }
    }
}
