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
- momentum 10.
- retorno medio 21.
- volatilidade de retornos 21.
- taxa de retornos positivos 21.
- Sharpe21 e Sortino21.
- downside deviation 21.
- VaR95 21.
- expected shortfall 95 21.
- estado de tendencia, estado de reversao, fonte e tamanho da amostra.

Esses indicadores entram como confluencia para sinais de reversao estatistica, pullback quantitativo, Bollinger mean reversion, continuidade por momentum e zonas relevantes do historico.

## Estatistica e backtest proxy

O motor calcula volatilidade historica e janelas de movimento. A aba `Indicadores` mostra pontos, percentual e percentil quando ha dados suficientes. O backtest proxy e direcional: compra e venda sao avaliadas separadamente em desvios historicos de `1`, `1,5` e `2` sigmas.

Para cada direcao, o backtest registra:

- amostras e toques;
- taxa de reversao;
- taxa de continuidade;
- ganho medio favoravel em pontos;
- perda media adversa em pontos;
- expectancy em pontos;
- profit factor proxy.
- confianca estatistica pela banda inferior Wilson;
- risco/retorno proxy com alvo medio favoravel e risco medio adverso;
- edge score composto.

Esse proxy serve para auditoria rapida da amostra, nao como promessa de performance futura. Sinais quantitativos recebem ajuste de score pelo edge da propria direcao. Um setup de compra nao herda edge de venda, e o inverso tambem nao acontece.

## Fluxo e tape reading

O `FlowProcessor` roda fora da thread RTD e usa fila limitada com descarte dos eventos mais antigos. O fluxo calcula:

- delta e cumulative delta;
- janelas de 1s, 5s, 15s, 60s e 300s;
- spread, mid, microprice e microbias;
- imbalance top-of-book;
- VWAP intraday/fallback;
- agressao por quote rule/tick rule;
- sinais de absorcao, defesa/perda de nivel, exaustao, rompimento com fluxo, sweep aproximado, VWAP reversion/continuation, rejeicao em LVN e defesa/perda de POC.

## Heatmap de book e negocios

A aba `Heatmap` usa ideias de plataformas de leitura visual de livro de ofertas: liquidez passiva por preco, volume executado sobreposto, delta/CVD e destaque de areas de absorcao ou interesse. O objetivo e mostrar onde o mercado deixou ordens no book e onde de fato houve negocio.

Entradas:

- `Book`: precos e volumes bid/ask por nivel para formar as barras de liquidez.
- `Times`: preco, quantidade e agressor para formar prints/volume dots, delta e CVD.
- `Cotacao`: ultimo preco e top-of-book como fallback quando o book profundo ainda nao esta ativo.

A leitura segue a mesma semantica de cor da plataforma: verde para compra/suporte/agressao compradora, vermelho para venda/resistencia/agressao vendedora e branco para neutro. A persistencia fica em SQLite local com fila em segundo plano.

## Score e qualidade

O score sempre deve ser lido junto da qualidade:

- `TopOfBookOnly`: leitura limitada, sem tape real.
- `DerivedTape`: tape inferido por RTD de preco/volume/top-of-book.
- `FullTimesAndTrades`: prints reais.
- `FullDepth`: book profundo real.

Sinais quant exibem score ajustado pelo fluxo e pelo edge direcional. Delta e imbalance a favor aumentam a confianca; conflito entre sinal tecnico e fluxo reduz o score e aparece nos motivos. Edge fragil, baixa confianca Wilson, risco/retorno ruim ou amostra de poucos toques limitam o score mesmo quando o indicador tecnico parece bom.

Cada sinal quant tambem mostra `Conf`, `R/R` e `Gate`. O `Gate` informa por que o sinal ainda esta limitado, por exemplo amostra historica baixa, poucos toques, expectancy/PF insuficiente, confianca baixa, risco/retorno desfavoravel ou falta de confirmacao RTD de fluxo.

