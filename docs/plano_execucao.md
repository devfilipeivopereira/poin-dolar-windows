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
- [x] Pipeline de flow em background com fila bounded/drop-old e coalescing.
- [x] Tape derivado, delta, cumulative delta, imbalance, microbias, VWAP e janelas.
- [x] Volume Profile intraday com POC, VAH, VAL, HVN, LVN e fallback por CSV diario.
- [x] Setups MVP: absorcao, defesa/perda de POC, rompimento com fluxo, rejeicao em LVN e VWAP reversion/continuation.
- [ ] SQLite opcional em segundo plano.
- [ ] Testes automatizados de paridade com fixtures reais do HTML.
- [ ] BookDepth e TimesAndTrades reais quando os campos RTD forem confirmados.
