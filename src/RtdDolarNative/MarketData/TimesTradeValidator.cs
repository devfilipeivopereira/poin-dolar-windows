using System;

namespace RtdDolarNative.MarketData
{
    public static class TimesTradeValidator
    {
        public static bool HasValidTradeData(string date, string buyer, string priceText, string quantityText, string seller, string aggressor)
        {
            decimal? price = ValueParser.ToDecimal(priceText);
            decimal? quantity = ValueParser.ToDecimal(quantityText);

            if (!price.HasValue || price.Value <= 0m || !quantity.HasValue || quantity.Value <= 0m)
            {
                return false;
            }

            return !LooksLikePlaceholder(date) &&
                   !LooksLikePlaceholder(buyer) &&
                   !LooksLikePlaceholder(seller) &&
                   !LooksLikePlaceholder(aggressor);
        }

        private static bool LooksLikePlaceholder(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            return value.IndexOf("Ferramenta", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   value.IndexOf("Comando Inv", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   value.IndexOf("Invalid", StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
