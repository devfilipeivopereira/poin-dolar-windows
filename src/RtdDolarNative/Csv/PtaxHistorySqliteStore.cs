using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Globalization;
using System.IO;
using System.Linq;

namespace RtdDolarNative.Csv
{
    public sealed class PtaxHistoryEntry
    {
        public DateTime TradeDate { get; set; }
        public decimal Value { get; set; }
        public DateTime UpdatedUtc { get; set; }
    }

    public sealed class PtaxHistorySqliteStore
    {
        private readonly string _databasePath;

        public PtaxHistorySqliteStore(string databasePath)
        {
            _databasePath = databasePath;
        }

        public string DatabasePath
        {
            get { return _databasePath; }
        }

        public void Upsert(DateTime tradeDate, decimal value)
        {
            using (SQLiteConnection connection = OpenConnection())
            using (SQLiteCommand command = connection.CreateCommand())
            {
                command.CommandText =
                    "INSERT OR REPLACE INTO ptax_history(trade_date, value, updated_utc) " +
                    "VALUES(@trade_date, @value, @updated_utc);";
                command.Parameters.AddWithValue("@trade_date", tradeDate.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                command.Parameters.AddWithValue("@value", Convert.ToDouble(value));
                command.Parameters.AddWithValue("@updated_utc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
                command.ExecuteNonQuery();
            }
        }

        public PtaxHistoryEntry Load(DateTime tradeDate)
        {
            using (SQLiteConnection connection = OpenConnection())
            using (SQLiteCommand command = connection.CreateCommand())
            {
                command.CommandText =
                    "SELECT trade_date, value, updated_utc " +
                    "FROM ptax_history WHERE trade_date = @trade_date;";
                command.Parameters.AddWithValue("@trade_date", tradeDate.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

                using (SQLiteDataReader reader = command.ExecuteReader())
                {
                    if (!reader.Read())
                    {
                        return null;
                    }

                    return ReadEntry(reader);
                }
            }
        }

        public decimal? LoadValue(DateTime tradeDate)
        {
            PtaxHistoryEntry entry = Load(tradeDate);
            return entry == null ? (decimal?)null : entry.Value;
        }

        public List<PtaxHistoryEntry> LoadAll()
        {
            List<PtaxHistoryEntry> rows = new List<PtaxHistoryEntry>();

            using (SQLiteConnection connection = OpenConnection())
            using (SQLiteCommand command = connection.CreateCommand())
            {
                command.CommandText =
                    "SELECT trade_date, value, updated_utc " +
                    "FROM ptax_history ORDER BY trade_date DESC;";

                using (SQLiteDataReader reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        rows.Add(ReadEntry(reader));
                    }
                }
            }

            return rows
                .Where(x => x != null)
                .OrderByDescending(x => x.TradeDate)
                .ThenByDescending(x => x.UpdatedUtc)
                .ToList();
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
            ExecuteNonQuery(connection, "CREATE TABLE IF NOT EXISTS ptax_history (trade_date TEXT NOT NULL PRIMARY KEY, value REAL NOT NULL, updated_utc TEXT NOT NULL);");
            ExecuteNonQuery(connection, "CREATE INDEX IF NOT EXISTS idx_ptax_history_trade_date ON ptax_history(trade_date DESC);");
        }

        private static void ExecuteNonQuery(SQLiteConnection connection, string sql)
        {
            using (SQLiteCommand command = connection.CreateCommand())
            {
                command.CommandText = sql;
                command.ExecuteNonQuery();
            }
        }

        private static PtaxHistoryEntry ReadEntry(SQLiteDataReader reader)
        {
            PtaxHistoryEntry entry = new PtaxHistoryEntry();
            entry.TradeDate = ReadDate(reader, 0);
            entry.Value = Convert.ToDecimal(reader.GetDouble(1));
            entry.UpdatedUtc = ReadDateTime(reader, 2);
            return entry;
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

        private static DateTime ReadDateTime(SQLiteDataReader reader, int ordinal)
        {
            if (reader.IsDBNull(ordinal))
            {
                return DateTime.MinValue;
            }

            string text = reader.GetString(ordinal);
            DateTime parsed;

            if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out parsed))
            {
                return parsed;
            }

            return DateTime.MinValue;
        }
    }
}
