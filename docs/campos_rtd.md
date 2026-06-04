# Campos RTD

Servidor COM:

```text
RTDTrading.RTDServer
```

Ativo inicial:

```text
WDOFUT_F_0
```

Fontes RTD padrao:

| Fonte | Papel | Ligado | Campos |
|---|---|---|---|
| `PrecoVolume-<ATIVO>` | `PriceVolume` | sim | `HOR`, `ULT`, `QUL`, `VOL`, `NEG`, `ABE`, `MAX`, `MIN`, `MED` |
| `TopBook-<ATIVO>` | `TopBook` | sim | `OCP`, `OVD`, `VOC`, `VOV` |
| `BookDepth-<ATIVO>` | `BookDepth` | nao | vazio ate confirmar codigos RTD |
| `TimesAndTrades-<ATIVO>` | `TimesAndTrades` | nao | vazio ate confirmar codigos RTD |

Na UI, esses papeis aparecem como canais por ativo:

| Canal | Papeis controlados |
|---|---|
| `Cotacao` | `PriceVolume` |
| `Book` | `TopBook` e `BookDepth` quando houver campos reais |
| `Times` | `TimesAndTrades` |

Campos minimos da prova nativa:

| Campo | Uso |
|---|---|
| HOR | Hora informada pelo Profit |
| ULT | Ultimo preco |
| VOL | Volume acumulado |

Campos preparados no catalogo para os proximos marcos:

```text
DAT, HOR, ULT, ABE, MAX, MIN, FEC, VAR, VARPTS, MED, NEG, QUL, QTT,
VOL, OCP, OVD, VOC, VOV, AJU, AJA, VPJ, VEN, VAL, CAB, EST
```

Observacoes:

- O app nao inventa campos para book profundo ou times and trades.
- `Rtd.Sources[]` e a fonte de verdade para assinatura. `Rtd.Fields` permanece como fallback legado.
- Novos ativos adicionados pela UI recebem automaticamente as fontes padrao.
