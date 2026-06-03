# Arquitetura

Fluxo do MVP nativo:

```text
Profit Pro
  -> RTDTrading.RTDServer
  -> RtdProbeService em thread STA
  -> MarketState
  -> LatestSnapshotBuffer
  -> DispatcherTimer WPF
  -> MainWindow
```

Regras aplicadas desde o primeiro marco:

- RTD roda fora da thread de UI.
- A UI nao recebe fila de ticks; ela le sempre o snapshot mais recente.
- A thread RTD nao grava SQLite e nao atualiza controles WPF.
- O app assina inicialmente apenas `HOR`, `ULT` e `VOL`.
- Logs por tick ficam desligados por padrao.

O proximo marco deve trocar a prova por uma engine RTD completa assinando todos os campos padrao.
