using System;
using System.Collections.Generic;

namespace RtdDolarNative.MarketData
{
    public sealed class MarketSnapshot
    {
        public MarketSnapshot()
        {
            LocalTimestamp = DateTimeOffset.Now;
            Status = "starting";
            Rtd = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            Raw = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            FieldUpdatedAt = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
        }

        public DateTimeOffset LocalTimestamp { get; set; }
        public string Asset { get; set; }
        public string Status { get; set; }
        public Dictionary<string, object> Rtd { get; set; }
        public Dictionary<string, string> Raw { get; set; }
        public Dictionary<string, DateTimeOffset> FieldUpdatedAt { get; set; }

        public string DataProfit
        {
            get { return GetText("DAT"); }
        }

        public string HoraProfit
        {
            get { return GetText("HOR"); }
        }

        public decimal? Ultimo
        {
            get { return GetDecimal("ULT"); }
        }

        public decimal? Abertura
        {
            get { return GetDecimal("ABE"); }
        }

        public decimal? Maxima
        {
            get { return GetDecimal("MAX"); }
        }

        public decimal? Minima
        {
            get { return GetDecimal("MIN"); }
        }

        public decimal? FechamentoAnterior
        {
            get { return GetDecimal("FEC"); }
        }

        public decimal? Ajuste
        {
            get
            {
                decimal? aju = GetDecimal("AJU");
                return aju.HasValue && aju.Value != 0m ? aju : GetDecimal("AJA");
            }
        }

        public decimal? AjusteAnterior
        {
            get { return GetDecimal("AJA"); }
        }

        public decimal? Ptax
        {
            get { return GetDecimal("PTAX"); }
        }

        public decimal? Variacao
        {
            get { return GetDecimal("VAR"); }
        }

        public decimal? VariacaoPontos
        {
            get { return GetDecimal("VARPTS"); }
        }

        public decimal? VariacaoPercentual
        {
            get
            {
                if (!Ultimo.HasValue || !FechamentoAnterior.HasValue || FechamentoAnterior.Value == 0m)
                {
                    return null;
                }

                decimal baseClose = FechamentoAnterior.Value;
                decimal change = Ultimo.Value - baseClose;
                return change / baseClose * 100m;
            }
        }

        public decimal? Media
        {
            get { return GetDecimal("MED") ?? GetDecimal("67"); }
        }

        public decimal? AmplitudeDia
        {
            get
            {
                if (!Maxima.HasValue || !Minima.HasValue)
                {
                    return null;
                }

                return Maxima.Value - Minima.Value;
            }
        }

        public decimal? DistanciaAbertura
        {
            get { return DistanciaPara(Abertura); }
        }

        public decimal? DistanciaMinima
        {
            get { return DistanciaPara(Minima); }
        }

        public decimal? DistanciaMaxima
        {
            get { return DistanciaPara(Maxima); }
        }

        public decimal? Volume
        {
            get { return GetDecimal("VOL"); }
        }

        public decimal? Quantidade
        {
            get { return GetDecimal("QTT"); }
        }

        public decimal? QuantidadeUltimoNegocio
        {
            get { return GetDecimal("QUL"); }
        }

        public decimal? Negocios
        {
            get { return GetDecimal("NEG"); }
        }

        public decimal? OfertaCompra
        {
            get { return GetDecimal("OCP"); }
        }

        public decimal? OfertaVenda
        {
            get { return GetDecimal("OVD"); }
        }

        public decimal? VolumeOfertaCompra
        {
            get { return GetDecimal("VOC"); }
        }

        public decimal? VolumeOfertaVenda
        {
            get { return GetDecimal("VOV"); }
        }

        public decimal? VolumeProjetado
        {
            get { return GetDecimal("VPJ"); }
        }

        public MarketSnapshot Clone()
        {
            MarketSnapshot clone = new MarketSnapshot();
            clone.LocalTimestamp = LocalTimestamp;
            clone.Asset = Asset;
            clone.Status = Status;
            clone.Rtd = new Dictionary<string, object>(Rtd, StringComparer.OrdinalIgnoreCase);
            clone.Raw = new Dictionary<string, string>(Raw, StringComparer.OrdinalIgnoreCase);
            clone.FieldUpdatedAt = new Dictionary<string, DateTimeOffset>(FieldUpdatedAt, StringComparer.OrdinalIgnoreCase);
            return clone;
        }

        private decimal? GetDecimal(string field)
        {
            object value;

            if (!Rtd.TryGetValue(field, out value))
            {
                return null;
            }

            return ValueParser.ToDecimal(value);
        }

        private string GetText(string field)
        {
            object value;

            if (!Rtd.TryGetValue(field, out value))
            {
                return null;
            }

            return ValueParser.ToText(value);
        }

        private decimal? DistanciaPara(decimal? referencia)
        {
            if (!Ultimo.HasValue || !referencia.HasValue)
            {
                return null;
            }

            return Ultimo.Value - referencia.Value;
        }
    }
}
