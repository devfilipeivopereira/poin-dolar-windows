# Campos RTD

Servidor COM:

```text
RTDTrading.RTDServer
```

Ativo inicial:

```text
WDOFUT_F_0
```

## Cadastro Por Ativo

Cada ativo em `Rtd.Assets[]` armazena:

| Campo | Uso |
|---|---|
| `Name` | Nome livre exibido na aba `Ativos`. |
| `Asset` | Chave logica interna do app. Por padrao e igual ao codigo de cotacao. |
| `QuoteCode` | Codigo do RTD de cotacao, exemplo `WDON26_G_0`. |
| `BookTopic` | Canal do book, exemplo `BOOK0`. |
| `TimesTopic` | Canal do times and trades, exemplo `T&T0`. |
| `CsvPath` | Historico CSV usado quando o ativo recebe foco. |
| `QuoteEnabled` | Liga/desliga a fonte `Cotacao`. |
| `BookEnabled` | Liga/desliga a fonte `Book`. |
| `TimesEnabled` | Liga/desliga a fonte `Times`. |

## Fontes Geradas

| Fonte | Papel | Ligado padrao | Formula RTD |
|---|---|---:|---|
| `Cotacao-<ATIVO>` | `PriceVolume` | sim | `RTD("rtdtrading.rtdserver",, QuoteCode, Field)` |
| `Book-<ATIVO>` | `BookDepth` | nao | `RTD("rtdtrading.rtdserver",, BookTopic, Field, Index)` |
| `Times-<ATIVO>` | `TimesAndTrades` | nao | `RTD("rtdtrading.rtdserver",, TimesTopic, Field, Index)` |

`Book` assina indices `0..49`. `Times` assina indices `0..99`.

## Campos

Cotacao:

```text
DAT, HOR, ULT, ABE, MAX, MIN, FEC, NEG, QTT, VOL,
OCP, OVD, AJU, AJA, 103, 98, 100, 99, 67
```

Book:

```text
HORC, ACP, VOC, OCP, OVD, VOV, AVD, HORV
```

Tambem sao assinados:

```text
RTD("rtdtrading.rtdserver",, BookTopic, "INFO", "ATV")
RTD("rtdtrading.rtdserver",, BookTopic, "INFO", "TAB")
```

Times and trades:

```text
DAT, ACP, PRE, QUL, AVD, AGR
```

Tambem sao assinados:

```text
RTD("rtdtrading.rtdserver",, TimesTopic, "INFO", "ATV")
RTD("rtdtrading.rtdserver",, TimesTopic, "INFO", "TAB")
```

## Mapeamento Interno

- Campos de `Cotacao` entram no snapshot com o proprio codigo (`ULT`, `VOL`, `67`, etc.).
- Campos de `Book` entram como `BOOK_<CAMPO>_<INDICE>`, exemplo `BOOK_OCP_0`.
- Campos de `Times` entram como `TIMES_<CAMPO>_<INDICE>`, exemplo `TIMES_PRE_0`.
- `67` e tratado como VWAP/MED quando `MED` nao existir.
- `Rtd.Fields` permanece como fallback legado.
