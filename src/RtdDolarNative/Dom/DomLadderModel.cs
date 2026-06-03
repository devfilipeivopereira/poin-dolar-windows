using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using RtdDolarNative.MarketData;
using RtdDolarNative.Quant;

namespace RtdDolarNative.Dom
{
    public static class DomLadderModel
    {
        public static List<DomRow> Build(MarketSnapshot snapshot, IEnumerable<KeyLevel> levels, decimal tickSize, int eachSide)
        {
            List<DomRow> rows = new List<DomRow>();
            CultureInfo ptBr = new CultureInfo("pt-BR");

            if (snapshot == null || !snapshot.Ultimo.HasValue)
            {
                return rows;
            }

            decimal current = RoundToTick(snapshot.Ultimo.Value, tickSize);
            decimal min = current - eachSide * tickSize;
            decimal max = current + eachSide * tickSize;
            Dictionary<decimal, List<KeyLevel>> byPrice = new Dictionary<decimal, List<KeyLevel>>();

            if (levels != null)
            {
                foreach (KeyLevel level in levels)
                {
                    decimal price = RoundToTick(level.Price, tickSize);

                    if (price < min)
                    {
                        min = price;
                    }

                    if (price > max)
                    {
                        max = price;
                    }

                    if (!byPrice.ContainsKey(price))
                    {
                        byPrice[price] = new List<KeyLevel>();
                    }

                    byPrice[price].Add(level);
                }
            }

            decimal bid = snapshot.OfertaCompra.HasValue ? RoundToTick(snapshot.OfertaCompra.Value, tickSize) : decimal.MinValue;
            decimal ask = snapshot.OfertaVenda.HasValue ? RoundToTick(snapshot.OfertaVenda.Value, tickSize) : decimal.MinValue;

            for (decimal price = max; price >= min; price -= tickSize)
            {
                List<KeyLevel> tags;
                byPrice.TryGetValue(price, out tags);
                DomRow row = new DomRow();
                row.Price = price;
                row.PriceText = price.ToString("N2", ptBr);
                row.IsCurrent = price == current;
                row.IsBid = price == bid;
                row.IsAsk = price == ask;
                row.BidVol = row.IsBid && snapshot.VolumeOfertaCompra.HasValue ? snapshot.VolumeOfertaCompra.Value.ToString("N0", ptBr) : string.Empty;
                row.AskVol = row.IsAsk && snapshot.VolumeOfertaVenda.HasValue ? snapshot.VolumeOfertaVenda.Value.ToString("N0", ptBr) : string.Empty;
                row.Markings = tags == null ? string.Empty : string.Join(" | ", tags.Select(x => x.Label).ToArray());
                row.Flags = (row.IsCurrent ? "ULT " : string.Empty) + (row.IsBid ? "BID " : string.Empty) + (row.IsAsk ? "ASK" : string.Empty);
                rows.Add(row);
            }

            return rows;
        }

        public static decimal RoundToTick(decimal price, decimal tickSize)
        {
            if (tickSize <= 0m)
            {
                return price;
            }

            return Math.Round(price / tickSize, 0, MidpointRounding.AwayFromZero) * tickSize;
        }
    }
}
