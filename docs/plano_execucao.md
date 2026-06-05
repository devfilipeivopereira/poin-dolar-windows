# Plano de Execucao

## Marco 0 - Criacao do Projeto

- [x] Criar pasta `D:\OneDrive\Documentos\RTD_Dolar_Nativo`.
- [x] Criar `RtdDolarNative.sln`.
- [x] Criar projeto WPF `.NET Framework 4.8`.
- [x] Configurar plataformas `x64` e `x86`.
- [x] Criar README inicial.
- [x] Criar `appsettings.json`.
- [x] Criar `.gitignore`.

## Marco 1 - Prova RTD Nativa

- [x] Copiar/adaptar interfaces COM.
- [x] Criar `RtdProbeService`.
- [x] Assinar `WDOFUT_F_0` com `HOR`, `ULT` e `VOL`.
- [x] Mostrar status, ultimo preco, volume e hora.
- [x] Preparar builds `x64` e `x86`.
- [x] Documentar como rodar e validar.

## Proximos Marcos

## Porte do dashboard `rtd_dolar`

- [x] Engine RTD assinando todos os campos padrao.
- [x] Painel para adicionar, focar, ligar, desligar e remover ativos RTD.
- [x] Tape em ring buffer.
- [x] DOM nativo com marcacoes de niveis.
- [x] CSV diario com parser C#, auto-load, drag/drop, caminho manual, `Ctrl+O` e fallback `Windows-1252`.
- [x] Motor quant em C# para volatilidade, ATR, POC/VAH/VAL, HVN/LVN, AVWAP, suportes/resistencias, percentuais e confluencias.
- [x] Telas nativas para DOM, niveis, abertura, POC, variacao %, volume profile, confluencia, backtest proxy, grafico e diagnostico.
- [x] Grafico nativo por `FrameworkElement.OnRender`.
- [x] RTD Manager com fontes por ativo/papel e liga/desliga por fonte.
- [x] Menu superior em abas por funcionalidade.
- [x] Tela `Ativos` com cadastro de nome, Codigo Cotacao, Canal Book, Canal Times, CSV historico e canais `Cotacao`, `Book`, `Times`.
- [x] Abertura em `idle`, sem auto-connect, para permitir navegar e editar ativos antes de assinar RTD pesado.
- [x] CRUD de ativos revisado: salvar, focar e remover ativo selecionado ou digitado; lista pode ficar vazia.
- [x] CSV historico por ativo e troca automatica ao focar outro ativo.
- [x] Assinatura RTD de cotacao no formato `QuoteCode, Field`.
- [x] Assinatura RTD de book no formato `BookTopic, Field, Index` com indices `0..49`.
- [x] Assinatura RTD de times and trades no formato `TimesTopic, Field, Index` com indices `0..99`.
- [x] Prints reais de `Times` alimentando o motor de order flow com deduplicacao por linha/horario/preco.
- [x] Pipeline de flow em background com fila bounded/drop-old e coalescing.
- [x] Tape derivado, delta, cumulative delta, imbalance, microbias, VWAP e janelas.
- [x] Volume Profile intraday com POC, VAH, VAL, HVN, LVN e fallback por CSV diario.
- [x] Setups MVP: absorcao, defesa/perda de POC, rompimento com fluxo, rejeicao em LVN e VWAP reversion/continuation.
- [x] SQLite em segundo plano para heatmap de book/trades.
- [x] Tela `Heatmap` com liquidez do book, negocios efetivados, delta, CVD e niveis de interesse.
- [ ] Testes automatizados de paridade com fixtures reais do HTML.
