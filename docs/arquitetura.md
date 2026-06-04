# Arquitetura

Fluxo nativo:

```text
Profit Pro
  -> RTDTrading.RTDServer
  -> Rtd.Assets[] com Codigo Cotacao, Canal Book e Canal Times
  -> Rtd.Sources[] geradas por ativo/papel/topico
  -> RtdProbeService em thread STA dedicada
  -> MarketState por ativo
  -> cache de snapshots por ativo
  -> FlowProcessor em background com fila bounded/drop-old
  -> FlowEngine / VolumeProfileEngine / SetupDetector
  -> DispatcherTimer WPF
  -> Mesa / Ativos / Cotacao / DOM Book / Tape / Order Flow / Volume Profile / Setups / Indicadores / Niveis / Grafico
```

Regras aplicadas desde o primeiro marco:

- RTD roda fora da thread de UI.
- A janela abre em `idle`; o RTD so inicia quando o usuario clica em `Conectar` ou quando `Rtd.AutoConnect` estiver explicitamente ligado.
- A UI nao recebe fila de ticks; ela le sempre o snapshot mais recente.
- A thread RTD nao grava SQLite e nao atualiza controles WPF.
- O app assina os campos configurados em `Rtd.Sources[]`; `Rtd.Fields` permanece como fallback legado.
- O app assina todos os ativos ligados em `Rtd.Assets` e mantem snapshots isolados por ativo.
- Cada ativo cadastra `QuoteCode`, `BookTopic`, `TimesTopic`, `CsvPath`, `QuoteEnabled`, `BookEnabled` e `TimesEnabled`.
- O dashboard renderiza o ativo em foco; o CSV historico tambem e carregado por ativo.
- Cada ativo recebe tres fontes padrao: `Cotacao-<ATIVO>`, `Book-<ATIVO>` e `Times-<ATIVO>`.
- Em instalacao limpa, `Cotacao` fica ligado por ativo; `Book` e `Times` ficam desligados ate o usuario ligar.
- `Cotacao` assina `RTD("rtdtrading.rtdserver",, QuoteCode, Field)`.
- `Book` assina `RTD("rtdtrading.rtdserver",, BookTopic, Field, Index)` com indices `0..49`, alem de `INFO/ATV` e `INFO/TAB`.
- `Times` assina `RTD("rtdtrading.rtdserver",, TimesTopic, Field, Index)` com indices `0..99`, alem de `INFO/ATV` e `INFO/TAB`.
- O tape usa times and trades real quando o canal `Times` traz linhas; caso contrario usa fallback derivado por coalescing de snapshots de preco/volume/top-of-book.
- Setups baseados apenas em top-of-book/tape derivado tem score limitado; score 100 fica reservado para feeds reais.
- O motor quant calcula RSI14, SMA20/50, EMA9/21/50, MACD, Bollinger20, z-score 20, ATR/VWAP, volatilidade historica, desvios, confluencias e backtest proxy.
- A tela `Indicadores` expõe fonte, amostra, qualidade RTD/CSV/fluxo, sinais quant, score ajustado por fluxo e edge estatistico.
- Logs por tick ficam desligados por padrao.

O historico diario e carregado por ativo na aba `Ativos` e alimenta o motor quant nativo. O carregamento aceita botao, seletor no cadastro, duplo clique, arrastar/soltar, caminho manual, `Ctrl+O` e auto-load do CSV mais recente em `Downloads\Dados_Dolar` ou `Documentos` quando o ativo ainda nao tem `CsvPath`. O parser tenta `UTF-8` e cai para `Windows-1252`, cobrindo exportacoes do Profit com acentos no cabecalho. O RTD preenche abertura, maxima, minima, ultimo preco, VWAP, volume, bid/ask, book real e times real quando os canais estao ligados.

Telas principais:

- `Mesa`: workspace inicial com grafico, DOM compacto, tape compacto, janelas de fluxo, niveis e oportunidades.
- `Ativos`: cadastro de nome, codigo de cotacao, canal book, canal times, CSV historico e liga/desliga por canal.
- `Cotacao`: ultimo preco, volume, indicadores RTD e campos de cotacao.
- `DOM / Book`: ladder por tick, profundidade real do book e niveis.
- `Tape`: prints reais quando disponiveis ou derivados por quote/tick rule.
- `Order Flow`: delta, cumulative delta, imbalance, microbias, VWAP e janelas 1s/5s/15s/60s/300s.
- `Volume Profile`: grafico horizontal por preco com POC, VAH/VAL 70%, HVN/LVN, tabela de nos e fallback por CSV diario.
- `Setups`: absorcao, defesa/perda de POC, rompimento com fluxo, rejeicao em LVN e VWAP reversion/continuation.
- `Indicadores`: auditoria de RSI, medias, MACD, Bollinger, z-score, ATR/VWAP, volatilidade, backtest proxy e sinais quant.
- `Niveis`: niveis principais, abertura, POC, variacao percentual e confluencia em subtabs.
- `Grafico`: desenho WPF customizado, com candles e niveis.
- `Backtest`: toque/reversao dos desvios historicos.
- `Diagnostico`: fontes RTD, topicos assinados, indices, erros e metricas.
