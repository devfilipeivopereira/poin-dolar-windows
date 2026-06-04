# Validacao

## x64 primeiro

1. Abrir `RtdDolarNative.sln`.
2. Selecionar `Debug|x64`.
3. Rodar com o Profit Pro aberto.
4. Confirmar que a janela abre em `idle`, sem travar, e que o menu superior abre `Ativos`, `Cotacao`, `DOM / Book`, `Tape` e `Diagnostico` antes de conectar.
5. Clicar em `Conectar`.
6. Confirmar que `ServerStart` retorna valor positivo no log.
7. Confirmar que `ULT` e `VOL` aparecem na janela.

## Fallback x86

Se aparecer erro de COM como `Class not registered` ou `RTDTrading.RTDServer nao encontrado`, selecionar `Debug|x86` e repetir o teste.

## Profit fechado

Com o Profit fechado, o app deve continuar aberto. O status deve alternar entre `connecting`, `reconnecting` ou `disconnected`, e o erro recente deve explicar a falha COM/RTD.

## Criterios do Marco 1

- Janela WPF abre sem navegador.
- RTD roda em thread STA dedicada.
- UI continua responsiva mesmo com erro COM.
- Snapshot exibido vem do buffer `latest wins`.
- Projeto antigo em `D:\OneDrive\Documentos\RTD_C#` nao foi alterado.

## Ativos / RTD Manager

1. Abrir a aba `Ativos`.
2. Confirmar que a navegacao entre abas funciona antes de conectar o RTD.
3. Cadastrar um ativo com:
   - `Codigo Cotacao = WDON26_G_0`
   - `Canal Book = BOOK0`
   - `Canal Times = T&T0`
   - `CSV Historico` selecionado para esse ativo.
4. Salvar, focar e remover o ativo cadastrado; se for o ultimo, confirmar lista vazia e status `idle`.
5. Cadastrar novamente e confirmar que a lista mostra `Cotacao`, `Book` e `Times` separados.
6. Ligar/desligar `Cotacao`, `Book` e `Times` separadamente e confirmar restart RTD sem fechar o app quando ja conectado.
7. Confirmar no `Diagnostico`:
   - `Cotacao-*` com formula `QuoteCode, Field`.
   - `Book-*` com indices `0..49`.
   - `Times-*` com indices `0..99`.
8. Confirmar que trocar o ativo em foco troca tambem o CSV historico carregado.
9. Confirmar que `Cotacao` mostra os campos de preco/indicadores.
10. Confirmar que `DOM / Book` preenche linhas reais quando `Book` esta ligado.
11. Confirmar que `Tape` mostra linhas reais quando `Times` esta ligado; se `Times` estiver desligado ou vazio, deve mostrar tape derivado.
12. Confirmar `Order Flow` com delta, cumulative delta, imbalance, microbias e janelas.
13. Confirmar `Volume Profile` com POC, VAH, VAL, HVN e LVN quando houver prints suficientes ou fallback por CSV.
14. Confirmar `Indicadores` por `Ctrl+Shift+I` ou botao `Indic.`:
   - RSI14, EMAs, SMA, MACD, Bollinger, z-score e ATR/VWAP aparecem quando ha CSV/snapshot suficiente.
   - A tela mostra fonte tecnica, amostra, CSV carregado, status RTD e qualidade do fluxo.
   - Sinais quant mostram score ajustado, nivel, edge estatistico, estado tecnico e motivos.
   - Quando `Times`/`Book` nao estao reais, a qualidade informa tape derivado/top-of-book e o score fica limitado.
15. Confirmar que `Setups` respeita cooldown e informa qualidade do dado.

## Performance

- UI deve continuar clicavel enquanto o RTD atualiza.
- `FlowProcessor` usa fila limitada; o contador `Fila drop` deve ficar estavel em fluxo normal.
- `ULT`, `QUL` e `VOL` chegando separados nao devem gerar prints duplicados quando `NEG` esta disponivel.
- Scores acima de 85 devem aparecer apenas quando uma fonte real de times and trades/book profundo for implementada.
- A aba `Indicadores` deve atualizar sem travar a UI e sem recalcular pesado na thread RTD.
