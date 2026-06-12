using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Globalization;
using System.IO;

namespace RtdDolarNative.Opportunities
{
    public sealed class OpportunityJournalSqliteStore : IDisposable
    {
        private readonly string _databasePath;

        public OpportunityJournalSqliteStore(string databasePath)
        {
            _databasePath = databasePath;
            EnsureDatabase();
        }

        public string DatabasePath
        {
            get { return _databasePath; }
        }

        public OpportunityKnowledgeCard Upsert(OpportunityKnowledgeCard card, TimeSpan dedupeWindow, decimal tickSize)
        {
            if (card == null)
            {
                return null;
            }

            card.Normalize(tickSize);
            TimeSpan effectiveWindow = dedupeWindow <= TimeSpan.Zero ? TimeSpan.FromMinutes(2) : dedupeWindow;
            DateTime sinceUtc = card.AsOfUtc.Add(-effectiveWindow);

            using (SQLiteConnection connection = OpenConnection())
            {
                OpportunityKnowledgeCard existing = LoadByDedupeKey(connection, card.DedupeKey, sinceUtc);

                if (existing != null)
                {
                    card.Id = existing.Id;
                    card.CreatedAtUtc = existing.CreatedAtUtc;
                    card.SeenCount = existing.SeenCount + 1;
                    card.LastSeenUtc = MaxUtc(existing.LastSeenUtc, card.AsOfUtc);
                    card.Normalize(tickSize);
                    Update(connection, card);
                    return card;
                }

                Insert(connection, card);
                return card;
            }
        }

        public List<OpportunityKnowledgeCard> LoadRecent(string asset, string robustness, string direction, DateTime sinceUtc, int limit)
        {
            List<OpportunityKnowledgeCard> rows = new List<OpportunityKnowledgeCard>();

            using (SQLiteConnection connection = OpenConnection())
            using (SQLiteCommand command = connection.CreateCommand())
            {
                List<string> where = new List<string>();
                where.Add("as_of_utc >= @since");
                command.Parameters.AddWithValue("@since", ToSqlUtc(sinceUtc));

                if (!IsAny(asset))
                {
                    where.Add("asset = @asset");
                    command.Parameters.AddWithValue("@asset", asset.Trim().ToUpperInvariant());
                }

                if (!IsAny(robustness))
                {
                    where.Add("robustness = @robustness");
                    command.Parameters.AddWithValue("@robustness", robustness.Trim());
                }

                if (!IsAny(direction))
                {
                    where.Add("direction = @direction");
                    command.Parameters.AddWithValue("@direction", direction.Trim());
                }

                command.Parameters.AddWithValue("@limit", Math.Max(1, limit));
                command.CommandText =
                    "SELECT id, item_type, dedupe_key, asset, as_of_utc, created_at_utc, last_seen_utc, seen_count, setup, direction, price, score, robustness, confidence, data_quality, source_kind, source_key, reasons, tags, level_name, level_price, snapshot_age_seconds, flow_delta, cumulative_delta, imbalance, expectancy_points, profit_factor, risk_reward " +
                    "FROM opportunity_cards WHERE " + string.Join(" AND ", where.ToArray()) + " " +
                    "ORDER BY last_seen_utc DESC, score DESC LIMIT @limit;";

                using (SQLiteDataReader reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        rows.Add(ReadCard(reader));
                    }
                }
            }

            return rows;
        }

        public void Dispose()
        {
        }

