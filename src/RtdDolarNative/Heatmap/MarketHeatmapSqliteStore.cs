using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using System.Threading;
using RtdDolarNative.Flow;
using RtdDolarNative.Logging;

namespace RtdDolarNative.Heatmap
{
    public sealed class MarketHeatmapSqliteStore : IDisposable
    {
        private readonly string _databasePath;
        private readonly Logger _log;
        private readonly object _queueLock = new object();
        private readonly Queue<StorageCommand> _queue = new Queue<StorageCommand>();
        private readonly AutoResetEvent _wake = new AutoResetEvent(false);
        private Thread _thread;
        private volatile bool _stopRequested;
        private long _bookRows;
        private long _tradeRows;
        private long _dropped;
        private Exception _lastError;

        public MarketHeatmapSqliteStore(string databasePath, Logger log)
        {
            _databasePath = databasePath;
            _log = log;
        }

        public string DatabasePath
        {
            get { return _databasePath; }
        }

        public long BookRows
        {
            get { return Interlocked.Read(ref _bookRows); }
        }

        public long TradeRows
        {
            get { return Interlocked.Read(ref _tradeRows); }
        }

        public long Dropped
        {
            get { return Interlocked.Read(ref _dropped); }
        }

        public int QueueDepth
        {
            get
            {
                lock (_queueLock)
                {
                    return _queue.Count;
                }
            }
        }

        public string Status
        {
            get
            {
                if (_lastError != null)
                {
                    return "sqlite erro: " + _lastError.Message;
                }

                return "sqlite ok | book " + BookRows + " | trades " + TradeRows + " | fila " + QueueDepth;
            }
        }

        public void Start()
        {
            if (_thread != null && _thread.IsAlive)
            {
                return;
            }

            _stopRequested = false;
            _thread = new Thread(Run);
            _thread.IsBackground = true;
            _thread.Name = "HeatmapSQLite";
            _thread.Start();
        }

        public void EnqueueBookLevels(string asset, DateTimeOffset timestamp, List<HeatmapBookLevel> levels)
        {
            if (levels == null || levels.Count == 0)
            {
                return;
            }

            StorageCommand command = new StorageCommand();
            command.Kind = StorageCommandKind.Book;
            command.Asset = asset;
            command.Timestamp = timestamp;
            command.BookLevels = levels;
            Enqueue(command);
        }

        public void EnqueueTrade(TradePrint trade)
        {
            if (trade == null)
            {
                return;
            }

            StorageCommand command = new StorageCommand();
            command.Kind = StorageCommandKind.Trade;
            command.Trade = trade;
            Enqueue(command);
        }

