using System;
using System.Collections.Generic;
using System.Linq;
using RtdDolarNative.Config;
using RtdDolarNative.MarketData;

namespace RtdDolarNative.Flow
{
    public sealed class FlowEngine
    {
        private readonly FlowConfig _config;
        private readonly decimal _tickSize;
        private readonly VolumeProfileEngine _profile;
        private readonly SetupDetector _setups;
        private readonly List<TradePrint> _recentTrades = new List<TradePrint>();
        private MarketSnapshot _previousSnapshot;
        private string _lastAggressor = "Neutral";
        private decimal _cumulativeDelta;
        private decimal _vwapVolume;
        private decimal _vwapPriceVolume;

        public FlowEngine(decimal tickSize, FlowConfig config)
        {
            _tickSize = tickSize <= 0m ? 0.5m : tickSize;
            _config = config ?? new FlowConfig();
            _config.Normalize();
            _profile = new VolumeProfileEngine(_tickSize, _config);
            _setups = new SetupDetector(_tickSize, _config);
        }

        public FlowUpdate Process(MarketSnapshot snapshot)
        {
            FlowUpdate update = new FlowUpdate();
            update.Signals = new List<FlowSignal>();

            if (snapshot == null)
            {
                return update;
            }

            TradePrint trade = CreateTradeIfNeeded(snapshot);

            if (trade != null)
            {
                _recentTrades.Add(trade);

                while (_recentTrades.Count > _config.MaxTradeBuffer)
                {
                    _recentTrades.RemoveAt(0);
                }

                _profile.AddTrade(trade);
                _cumulativeDelta += trade.Delta;
                _vwapVolume += trade.Quantity;
                _vwapPriceVolume += trade.Price * trade.Quantity;
                update.Trade = trade;
            }

            FlowMetrics metrics = BuildMetrics(snapshot, trade);
            update.Metrics = metrics;

            if (metrics != null)
            {
                update.Signals = _setups.Detect(metrics, trade);
            }

            _previousSnapshot = snapshot.Clone();
            return update;
        }

        private TradePrint CreateTradeIfNeeded(MarketSnapshot snapshot)
        {
            if (_previousSnapshot == null || !snapshot.Ultimo.HasValue)
            {
                return null;
            }

            decimal price = snapshot.Ultimo.Value;
            decimal previousPrice = _previousSnapshot.Ultimo.HasValue ? _previousSnapshot.Ultimo.Value : price;
            bool priceChanged = Math.Abs(price - previousPrice) >= (_tickSize / 2m);
            decimal tradesDelta = PositiveDelta(snapshot.Negocios, _previousSnapshot.Negocios);
            decimal volumeDelta = PositiveDelta(snapshot.Volume, _previousSnapshot.Volume);
            decimal quantityDelta = PositiveDelta(snapshot.Quantidade, _previousSnapshot.Quantidade);
            bool hasTradeCounter = snapshot.Negocios.HasValue && _previousSnapshot.Negocios.HasValue;
            bool hasVolumeCounter = snapshot.Volume.HasValue && _previousSnapshot.Volume.HasValue;
            bool shouldPrint;

            if (hasTradeCounter)
            {
                shouldPrint = tradesDelta > 0m;
            }
            else if (hasVolumeCounter)
            {
                shouldPrint = volumeDelta > 0m;
            }
            else
            {
                shouldPrint = priceChanged;
            }

            if (!shouldPrint)
            {
                return null;
            }

            decimal quantity = snapshot.QuantidadeUltimoNegocio.HasValue ? snapshot.QuantidadeUltimoNegocio.Value : 0m;

            if (quantity <= 0m && quantityDelta > 0m && quantityDelta <= 100000m)
            {
                quantity = quantityDelta;
            }

            if (quantity <= 0m && tradesDelta > 0m)
            {
                quantity = tradesDelta;
            }

            if (quantity <= 0m && volumeDelta > 0m && volumeDelta <= 10000m)
            {
                quantity = volumeDelta;
            }

            if (quantity <= 0m)
            {
                quantity = 1m;
            }

            string aggressor = ClassifyAggressor(snapshot, price, previousPrice);
            decimal delta = 0m;

            if (aggressor == "Buy")
            {
                delta = quantity;
            }
            else if (aggressor == "Sell")
            {
                delta = -quantity;
            }

            _lastAggressor = aggressor;

            TradePrint trade = new TradePrint();
            trade.Asset = snapshot.Asset;
            trade.LocalTimestamp = snapshot.LocalTimestamp;
            trade.ProfitTime = snapshot.HoraProfit;
            trade.Price = price;
            trade.Quantity = quantity;
            trade.Volume = volumeDelta > 0m ? volumeDelta : quantity;
            trade.Delta = delta;
            trade.Aggressor = aggressor;
            trade.Classification = aggressor == "Buy" ? "agressao compra" : (aggressor == "Sell" ? "agressao venda" : "neutro");
            trade.Derived = true;
            trade.DataQuality = MarketDataQuality.DerivedTape;
            trade.Bid = snapshot.OfertaCompra;
            trade.Ask = snapshot.OfertaVenda;
            return trade;
        }

