# RtdDolarNative

Aplicativo Windows nativo para ler o RTD do Profit diretamente via COM, sem Excel, navegador, HTML ou WebSocket no fluxo principal da UI.

## Marco atual

Este projeto implementa os Marcos 0 e 1 do plano nativo low-latency:

- solucao WPF C# `.NET Framework 4.8`;
- configuracoes `x64` e `x86`;
- thread RTD `STA` dedicada;
- assinatura COM reaproveitando a assinatura validada em `RTD_C#`;
- prova RTD com `WDOFUT_F_0` e campos `HOR`, `ULT`, `VOL`;
- UI WPF com status, arquitetura do processo, hora do Profit, ultimo preco, volume e idade do snapshot;
- buffer `latest wins` para a UI consumir apenas o snapshot mais recente.

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
