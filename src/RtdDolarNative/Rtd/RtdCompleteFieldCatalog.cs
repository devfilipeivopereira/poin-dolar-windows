using System;
using System.Collections.Generic;
using System.Linq;

namespace RtdDolarNative.Rtd
{
    public sealed class RtdCompleteFieldInfo
    {
        public RtdCompleteFieldInfo(string code, string label, string group, string subgroup, string displayKind, int priority, bool defaultLive)
        {
            Code = code;
            Label = label;
            Group = group;
            Subgroup = subgroup;
            DisplayKind = displayKind;
            Priority = priority;
            DefaultLive = defaultLive;
        }

        public string Code { get; private set; }
        public string Label { get; private set; }
        public string Group { get; private set; }
        public string Subgroup { get; private set; }
        public string DisplayKind { get; private set; }
        public int Priority { get; private set; }
        public bool DefaultLive { get; private set; }
    }

    public static class RtdCompleteFieldCatalog
    {
        public const string GroupMarket = "Mercado";
        public const string GroupContract = "Contrato";
        public const string GroupPerformance = "Performance";
        public const string GroupOptions = "Opcoes";
        public const string GroupVolatility = "Volatilidade";
        public const string GroupTechnical = "Tecnicos";
        public const string GroupFlow = "Fluxo/Agressao";
        public const string GroupVwap = "VWAP/Medias";
        public const string GroupDiagnostics = "Diagnostico";

        public static readonly IReadOnlyList<string> Groups = new[]
        {
            GroupMarket,
            GroupContract,
            GroupPerformance,
            GroupOptions,
            GroupVolatility,
            GroupTechnical,
            GroupFlow,
            GroupVwap,
            GroupDiagnostics
        };

        private static readonly HashSet<string> DefaultLiveCodes = new HashSet<string>(RtdFieldCatalog.DefaultLiveFields, StringComparer.OrdinalIgnoreCase);

