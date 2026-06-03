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
