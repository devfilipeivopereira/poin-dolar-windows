using System;
using System.Globalization;

namespace RtdDolarNative.MarketData
{
    public static class ValueParser
    {
        private static readonly CultureInfo PtBr = new CultureInfo("pt-BR");
        private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

        public static decimal? ToDecimal(object value)
        {
            if (value == null)
            {
                return null;
            }

            if (value is decimal)
            {
                return (decimal)value;
            }

            if (value is double)
            {
                return Convert.ToDecimal((double)value);
            }

            if (value is float)
            {
                return Convert.ToDecimal((float)value);
            }

            if (value is int)
            {
                return (int)value;
            }

            if (value is long)
            {
                return (long)value;
            }

            string text = value.ToString();

            if (text == null)
            {
                return null;
            }

            text = text.Trim();

            if (string.IsNullOrWhiteSpace(text) || text == "-")
            {
                return null;
            }

            decimal ptBrValue;

            if (decimal.TryParse(text, NumberStyles.Any, PtBr, out ptBrValue))
            {
                return ptBrValue;
            }

            decimal invariantValue;

            if (decimal.TryParse(text, NumberStyles.Any, Invariant, out invariantValue))
            {
                return invariantValue;
            }

            return null;
        }

        public static string ToText(object value)
        {
            return value == null ? null : value.ToString();
        }

        public static object ToJsonValue(object value)
        {
            decimal? number = ToDecimal(value);

            if (number.HasValue)
            {
                return number.Value;
            }

            return ToText(value);
        }
    }
}