        private OpportunityKnowledgeCard LoadByDedupeKey(SQLiteConnection connection, string dedupeKey, DateTime sinceUtc)
        {
            using (SQLiteCommand command = connection.CreateCommand())
            {
                command.CommandText =
                    "SELECT id, item_type, dedupe_key, asset, as_of_utc, created_at_utc, last_seen_utc, seen_count, setup, direction, price, score, robustness, confidence, data_quality, source_kind, source_key, reasons, tags, level_name, level_price, snapshot_age_seconds, flow_delta, cumulative_delta, imbalance, expectancy_points, profit_factor, risk_reward " +
                    "FROM opportunity_cards WHERE dedupe_key = @dedupe AND last_seen_utc >= @since ORDER BY last_seen_utc DESC LIMIT 1;";
                command.Parameters.AddWithValue("@dedupe", dedupeKey);
                command.Parameters.AddWithValue("@since", ToSqlUtc(sinceUtc));

                using (SQLiteDataReader reader = command.ExecuteReader())
                {
                    return reader.Read() ? ReadCard(reader) : null;
                }
            }
        }

        private void Insert(SQLiteConnection connection, OpportunityKnowledgeCard card)
        {
            using (SQLiteCommand command = connection.CreateCommand())
            {
                command.CommandText =
                    "INSERT INTO opportunity_cards(id, item_type, dedupe_key, asset, as_of_utc, created_at_utc, last_seen_utc, seen_count, setup, direction, price, score, robustness, confidence, data_quality, source_kind, source_key, reasons, tags, level_name, level_price, snapshot_age_seconds, flow_delta, cumulative_delta, imbalance, expectancy_points, profit_factor, risk_reward) " +
                    "VALUES(@id, @item_type, @dedupe_key, @asset, @as_of_utc, @created_at_utc, @last_seen_utc, @seen_count, @setup, @direction, @price, @score, @robustness, @confidence, @data_quality, @source_kind, @source_key, @reasons, @tags, @level_name, @level_price, @snapshot_age_seconds, @flow_delta, @cumulative_delta, @imbalance, @expectancy_points, @profit_factor, @risk_reward);";
                BindCard(command, card);
                command.ExecuteNonQuery();
            }
        }

        private void Update(SQLiteConnection connection, OpportunityKnowledgeCard card)
        {
            using (SQLiteCommand command = connection.CreateCommand())
            {
                command.CommandText =
                    "UPDATE opportunity_cards SET item_type=@item_type, dedupe_key=@dedupe_key, asset=@asset, as_of_utc=@as_of_utc, created_at_utc=@created_at_utc, last_seen_utc=@last_seen_utc, seen_count=@seen_count, setup=@setup, direction=@direction, price=@price, score=@score, robustness=@robustness, confidence=@confidence, data_quality=@data_quality, source_kind=@source_kind, source_key=@source_key, reasons=@reasons, tags=@tags, level_name=@level_name, level_price=@level_price, snapshot_age_seconds=@snapshot_age_seconds, flow_delta=@flow_delta, cumulative_delta=@cumulative_delta, imbalance=@imbalance, expectancy_points=@expectancy_points, profit_factor=@profit_factor, risk_reward=@risk_reward WHERE id=@id;";
                BindCard(command, card);
                command.ExecuteNonQuery();
            }
        }

