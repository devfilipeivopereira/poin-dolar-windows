# Arquitetura

Fluxo nativo:

```text
Profit Pro
  -> RTDTrading.RTDServer
  -> Rtd.Sources[] por ativo/papel
  -> RtdProbeService em thread STA dedicada
  -> MarketState por ativo
  -> cache de snapshots por ativo
  -> FlowProcessor em background com fila bounded/drop-old
  -> FlowEngine / VolumeProfileEngine / SetupDetector
  -> DispatcherTimer WPF
  -> DOM / Tape / Order Flow / Volume Profile / Setups / Grafico
```

Regras aplicadas desde o primeiro marco:

- RTD roda fora da thread de UI.
- A UI nao recebe fila de ticks; ela le sempre o snapshot mais recente.
- A thread RTD nao grava SQLite e nao atualiza controles WPF.
- O app assina os campos configurados em `Rtd.Sources[]`; `Rtd.Fields` permanece como fallback legado.
- O app assina todos os ativos ligados em `Rtd.Assets` e mantem snapshots isolados por ativo.
- O dashboard renderiza o ativo em foco; o tape e filtrado pelo mesmo ativo.
- Cada ativo recebe fontes padrao: `PriceVolume`, `TopBook`, `BookDepth` e `TimesAndTrades`.
- `BookDepth` e `TimesAndTrades` ficam desligados e sem campos ate os codigos RTD reais serem confirmados.
- O tape V1 e derivado por coalescing de snapshots de preco/volume/top-of-book, com qualidade `DerivedTape`.
- Setups baseados apenas em top-of-book/tape derivado tem score limitado; score 100 fica reservado para feeds reais.
- Logs por tick ficam desligados por padrao.

O historico diario e carregado por CSV e alimenta o motor quant nativo. O carregamento aceita botao, seletor no painel lateral, duplo clique, arrastar/soltar, caminho manual, `Ctrl+O` e auto-load do CSV mais recente em `Downloads\Dados_Dolar` ou `Documentos`. O parser tenta `UTF-8` e cai para `Windows-1252`, cobrindo exportacoes do Profit com acentos no cabecalho. O RTD preenche abertura, maxima, minima, ultimo preco, MED/VWAP, volume, bid/ask e volumes de book.

Telas principais:

- `DOM`: ladder por tick com tape e tags por preco.
- `RTD Manager`: escolha de canais por ativo (`Cotacao`, `Book`, `Times`) e detalhe das fontes RTD.
- `Tape`: prints reais quando disponiveis ou derivados por quote/tick rule.
- `Order Flow`: delta, cumulative delta, imbalance, microbias, VWAP e janelas 1s/5s/15s/60s/300s.
- `Niveis`: pontos brutos e clusters.
- `Abertura`, `POC`, `Variacao %`: mapas derivados.
- `Volume Profile`: grafico horizontal por preco com POC, VAH/VAL 70%, HVN/LVN, tabela de nos e fallback por CSV diario.
- `Setups`: absorcao, defesa/perda de POC, rompimento com fluxo, rejeicao em LVN e VWAP reversion/continuation.
- `Confluencia`: score por proximidade, diversidade de fontes e estabilidade.
- `Backtest Proxy`: toque/reversao dos desvios historicos.
- `Grafico`: desenho WPF customizado, com candles e niveis.
