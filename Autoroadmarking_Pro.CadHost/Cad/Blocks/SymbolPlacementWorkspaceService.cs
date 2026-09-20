using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autoroadmarking_Pro.CadHost.Cad.Metadata;
using Autoroadmarking_Pro.CadHost.Cad.State;

namespace Autoroadmarking_Pro.CadHost.Cad.Blocks
{
    /// <summary>
    /// Dựng workspace Tab 4 từ ba nguồn có thẩm quyền:
    /// - Tab 2: MCN/lane geometry.
    /// - Tab 3: comparison TIM-MÉP-MCN.
    /// - Entity ARM metadata: mốc 7.1/7.3 theo từng polygon/nút giao + approach.
    ///
    /// Một RoadAxis có thể đi qua nhiều nút. Một nút có hai approach theo cùng axis.
    /// Vì vậy anchor được khóa bằng (AxisKey, NodeId, InboundDirection), tuyệt đối
    /// không lấy median của tất cả 7.1/7.3 trên cùng node rồi dùng cho hai hướng.
    /// </summary>
    public sealed class SymbolPlacementWorkspaceService
    {
        private readonly EntityMetadataStore _metadata = new EntityMetadataStore();

        public SymbolPlacementWorkspace Build(Database db, Transaction tr, ArmProjectState state)
        {
            var workspace = new SymbolPlacementWorkspace
            {
                LibraryPath = state.BlockLibraryPath,
                Blocks = state.BlockCatalog.Select(ToBlock).ToList(),
                Proposals = state.BlockProposals
            };

            Dictionary<string, List<IntersectionReferenceAnchor>> anchorsByRoad = ReadAnchors(db, tr)
                .GroupBy(x => x.RoadKey, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    x => x.Key,
                    x => x.OrderBy(y => y.ReferenceStation).ThenBy(y => y.InboundDirection).ToList(),
                    StringComparer.OrdinalIgnoreCase);

            foreach (ArmComparisonState comparison in state.ComparisonResults
                         .Where(x => x.Status.Equals("matched", StringComparison.OrdinalIgnoreCase)))
            {
                ArmCrossSectionState? mcn = state.GetEffectiveCrossSections().FirstOrDefault(x =>
                    x.Id.Equals(comparison.Mcn, StringComparison.OrdinalIgnoreCase) ||
                    x.Name.Equals(comparison.Mcn, StringComparison.OrdinalIgnoreCase));
                if (mcn == null)
                    continue;

                List<SymbolWorkspaceLane> lanes = BuildLanes(mcn);

                if (!anchorsByRoad.TryGetValue(comparison.RoadKey, out List<IntersectionReferenceAnchor>? roadAnchors) ||
                    roadAnchors.Count == 0)
                {
                    workspace.Nodes.Add(BuildUnanchoredNode(comparison, mcn, lanes));
                    continue;
                }

                foreach (IGrouping<string, IntersectionReferenceAnchor> nodeGroup in roadAnchors
                             .GroupBy(x => x.OwnerId, StringComparer.OrdinalIgnoreCase))
                {
                    List<IntersectionReferenceAnchor> nodeAnchors = nodeGroup.ToList();
                    var node = new SymbolWorkspaceNode
                    {
                        Id = nodeGroup.Key,
                        Name = BuildNodeName(comparison.Road, nodeAnchors),
                        RoadKey = comparison.RoadKey,
                        Station71 = MedianNullable(nodeAnchors.Select(x => x.Station71)),
                        Station73 = MedianNullable(nodeAnchors.Select(x => x.Station73))
                    };

                    foreach (string direction in new[] { "FORWARD", "REVERSE" })
                    {
                        IntersectionReferenceAnchor? anchor = nodeAnchors.FirstOrDefault(x =>
                            x.InboundDirection.Equals(direction, StringComparison.OrdinalIgnoreCase));

                        node.Approaches.Add(BuildApproach(
                            comparison,
                            mcn,
                            node.Id,
                            direction,
                            lanes,
                            anchor ?? IntersectionReferenceAnchor.Empty(comparison.RoadKey, node.Id, direction)));
                    }

                    workspace.Nodes.Add(node);
                }
            }

            workspace.Nodes = workspace.Nodes
                .GroupBy(x => x.RoadKey + "|" + x.Id, StringComparer.OrdinalIgnoreCase)
                .Select(g =>
                {
                    SymbolWorkspaceNode first = g.First();
                    first.Approaches = g.SelectMany(x => x.Approaches)
                        .GroupBy(a => a.Id, StringComparer.OrdinalIgnoreCase)
                        .Select(x => x.First())
                        .OrderBy(x => x.InboundDirection)
                        .ToList();
                    return first;
                })
                .OrderBy(x => x.RoadKey)
                .ThenBy(x => x.Station73 ?? x.Station71 ?? double.MaxValue)
                .ToList();

            return workspace;
        }