## Regua robusta de oportunidade

As telas `Scanner` e `Oportunidades` nao usam apenas o ultimo setup bruto. Elas calculam um score composto com:

- sinal de fluxo (`FlowSignal`);
- sinal quantitativo (`QuantSignal`);
- alinhamento entre as direcoes;
- confirmacao por delta/cumulative delta e imbalance;
- proximidade de POC, VAH, VAL, HVN, LVN ou nivel estatistico;
- amostra historica carregada no CSV;
- backtest proxy direcional de toque/reversao, expectancy e profit factor;
- freshness do snapshot RTD;
- qualidade do dado: top-of-book, tape derivado, times real ou depth real;
- eventos descartados pela fila bounded.

O resultado aparece como `Robusto`, `Acionavel`, `Monitorar`, `Fraco` ou `Bloqueado`.

O score e capado quando a alimentacao nao sustenta a leitura:

- sem snapshot ou sem `ULT`: cap baixo;
- snapshot atrasado: cap reduzido;
- `TopOfBookOnly`: cap ate o limite de top-of-book;
- `DerivedTape`: cap ate o limite de tape derivado;
- CSV com menos de 21 pregoes em sinal quant: cap reduzido;
- edge direcional sem expectancy positiva: cap reduzido;
- profit factor proxy abaixo de `1,05`: cap reduzido;
- confianca Wilson abaixo do minimo: cap reduzido;
- risco/retorno proxy abaixo de `1`: cap reduzido;
- sem `Book` e sem `Times`: cap reduzido;
- divergencia entre fluxo e estatistica: penalidade explicita nos motivos.

`Robusto` e a classe mais exigente. Ela requer score alto, cap alto, varias confirmacoes, fluxo confirmando, dados reais de `Times`/depth quando disponiveis e edge quant direcional positivo. Com top-of-book ou tape derivado, a plataforma pode apontar monitoramento ou oportunidade limitada, mas nao deve vender a leitura como robusta.

## Robustez operacional

A robustez financeira da leitura vem de confluencia, qualidade do dado e auditoria, nao de promessa de acerto. A plataforma cruza:

- RTD de `Cotacao` para preco, volume, negocios, VWAP/MED, bid/ask e variacao intraday.
- RTD de `Book` para spread, liquidez por nivel, imbalance, microprice e marcacoes no DOM.
- RTD de `Times` para prints reais, agressor, quantidade, delta e cumulative delta.
- CSV historico para volatilidade, desvios, suportes/resistencias, indicadores tecnicos, profile proxy e backtest proxy.

Um sinal fica mais forte quando ha confirmacao entre nivel estatistico, edge direcional, fluxo, profile e tape. Quando ha conflito, baixa amostra, RTD derivado ou ausencia de book/times real, o score e limitado e a qualidade aparece na UI. Essa regra evita que uma leitura incompleta pareca mais confiavel do que realmente e.

Nao existe garantia tecnica honesta de lucro ou acerto. O que a plataforma garante operacionalmente e que oportunidades classificadas como fortes precisam passar por varias travas quantitativas e de dados: RTD fresco, canais corretos, estatistica positiva, confianca minima, risco/retorno coerente, fluxo confirmando e qualidade real de tape/book quando necessaria.

## Tela Indicadores

Use `Ctrl+Shift+I` ou o botao `Indic.` no menu superior. A tela mostra:

- resumo de RTD, CSV, canais, qualidade e amostra;
- grade de indicadores tecnicos;
- sinais quant com score, nivel, edge, expectancy, profit factor, estado tecnico e motivos;
- `Conf`, `R/R`, `Gate`, alvo medio e risco medio proxy por sinal;
- volatilidade/estatistica;
- backtest proxy direcional de toque/reversao/continuidade, confianca, R/R e edge score.

Essa tela deve ser usada antes de `Oportunidades` quando for necessario auditar por que um setup apareceu.
