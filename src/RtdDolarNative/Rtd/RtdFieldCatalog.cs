using System.Collections.Generic;
using System.Linq;

namespace RtdDolarNative.Rtd
{
    public sealed class RtdFieldInfo
    {
        public RtdFieldInfo(string code, string label, bool defaultLive)
        {
            Code = code;
            Label = label;
            DefaultLive = defaultLive;
        }

        public string Code { get; private set; }
        public string Label { get; private set; }
        public bool DefaultLive { get; private set; }
    }

    public static class RtdFieldCatalog
    {
        public static readonly IReadOnlyList<RtdFieldInfo> Fields = new[]
        {
            new RtdFieldInfo("DAT", "Data", true),
            new RtdFieldInfo("HOR", "Hora", true),
            new RtdFieldInfo("ULT", "Ultimo", true),
            new RtdFieldInfo("ABE", "Abertura", true),
            new RtdFieldInfo("MAX", "Maximo", true),
            new RtdFieldInfo("MIN", "Minimo", true),
            new RtdFieldInfo("FEC", "Fechamento Anterior", true),
            new RtdFieldInfo("VAR", "Variacao", true),
            new RtdFieldInfo("VARPTS", "Variacao em pontos", true),
            new RtdFieldInfo("MED", "Media", true),
            new RtdFieldInfo("NEG", "Negocios", true),
            new RtdFieldInfo("QUL", "Quantidade do ultimo negocio", true),
            new RtdFieldInfo("QTT", "Quantidade", true),
            new RtdFieldInfo("VOL", "Volume", true),
            new RtdFieldInfo("OCP", "Oferta de compra", true),
            new RtdFieldInfo("OVD", "Oferta de venda", true),
            new RtdFieldInfo("VOC", "Volume oferta compra", true),
            new RtdFieldInfo("VOV", "Volume oferta venda", true),
            new RtdFieldInfo("AJU", "Ajuste", true),
            new RtdFieldInfo("AJA", "Ajuste anterior", true),
            new RtdFieldInfo("103", "TR - Saldo acumulado de agressao", true),
            new RtdFieldInfo("98", "TR - Volume de agressao - compra", true),
            new RtdFieldInfo("100", "TR - Volume de agressao - saldo", true),
            new RtdFieldInfo("99", "TR - Volume de agressao - venda", true),
            new RtdFieldInfo("67", "VWAP", true),
            new RtdFieldInfo("VPJ", "Volume projetado", true),
            new RtdFieldInfo("VEN", "Vencimento", true),
            new RtdFieldInfo("VAL", "Validade", true),
            new RtdFieldInfo("CAB", "Contratos abertos", true),
            new RtdFieldInfo("EST", "Estado atual", true)
        };

        public static readonly IReadOnlyList<string> DefaultLiveFields = Fields
            .Where(x => x.DefaultLive)
            .Select(x => x.Code)
            .ToList();
    }
}
