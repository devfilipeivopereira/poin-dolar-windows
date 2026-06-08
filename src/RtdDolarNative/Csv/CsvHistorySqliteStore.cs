using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Globalization;
using System.IO;
using System.Linq;

namespace RtdDolarNative.Csv
{
    public sealed class CsvHistorySqliteStore
    {
        private readonly string _databasePath;

        public CsvHistorySqliteStore(string databasePath)
        {
            _databasePath = databasePath;
        }

        public string DatabasePath
        {
            get { return _databasePath; }
        }

        public int UpsertBars(IEnumerable<DailyBar> bars, string defaultAsset, string sourcePath)
        {
            if (bars == null)
            {
                return 0;
            }

            int written = 0;
            string fallbackAsset = NormalizeAsset(defaultAsset);

            using (SQLiteConnection connection = OpenConnection())
            using (SQLiteTransaction transaction = connection.BeginTransaction())
            using (SQLiteCommand command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText =
                    "INSERT OR REPLACE INTO daily_bars(asset, trade_date, open, high, low, close, volume, quantity, source_path, updated_utc) " +
                    "VALUES(@asset, @trade_date, @open, @high, @low, @close, @volume, @quantity, @source_path, @updated_utc);";

                SQLiteParameter asset = command.Parameters.Add("@asset", System.Data.DbType.String);
                SQLiteParameter tradeDate = command.Parameters.Add("@trade_date", System.Data.DbType.String);
                SQLiteParameter open = command.Parameters.Add("@open", System.Data.DbType.Double);
                SQLiteParameter high = command.Parameters.Add("@high", System.Data.DbType.Double);
                SQLiteParameter low = command.Parameters.Add("@low", System.Data.DbType.Double);
                SQLiteParameter close = command.Parameters.Add("@close", System.Data.DbType.Double);
                SQLiteParameter volume = command.Parameters.Add("@volume", System.Data.DbType.Double);
                SQLiteParameter quantity = command.Parameters.Add("@quantity", System.Data.DbType.Double);
                SQLiteParameter sourcePathParam = command.Parameters.Add("@source_path", System.Data.DbType.String);
                SQLiteParameter updatedUtc = command.Parameters.Add("@updated_utc", System.Data.DbType.String);

                string updatedText = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);

                foreach (DailyBar bar in bars)
                {
                    if (bar == null || bar.Date == DateTime.MinValue)
                    {
                        continue;
                    }

                    string rowAsset = NormalizeAsset(string.IsNullOrWhiteSpace(fallbackAsset) ? bar.Asset : fallbackAsset);

                    if (string.IsNullOrWhiteSpace(rowAsset))
                    {
                        continue;
                    }

                    asset.Value = rowAsset;
                    tradeDate.Value = bar.Date.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                    open.Value = Convert.ToDouble(bar.Open);
                    high.Value = Convert.ToDouble(bar.High);
                    low.Value = Convert.ToDouble(bar.Low);
                    close.Value = Convert.ToDouble(bar.Close);
                    volume.Value = bar.Volume.HasValue ? (object)Convert.ToDouble(bar.Volume.Value) : DBNull.Value;
                    quantity.Value = bar.Quantity.HasValue ? (object)Convert.ToDouble(bar.Quantity.Value) : DBNull.Value;
                    sourcePathParam.Value = string.IsNullOrWhiteSpace(sourcePath) ? (object)DBNull.Value : sourcePath;
                    updatedUtc.Value = updatedText;
                    command.ExecuteNonQuery();
                    written++;
                }

                transaction.Commit();
            }

            return written;
        }