        private FlowMetrics BuildMetrics(MarketSnapshot snapshot, TradePrint trade)
        {
            BookSnapshot book = BuildBook(snapshot);
            decimal? price = snapshot.Ultimo;
            decimal? vwap = null;

            if (_vwapVolume > 0m)
            {
                vwap = _vwapPriceVolume / _vwapVolume;
            }
            else if (_config.UseMedAsVwapFallback && snapshot.Media.HasValue)
            {
                vwap = snapshot.Media.Value;
            }

            FlowMetrics metrics = new FlowMetrics();
            metrics.Asset = snapshot.Asset;
            metrics.LocalTimestamp = snapshot.LocalTimestamp;
            metrics.Price = price;
            metrics.Bid = book.Bid;
            metrics.Ask = book.Ask;
            metrics.Spread = book.Spread;
            metrics.Mid = book.Mid;
            metrics.MicroPrice = book.MicroPrice;
            metrics.MicroBias = book.MicroBias;
            metrics.TopBookImbalance = book.Imbalance;
            metrics.LastDelta = trade == null ? 0m : trade.Delta;
            metrics.CumulativeDelta = _cumulativeDelta;
            metrics.Vwap = vwap;
            metrics.VwapDistance = price.HasValue && vwap.HasValue ? price.Value - vwap.Value : (decimal?)null;
            metrics.DataQuality = trade == null ? book.DataQuality : MarketDataQuality.DerivedTape;
            metrics.Derived = true;
            metrics.Windows = BuildWindows(snapshot.LocalTimestamp);
            metrics.Profile = _profile.Build(price);
            metrics.LastTrade = trade;
            return metrics;
        }

        private BookSnapshot BuildBook(MarketSnapshot snapshot)
        {
            BookSnapshot book = new BookSnapshot();
            book.Asset = snapshot.Asset;
            book.LocalTimestamp = snapshot.LocalTimestamp;
            book.Bid = snapshot.OfertaCompra;
            book.Ask = snapshot.OfertaVenda;
            book.BidVolume = snapshot.VolumeOfertaCompra;
            book.AskVolume = snapshot.VolumeOfertaVenda;
            book.DataQuality = MarketDataQuality.TopOfBookOnly;

            if (book.Bid.HasValue && book.Ask.HasValue)
            {
                book.Spread = book.Ask.Value - book.Bid.Value;
                book.Mid = (book.Bid.Value + book.Ask.Value) / 2m;
            }

            if (book.Bid.HasValue && book.Ask.HasValue && book.BidVolume.HasValue && book.AskVolume.HasValue)
            {
                decimal total = book.BidVolume.Value + book.AskVolume.Value;

                if (total > 0m)
                {
                    book.MicroPrice = ((book.Bid.Value * book.AskVolume.Value) + (book.Ask.Value * book.BidVolume.Value)) / total;
                    book.Imbalance = (book.BidVolume.Value - book.AskVolume.Value) / total;

                    if (book.Mid.HasValue)
                    {
                        book.MicroBias = book.MicroPrice.Value - book.Mid.Value;
                    }
                }
            }

            return book;
        }

        private List<FlowWindowMetrics> BuildWindows(DateTimeOffset now)
        {
            int[] windows = new[] { 1, 5, 15, 60, 300 };
            List<FlowWindowMetrics> result = new List<FlowWindowMetrics>();

            foreach (int seconds in windows)
            {
                DateTimeOffset cutoff = now.AddSeconds(-seconds);
                List<TradePrint> trades = _recentTrades.Where(x => x.LocalTimestamp >= cutoff).ToList();
                decimal buy = trades.Where(x => x.Delta > 0m).Sum(x => x.Quantity);
                decimal sell = trades.Where(x => x.Delta < 0m).Sum(x => x.Quantity);
                decimal total = buy + sell + trades.Where(x => x.Delta == 0m).Sum(x => x.Quantity);
                decimal delta = trades.Sum(x => x.Delta);

                FlowWindowMetrics row = new FlowWindowMetrics();
                row.Window = seconds.ToString() + "s";
                row.Seconds = seconds;
                row.TradeCount = trades.Count;
                row.BuyVolume = buy;
                row.SellVolume = sell;
                row.Delta = delta;
                row.TotalVolume = total;
                row.DeltaRatio = total > 0m ? delta / total : 0m;
                result.Add(row);
            }

            return result;
        }

        private string ClassifyAggressor(MarketSnapshot snapshot, decimal price, decimal previousPrice)
        {
            if (snapshot.OfertaVenda.HasValue && price >= snapshot.OfertaVenda.Value)
            {
                return "Buy";
            }

            if (snapshot.OfertaCompra.HasValue && price <= snapshot.OfertaCompra.Value)
            {
                return "Sell";
            }

            if (price > previousPrice)
            {
                return "Buy";
            }

            if (price < previousPrice)
            {
                return "Sell";
            }

            if (_lastAggressor == "Buy" || _lastAggressor == "Sell")
            {
                return _lastAggressor;
            }

            return "Neutral";
        }

        private decimal PositiveDelta(decimal? current, decimal? previous)
        {
            if (!current.HasValue || !previous.HasValue)
            {
                return 0m;
            }

            decimal delta = current.Value - previous.Value;
            return delta > 0m ? delta : 0m;
        }
    }
}
