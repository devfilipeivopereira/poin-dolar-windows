# Camada Quant, Indicadores e Sinais

O objetivo da camada quant e transformar RTD, book, times and trades e CSV historico em evidencias auditaveis para encontrar pontos de reversao, continuidade e oportunidade com fluxo. O aplicativo nao envia ordens e nao promete resultado; ele qualifica contexto, qualidade do dado, amostra, score e conflito/confirmacao de fluxo.

## Entradas

- `Cotacao`: `ULT`, `ABE`, `MAX`, `MIN`, `MED`, `VOL`, `NEG`, `QUL`, bid/ask e campos de preco/volume.
- `Book`: top-of-book e profundidade `0..49`, quando o canal `Book` estiver ligado e alimentando dados.
- `Times`: prints `0..99`, quando o canal `Times` estiver ligado e alimentando dados.
- `CSV Historico`: barras diarias por ativo, usadas para volatilidade, desvios, suporte/resistencia, profile proxy, backtest proxy e indicadores tecnicos.

Quando `Times` nao esta disponivel, o tape pode ser derivado por coalescing de preco/volume/top-of-book. Essa qualidade aparece na UI e limita a confianca do score.

## Indicadores tecnicos

A aba `Indicadores` expõe:

- RSI14.
- SMA20 e SMA50.
- EMA9, EMA21 e EMA50.
- MACD, sinal e histograma.
- Bollinger20 inferior, media e superior.
- z-score 20.
- distancia ATR/VWAP.
- estado de tendencia, estado de reversao, fonte e tamanho da amostra.

Esses indicadores entram como confluencia para sinais de reversao estatistica, pullback quantitativo, Bollinger mean reversion e zonas relevantes do historico.

## Estatistica e backtest proxy

O motor calcula volatilidade historica e janelas de movimento. A aba `Indicadores` mostra pontos, percentual e percentil quando ha dados suficientes. O backtest proxy resume toques e reversoes em desvios historicos; ele serve para auditoria rapida da amostra, nao como promessa de performance futura.

## Fluxo e tape reading

O `FlowProcessor` roda fora da thread RTD e usa fila limitada com descarte dos eventos mais antigos. O fluxo calcula:

- delta e cumulative delta;
- janelas de 1s, 5s, 15s, 60s e 300s;
- spread, mid, microprice e microbias;
- imbalance top-of-book;
- VWAP intraday/fallback;
- agressao por quote rule/tick rule;
- sinais de absorcao, defesa/perda de nivel, exaustao, rompimento com fluxo, sweep aproximado, VWAP reversion/continuation, rejeicao em LVN e defesa/perda de POC.

## Score e qualidade

O score sempre deve ser lido junto da qualidade:

- `TopOfBookOnly`: leitura limitada, sem tape real.
- `DerivedTape`: tape inferido por RTD de preco/volume/top-of-book.
- `FullTimesAndTrades`: prints reais.
- `FullDepth`: book profundo real.

Sinais quant exibem score ajustado pelo fluxo. Delta e imbalance a favor aumentam a confianca; conflito entre sinal tecnico e fluxo reduz o score e aparece nos motivos.

## Robustez operacional

A robustez financeira da leitura vem de confluencia, qualidade do dado e auditoria, nao de promessa de acerto. A plataforma cruza:

- RTD de `Cotacao` para preco, volume, negocios, VWAP/MED, bid/ask e variacao intraday.
- RTD de `Book` para spread, liquidez por nivel, imbalance, microprice e marcacoes no DOM.
- RTD de `Times` para prints reais, agressor, quantidade, delta e cumulative delta.
- CSV historico para volatilidade, desvios, suportes/resistencias, indicadores tecnicos, profile proxy e backtest proxy.

Um sinal fica mais forte quando ha confirmacao entre nivel estatistico, fluxo, profile e tape. Quando ha conflito, baixa amostra, RTD derivado ou ausencia de book/times real, o score e limitado e a qualidade aparece na UI. Essa regra evita que uma leitura incompleta pareca mais confiavel do que realmente e.

## Tela Indicadores

Use `Ctrl+Shift+I` ou o botao `Indic.` no menu superior. A tela mostra:

- resumo de RTD, CSV, canais, qualidade e amostra;
- grade de indicadores tecnicos;
- sinais quant com score, nivel, edge, estado tecnico e motivos;
- volatilidade/estatistica;
- backtest proxy de toque/reversao.

Essa tela deve ser usada antes de `Oportunidades` quando for necessario auditar por que um setup apareceu.
