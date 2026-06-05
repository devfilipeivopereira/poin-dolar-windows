# RtdDolarNative

Aplicativo Windows nativo para ler o RTD do Profit diretamente via COM, sem Excel, navegador, HTML ou WebSocket no fluxo principal da UI.

## Marco atual

Este projeto implementa a base nativa low-latency e um porte inicial amplo do dashboard `rtd_dolar`:

- solucao WPF C# `.NET Framework 4.8`;
- configuracoes `x64` e `x86`;
- thread RTD `STA` dedicada;
- assinatura COM reaproveitando a assinatura validada em `RTD_C#`;
- assinatura dos campos RTD completos configurados em `appsettings.json`;
- menu superior em abas para separar cadastro, cotacao, book, tape, order flow, volume profile, setups, indicadores, niveis, grafico, backtest e diagnostico;
- `Mesa` inicial com grafico, DOM, tape, janelas de fluxo, niveis e oportunidades no mesmo workspace;
- tela `Ativos` para cadastrar `Codigo Cotacao`, `Canal Book`, `Canal Times`, CSV historico e ligar/desligar `Cotacao`, `Book` e `Times` por ativo;
- UI WPF com cotacao, DOM/book real, tape real ou derivado, niveis, volume profile, setups, backtest e grafico nativo;
- parser CSV diario com delimitadores `;`, `,` e tab;
- carregamento de CSV por ativo via botao, duplo clique, arrastar/soltar, caminho manual, `Ctrl+O` e auto-load em `Downloads\Dados_Dolar`;
- calculos de volatilidade, ATR, RSI, medias moveis, MACD, Bollinger, z-score, momentum, profile proxy, AVWAP, suporte/resistencia, desvios, percentuais, confluencias e sinais quant;
- metricas de risco estatistico de retornos, incluindo retorno medio 21, retornos positivos, volatilidade de retornos, downside deviation, Sharpe21, Sortino21, VaR95 e expected shortfall;
- backtest proxy direcional com compra/venda separadas, taxa de reversao, continuidade, expectancy em pontos, profit factor, confianca Wilson, risco/retorno e edge score;
- regua de oportunidade robusta em `Scanner` e `Oportunidades`, combinando RTD, CSV, indicadores, fluxo, volume profile, edge direcional, backtest proxy e qualidade dos canais;
- buffer `latest wins` para a UI consumir apenas o snapshot mais recente.

## Telas nativas

Use o menu superior como navegacao principal; ele seleciona cada tela diretamente e fica operacional mesmo antes de conectar o RTD. O menu e a barra rapida ficam organizados pelo mesmo fluxo de trabalho: `Operacao`, `Mercado`, `Fluxo`, `Analise` e `Controle`. O menu `Janelas` lista todas as telas em uma unica lista quando for preciso navegar sem procurar o grupo.

- `Mesa`: primeira tela operacional com faixa de prontidao, grafico, DOM, tape, fluxo, niveis e oportunidades.
- `Ativos`: cadastro de ativo, canais RTD e CSV historico.
- `Cotacao`: campos RTD de preco, volume e indicadores.
- `DOM / Book`: escada por tick, book real `0..49`, volumes bid/ask, marcacoes por preco e tape recente.
- `Tape`: times and trades real quando `Times` estiver ligado; fallback para tape derivado.
- `Order Flow`: delta, cumulative delta, imbalance, microbias, VWAP e janelas.
- `Volume Profile`: bins, POC, VAH, VAL, HVN e LVN.
- `Setups`: sinais de fluxo, direcao, score, nivel associado e qualidade do dado.
- `Indicadores`: auditoria de tecnicos, estatistica, score quant, fonte RTD/CSV e confirmacao de fluxo.
- `Niveis`: niveis principais, abertura, POC, variacao percentual e confluencia em subtabs.
- `Grafico`: candles diarios, candle atual e linhas horizontais de niveis.
- `Backtest`: toque/reversao de desvios historicos.
- `Diagnostico`: fontes RTD, topicos, indices, erros e metricas.

## Como compilar

1. Abra `RtdDolarNative.sln` no Visual Studio 2022.
2. Selecione `Debug|x64` primeiro.
3. Compile e execute `RtdDolarNative`.
4. Se o COM falhar com classe nao registrada, selecione `Debug|x86` e rode novamente.

## Como validar

1. Abra o Profit Pro e deixe conectado.
2. Execute o app.
3. Confirme que o status fica `connected`.
4. Confirme que `ULT` e `VOL` mudam na janela.
5. Feche o Profit ou rode sem o Profit aberto para validar que o app continua vivo e mostra reconexao/erro.

Logs ficam em `logs/rtd-dolar-native.log`.

## Ativos RTD

A aba `Ativos` permite cadastrar novos ativos e separar os tres canais de RTD vindos do Profit:

