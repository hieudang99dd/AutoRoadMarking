using System.Collections.Generic;
using Autoroadmarking_Pro.Domain.Blocks;
using Autoroadmarking_Pro.Domain.CrossSections;

namespace Autoroadmarking_Pro.Application.Blocks
{
    public sealed class BlockPlacementProposal
    {
        public int LaneIndex { get; set; }
        public double Station { get; set; }
        public double Offset { get; set; }
        public string BlockName { get; set; } = string.Empty;
    }

    public sealed class BlockPlacementPlanner
    {
        public List<BlockPlacementProposal> Build(
            IEnumerable<LaneGeometry> lanes,
            BlockCatalogItem block,
            double anchorStation,
            double distance,
            int directionSign)
        {
            var result = new List<BlockPlacementProposal>();
            if (lanes == null || block == null) return result;

            foreach (var lane in lanes)
            {
                result.Add(new BlockPlacementProposal
                {
                    LaneIndex = lane.LaneIndex,
                    Station = anchorStation - directionSign * distance,
                    Offset = lane.CenterOffset,
                    BlockName = block.BlockName
                });
            }

            return result;
        }
    }
}
