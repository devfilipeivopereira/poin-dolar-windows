# Arquitetura

Fluxo nativo:

```text
Profit Pro
  -> RTDTrading.RTDServer
  -> RtdProbeService em thread STA dedicada
  -> MarketState
  -> LatestSnapshotBuffer
  -> DispatcherTimer WPF
  -> DOM / Tape / Niveis / Grafico
```

Regras aplicadas desde o primeiro marco:

- RTD roda fora da thread de UI.
- A UI nao recebe fila de ticks; ela le sempre o snapshot mais recente.
- A thread RTD nao grava SQLite e nao atualiza controles WPF.
- O app assina os campos configurados em `Rtd.Fields`.
- Logs por tick ficam desligados por padrao.

O historico diario e carregado por CSV e alimenta o motor quant nativo. O RTD preenche abertura, maxima, minima, ultimo preco, MED/VWAP, volume, bid/ask e volumes de book.

Telas principais:

- `DOM`: ladder por tick com tape e tags por preco.
- `Niveis`: pontos brutos e clusters.
- `Abertura`, `POC`, `Variacao %`: mapas derivados.
- `Volume Profile`: proxy por distribuicao de volume diario.
- `Confluencia`: score por proximidade, diversidade de fontes e estabilidade.
- `Backtest Proxy`: toque/reversao dos desvios historicos.
- `Grafico`: desenho WPF customizado, com candles e niveis.