        public static readonly IReadOnlyList<RtdCompleteFieldInfo> Fields = new[]
        {
            Field("DAT", "Data", GroupMarket, "Tempo", "Date", 1),
            Field("HOR", "Hora", GroupMarket, "Tempo", "Time", 2),
            Field("ULT", "Ultimo", GroupMarket, "Preco", "Price", 3),
            Field("ABE", "Abertura", GroupMarket, "Preco", "Price", 4),
            Field("MAX", "Maximo", GroupMarket, "Preco", "Price", 5),
            Field("MIN", "Minimo", GroupMarket, "Preco", "Price", 6),
            Field("FEC", "Fechamento Anterior", GroupMarket, "Preco", "Price", 7),
            Field("PEX", "Strike", GroupContract, "Contrato", "Price", 8),
            Field("VAR", "Variacao", GroupPerformance, "Dia", "Percent", 9),
            Field("VARPTS", "Variacao(pts)", GroupPerformance, "Dia", "Price", 10),
            Field("MED", "Media", GroupMarket, "Preco", "Price", 11),
            Field("NEG", "Negocios", GroupMarket, "Volume", "Quantity", 12),
            Field("QUL", "QUL", GroupMarket, "Volume", "Quantity", 13),
            Field("QTT", "Quantidade", GroupMarket, "Volume", "Quantity", 14),
            Field("VOL", "Volume", GroupMarket, "Volume", "Currency", 15),
            Field("OCP", "Of. Compra", GroupMarket, "Book topo", "Price", 16),
            Field("OVD", "Of. Venda", GroupMarket, "Book topo", "Price", 17),
            Field("VOC", "VOC", GroupMarket, "Book topo", "Quantity", 18),
            Field("VOV", "VOV", GroupMarket, "Book topo", "Quantity", 19),
            Field("AJU", "Ajuste", GroupMarket, "Preco", "Price", 20),
            Field("AJA", "Aj. Anterior", GroupMarket, "Preco", "Price", 21),
            Field("PRT", "Preco Teorico", GroupContract, "Leilao", "Price", 22),
            Field("QTE", "Qtd. Teorica", GroupContract, "Leilao", "Quantity", 23),
            Field("VPJ", "Volume Projetado", GroupMarket, "Volume", "Quantity", 24),
            Field("SEM", "Semana", GroupPerformance, "Periodo", "Percent", 25),
            Field("MES", "Mes", GroupPerformance, "Periodo", "Percent", 26),
            Field("3M", "3 meses", GroupPerformance, "Periodo", "Percent", 27),
            Field("6M", "6 meses", GroupPerformance, "Periodo", "Percent", 28),
            Field("12M", "12 meses", GroupPerformance, "Periodo", "Percent", 29),
            Field("ANO", "Ano", GroupPerformance, "Periodo", "Percent", 30),
            Field("TRIM", "Trimestre", GroupPerformance, "Periodo", "Percent", 31),
            Field("SEMES", "Semestre", GroupPerformance, "Periodo", "Percent", 32),
            Field("VEN", "Vencimento", GroupContract, "Contrato", "Date", 33),
            Field("VAL", "Validade", GroupContract, "Contrato", "Date", 34),
            Field("CAB", "Cont. Abertos", GroupContract, "Contrato", "Quantity", 35),
            Field("EST", "Estado Atual", GroupContract, "Contrato", "Text", 36),
            Field("BLACK", "Black Scholes", GroupOptions, "Modelo", "Number", 37),
            Field("IMPVT", "Volt. Implicita", GroupOptions, "Volatilidade", "Percent", 38),
            Field("DELTA", "Delta", GroupOptions, "Gregas", "Number", 39),
            Field("GAMA", "Gama", GroupOptions, "Gregas", "Number", 40),
            Field("THETA", "Theta", GroupOptions, "Gregas", "Number", 41),
            Field("RHO", "Rho", GroupOptions, "Gregas", "Number", 42),
            Field("VEGA", "Vega", GroupOptions, "Gregas", "Number", 43),
            Field("VIA", "VI Ask", GroupOptions, "Volatilidade", "Percent", 44),
            Field("VIB", "VI Bid", GroupOptions, "Volatilidade", "Percent", 45),
            Field("DOBRAR", "Dobrar %", GroupOptions, "Modelo", "Percent", 46),
            Field("VIVH", "VI / VH", GroupOptions, "Volatilidade", "Number", 47),
            Field("VINT", "Valor Intrinseco", GroupOptions, "Preco", "Price", 48),
            Field("VEXT", "Valor Extrinseco", GroupOptions, "Preco", "Price", 49),
            Field("8", "Acumulacao/Distribuicao", GroupFlow, "Fluxo", "Number", 50),
            Field("88", "Acumulacao/Distribuicao Williams", GroupFlow, "Fluxo", "Number", 51),
            Field("124", "Adaptive Moving Average (AMA)", GroupVwap, "Medias", "Number", 52),
            Field("22", "ADX", GroupTechnical, "Tendencia", "Number", 53),
            Field("126", "Afastamento Medio", GroupVolatility, "Dispersao", "Number", 54),
            Field("16", "Arms Ease of Movement", GroupFlow, "Fluxo", "Number", 55),
            Field("51", "Aroon Linha", GroupTechnical, "Tendencia", "Number", 56),
            Field("49", "Aroon Oscilador", GroupTechnical, "Tendencia", "Number", 57),
            Field("27", "Balanca de Poder", GroupFlow, "Fluxo", "Number", 58),
            Field("12", "Bandas de Bollinger", GroupVolatility, "Bandas", "Number", 59),
            Field("32", "Bear Power", GroupTechnical, "Forca", "Number", 60),
            Field("41", "Bollinger b%", GroupVolatility, "Bandas", "Percent", 61),
            Field("42", "Bollinger Band Width", GroupVolatility, "Bandas", "Number", 62),
            Field("31", "Bull Power", GroupTechnical, "Forca", "Number", 63),
            Field("73", "Canal Donchian", GroupVolatility, "Canais", "Number", 64),
            Field("82", "Candle Code", GroupDiagnostics, "Codigo", "Text", 65),
            Field("5", "CCI Linha", GroupTechnical, "Osciladores", "Number", 66),
            Field("10", "Chaikin Money Flow", GroupFlow, "Fluxo", "Number", 67),
            Field("11", "Desvio Padrao", GroupTechnical, "Dispersao", "Number", 68),
            Field("18", "DI+/DI-", GroupTechnical, "Tendencia", "Number", 69),
            Field("38", "DI+/DI-/ADX", GroupTechnical, "Tendencia", "Number", 70),
            Field("40", "Didi Index", GroupVwap, "Medias", "Number", 71),
            Field("204", "Dividend Yield", GroupContract, "Fundamentos", "Percent", 72),
            Field("125", "DT Oscillator", GroupTechnical, "Osciladores", "Number", 73),
            Field("20", "Envelope", GroupVolatility, "Bandas", "Number", 74),
            Field("14", "Estocastico Lento", GroupTechnical, "Osciladores", "Number", 75),
            Field("15", "Estocastico Pleno", GroupTechnical, "Osciladores", "Number", 76),
            Field("13", "Estocastico Rapido", GroupTechnical, "Osciladores", "Number", 77),
            Field("383", "Estudo", GroupDiagnostics, "Geral", "Text", 78),
            Field("29", "Force Index", GroupFlow, "Fluxo", "Number", 79),
            Field("78", "Frasson ATR", GroupVolatility, "ATR", "Number", 80),
            Field("79", "Frasson VH", GroupVolatility, "Volatilidade", "Number", 81),
            Field("63", "Fura-Chao", GroupTechnical, "Tendencia", "Number", 82),
            Field("62", "Fura-Teto", GroupTechnical, "Tendencia", "Number", 83),
            Field("80", "Highest", GroupTechnical, "Extremos", "Price", 84),
            Field("48", "HiLo Activator", GroupTechnical, "Tendencia", "Number", 85),
            Field("118", "Hull Moving Average (HMA)", GroupVwap, "Medias", "Number", 86),
            Field("128", "IFH (HSI)", GroupDiagnostics, "Nelogica", "Number", 87),
            Field("1", "IFR (RSI)", GroupTechnical, "Osciladores", "Number", 88),
            Field("68", "IFR (RSI) Estocastico", GroupTechnical, "Osciladores", "Number", 89),
            Field("52", "Keltner Channels", GroupVolatility, "Canais", "Number", 90),
            Field("92", "KVO Histograma", GroupTechnical, "Osciladores", "Number", 91),
            Field("91", "KVO Linha", GroupTechnical, "Osciladores", "Number", 92),
            Field("93", "KVO Linha & Histograma", GroupTechnical, "Osciladores", "Number", 93),
            Field("101_", "Campo 101_", GroupDiagnostics, "Geral", "Raw", 94),
            Field("75", "Lowest", GroupTechnical, "Extremos", "Price", 95),
            Field("6", "MACD Histograma", GroupTechnical, "Osciladores", "Number", 96),
            Field("7", "MACD Linha", GroupTechnical, "Osciladores", "Number", 97),
            Field("74", "MACD Linha & Histograma", GroupTechnical, "Osciladores", "Number", 98),
            Field("50", "Market Facilitation Index", GroupFlow, "Fluxo", "Number", 99),
            Field("76", "Momento Estocastico", GroupTechnical, "Osciladores", "Number", 100),
            Field("33", "Momentum", GroupTechnical, "Osciladores", "Number", 101),
            Field("19", "Money Flow", GroupFlow, "Fluxo", "Number", 102),
            Field("90", "Money Flow Index", GroupFlow, "Fluxo", "Number", 103),
            Field("3", "Media Movel", GroupVwap, "Medias", "Number", 104),
            Field("84", "Nelogica - Bottom Finder", GroupDiagnostics, "Nelogica", "Number", 105),
            Field("85", "Nelogica - Pullback Finder", GroupDiagnostics, "Nelogica", "Number", 106),
            Field("185", "Nelogica - Weis Wave", GroupDiagnostics, "Nelogica", "Number", 107),
            Field("4", "OBV", GroupFlow, "Fluxo", "Number", 108),
            Field("121", "OBV Ponderado", GroupFlow, "Fluxo", "Number", 109),
            Field("64", "On-Balance True Range", GroupFlow, "Fluxo", "Number", 110),
            Field("9", "Oscilador Chaikin", GroupFlow, "Fluxo", "Number", 111),
            Field("37", "Oscilador de Precos", GroupTechnical, "Osciladores", "Number", 112),
            Field("86", "Prior Cote", GroupDiagnostics, "Geral", "Price", 113),
            Field("190", "Prior Cote Ajuste", GroupDiagnostics, "Geral", "Price", 114),
            Field("102", "Rafi", GroupTechnical, "Tendencia", "Number", 115),
            Field("53", "Ravi", GroupTechnical, "Tendencia", "Number", 116),
            Field("123", "RenkoV2", GroupDiagnostics, "Geral", "Number", 117),
            Field("23", "ROC Linha", GroupTechnical, "Osciladores", "Number", 118),
            Field("21", "SAR Parabolico", GroupTechnical, "Tendencia", "Number", 119),
            Field("66", "Stop ATR", GroupVolatility, "ATR", "Number", 120),
            Field("57", "Stop SafeZone Downtrend", GroupVolatility, "ATR", "Number", 121),
            Field("56", "Stop SafeZone Uptrend", GroupVolatility, "ATR", "Number", 122),
            Field("97", "Tendencia Preco/Volume", GroupFlow, "Fluxo", "Number", 123),
            Field("89", "Tillson's T3 Moving Average", GroupVwap, "Medias", "Number", 124),
            Field("184", "TR - Histograma de Agressao", GroupFlow, "Agressao", "Number", 125),
            Field("103", "TR - Saldo Acumulado de Agressao", GroupFlow, "Agressao", "Number", 126),
            Field("98", "TR - Volume de Agressao - Compra", GroupFlow, "Agressao", "Number", 127),
            Field("100", "TR - Volume de Agressao - Saldo", GroupFlow, "Agressao", "Number", 128),
            Field("99", "TR - Volume de Agressao - Venda", GroupFlow, "Agressao", "Number", 129),
            Field("30", "TRIX", GroupTechnical, "Osciladores", "Number", 130),
            Field("34", "TRIXM", GroupTechnical, "Osciladores", "Number", 131),
            Field("17", "True Range", GroupVolatility, "ATR", "Number", 132),
            Field("267", "TWAP", GroupVwap, "Medias", "Number", 133),
            Field("45", "Volatilidade Historica", GroupVolatility, "Historica", "Percent", 134),
            Field("120", "Volatilidade Historica Media", GroupVolatility, "Historica", "Percent", 135),
            Field("387", "Volatilidade Implicita", GroupOptions, "Volatilidade", "Percent", 136),
            Field("81", "Volatilidade Implicita - Opcoes", GroupOptions, "Volatilidade", "Percent", 137),
            Field("55", "VSS", GroupVolatility, "Volatilidade", "Number", 138),
            Field("67", "VWAP", GroupVwap, "VWAP", "Price", 139),
            Field("262", "VWAP Data", GroupVwap, "VWAP", "Price", 140),
            Field("131", "VWAP Mensal", GroupVwap, "VWAP", "Price", 141),
            Field("130", "VWAP Semanal", GroupVwap, "VWAP", "Price", 142),
            Field("122", "VWMA", GroupVwap, "Medias", "Number", 143),
            Field("24", "Williams %R", GroupTechnical, "Osciladores", "Number", 144),
            Field("408", "WMA - Media Movel Ponderada", GroupVwap, "Medias", "Number", 145)
        };

        public static readonly IReadOnlyList<string> Codes = Fields
            .Select(x => x.Code)
            .ToList();

        public static RtdCompleteFieldInfo Find(string code)
        {
            if (string.IsNullOrWhiteSpace(code))
            {
                return null;
            }

            string normalized = code.Trim().ToUpperInvariant();
            return Fields.FirstOrDefault(x => string.Equals(x.Code, normalized, StringComparison.OrdinalIgnoreCase));
        }

        public static IReadOnlyList<RtdCompleteFieldInfo> ForGroups(IEnumerable<string> groups)
        {
            HashSet<string> active = new HashSet<string>(groups ?? new string[0], StringComparer.OrdinalIgnoreCase);

            return Fields
                .Where(x => active.Contains(x.Group))
                .OrderBy(x => x.Priority)
                .ToList();
        }

        private static RtdCompleteFieldInfo Field(string code, string label, string group, string subgroup, string displayKind, int priority)
        {
            string normalizedCode = string.IsNullOrWhiteSpace(code) ? string.Empty : code.Trim().ToUpperInvariant();
            return new RtdCompleteFieldInfo(normalizedCode, label, group, subgroup, displayKind, priority, DefaultLiveCodes.Contains(normalizedCode));
        }
    }
}