        public List<DailyBar> LoadBars(string asset)
        {
            string normalizedAsset = NormalizeAsset(asset);
            Dictionary<DateTime, DailyBar> rows = new Dictionary<DateTime, DailyBar>();

            if (string.IsNullOrWhiteSpace(normalizedAsset))
            {
                return rows.Values.OrderBy(x => x.Date).ToList();
            }

            using (SQLiteConnection connection = OpenConnection())
            {
                foreach (string candidateAsset in AssetCandidates(normalizedAsset))
                {
                    using (SQLiteCommand command = connection.CreateCommand())
                    {
                        command.CommandText =
                            "SELECT asset, trade_date, open, high, low, close, volume, quantity " +
                            "FROM daily_bars WHERE asset = @asset ORDER BY trade_date ASC;";
                        command.Parameters.AddWithValue("@asset", candidateAsset);

                        using (SQLiteDataReader reader = command.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                DailyBar bar = new DailyBar();
                                bar.Asset = normalizedAsset;
                                bar.Date = ReadDate(reader, 1);

                                if (bar.Date == DateTime.MinValue || rows.ContainsKey(bar.Date.Date))
                                {
                                    continue;
                                }

                                bar.Open = Convert.ToDecimal(reader.GetDouble(2));
                                bar.High = Convert.ToDecimal(reader.GetDouble(3));
                                bar.Low = Convert.ToDecimal(reader.GetDouble(4));
                                bar.Close = Convert.ToDecimal(reader.GetDouble(5));
                                bar.Volume = reader.IsDBNull(6) ? (decimal?)null : Convert.ToDecimal(reader.GetDouble(6));
                                bar.Quantity = reader.IsDBNull(7) ? (decimal?)null : Convert.ToDecimal(reader.GetDouble(7));
                                rows[bar.Date.Date] = bar;
                            }
                        }
                    }
                }
            }

            return rows.Values.OrderBy(x => x.Date).ToList();
        }

        private SQLiteConnection OpenConnection()
        {
            string directory = Path.GetDirectoryName(_databasePath);

            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            SQLiteConnection connection = new SQLiteConnection("Data Source=" + _databasePath + ";Version=3;Pooling=True;");
            connection.Open();
            Initialize(connection);
            return connection;
        }

        private static void Initialize(SQLiteConnection connection)
        {
            ExecuteNonQuery(connection, "PRAGMA journal_mode=WAL;");
            ExecuteNonQuery(connection, "PRAGMA synchronous=NORMAL;");
            ExecuteNonQuery(connection, "CREATE TABLE IF NOT EXISTS daily_bars (asset TEXT NOT NULL, trade_date TEXT NOT NULL, open REAL NOT NULL, high REAL NOT NULL, low REAL NOT NULL, close REAL NOT NULL, volume REAL, quantity REAL, source_path TEXT, updated_utc TEXT NOT NULL, PRIMARY KEY(asset, trade_date));");
            ExecuteNonQuery(connection, "CREATE INDEX IF NOT EXISTS idx_daily_bars_asset_date ON daily_bars(asset, trade_date);");
        }

        private static void ExecuteNonQuery(SQLiteConnection connection, string sql)
        {
            using (SQLiteCommand command = connection.CreateCommand())
            {
                command.CommandText = sql;
                command.ExecuteNonQuery();
            }
        }

        private static DateTime ReadDate(SQLiteDataReader reader, int ordinal)
        {
            if (reader.IsDBNull(ordinal))
            {
                return DateTime.MinValue;
            }

            string text = reader.GetString(ordinal);
            DateTime parsed;

            if (DateTime.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed))
            {
                return parsed.Date;
            }

            if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed))
            {
                return parsed.Date;
            }

            return DateTime.MinValue;
        }

        private static string NormalizeAsset(string asset)
        {
            return string.IsNullOrWhiteSpace(asset) ? string.Empty : asset.Trim().ToUpperInvariant();
        }

        private static IEnumerable<string> AssetCandidates(string asset)
        {
            string normalized = NormalizeAsset(asset);

            if (string.IsNullOrWhiteSpace(normalized))
            {
                yield break;
            }

            yield return normalized;

            if (normalized.EndsWith("_F_0", StringComparison.OrdinalIgnoreCase) && normalized.Length > 4)
            {
                string legacy = normalized.Substring(0, normalized.Length - 4);

                if (!string.IsNullOrWhiteSpace(legacy))
                {
                    yield return legacy;
                }
            }
            else
            {
                yield return normalized + "_F_0";
            }
        }
    }
}