        private static void BindCard(SQLiteCommand command, OpportunityKnowledgeCard card)
        {
            command.Parameters.AddWithValue("@id", card.Id.ToString("D"));
            command.Parameters.AddWithValue("@item_type", card.ItemType);
            command.Parameters.AddWithValue("@dedupe_key", card.DedupeKey);
            command.Parameters.AddWithValue("@asset", card.Asset);
            command.Parameters.AddWithValue("@as_of_utc", ToSqlUtc(card.AsOfUtc));
            command.Parameters.AddWithValue("@created_at_utc", ToSqlUtc(card.CreatedAtUtc));
            command.Parameters.AddWithValue("@last_seen_utc", ToSqlUtc(card.LastSeenUtc));
            command.Parameters.AddWithValue("@seen_count", card.SeenCount);
            command.Parameters.AddWithValue("@setup", card.Setup);
            command.Parameters.AddWithValue("@direction", card.Direction);
            command.Parameters.AddWithValue("@price", DecimalToDouble(card.Price));
            command.Parameters.AddWithValue("@score", card.Score);
            command.Parameters.AddWithValue("@robustness", card.Robustness);
            command.Parameters.AddWithValue("@confidence", card.Confidence);
            command.Parameters.AddWithValue("@data_quality", card.DataQuality);
            command.Parameters.AddWithValue("@source_kind", card.SourceKind);
            command.Parameters.AddWithValue("@source_key", card.SourceKey);
            command.Parameters.AddWithValue("@reasons", card.Reasons);
            command.Parameters.AddWithValue("@tags", card.Tags);
            command.Parameters.AddWithValue("@level_name", NullIfEmpty(card.LevelName));
            command.Parameters.AddWithValue("@level_price", NullableDecimal(card.LevelPrice));
            command.Parameters.AddWithValue("@snapshot_age_seconds", NullableDouble(card.SnapshotAgeSeconds));
            command.Parameters.AddWithValue("@flow_delta", NullableDecimal(card.FlowDelta));
            command.Parameters.AddWithValue("@cumulative_delta", NullableDecimal(card.CumulativeDelta));
            command.Parameters.AddWithValue("@imbalance", NullableDecimal(card.Imbalance));
            command.Parameters.AddWithValue("@expectancy_points", NullableDecimal(card.ExpectancyPoints));
            command.Parameters.AddWithValue("@profit_factor", NullableDouble(card.ProfitFactor));
            command.Parameters.AddWithValue("@risk_reward", NullableDecimal(card.RiskReward));
        }

        private SQLiteConnection OpenConnection()
        {
            EnsureDatabase();
            SQLiteConnection connection = new SQLiteConnection("Data Source=" + _databasePath + ";Version=3;Pooling=True;");
            connection.Open();
            Initialize(connection);
            return connection;
        }

