using System;
using System.Collections.Generic;
using System.Linq;
using RtdDolarNative.Quant;

namespace RtdDolarNative.Dom
{
    public static class DomAnnotationFilter
    {
        private const string BaseCategory = "base";
        private const string GarmanCategory = "garman";
        private const string GaussCategory = "gauss";
        private const string StdDevCategory = "stddev";
        private const string GarchCategory = "garch";
        private const string PercentCategory = "percent";
        private const string MaxMin7Category = "maxmin7";
        private const string ProfileCategory = "profile";
        private const string TechnicalCategory = "technical";
        private const string FlowCategory = "flow";

        private static readonly char[] TokenSeparators = new[] { ',', ';', '+', '/', '|', '-', ' ', '\t', '\r', '\n', '(', ')', '[', ']', ':' };

        public static IEnumerable<KeyLevel> Apply(IEnumerable<KeyLevel> levels, DomAnnotationOptions options)
        {
            if (levels == null)
            {
                return Enumerable.Empty<KeyLevel>();
            }

            DomAnnotationOptions effective = options ?? new DomAnnotationOptions();
            return levels.Where(x => IsVisible(x, effective));
        }

        public static bool IsVisible(KeyLevel level, DomAnnotationOptions options)
        {
            if (level == null)
            {
                return false;
            }

            DomAnnotationOptions effective = options ?? new DomAnnotationOptions();
            HashSet<string> categories = Categories(level);

            if (categories.Count == 0)
            {
                categories.Add(BaseCategory);
            }

            foreach (string category in categories)
            {
                if (!IsCategoryVisible(category, effective))
                {
                    return false;
                }
            }

            return true;
        }

        private static HashSet<string> Categories(KeyLevel level)
        {
            HashSet<string> categories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string text = Join(level.Source, level.Layer, level.Tags, level.Evidence, level.Label, level.Type);
            List<string> tokens = Tokens(text);

            if (Contains(text, "MaxMin7"))
            {
                categories.Add(MaxMin7Category);
            }

            if (Contains(text, "GARCH"))
            {
                categories.Add(GarchCategory);
            }

            if (Contains(text, "Garman") || HasToken(tokens, "GK"))
            {
                categories.Add(GarmanCategory);
            }

            if (Contains(text, "Gauss"))
            {
                categories.Add(GaussCategory);
            }

            if (Contains(text, "Desvio") ||
                Contains(text, "StdDev") ||
                Contains(text, "StandardDeviation") ||
                Contains(text, "Standard Deviation"))
            {
                categories.Add(StdDevCategory);
            }

            if (HasToken(tokens, "Percent") ||
                Contains(text, "% D-1"))
            {
                categories.Add(PercentCategory);
            }

            if (HasAnyToken(tokens, "POC", "VAH", "VAL", "HVN", "LVN", "AVWAP", "Profile") ||
                Contains(text, "Volume Profile") ||
                Contains(text, "Anchored VWAP"))
            {
                categories.Add(ProfileCategory);
            }

            if (HasAnyToken(tokens, "Tecnico", "Tecnica", "Tecnica", "EMA9", "EMA21", "EMA50", "SMA20", "SMA50", "Bollinger", "RSI", "MACD", "Momentum") ||
                Contains(text, "Tecnico") ||
                Contains(text, "Tecnica"))
            {
                categories.Add(TechnicalCategory);
            }

            if (HasAnyToken(tokens, "Setups", "Setup", "Flow", "Agressao", "Agressao", "Absorcao", "Absorcao", "Sweep", "Tape") ||
                Contains(text, "Order Flow"))
            {
                categories.Add(FlowCategory);
            }

            if (HasAnyToken(tokens, "RTD", "Open", "Abertura", "Atual", "VWAP", "MED", "Maxima", "Minima", "D1", "D", "CSV", "Round", "SR", "Sigma", "Market", "Fechamento"))
            {
                categories.Add(BaseCategory);
            }

            return categories;
        }

        private static bool IsCategoryVisible(string category, DomAnnotationOptions options)
        {
            if (string.Equals(category, BaseCategory, StringComparison.OrdinalIgnoreCase))
            {
                return options.ShowBase;
            }

            if (string.Equals(category, GarmanCategory, StringComparison.OrdinalIgnoreCase))
            {
                return options.ShowGarman;
            }

            if (string.Equals(category, GaussCategory, StringComparison.OrdinalIgnoreCase))
            {
                return options.ShowGauss;
            }

            if (string.Equals(category, StdDevCategory, StringComparison.OrdinalIgnoreCase))
            {
                return options.ShowStdDev;
            }

            if (string.Equals(category, GarchCategory, StringComparison.OrdinalIgnoreCase))
            {
                return options.ShowGarch;
            }

            if (string.Equals(category, PercentCategory, StringComparison.OrdinalIgnoreCase))
            {
                return options.ShowPercent;
            }

            if (string.Equals(category, MaxMin7Category, StringComparison.OrdinalIgnoreCase))
            {
                return options.ShowMaxMin7;
            }

            if (string.Equals(category, ProfileCategory, StringComparison.OrdinalIgnoreCase))
            {
                return options.ShowProfile;
            }

            if (string.Equals(category, TechnicalCategory, StringComparison.OrdinalIgnoreCase))
            {
                return options.ShowTechnical;
            }

            if (string.Equals(category, FlowCategory, StringComparison.OrdinalIgnoreCase))
            {
                return options.ShowFlow;
            }

            return true;
        }

        private static string Join(params string[] values)
        {
            return string.Join(" ", values.Where(x => !string.IsNullOrWhiteSpace(x)).ToArray());
        }

        private static List<string> Tokens(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return new List<string>();
            }

            return text
                .Split(TokenSeparators, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static bool HasAnyToken(IEnumerable<string> tokens, params string[] candidates)
        {
            return candidates.Any(candidate => HasToken(tokens, candidate));
        }

        private static bool HasToken(IEnumerable<string> tokens, string candidate)
        {
            return tokens.Any(x => string.Equals(x, candidate, StringComparison.OrdinalIgnoreCase));
        }

        private static bool Contains(string text, string value)
        {
            return !string.IsNullOrWhiteSpace(text) &&
                   text.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
