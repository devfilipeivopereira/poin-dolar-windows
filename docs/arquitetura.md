# Arquitetura

Fluxo nativo:

```text
Profit Pro
  -> RTDTrading.RTDServer
  -> RtdProbeService em thread STA dedicada
  -> MarketState por ativo
  -> cache de snapshots por ativo
  -> DispatcherTimer WPF
  -> DOM / Tape / Niveis / Grafico
```

Regras aplicadas desde o primeiro marco:

- RTD roda fora da thread de UI.
- A UI nao recebe fila de ticks; ela le sempre o snapshot mais recente.
- A thread RTD nao grava SQLite e nao atualiza controles WPF.
- O app assina os campos configurados em `Rtd.Fields`.
- O app assina todos os ativos ligados em `Rtd.Assets` e mantem snapshots isolados por ativo.
- O dashboard renderiza o ativo em foco; o tape e filtrado pelo mesmo ativo.
- Logs por tick ficam desligados por padrao.

O historico diario e carregado por CSV e alimenta o motor quant nativo. O carregamento aceita botao, seletor no painel lateral, duplo clique, arrastar/soltar, caminho manual, `Ctrl+O` e auto-load do CSV mais recente em `Downloads\Dados_Dolar` ou `Documentos`. O parser tenta `UTF-8` e cai para `Windows-1252`, cobrindo exportacoes do Profit com acentos no cabecalho. O RTD preenche abertura, maxima, minima, ultimo preco, MED/VWAP, volume, bid/ask e volumes de book.

Telas principais:

- `DOM`: ladder por tick com tape e tags por preco.
- `Niveis`: pontos brutos e clusters.
- `Abertura`, `POC`, `Variacao %`: mapas derivados.
- `Volume Profile`: proxy por distribuicao de volume diario.
- `Confluencia`: score por proximidade, diversidade de fontes e estabilidade.
- `Backtest Proxy`: toque/reversao dos desvios historicos.
- `Grafico`: desenho WPF customizado, com candles e niveis.