        private static SymbolWorkspaceNode BuildUnanchoredNode(
            ArmComparisonState comparison,
            ArmCrossSectionState mcn,
            List<SymbolWorkspaceLane> lanes)
        {
            string nodeId = "UNANCHORED_" + comparison.Id;
            var node = new SymbolWorkspaceNode
            {
                Id = nodeId,
                Name = comparison.Road + " · chưa có mốc 7.1/7.3",
                RoadKey = comparison.RoadKey
            };

            node.Approaches.Add(BuildApproach(
                comparison, mcn, nodeId, "FORWARD", lanes,
                IntersectionReferenceAnchor.Empty(comparison.RoadKey, nodeId, "FORWARD")));
            node.Approaches.Add(BuildApproach(
                comparison, mcn, nodeId, "REVERSE", lanes,
                IntersectionReferenceAnchor.Empty(comparison.RoadKey, nodeId, "REVERSE")));
            return node;
        }

        private static string BuildNodeName(
            string roadName,
            IReadOnlyCollection<IntersectionReferenceAnchor> anchors)
        {
            List<double> references = anchors
                .Select(x => x.ReferenceStation)
                .Where(IsFinite)
                .OrderBy(x => x)
                .ToList();

            string stationText = references.Count == 0
                ? "chưa có mốc"
                : FormatStation(Median(references));
            return roadName + " · " + stationText;
        }

        private static SymbolWorkspaceApproach BuildApproach(
            ArmComparisonState comparison,
            ArmCrossSectionState mcn,
            string nodeId,
            string direction,
            List<SymbolWorkspaceLane> lanes,
            IntersectionReferenceAnchor anchor)
        {
            return new SymbolWorkspaceApproach
            {
                Id = comparison.Id + "|" + mcn.Id + "|" + direction + "|" + nodeId,
                NodeId = nodeId,
                Name = comparison.Road + " · " + direction,
                RoadName = comparison.Road,
                RoadKey = comparison.RoadKey,
                AssemblyId = mcn.Id,
                AssemblyName = mcn.Name,
                InboundDirection = direction,
                StopLineFound = anchor.HasStopLine,
                CrosswalkFound = anchor.HasCrosswalk,
                Station71 = FiniteOrNull(anchor.Station71),
                Station73 = FiniteOrNull(anchor.Station73),
                Lanes = lanes.Select(CloneLane).ToList()
            };
        }

