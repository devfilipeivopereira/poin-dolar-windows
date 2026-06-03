# RtdDolarNative

Aplicativo Windows nativo para ler o RTD do Profit diretamente via COM, sem Excel, navegador, HTML ou WebSocket no fluxo principal da UI.

## Marco atual

Este projeto implementa a base nativa low-latency e um porte inicial amplo do dashboard `rtd_dolar`:

- solucao WPF C# `.NET Framework 4.8`;
- configuracoes `x64` e `x86`;
- thread RTD `STA` dedicada;
- assinatura COM reaproveitando a assinatura validada em `RTD_C#`;
- assinatura dos campos RTD completos configurados em `appsettings.json`;
- UI WPF com DOM, tape, niveis, abertura, POC, variacao percentual, volume profile, confluencia, backtest proxy e grafico nativo;
- parser CSV diario com delimitadores `;`, `,` e tab;
- carregamento de CSV por botao, duplo clique, arrastar/soltar, caminho manual, `Ctrl+O` e auto-load em `Downloads\Dados_Dolar`;
- calculos de volatilidade, ATR, profile proxy, AVWAP, suporte/resistencia, desvios, percentuais e confluencias;
- buffer `latest wins` para a UI consumir apenas o snapshot mais recente.

## Telas nativas

- `DOM`: escada por tick, volumes bid/ask, marcacoes por preco e tape recente.
- `Niveis`: todos os pontos calculados ordenaveis.
- `Abertura`: desvios da abertura por sigma.
- `POC`: desvios do POC proxy por sigma.
- `Variacao %`: mapas percentuais por fechamento anterior, abertura e POC.
- `Volume Profile`: bins, POC, VAH, VAL, HVN e LVN.
- `Confluencia`: clusters de niveis com score e evidencia.
- `Backtest Proxy`: toque/reversao de desvios historicos.
- `Grafico`: candles diarios, candle atual e linhas horizontais de niveis.

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

## CSV diario

O app tenta carregar automaticamente o CSV mais recente em `%USERPROFILE%\Downloads\Dados_Dolar` ou em `Documentos` quando o nome contem o ativo configurado ou `WDO`.

Tambem e possivel carregar manualmente pelo botao `Carregar CSV`, pelo painel lateral `Selecionar arquivo`, com duplo clique no painel de CSV, arrastando um arquivo para o painel, usando `Ctrl+O`, ou colando o caminho e clicando em `Carregar caminho`.

O parser aceita CSV em `UTF-8` e `Windows-1252`, incluindo exportacoes do Profit com cabecalho em portugues como `Ativo;Data;Abertura;Maximo;Minimo;Fechamento;Volume;Quantidade`.

## Instalador

O instalador local e gerado em:

```text
dist\PoinDolarWindowsSetup.exe
```

Para regerar:

```powershell
powershell -ExecutionPolicy Bypass -File installer\build-installer.ps1
```

O setup instala em `%LOCALAPPDATA%\PoinDolarWindows`, cria atalhos no Menu Iniciar e na area de trabalho, e registra desinstalacao em "Aplicativos instalados" do Windows. Ele inclui builds `x64` e `x86`; em Windows 64-bit o atalho principal usa `x64`.

## Projeto antigo

O projeto antigo em `D:\OneDrive\Documentos\RTD_C#` deve permanecer intacto. Este projeto copia/adapta apenas contratos e ideias ja validados.
