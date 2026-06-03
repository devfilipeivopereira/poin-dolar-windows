using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using RtdDolarNative.MarketData;

namespace RtdDolarNative.Csv
{
    public sealed class DailyCsvParseResult
    {
        public DailyCsvParseResult()
        {
            Bars = new List<DailyBar>();
            Warnings = new List<string>();
        }

        public List<DailyBar> Bars { get; set; }
        public List<string> Warnings { get; set; }
        public string EncodingName { get; set; }
        public char Delimiter { get; set; }
    }

    public static class DailyCsvParser
    {
        public static DailyCsvParseResult ParseFile(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);
            string encodingName;
            string text = Decode(bytes, out encodingName);
            DailyCsvParseResult result = Parse(text);
            result.EncodingName = encodingName;
            return result;
        }

        public static DailyCsvParseResult Parse(string text)
        {
            DailyCsvParseResult result = new DailyCsvParseResult();

            if (string.IsNullOrWhiteSpace(text))
            {
                result.Warnings.Add("Arquivo CSV vazio.");
                return result;
            }

            char delimiter = DetectDelimiter(text);
            result.Delimiter = delimiter;

            List<string[]> rows = new List<string[]>();
            string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

            foreach (string line in lines)
            {
                if (!string.IsNullOrWhiteSpace(line))
                {
                    rows.Add(SplitCsvLine(line, delimiter).ToArray());
                }
            }

            if (rows.Count == 0)
            {
                result.Warnings.Add("Nenhuma linha valida no CSV.");
                return result;
            }

            bool hasHeader = RowLooksLikeHeader(rows[0]);
            Dictionary<string, int> map = hasHeader ? MapHeader(rows[0]) : PositionalMap(rows[0].Length);
            int start = hasHeader ? 1 : 0;
            Dictionary<DateTime, DailyBar> byDate = new Dictionary<DateTime, DailyBar>();

            for (int i = start; i < rows.Count; i++)
            {
                DailyBar bar;

                if (TryParseRow(rows[i], map, out bar))
                {
                    byDate[bar.Date.Date] = bar;
                }
            }

            result.Bars = byDate.Values.OrderBy(x => x.Date).ToList();

            if (result.Bars.Count < 21)
            {
                result.Warnings.Add("CSV tem menos de 21 pregoes validos.");
            }

            return result;
        }

        private static string Decode(byte[] bytes, out string encodingName)
        {
            string utf8 = new UTF8Encoding(false, true).GetString(bytes);

            if (utf8.IndexOf('\uFFFD') < 0)
            {
                encodingName = "UTF-8";
                return utf8;
            }

            encodingName = "Windows-1252";
            return Encoding.GetEncoding(1252).GetString(bytes);
        }

        private static char DetectDelimiter(string text)
        {
            string sample = string.Join("\n", text.Split('\n').Take(8).ToArray());
            char[] candidates = new[] { ';', ',', '\t' };
            int bestScore = -1;
            char best = ';';

            foreach (char candidate in candidates)
            {
                int score = sample.Count(c => c == candidate);

                if (score > bestScore)
                {
                    bestScore = score;
                    best = candidate;
                }
            }

            return best;
        }

        private static List<string> SplitCsvLine(string line, char delimiter)
        {
            List<string> cells = new List<string>();
            StringBuilder cell = new StringBuilder();
            bool quoted = false;

            for (int i = 0; i < line.Length; i++)
            {
                char ch = line[i];

                if (ch == '"')
                {
                    if (quoted && i + 1 < line.Length && line[i + 1] == '"')
                    {
                        cell.Append('"');
                        i++;
                    }
                    else
                    {
                        quoted = !quoted;
                    }
                }
                else if (ch == delimiter && !quoted)
                {
                    cells.Add(cell.ToString().Trim());
                    cell.Length = 0;
                }
                else
                {
                    cell.Append(ch);
                }
            }

            cells.Add(cell.ToString().Trim());
            return cells;
        }

        private static bool RowLooksLikeHeader(string[] row)
        {
            int hits = 0;

            foreach (string cell in row)
            {
                string n = NormalizeHeader(cell);

                if (n == "data" || n == "date" || n == "abertura" || n == "open" || n == "fechamento" || n == "close" || n == "ativo" || n == "symbol")
                {
                    hits++;
                }
            }

            return hits >= 2;
        }