        private static List<SymbolWorkspaceLane> BuildLanes(ArmCrossSectionState mcn)
        {
            var result = new List<SymbolWorkspaceLane>();
            foreach (string side in new[] { "Left", "Right" })
            {
                List<ArmCrossSectionPartState> sideLanes = mcn.Components
                    .Where(x => x.Role.Equals("Lane", StringComparison.OrdinalIgnoreCase) &&
                                x.Side.Equals(side, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(x => Math.Abs(x.Offset))
                    .ThenBy(x => x.Order)
                    .ToList();

                double accumulated = 0.0;
                for (int i = 0; i < sideLanes.Count; i++)
                {
                    ArmCrossSectionPartState lane = sideLanes[i];
                    string prefix = side == "Left" ? "L" : "R";
                    double fallbackCenter = accumulated + Math.Max(0.0, lane.Width) * 0.5;
                    double center = Math.Abs(lane.Offset) > 1e-9
                        ? lane.Offset
                        : (side == "Left" ? -fallbackCenter : fallbackCenter);

                    result.Add(new SymbolWorkspaceLane
                    {
                        Id = prefix + (i + 1),
                        SourceComponentId = lane.Id,
                        LaneIndex = i + 1,
                        Width = lane.Width,
                        CenterOffset = center,
                        Side = side,
                        AllowLeft = i == 0,
                        AllowStraight = true,
                        AllowRight = i == sideLanes.Count - 1
                    });
                    accumulated += Math.Max(0.0, lane.Width);
                }
            }
            return result;
        }

        private static SymbolWorkspaceLane CloneLane(SymbolWorkspaceLane lane) => new SymbolWorkspaceLane
        {
            Id = lane.Id,
            SourceComponentId = lane.SourceComponentId,
            LaneIndex = lane.LaneIndex,
            Width = lane.Width,
            CenterOffset = lane.CenterOffset,
            Side = lane.Side,
            AllowLeft = lane.AllowLeft,
            AllowStraight = lane.AllowStraight,
            AllowRight = lane.AllowRight
        };

        private List<IntersectionReferenceAnchor> ReadAnchors(Database db, Transaction tr)
        {
            BlockTableRecord modelSpace = (BlockTableRecord)tr.GetObject(
                SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForRead);

            var rawByNode = new Dictionary<string, List<RawAnchorMark>>(StringComparer.OrdinalIgnoreCase);

            foreach (ObjectId id in modelSpace)
            {
                if (!(tr.GetObject(id, OpenMode.ForRead, false) is Entity entity))
                    continue;

                ArmEntityMetadata? md = _metadata.Read(entity, tr);
                if (md == null || string.IsNullOrWhiteSpace(md.OwnerId))
                    continue;
                if (!md.MarkingCode.Equals("7.1", StringComparison.OrdinalIgnoreCase) &&
                    !md.MarkingCode.Equals("7.3", StringComparison.OrdinalIgnoreCase))
                    continue;

                string roadKey = !string.IsNullOrWhiteSpace(md.AxisKey) ? md.AxisKey : md.RoadKey;
                if (string.IsNullOrWhiteSpace(roadKey) || !IsFinite(md.Station))
                    continue;

                string direction = ReadExtra(md, "ApproachDirection").ToUpperInvariant();
                if (direction != "FORWARD" && direction != "REVERSE")
                    direction = string.Empty;

                string nodeKey = roadKey + "|" + md.OwnerId;
                if (!rawByNode.TryGetValue(nodeKey, out List<RawAnchorMark>? marks))
                {
                    marks = new List<RawAnchorMark>();
                    rawByNode.Add(nodeKey, marks);
                }

                marks.Add(new RawAnchorMark
                {
                    RoadKey = roadKey,
                    OwnerId = md.OwnerId,
                    Code = md.MarkingCode,
                    Station = md.Station,
                    InboundDirection = direction
                });
            }

            var result = new List<IntersectionReferenceAnchor>();
            foreach (List<RawAnchorMark> marks in rawByNode.Values)
            {
                if (marks.Count == 0)
                    continue;

                string roadKey = marks[0].RoadKey;
                string ownerId = marks[0].OwnerId;

                // Schema mới: direction đã được ghi trực tiếp.
                foreach (string direction in new[] { "FORWARD", "REVERSE" })
                {
                    List<RawAnchorMark> directed = marks
                        .Where(x => x.InboundDirection.Equals(direction, StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    if (directed.Count == 0)
                        continue;

                    result.Add(BuildAnchor(roadKey, ownerId, direction, directed));
                }

                // Migration schema cũ: ghép 7.3 với 7.1 gần nhất. Dấu của (Sta71-Sta73)
                // xác định chiều xe đi vào nút: 7.1 phía +station => REVERSE; phía - => FORWARD.
                List<RawAnchorMark> legacy = marks.Where(x => string.IsNullOrWhiteSpace(x.InboundDirection)).ToList();
                if (legacy.Count > 0)
                    result.AddRange(BuildLegacyAnchors(roadKey, ownerId, legacy));
            }

            return result
                .GroupBy(x => x.RoadKey + "|" + x.OwnerId + "|" + x.InboundDirection, StringComparer.OrdinalIgnoreCase)
                .Select(g => MergeAnchors(g.ToList()))
                .ToList();
        }

        private static IntersectionReferenceAnchor BuildAnchor(
            string roadKey,
            string ownerId,
            string direction,
            IEnumerable<RawAnchorMark> marks)
        {
            List<double> stops = marks.Where(x => x.Code.Equals("7.1", StringComparison.OrdinalIgnoreCase)).Select(x => x.Station).ToList();
            List<double> crosses = marks.Where(x => x.Code.Equals("7.3", StringComparison.OrdinalIgnoreCase)).Select(x => x.Station).ToList();
            return CreateAnchor(roadKey, ownerId, direction, stops, crosses);
        }

        private static IEnumerable<IntersectionReferenceAnchor> BuildLegacyAnchors(
            string roadKey,
            string ownerId,
            List<RawAnchorMark> marks)
        {
            var stops = marks.Where(x => x.Code.Equals("7.1", StringComparison.OrdinalIgnoreCase))
                .Select(x => x.Station).OrderBy(x => x).ToList();
            var crosses = marks.Where(x => x.Code.Equals("7.3", StringComparison.OrdinalIgnoreCase))
                .Select(x => x.Station).OrderBy(x => x).ToList();
            var unusedStops = new List<double>(stops);
            var grouped = new Dictionary<string, Tuple<List<double>, List<double>>>(StringComparer.OrdinalIgnoreCase);

            foreach (double cross in crosses)
            {
                double? stop = null;
                if (unusedStops.Count > 0)
                {
                    double nearest = unusedStops.OrderBy(x => Math.Abs(x - cross)).First();
                    unusedStops.Remove(nearest);
                    stop = nearest;
                }

                string direction = stop.HasValue && stop.Value > cross ? "REVERSE" : "FORWARD";
                if (!grouped.TryGetValue(direction, out Tuple<List<double>, List<double>>? pair))
                {
                    pair = Tuple.Create(new List<double>(), new List<double>());
                    grouped.Add(direction, pair);
                }
                pair.Item2.Add(cross);
                if (stop.HasValue)
                    pair.Item1.Add(stop.Value);
            }

            // Trường hợp chỉ có 7.1 legacy, không thể suy chiều chắc chắn. Giữ dưới FORWARD
            // để workspace báo thiếu 7.3 thay vì bịa thêm hai approach có mốc giống nhau.
            foreach (double stop in unusedStops)
            {
                if (!grouped.TryGetValue("FORWARD", out Tuple<List<double>, List<double>>? pair))
                {
                    pair = Tuple.Create(new List<double>(), new List<double>());
                    grouped.Add("FORWARD", pair);
                }
                pair.Item1.Add(stop);
            }

            foreach (KeyValuePair<string, Tuple<List<double>, List<double>>> item in grouped)
                yield return CreateAnchor(roadKey, ownerId, item.Key, item.Value.Item1, item.Value.Item2);
        }

        private static IntersectionReferenceAnchor MergeAnchors(List<IntersectionReferenceAnchor> anchors)
        {
            IntersectionReferenceAnchor first = anchors[0];
            return CreateAnchor(
                first.RoadKey,
                first.OwnerId,
                first.InboundDirection,
                anchors.SelectMany(x => x.StopStations).ToList(),
                anchors.SelectMany(x => x.CrosswalkStations).ToList());
        }

        private static IntersectionReferenceAnchor CreateAnchor(
            string roadKey,
            string ownerId,
            string direction,
            List<double> stops,
            List<double> crosses)
        {
            var anchor = new IntersectionReferenceAnchor
            {
                RoadKey = roadKey,
                OwnerId = ownerId,
                InboundDirection = direction,
                HasStopLine = stops.Count > 0,
                HasCrosswalk = crosses.Count > 0
            };
            anchor.StopStations.AddRange(stops.Where(IsFinite));
            anchor.CrosswalkStations.AddRange(crosses.Where(IsFinite));
            anchor.Station71 = Median(anchor.StopStations);
            anchor.Station73 = Median(anchor.CrosswalkStations);
            anchor.ReferenceStation = IsFinite(anchor.Station73) ? anchor.Station73 : anchor.Station71;
            return anchor;
        }

        private static string ReadExtra(ArmEntityMetadata md, string key)
        {
            return md.Extra != null && md.Extra.TryGetValue(key, out string? value)
                ? value ?? string.Empty
                : string.Empty;
        }

        private static double? FiniteOrNull(double value) => IsFinite(value) ? (double?)value : null;

        private static double? MedianNullable(IEnumerable<double> values)
        {
            List<double> finite = values.Where(IsFinite).OrderBy(x => x).ToList();
            return finite.Count == 0 ? (double?)null : Median(finite);
        }

        private static double Median(List<double> values)
        {
            if (values == null || values.Count == 0)
                return double.NaN;
            List<double> ordered = values.Where(IsFinite).OrderBy(x => x).ToList();
            if (ordered.Count == 0)
                return double.NaN;
            int m = ordered.Count / 2;
            return ordered.Count % 2 == 1 ? ordered[m] : (ordered[m - 1] + ordered[m]) * 0.5;
        }

        private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

        private static string FormatStation(double station)
        {
            int km = (int)Math.Floor(station / 1000.0);
            return km + "+" + (station - km * 1000.0).ToString("000.00", System.Globalization.CultureInfo.InvariantCulture);
        }

        private static SymbolWorkspaceBlock ToBlock(ArmBlockCatalogState item) => new SymbolWorkspaceBlock
        {
            Code = item.Code,
            Variant = item.Variant,
            BlockName = item.Name,
            FilePath = item.SourceDwgPath,
            Exists = true
        };

        private sealed class RawAnchorMark
        {
            public string RoadKey { get; set; } = string.Empty;
            public string OwnerId { get; set; } = string.Empty;
            public string Code { get; set; } = string.Empty;
            public double Station { get; set; }
            public string InboundDirection { get; set; } = string.Empty;
        }

        private sealed class IntersectionReferenceAnchor
        {
            public string RoadKey { get; set; } = string.Empty;
            public string OwnerId { get; set; } = string.Empty;
            public string InboundDirection { get; set; } = string.Empty;
            public bool HasStopLine { get; set; }
            public bool HasCrosswalk { get; set; }
            public List<double> StopStations { get; } = new List<double>();
            public List<double> CrosswalkStations { get; } = new List<double>();
            public double Station71 { get; set; } = double.NaN;
            public double Station73 { get; set; } = double.NaN;
            public double ReferenceStation { get; set; } = double.NaN;

            public static IntersectionReferenceAnchor Empty(string roadKey, string ownerId, string direction) =>
                new IntersectionReferenceAnchor
                {
                    RoadKey = roadKey,
                    OwnerId = ownerId,
                    InboundDirection = direction
                };
        }
    }

    public sealed class SymbolPlacementWorkspace
    {
        public string LibraryPath { get; set; } = string.Empty;
        public List<SymbolWorkspaceBlock> Blocks { get; set; } = new List<SymbolWorkspaceBlock>();
        public List<SymbolWorkspaceNode> Nodes { get; set; } = new List<SymbolWorkspaceNode>();
        public List<ArmBlockProposalState> Proposals { get; set; } = new List<ArmBlockProposalState>();
    }

    public sealed class SymbolWorkspaceBlock
    {
        public string Code { get; set; } = string.Empty;
        public string Variant { get; set; } = string.Empty;
        public string BlockName { get; set; } = string.Empty;
        public string FilePath { get; set; } = string.Empty;
        public bool Exists { get; set; }
    }

    public sealed class SymbolWorkspaceNode
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string RoadKey { get; set; } = string.Empty;
        public double? Station71 { get; set; }
        public double? Station73 { get; set; }
        public List<SymbolWorkspaceApproach> Approaches { get; set; } = new List<SymbolWorkspaceApproach>();
    }

    public sealed class SymbolWorkspaceApproach
    {
        public string Id { get; set; } = string.Empty;
        public string NodeId { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string RoadName { get; set; } = string.Empty;
        public string RoadKey { get; set; } = string.Empty;
        public string AssemblyId { get; set; } = string.Empty;
        public string AssemblyName { get; set; } = string.Empty;
        public string InboundDirection { get; set; } = string.Empty;
        public bool StopLineFound { get; set; }
        public bool CrosswalkFound { get; set; }
        public double? Station71 { get; set; }
        public double? Station73 { get; set; }
        public List<SymbolWorkspaceLane> Lanes { get; set; } = new List<SymbolWorkspaceLane>();
    }

    public sealed class SymbolWorkspaceLane
    {
        public string Id { get; set; } = string.Empty;
        public string SourceComponentId { get; set; } = string.Empty;
        public int LaneIndex { get; set; }
        public double Width { get; set; }
        public double CenterOffset { get; set; }
        public string Side { get; set; } = string.Empty;
        public bool AllowLeft { get; set; }
        public bool AllowStraight { get; set; }
        public bool AllowRight { get; set; }
    }
}
