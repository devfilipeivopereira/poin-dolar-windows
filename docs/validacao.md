# Validacao

## x64 primeiro

1. Abrir `RtdDolarNative.sln`.
2. Selecionar `Debug|x64`.
3. Rodar com o Profit Pro aberto.
4. Confirmar que `ServerStart` retorna valor positivo no log.
5. Confirmar que `ULT` e `VOL` aparecem na janela.

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

## Order Flow / RTD Manager

1. Abrir a aba `RTD Manager`.
2. Confirmar fontes `PrecoVolume-*` e `TopBook-*` ligadas.
3. Ligar/desligar uma fonte e confirmar restart RTD sem fechar o app.
4. Adicionar novo ativo no painel lateral e confirmar criacao das fontes padrao.
5. Confirmar que a aba `Tape` mostra prints derivados quando `NEG`/`VOL` avancam.
6. Confirmar `Order Flow` com delta, cumulative delta, imbalance, microbias e janelas.
7. Confirmar `Volume Profile` com POC, VAH, VAL, HVN e LVN quando houver prints suficientes.
8. Confirmar que `Setups` respeita cooldown e informa qualidade `DerivedTape`.

## Performance

- UI deve continuar clicavel enquanto o RTD atualiza.
- `FlowProcessor` usa fila limitada; o contador `Fila drop` deve ficar estavel em fluxo normal.
- `ULT`, `QUL` e `VOL` chegando separados nao devem gerar prints duplicados quando `NEG` esta disponivel.
- Scores acima de 85 devem aparecer apenas quando uma fonte real de times and trades/book profundo for implementada.
