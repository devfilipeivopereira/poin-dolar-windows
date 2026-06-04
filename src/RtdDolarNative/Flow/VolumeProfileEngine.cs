using System;
using System.Collections.Generic;
using System.Linq;
using RtdDolarNative.Config;

namespace RtdDolarNative.Flow
{
    public sealed class VolumeProfileEngine
    {
        private readonly decimal _tickSize;
        private readonly FlowConfig _config;
        private readonly Dictionary<decimal, VolumeProfileBin> _bins = new Dictionary<decimal, VolumeProfileBin>();
        private string _asset;

        public VolumeProfileEngine(decimal tickSize, FlowConfig config)
        {
            _tickSize = tickSize <= 0m ? 0.5m : tickSize;
            _config = config ?? new FlowConfig();
            _config.Normalize();
        }

        public void AddTrade(TradePrint trade)
        {
            if (trade == null || trade.Price <= 0m || trade.Quantity <= 0m)
            {
                return;
            }

            _asset = trade.Asset;
            decimal price = RoundToTick(trade.Price);
            VolumeProfileBin bin;

            if (!_bins.TryGetValue(price, out bin))
            {
                bin = new VolumeProfileBin();
                bin.Price = price;
                bin.Low = price - (_tickSize / 2m);
                bin.High = price + (_tickSize / 2m);
                _bins[price] = bin;
            }

            bin.Volume += trade.Quantity;

            if (string.Equals(trade.Aggressor, "Buy", StringComparison.OrdinalIgnoreCase))
            {
                bin.BuyVolume += trade.Quantity;
            }
            else if (string.Equals(trade.Aggressor, "Sell", StringComparison.OrdinalIgnoreCase))
            {
                bin.SellVolume += trade.Quantity;
            }

            bin.Delta += trade.Delta;
        }

        public VolumeProfileMetrics Build(decimal? currentPrice)
        {
            VolumeProfileMetrics metrics = new VolumeProfileMetrics();
            metrics.Asset = _asset;
            metrics.LocalTimestamp = DateTimeOffset.Now;
            metrics.Source = "Intraday tape";

            List<VolumeProfileBin> bins = _bins.Values
                .Where(x => x.Volume > 0m)
                .OrderBy(x => x.Price)
                .Select(CloneBin)
                .ToList();

            if (bins.Count == 0)
            {
                return metrics;
            }

            decimal total = bins.Sum(x => x.Volume);
            VolumeProfileBin poc = bins.OrderByDescending(x => x.Volume).ThenBy(x => x.Price).First();
            decimal target = total * _config.ValueAreaPercent;
            decimal accumulated = poc.Volume;
            int pocIndex = bins.FindIndex(x => x.Price == poc.Price);
            int lowIndex = pocIndex;
            int highIndex = pocIndex;
            bins[pocIndex].InValueArea = true;

            while (accumulated < target && (lowIndex > 0 || highIndex < bins.Count - 1))
            {
                decimal below = lowIndex > 0 ? bins[lowIndex - 1].Volume : -1m;
                decimal above = highIndex < bins.Count - 1 ? bins[highIndex + 1].Volume : -1m;

                if (above >= below)
                {
                    highIndex++;
                    accumulated += bins[highIndex].Volume;
                    bins[highIndex].InValueArea = true;
                }
                else
                {
                    lowIndex--;
                    accumulated += bins[lowIndex].Volume;
                    bins[lowIndex].InValueArea = true;
                }
            }

            decimal pocVolume = poc.Volume;

            foreach (VolumeProfileBin bin in bins)
            {
                bin.IsPoc = bin.Price == poc.Price;
                bin.Rank = pocVolume <= 0m ? 0m : bin.Volume / pocVolume;
                bin.IsHvn = !bin.IsPoc && bin.Rank >= _config.HvnThresholdRatio;
            }

            MarkLvns(bins, lowIndex, highIndex, pocVolume);

            metrics.Poc = poc.Price;
            metrics.Vah = bins[highIndex].Price;
            metrics.Val = bins[lowIndex].Price;
            metrics.TotalVolume = total;
            metrics.ValueAreaVolume = accumulated;
            metrics.CurrentDistanceToPoc = currentPrice.HasValue ? currentPrice.Value - poc.Price : (decimal?)null;
            metrics.Bins = bins.OrderByDescending(x => x.Price).ToList();
            metrics.Nodes = BuildNodes(bins, pocVolume);
            metrics.Levels = BuildLevels(metrics, bins);
            return metrics;
        }

