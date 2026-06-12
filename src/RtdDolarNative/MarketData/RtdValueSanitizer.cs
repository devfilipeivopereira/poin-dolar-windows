using System;
using System.Globalization;
using System.Text;

namespace RtdDolarNative.MarketData
{
    public static class RtdValueSanitizer
    {
        private static readonly string[] ErrorPhrases = new[]
        {
            "ATIVO INVALIDO",
            "ATRIBUTO INVALIDO",
            "RTD DESATIVADO",
            "RTD PAUSADO",
            "A JANELA FOI FECHADA",
            "FERRAMENTA INVALIDA",
            "LINHA INVALIDA",
            "INFORMACAO REQUISITADA INVALIDA",
            "COMANDO INVALIDO"
        };

        public static string CleanDisplayText(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            string trimmed = value.Trim();
            return IsRtdErrorText(trimmed) ? string.Empty : trimmed;
        }

        public static bool IsRtdErrorText(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            string folded = Fold(value);

            foreach (string phrase in ErrorPhrases)
            {
                if (folded.IndexOf(phrase, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            return false;
        }

        private static string Fold(string value)
        {
            string normalized = value.Normalize(NormalizationForm.FormD);
            StringBuilder builder = new StringBuilder(normalized.Length);

            foreach (char c in normalized)
            {
                UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(c);
                if (category != UnicodeCategory.NonSpacingMark)
                {
                    builder.Append(char.ToUpperInvariant(c));
                }
            }

            return builder.ToString().Normalize(NormalizationForm.FormC);
        }
    }
}