        private void EnsureDatabase()
        {
            string directory = Path.GetDirectoryName(_databasePath);

            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }
        }

        private static void Initialize(SQLiteConnection connection)
        {
            ExecuteNonQuery(connection, "PRAGMA journal_mode=WAL;");
            ExecuteNonQuery(connection, "CREATE TABLE IF NOT EXISTS opportunity_cards (id TEXT NOT NULL PRIMARY KEY, item_type TEXT NOT NULL, dedupe_key TEXT NOT NULL, asset TEXT NOT NULL, as_of_utc TEXT NOT NULL, created_at_utc TEXT NOT NULL, last_seen_utc TEXT NOT NULL, seen_count INTEGER NOT NULL, setup TEXT NOT NULL, direction TEXT NOT NULL, price REAL NOT NULL, score INTEGER NOT NULL, robustness TEXT NOT NULL, confidence TEXT NOT NULL, data_quality TEXT NOT NULL, source_kind TEXT NOT NULL, source_key TEXT NOT NULL, reasons TEXT NOT NULL, tags TEXT NOT NULL, level_name TEXT, level_price REAL, snapshot_age_seconds REAL, flow_delta REAL, cumulative_delta REAL, imbalance REAL, expectancy_points REAL, profit_factor REAL, risk_reward REAL);");
            ExecuteNonQuery(connection, "CREATE INDEX IF NOT EXISTS idx_opportunity_cards_asset_time ON opportunity_cards(asset, as_of_utc);");
            ExecuteNonQuery(connection, "CREATE INDEX IF NOT EXISTS idx_opportunity_cards_robustness ON opportunity_cards(robustness);");
            ExecuteNonQuery(connection, "CREATE INDEX IF NOT EXISTS idx_opportunity_cards_setup_direction ON opportunity_cards(setup, direction);");
            ExecuteNonQuery(connection, "CREATE INDEX IF NOT EXISTS idx_opportunity_cards_dedupe ON opportunity_cards(dedupe_key, last_seen_utc);");
        }

        private static void ExecuteNonQuery(SQLiteConnection connection, string sql)
        {
            using (SQLiteCommand command = connection.CreateCommand())
            {
                command.CommandText = sql;
                command.ExecuteNonQuery();
            }
        }

        private static OpportunityKnowledgeCard ReadCard(SQLiteDataReader reader)
        {
            OpportunityKnowledgeCard card = new OpportunityKnowledgeCard();
            card.Id = Guid.Parse(reader.GetString(0));
            card.ItemType = reader.GetString(1);
            card.DedupeKey = reader.GetString(2);
            card.Asset = reader.GetString(3);
            card.AsOfUtc = ParseUtc(reader.GetString(4));
            card.CreatedAtUtc = ParseUtc(reader.GetString(5));
            card.LastSeenUtc = ParseUtc(reader.GetString(6));
            card.SeenCount = reader.GetInt32(7);
            card.Setup = reader.GetString(8);
            card.Direction = reader.GetString(9);
            card.Price = ReadDecimal(reader, 10);
            card.Score = reader.GetInt32(11);
            card.Robustness = reader.GetString(12);
            card.Confidence = reader.GetString(13);
            card.DataQuality = reader.GetString(14);
            card.SourceKind = reader.GetString(15);
            card.SourceKey = reader.GetString(16);
            card.Reasons = reader.GetString(17);
            card.Tags = reader.GetString(18);
            card.LevelName = reader.IsDBNull(19) ? string.Empty : reader.GetString(19);
            card.LevelPrice = ReadNullableDecimal(reader, 20);
            card.SnapshotAgeSeconds = ReadNullableDouble(reader, 21);
            card.FlowDelta = ReadNullableDecimal(reader, 22);
            card.CumulativeDelta = ReadNullableDecimal(reader, 23);
            card.Imbalance = ReadNullableDecimal(reader, 24);
            card.ExpectancyPoints = ReadNullableDecimal(reader, 25);
            card.ProfitFactor = ReadNullableDouble(reader, 26);
            card.RiskReward = ReadNullableDecimal(reader, 27);
            return card;
        }

        private static bool IsAny(string value)
        {
            return string.IsNullOrWhiteSpace(value) ||
                   string.Equals(value, "Todos", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(value, "Todas", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(value, "-", StringComparison.OrdinalIgnoreCase);
        }

        private static string ToSqlUtc(DateTime value)
        {
            DateTime utc = value.Kind == DateTimeKind.Utc
                ? value
                : value.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(value, DateTimeKind.Utc) : value.ToUniversalTime();
            return utc.ToString("o", CultureInfo.InvariantCulture);
        }

        private static DateTime ParseUtc(string value)
        {
            return DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();
        }

        private static DateTime MaxUtc(DateTime left, DateTime right)
        {
            DateTime l = left.Kind == DateTimeKind.Utc ? left : left.ToUniversalTime();
            DateTime r = right.Kind == DateTimeKind.Utc ? right : right.ToUniversalTime();
            return l >= r ? l : r;
        }

        private static object NullIfEmpty(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? (object)DBNull.Value : value;
        }

        private static object NullableDecimal(decimal? value)
        {
            return value.HasValue ? (object)DecimalToDouble(value.Value) : DBNull.Value;
        }

        private static object NullableDouble(double? value)
        {
            return value.HasValue ? (object)value.Value : DBNull.Value;
        }

        private static decimal ReadDecimal(SQLiteDataReader reader, int ordinal)
        {
            return Convert.ToDecimal(reader.GetDouble(ordinal), CultureInfo.InvariantCulture);
        }

        private static decimal? ReadNullableDecimal(SQLiteDataReader reader, int ordinal)
        {
            return reader.IsDBNull(ordinal) ? (decimal?)null : ReadDecimal(reader, ordinal);
        }

        private static double? ReadNullableDouble(SQLiteDataReader reader, int ordinal)
        {
            return reader.IsDBNull(ordinal) ? (double?)null : reader.GetDouble(ordinal);
        }

        private static double DecimalToDouble(decimal value)
        {
            return decimal.ToDouble(value);
        }
    }
}