- `Codigo Cotacao`, exemplo `WDON26_G_0`: assina `RTD("rtdtrading.rtdserver",, QuoteCode, Field)`.
- `Canal Book`, exemplo `BOOK0`: assina `RTD("rtdtrading.rtdserver",, BookTopic, Field, Index)` com indices `0..49`.
- `Canal Times`, exemplo `T&T0`: assina `RTD("rtdtrading.rtdserver",, TimesTopic, Field, Index)` com indices `0..99`.
- `CSV Historico`: arquivo carregado automaticamente quando o ativo recebe foco.

- `Adicionar` cria o ativo, liga a assinatura e coloca o ativo em foco.
- `Focar` ou duplo clique na grade troca o ativo usado no DOM, tape filtrado, topo e calculos intraday.
- `Ligar` e `Desligar` reiniciam o loop RTD com a lista atual de ativos habilitados.
- `Cotacao`, `Book` e `Times` podem ser ligados/desligados separadamente.
- `Remover` apaga o ativo selecionado ou o `Codigo Cotacao` digitado; se for o ultimo, a lista fica vazia e o RTD permanece em `idle`.

Por padrao o app abre sem conectar automaticamente ao RTD. Isso deixa a aba `Ativos` operavel antes de qualquer assinatura pesada de book/times. Depois de cadastrar e escolher os canais, use `Conectar`.

A lista e persistida em `appsettings.json` na chave `Rtd.Assets`.

## CSV diario

O app carrega primeiro o `CSV Historico` do ativo em foco. Se o ativo ainda nao tiver CSV cadastrado, tenta carregar automaticamente o CSV mais recente em `%USERPROFILE%\Downloads\Dados_Dolar` ou em `Documentos` quando o nome contem o ativo configurado ou `WDO`.

Tambem e possivel carregar manualmente pelo botao `Carregar CSV`, pela aba `Ativos`, com duplo clique no painel de CSV, arrastando um arquivo para o painel, usando `Ctrl+O`, ou colando o caminho e clicando em `Carregar caminho`.

O parser aceita CSV em `UTF-8` e `Windows-1252`, incluindo exportacoes do Profit com cabecalho em portugues como `Ativo;Data;Abertura;Maximo;Minimo;Fechamento;Volume;Quantidade`.

## Indicadores e sinais quant

A tela `Indicadores` mostra os calculos que sustentam a triagem:

- RSI14, SMA20/50, EMA9/21/50, MACD, Bollinger20, z-score 20 e distancia ATR/VWAP.
- Momentum10, retornos positivos 21, Sharpe21, Sortino21, VaR95 e expected shortfall.
- volatilidade historica, janelas, percentil, backtest proxy direcional, expectancy, profit factor, confianca Wilson, risco/retorno, edge score e amostra usada.
- sinais quant com score ajustado por fluxo, nivel associado, edge estatistico, `Conf`, `R/R`, `Gate`, estado tecnico e motivos.
- robustez da oportunidade: `Robusto`, `Acionavel`, `Monitorar`, `Fraco` ou `Bloqueado`.
- qualidade da alimentacao: RTD, CSV, canais `Cotacao`, `Book`, `Times`, tape real ou derivado.

O caminho esperado de decisao e: cadastrar ativo e canais RTD, conectar, validar qualidade em `Diagnostico`, acompanhar `Mesa`, auditar `DOM / Book` e `Tape`, revisar `Order Flow`, `Volume Profile`, `Indicadores`, `Setups`, `Scanner` e `Oportunidades`. Os RTDs alimentam preco/volume, book e prints; o CSV historico alimenta volatilidade, desvios, indicadores, profile proxy, risco estatistico de retornos e backtest proxy.

O app e uma plataforma de analise e busca de oportunidades. Ele nao envia ordens. Scores sao evidencias operacionais para revisao do trader, nao promessa de resultado. `Robusto` exige RTD fresco, fluxo confirmando, qualidade real de dados, edge direcional positivo, confianca estatistica minima e risco/retorno coerente; com `TopOfBookOnly`, tape derivado, snapshot atrasado, CSV insuficiente ou estatistica conflitante, a UI informa a qualidade do dado e o score fica limitado.

Detalhes: `docs/quant_trading.md`.

## Instalador

O instalador local e gerado em:

```text
dist\PoinDolarWindowsSetup.exe
```

Para regerar:

```powershell
powershell -ExecutionPolicy Bypass -File installer\build-installer.ps1
```

O setup instala em `%LOCALAPPDATA%\PoinDolarWindows`, cria atalhos no Menu Iniciar e na area de trabalho, e registra desinstalacao em "Aplicativos instalados" do Windows. Ele inclui builds `x64` e `x86`; em Windows 64-bit o atalho principal usa `x64`. Em atualizacoes, o instalador preserva `appsettings.json` existente para manter a lista de ativos RTD configurada no app.

## Projeto antigo

O projeto antigo em `D:\OneDrive\Documentos\RTD_C#` deve permanecer intacto. Este projeto copia/adapta apenas contratos e ideias ja validados.