        public List<HeatmapHistoricalLevel> LoadRecentBookContext(string asset, DateTimeOffset since, int maxRows)
        {
            List<HeatmapHistoricalLevel> levels = new List<HeatmapHistoricalLevel>();

            if (string.IsNullOrWhiteSpace(asset))
            {
                return levels;
            }

            try
            {
                string folder = Path.GetDirectoryName(_databasePath);

                if (!string.IsNullOrWhiteSpace(folder))
                {
                    Directory.CreateDirectory(folder);
                }

                using (SQLiteConnection connection = new SQLiteConnection("Data Source=" + _databasePath + ";Version=3;Pooling=True;"))
                {
                    connection.Open();
                    Initialize(connection);

                    using (SQLiteCommand command = connection.CreateCommand())
                    {
                        command.CommandText =
                            "SELECT price, SUM(bid_size) AS bid_sum, SUM(ask_size) AS ask_sum, COUNT(1) AS samples, MAX(ts_unix_ms) AS last_ms " +
                            "FROM book_levels " +
                            "WHERE asset = @asset AND ts_unix_ms >= @since_ms AND (bid_size > 0 OR ask_size > 0) " +
                            "GROUP BY price " +
                            "ORDER BY (SUM(bid_size) + SUM(ask_size)) DESC, COUNT(1) DESC " +
                            "LIMIT @limit;";
                        command.Parameters.AddWithValue("@asset", asset);
                        command.Parameters.AddWithValue("@since_ms", since.ToUnixTimeMilliseconds());
                        command.Parameters.AddWithValue("@limit", Math.Max(1, maxRows));

                        using (SQLiteDataReader reader = command.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                HeatmapHistoricalLevel level = new HeatmapHistoricalLevel();
                                level.Price = Convert.ToDecimal(reader["price"]);
                                level.BidLiquidity = Convert.ToDecimal(reader["bid_sum"]);
                                level.AskLiquidity = Convert.ToDecimal(reader["ask_sum"]);
                                level.Samples = Convert.ToInt32(reader["samples"]);
                                level.LastSeen = DateTimeOffset.FromUnixTimeMilliseconds(Convert.ToInt64(reader["last_ms"])).ToLocalTime();
                                levels.Add(level);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _lastError = ex;

                if (_log != null)
                {
                    _log.Error("Falha ao ler contexto historico SQLite do heatmap.", ex);
                }
            }

            return levels;
        }

        public List<HeatmapHistoricalTradeLevel> LoadRecentTradeContext(string asset, DateTimeOffset since, int maxRows)
        {
            List<HeatmapHistoricalTradeLevel> levels = new List<HeatmapHistoricalTradeLevel>();

            if (string.IsNullOrWhiteSpace(asset))
            {
                return levels;
            }

            try
            {
                string folder = Path.GetDirectoryName(_databasePath);

                if (!string.IsNullOrWhiteSpace(folder))
                {
                    Directory.CreateDirectory(folder);
                }

                using (SQLiteConnection connection = new SQLiteConnection("Data Source=" + _databasePath + ";Version=3;Pooling=True;"))
                {
                    connection.Open();
                    Initialize(connection);

                    using (SQLiteCommand command = connection.CreateCommand())
                    {
                        command.CommandText =
                            "SELECT price, " +
                            "SUM(CASE WHEN delta > 0 OR UPPER(aggressor) = 'BUY' THEN quantity ELSE 0 END) AS buy_sum, " +
                            "SUM(CASE WHEN delta < 0 OR UPPER(aggressor) = 'SELL' THEN quantity ELSE 0 END) AS sell_sum, " +
                            "SUM(CASE WHEN delta = 0 AND UPPER(aggressor) <> 'BUY' AND UPPER(aggressor) <> 'SELL' THEN quantity ELSE 0 END) AS neutral_sum, " +
                            "SUM(delta) AS delta_sum, COUNT(1) AS samples, MAX(ts_unix_ms) AS last_ms " +
                            "FROM trades " +
                            "WHERE asset = @asset AND ts_unix_ms >= @since_ms AND quantity > 0 " +
                            "GROUP BY price " +
                            "ORDER BY ABS(SUM(delta)) DESC, SUM(quantity) DESC, COUNT(1) DESC " +
                            "LIMIT @limit;";
                        command.Parameters.AddWithValue("@asset", asset);
                        command.Parameters.AddWithValue("@since_ms", since.ToUnixTimeMilliseconds());
                        command.Parameters.AddWithValue("@limit", Math.Max(1, maxRows));

                        using (SQLiteDataReader reader = command.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                HeatmapHistoricalTradeLevel level = new HeatmapHistoricalTradeLevel();
                                level.Price = Convert.ToDecimal(reader["price"]);
                                level.BuyVolume = Convert.ToDecimal(reader["buy_sum"]);
                                level.SellVolume = Convert.ToDecimal(reader["sell_sum"]);
                                level.NeutralVolume = Convert.ToDecimal(reader["neutral_sum"]);
                                level.Delta = Convert.ToDecimal(reader["delta_sum"]);
                                level.Samples = Convert.ToInt32(reader["samples"]);
                                level.LastSeen = DateTimeOffset.FromUnixTimeMilliseconds(Convert.ToInt64(reader["last_ms"])).ToLocalTime();
                                levels.Add(level);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _lastError = ex;

                if (_log != null)
                {
                    _log.Error("Falha ao ler fluxo historico SQLite do heatmap.", ex);
                }
            }

            return levels;
        }

        public void Dispose()
        {
            _stopRequested = true;
            _wake.Set();

            if (_thread != null && !_thread.Join(3000))
            {
                try
                {
                    _thread.Abort();
                }
                catch
                {
                }
            }

            _wake.Dispose();
        }

        private void Enqueue(StorageCommand command)
        {
            lock (_queueLock)
            {
                while (_queue.Count >= 2000)
                {
                    _queue.Dequeue();
                    Interlocked.Increment(ref _dropped);
                }

                _queue.Enqueue(command);
            }

            _wake.Set();
        }

        private void Run()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_databasePath));

                using (SQLiteConnection connection = new SQLiteConnection("Data Source=" + _databasePath + ";Version=3;Pooling=True;"))
                {
                    connection.Open();
                    Initialize(connection);

                    while (!_stopRequested)
                    {
                        _wake.WaitOne(500);
                        List<StorageCommand> batch = TakeBatch();

                        if (batch.Count == 0)
                        {
                            continue;
                        }

                        WriteBatch(connection, batch);
                    }
                }
            }
            catch (ThreadAbortException)
            {
            }
            catch (Exception ex)
            {
                _lastError = ex;

                if (_log != null)
                {
                    _log.Error("Falha no storage SQLite do heatmap.", ex);
                }
            }
        }

        private List<StorageCommand> TakeBatch()
        {
            lock (_queueLock)
            {
                List<StorageCommand> batch = new List<StorageCommand>();

                while (_queue.Count > 0 && batch.Count < 250)
                {
                    batch.Add(_queue.Dequeue());
                }

                return batch;
            }
        }

        private static void Initialize(SQLiteConnection connection)
        {
            ExecuteNonQuery(connection, "PRAGMA journal_mode=WAL;");
            ExecuteNonQuery(connection, "PRAGMA synchronous=NORMAL;");
            ExecuteNonQuery(connection, "CREATE TABLE IF NOT EXISTS book_levels (id INTEGER PRIMARY KEY AUTOINCREMENT, ts_utc TEXT NOT NULL, ts_unix_ms INTEGER NOT NULL, asset TEXT NOT NULL, price REAL NOT NULL, bid_size REAL NOT NULL, ask_size REAL NOT NULL, level_index INTEGER NOT NULL, source TEXT NOT NULL);");
            ExecuteNonQuery(connection, "CREATE TABLE IF NOT EXISTS trades (id INTEGER PRIMARY KEY AUTOINCREMENT, ts_utc TEXT NOT NULL, ts_unix_ms INTEGER NOT NULL, asset TEXT NOT NULL, price REAL NOT NULL, quantity REAL NOT NULL, delta REAL NOT NULL, aggressor TEXT NOT NULL, derived INTEGER NOT NULL, quality TEXT NOT NULL);");
            ExecuteNonQuery(connection, "CREATE INDEX IF NOT EXISTS idx_book_levels_asset_ts ON book_levels(asset, ts_unix_ms);");
            ExecuteNonQuery(connection, "CREATE INDEX IF NOT EXISTS idx_book_levels_asset_price ON book_levels(asset, price);");
            ExecuteNonQuery(connection, "CREATE INDEX IF NOT EXISTS idx_trades_asset_ts ON trades(asset, ts_unix_ms);");
            ExecuteNonQuery(connection, "CREATE INDEX IF NOT EXISTS idx_trades_asset_price ON trades(asset, price);");
        }

        private void WriteBatch(SQLiteConnection connection, List<StorageCommand> batch)
        {
            using (SQLiteTransaction transaction = connection.BeginTransaction())
            {
                foreach (StorageCommand command in batch)
                {
                    if (command.Kind == StorageCommandKind.Book)
                    {
                        WriteBookLevels(connection, transaction, command);
                    }
                    else if (command.Kind == StorageCommandKind.Trade)
                    {
                        WriteTrade(connection, transaction, command.Trade);
                    }
                }

                transaction.Commit();
            }
        }

        private void WriteBookLevels(SQLiteConnection connection, SQLiteTransaction transaction, StorageCommand command)
        {
            using (SQLiteCommand insert = connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText = "INSERT INTO book_levels(ts_utc, ts_unix_ms, asset, price, bid_size, ask_size, level_index, source) VALUES(@ts, @ms, @asset, @price, @bid, @ask, @level, @source);";
                SQLiteParameter ts = insert.Parameters.Add("@ts", System.Data.DbType.String);
                SQLiteParameter ms = insert.Parameters.Add("@ms", System.Data.DbType.Int64);
                SQLiteParameter asset = insert.Parameters.Add("@asset", System.Data.DbType.String);
                SQLiteParameter price = insert.Parameters.Add("@price", System.Data.DbType.Double);
                SQLiteParameter bid = insert.Parameters.Add("@bid", System.Data.DbType.Double);
                SQLiteParameter ask = insert.Parameters.Add("@ask", System.Data.DbType.Double);
                SQLiteParameter level = insert.Parameters.Add("@level", System.Data.DbType.Int32);
                SQLiteParameter source = insert.Parameters.Add("@source", System.Data.DbType.String);

                foreach (HeatmapBookLevel item in command.BookLevels)
                {
                    ts.Value = command.Timestamp.UtcDateTime.ToString("O");
                    ms.Value = command.Timestamp.ToUnixTimeMilliseconds();
                    asset.Value = command.Asset ?? item.Asset ?? string.Empty;
                    price.Value = Convert.ToDouble(item.Price);
                    bid.Value = Convert.ToDouble(item.BidSize);
                    ask.Value = Convert.ToDouble(item.AskSize);
                    level.Value = item.LevelIndex;
                    source.Value = "RTD_BOOK";
                    insert.ExecuteNonQuery();
                    Interlocked.Increment(ref _bookRows);
                }
            }
        }

        private void WriteTrade(SQLiteConnection connection, SQLiteTransaction transaction, TradePrint trade)
        {
            using (SQLiteCommand insert = connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText = "INSERT INTO trades(ts_utc, ts_unix_ms, asset, price, quantity, delta, aggressor, derived, quality) VALUES(@ts, @ms, @asset, @price, @qty, @delta, @aggressor, @derived, @quality);";
                insert.Parameters.AddWithValue("@ts", trade.LocalTimestamp.UtcDateTime.ToString("O"));
                insert.Parameters.AddWithValue("@ms", trade.LocalTimestamp.ToUnixTimeMilliseconds());
                insert.Parameters.AddWithValue("@asset", trade.Asset ?? string.Empty);
                insert.Parameters.AddWithValue("@price", Convert.ToDouble(trade.Price));
                insert.Parameters.AddWithValue("@qty", Convert.ToDouble(trade.Quantity));
                insert.Parameters.AddWithValue("@delta", Convert.ToDouble(trade.Delta));
                insert.Parameters.AddWithValue("@aggressor", trade.Aggressor ?? "Neutral");
                insert.Parameters.AddWithValue("@derived", trade.Derived ? 1 : 0);
                insert.Parameters.AddWithValue("@quality", trade.DataQuality.ToString());
                insert.ExecuteNonQuery();
                Interlocked.Increment(ref _tradeRows);
            }
        }

        private static void ExecuteNonQuery(SQLiteConnection connection, string sql)
        {
            using (SQLiteCommand command = connection.CreateCommand())
            {
                command.CommandText = sql;
                command.ExecuteNonQuery();
            }
        }

        private enum StorageCommandKind
        {
            Book,
            Trade
        }

        private sealed class StorageCommand
        {
            public StorageCommandKind Kind { get; set; }
            public string Asset { get; set; }
            public DateTimeOffset Timestamp { get; set; }
            public List<HeatmapBookLevel> BookLevels { get; set; }
            public TradePrint Trade { get; set; }
        }
    }
}