        private void MarkLvns(List<VolumeProfileBin> bins, int lowIndex, int highIndex, decimal pocVolume)
        {
            if (pocVolume <= 0m || bins.Count < 3)
            {
                return;
            }

            for (int i = 1; i < bins.Count - 1; i++)
            {
                VolumeProfileBin previous = bins[i - 1];
                VolumeProfileBin current = bins[i];
                VolumeProfileBin next = bins[i + 1];
                bool betweenRelevantVolume = previous.Volume > current.Volume && next.Volume > current.Volume;
                bool lowRelative = current.Volume <= pocVolume * _config.LvnThresholdRatio;
                bool insideProfile = i >= lowIndex && i <= highIndex;

                if (betweenRelevantVolume && lowRelative && !current.IsPoc)
                {
                    current.IsLvn = true;
                }
                else if (!insideProfile && lowRelative && previous.Volume > 0m && next.Volume > 0m)
                {
                    current.IsLvn = true;
                }
            }
        }

        private List<VolumeNode> BuildNodes(List<VolumeProfileBin> bins, decimal pocVolume)
        {
            List<VolumeNode> nodes = new List<VolumeNode>();
            AddClusterNodes(nodes, bins.Where(x => x.IsHvn || x.IsPoc).OrderBy(x => x.Price).ToList(), "hvn", pocVolume);
            AddClusterNodes(nodes, bins.Where(x => x.IsLvn).OrderBy(x => x.Price).ToList(), "lvn", pocVolume);
            return nodes.OrderByDescending(x => x.Score).ThenBy(x => x.Price).ToList();
        }

        private void AddClusterNodes(List<VolumeNode> nodes, List<VolumeProfileBin> candidates, string type, decimal pocVolume)
        {
            if (candidates.Count == 0)
            {
                return;
            }

            List<VolumeProfileBin> cluster = new List<VolumeProfileBin>();
            decimal last = decimal.MinValue;

            foreach (VolumeProfileBin bin in candidates)
            {
                if (cluster.Count > 0 && bin.Price - last > _tickSize * 2m)
                {
                    AddNode(nodes, cluster, type, pocVolume);
                    cluster.Clear();
                }

                cluster.Add(bin);
                last = bin.Price;
            }

            AddNode(nodes, cluster, type, pocVolume);
        }

        private void AddNode(List<VolumeNode> nodes, List<VolumeProfileBin> cluster, string type, decimal pocVolume)
        {
            if (cluster == null || cluster.Count == 0)
            {
                return;
            }

            decimal volume = cluster.Sum(x => x.Volume);
            VolumeProfileBin peak = cluster.OrderByDescending(x => x.Volume).First();
            VolumeNode node = new VolumeNode();
            node.Type = type;
            node.Price = peak.Price;
            node.Low = cluster.Min(x => x.Price);
            node.High = cluster.Max(x => x.Price);
            node.Volume = volume;
            node.Score = pocVolume <= 0m ? 0m : Math.Min(100m, (volume / pocVolume) * 100m);
            node.Description = type.ToUpperInvariant() + " " + node.Low.ToString("N2") + "-" + node.High.ToString("N2");
            nodes.Add(node);
        }

        private List<ProfileLevel> BuildLevels(VolumeProfileMetrics metrics, List<VolumeProfileBin> bins)
        {
            List<ProfileLevel> levels = new List<ProfileLevel>();

            AddLevel(levels, metrics.Poc, "poc", "POC", 96d);
            AddLevel(levels, metrics.Vah, "vah", "VAH", 88d);
            AddLevel(levels, metrics.Val, "val", "VAL", 88d);

            foreach (VolumeProfileBin bin in bins.Where(x => x.IsHvn).OrderByDescending(x => x.Rank).Take(8))
            {
                AddLevel(levels, bin.Price, "hvn", "HVN", 82d);
            }

            foreach (VolumeProfileBin bin in bins.Where(x => x.IsLvn).OrderBy(x => x.Price).Take(10))
            {
                AddLevel(levels, bin.Price, "lvn", "LVN", 78d);
            }

            return levels;
        }

        private void AddLevel(List<ProfileLevel> levels, decimal? price, string type, string label, double score)
        {
            if (!price.HasValue)
            {
                return;
            }

            ProfileLevel level = new ProfileLevel();
            level.Type = type;
            level.Price = price.Value;
            level.Label = label;
            level.Score = score;
            level.Source = "Volume Profile";
            levels.Add(level);
        }

        private VolumeProfileBin CloneBin(VolumeProfileBin source)
        {
            VolumeProfileBin clone = new VolumeProfileBin();
            clone.Price = source.Price;
            clone.Low = source.Low;
            clone.High = source.High;
            clone.Volume = source.Volume;
            clone.BuyVolume = source.BuyVolume;
            clone.SellVolume = source.SellVolume;
            clone.Delta = source.Delta;
            return clone;
        }

        private decimal RoundToTick(decimal price)
        {
            return Math.Round(price / _tickSize, 0, MidpointRounding.AwayFromZero) * _tickSize;
        }
    }
}