        private static Dictionary<string, int> MapHeader(string[] row)
        {
            Dictionary<string, int> map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < row.Length; i++)
            {
                string key = CanonicalName(row[i]);

                if (!string.IsNullOrWhiteSpace(key) && !map.ContainsKey(key))
                {
                    map[key] = i;
                }
            }

            return map;
        }

        private static Dictionary<string, int> PositionalMap(int count)
        {
            Dictionary<string, int> map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            if (count >= 8)
            {
                map["asset"] = 0;
                map["date"] = 1;
                map["open"] = 2;
                map["high"] = 3;
                map["low"] = 4;
                map["close"] = 5;
                map["volume"] = 6;
                map["quantity"] = 7;
            }
            else
            {
                map["date"] = 0;
                map["open"] = 1;
                map["high"] = 2;
                map["low"] = 3;
                map["close"] = 4;
                map["volume"] = 5;
                map["quantity"] = 6;
            }

            return map;
        }

        private static bool TryParseRow(string[] row, Dictionary<string, int> map, out DailyBar bar)
        {
            bar = null;
            DateTime date;
            decimal? open = ReadDecimal(row, map, "open");
            decimal? high = ReadDecimal(row, map, "high");
            decimal? low = ReadDecimal(row, map, "low");
            decimal? close = ReadDecimal(row, map, "close");

            if (!TryReadDate(row, map, out date) || !open.HasValue || !high.HasValue || !low.HasValue || !close.HasValue)
            {
                return false;
            }

            if (high.Value <= 0 || low.Value <= 0 || close.Value <= 0 || open.Value <= 0)
            {
                return false;
            }

            bar = new DailyBar();
            bar.Asset = ReadText(row, map, "asset");
            bar.Date = date.Date;
            bar.Open = open.Value;
            bar.High = high.Value;
            bar.Low = low.Value;
            bar.Close = close.Value;
            bar.Volume = ReadDecimal(row, map, "volume");
            bar.Quantity = ReadDecimal(row, map, "quantity");
            return true;
        }

        private static bool TryReadDate(string[] row, Dictionary<string, int> map, out DateTime date)
        {
            date = DateTime.MinValue;
            string text = ReadText(row, map, "date");

            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            string[] formats = new[] { "dd/MM/yyyy", "d/M/yyyy", "yyyy-MM-dd", "dd-MM-yyyy", "yyyyMMdd", "M/d/yyyy" };

            if (DateTime.TryParseExact(text.Trim(), formats, new CultureInfo("pt-BR"), DateTimeStyles.None, out date))
            {
                return true;
            }

            return DateTime.TryParse(text, new CultureInfo("pt-BR"), DateTimeStyles.None, out date) ||
                   DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
        }

        private static decimal? ReadDecimal(string[] row, Dictionary<string, int> map, string key)
        {
            string text = ReadText(row, map, key);
            return ValueParser.ToDecimal(text);
        }

        private static string ReadText(string[] row, Dictionary<string, int> map, string key)
        {
            int index;

            if (!map.TryGetValue(key, out index) || index < 0 || index >= row.Length)
            {
                return null;
            }

            return row[index];
        }

        private static string CanonicalName(string header)
        {
            string n = NormalizeHeader(header);

            if (n == "ativo" || n == "symbol" || n == "ticker")
            {
                return "asset";
            }

            if (n == "data" || n == "date")
            {
                return "date";
            }

            if (n == "abertura" || n == "open")
            {
                return "open";
            }

            if (n == "max" || n == "maxima" || n == "maximo" || n == "high")
            {
                return "high";
            }

            if (n == "min" || n == "minima" || n == "minimo" || n == "low")
            {
                return "low";
            }

            if (n == "fech" || n == "fechamento" || n == "close" || n == "ultimo")
            {
                return "close";
            }

            if (n == "volume" || n == "vol")
            {
                return "volume";
            }

            if (n == "quant" || n == "qty" || n == "trades" || n == "quantidade")
            {
                return "quantity";
            }

            return null;
        }

        private static string NormalizeHeader(string value)
        {
            if (value == null)
            {
                return string.Empty;
            }

            string text = value.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
            StringBuilder sb = new StringBuilder();

            foreach (char ch in text)
            {
                UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(ch);

                if (category != UnicodeCategory.NonSpacingMark && (char.IsLetterOrDigit(ch) || ch == '_'))
                {
                    sb.Append(ch);
                }
            }

            return sb.ToString();
        }
    }
}
