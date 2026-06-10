# Plano minucioso para implementar GARCH(1,1) no Poin Dolar Windows

**Projeto analisado:** `poin-dolar-windows-main.zip`  
**Sistema:** WPF C# nativo para RTD do Profit / WDO / dólar futuro B3  
**Objetivo:** adicionar uma tela específica de **GARCH(1,1)** com análise diária e intraday, bandas estatísticas, pontos de compra/venda, scoring de oportunidade, integração com volume profile, order flow e tela de oportunidades.  
**Executor-alvo:** Codex 5.3 Spark, trabalhando diretamente no projeto.

> Este plano é técnico e operacional para implementação de software. A tela deve mostrar leituras estatísticas e pontos de interesse; o sistema não deve enviar ordens nem prometer acerto.

---

## 1. Diagnóstico minucioso do projeto atual

### 1.1. Arquitetura geral encontrada

O projeto já está bem avançado como plataforma nativa de leitura de mercado. A arquitetura atual é:

```text
Profit Pro
  -> RTDTrading.RTDServer
  -> Rtd.Assets[] / Rtd.Sources[]
  -> RtdProbeService em thread STA dedicada
  -> MarketSnapshot por ativo
  -> LatestSnapshotBuffer
  -> FlowProcessor em background
  -> FlowEngine / VolumeProfileEngine / SetupDetector
  -> QuantEngine com CSV diário
  -> MainWindow WPF / telas operacionais
```

Arquivos centrais:

| Área | Arquivo | Situação atual |
|---|---|---|
| UI principal | `src/RtdDolarNative/MainWindow.xaml` | Tela monolítica com todas as abas. Tem 23 abas indexadas de `0` a `22`. |
| Code-behind | `src/RtdDolarNative/MainWindow.xaml.cs` | Muito grande, aproximadamente 9.872 linhas. Controla timers, renderização, RTD, CSV, oportunidades e indicadores. |
| Quant | `src/RtdDolarNative/Quant/QuantEngine.cs` | Motor quantitativo atual. Calcula volatilidade histórica, Garman-Klass, Parkinson, Rogers-Satchell, Yang-Zhang, Gauss robusto, ATR, indicadores técnicos, níveis, backtest proxy e sinais quant. |
| Modelos quant | `src/RtdDolarNative/Quant/QuantModels.cs` | Contém `QuantResult`, `VolatilityMetric`, `DeviationLevel`, `QuantSignal`, `BacktestRow`, `KeyLevel`, etc. |
| CSV diário | `src/RtdDolarNative/Csv/DailyBar.cs`, `DailyCsvParser.cs`, `CsvHistorySqliteStore.cs` | Já suporta histórico diário, parser sem/com cabeçalho, UTF-8/Windows-1252, persistência em SQLite. |
| RTD / snapshot | `src/RtdDolarNative/MarketData/MarketSnapshot.cs` | Tem propriedades úteis: `Ultimo`, `Abertura`, `Maxima`, `Minima`, `FechamentoAnterior`, `Ajuste`, `AjusteAnterior`, `Media`, `Volume`, `Quantidade`, `Negocios`, bid/ask etc. |
| Ticks | `src/RtdDolarNative/MarketData/TickEvent.cs` | Já existe estrutura de evento com preço, quantidade, volume, delta, bid, ask. |
| Order flow | `src/RtdDolarNative/Flow/FlowProcessor.cs`, `FlowEngine.cs`, `FlowModels.cs` | Já calcula delta, cumulative delta, VWAP, imbalance, microprice, perfil intraday e qualidade do dado. |
| Setups | `src/RtdDolarNative/Flow/SetupDetector.cs` | Já detecta absorção, defesa/perda de POC, rompimento com fluxo, rejeição em LVN e VWAP reversion/continuation. |
| Volume profile | `src/RtdDolarNative/Flow/VolumeProfileEngine.cs` | Já calcula POC, VAH, VAL, HVN e LVN por preço com base em trades. |
| Gráfico | `src/RtdDolarNative/Charts/NativeChartControl.cs` | Desenha candles e níveis via `KeyLevel`. Pode receber níveis GARCH como `KeyLevel` sem reescrever o gráfico. |
| Configuração | `src/RtdDolarNative/Config/AppConfig.cs` e `appsettings.json` | Já possui `Rtd`, `Ui`, `Storage`, `Diagnostics`, `Flow`. Deve receber uma seção `Garch`. |
| Testes | `tests/QuantEngineTests/Program.cs` | Testes console simples. Ideal adicionar testes GARCH aqui. |

### 1.2. Ponto forte do projeto para receber GARCH

O projeto já tem praticamente tudo que o GARCH precisa:

1. **CSV diário consolidado** por ativo, via `_dailyBars`.
2. **Snapshot RTD fresco** com último preço, abertura, máxima, mínima, ajuste/fechamento anterior e VWAP/MED.
3. **Tick size configurado** em `_config.Rtd.TickSize`, atualmente `0.5`.
4. **Order flow e volume profile intraday** para confirmar ou negar reversões estatísticas.
5. **Estrutura de níveis (`KeyLevel`)** já usada pelo gráfico, DOM, níveis e oportunidades.
6. **Sistema de oportunidade com score/cap/robustez**, já pronto para receber mais uma fonte quantitativa.
7. **Timer quant** (`_quantTimer`) que recalcula a análise fora da thread RTD.

### 1.3. Lacuna atual

O sistema atual calcula várias volatilidades históricas, mas **não calcula GARCH(1,1)**. Hoje existem:

```text
Garman-Klass
Parkinson
Rogers-Satchell
Yang-Zhang
Close-to-close
Desvio padrão
Gauss robusto
ATR
```

O GARCH deve entrar como uma nova camada:

```text
Volatilidade condicional esperada
Bandas condicionais diárias e intraday
Z-score condicional
Persistência da volatilidade
Meia-vida do choque
Forecast h-passos
Sinais de reversão/continuação baseados em esticamento estatístico
```

### 1.4. Observações importantes para o Codex

1. **Evitar refatoração grande de `MainWindow.xaml.cs` agora.** O arquivo é monolítico, mas funciona. Implementar GARCH de forma incremental.
2. **Não colocar otimização GARCH na thread RTD.** O RTD deve continuar leve. A estimação entra no `_quantTimer` ou em cache.
3. **Não recalcular MLE pesado a cada tick.** Estimar parâmetros em intervalos controlados e reutilizar entre ticks.
4. **Não adicionar pacote externo sem necessidade.** O projeto é `.NET Framework`; implementar otimizador simples em C# puro.
5. **O README menciona .NET Framework 4.8, mas o `.csproj` está com `TargetFrameworkVersion v4.6.1`.** Antes de usar recursos modernos, decidir se mantém 4.6.1 ou atualiza para 4.8. Para menor risco, implementar compatível com C#/.NET Framework já usado.
6. **O gráfico já desenha `KeyLevel`.** A forma mais simples de mostrar bandas GARCH no gráfico é converter bandas GARCH em `KeyLevel` com `Source = "GARCH-D"` ou `Source = "GARCH-I"`.

---

## 2. Decisão conceitual: como usar GARCH no seu sistema

### 2.1. O que o GARCH deve responder

A tela GARCH deve responder estas perguntas:

```text
1. O dólar está normal ou estatisticamente esticado contra o D-1?
2. O dólar está normal ou estatisticamente esticado dentro do intraday?
3. A volatilidade atual está subindo, caindo ou normalizando?
4. O preço está perto de banda de compra ou venda?
5. Existe rejeição/aceitação na banda?
6. O volume/fluxo confirma reversão ou continuação?
7. Qual stop e alvo fazem sentido em pontos?
8. O setup é robusto, acionável, monitorável, fraco ou bloqueado?
```

### 2.2. O que o GARCH não deve fazer sozinho

O GARCH **não deve ser sinal direcional isolado**.

Não implementar regra simplista assim:

```text
Preço tocou banda inferior => compra automática.
Preço tocou banda superior => venda automática.
```

Implementar assim:

```text
GARCH mede o esticamento estatístico.
Volume profile mostra aceitação/rejeição.
Fluxo mostra se agressão confirma ou falha.
Candle/tick confirma gatilho.
Score decide se vira oportunidade.
```

### 2.3. Regra de referência principal

Para o **GARCH diário**, a referência principal deve ser o **ajuste/fechamento D-1**.

Ordem recomendada para referência diária:

```csharp
snapshot.AjusteAnterior
?? snapshot.FechamentoAnterior
?? snapshot.Ajuste
?? previousDay.Close
```

Observação: se `AJU` no Profit representar ajuste atual em tempo real e `AJA` representar ajuste anterior, preferir `AJA`. A tela deve mostrar a fonte usada:

```text
Referência diária: AJA / FEC / AJU / CSV Close D-1
```

Para o **GARCH intraday**, a referência deve ser configurável:

1. VWAP/MED do dia.
2. POC intraday do volume profile.
3. Abertura do dia.
4. Abertura do range inicial.
5. Último pivô/nível relevante.

Regra prática:

```text
D-1 / ajuste define o mapa estatístico do dia.
VWAP/POC/abertura definem o mapa estatístico intraday.
A oportunidade boa aparece quando os dois mapas concordam.
```

---

## 3. Fórmulas que devem ser implementadas

### 3.1. Retorno logarítmico

Para histórico diário:

```text
r_t = ln(Close_t / Close_{t-1})
```

Para intraday:

```text
r_t = ln(CloseBar_t / CloseBar_{t-1})
```

Usar `double` internamente na estimação. Converter para `decimal` apenas na saída de preço/pontos.

### 3.2. Modelo GARCH(1,1)

Modelo de retorno:

```text
r_t = μ + ε_t
ε_t = σ_t z_t
z_t ~ Normal(0,1) ou t-Student
```

Variância condicional:

```text
σ²_t = ω + α ε²_{t-1} + β σ²_{t-1}
```

Restrições:

```text
ω > 0
α >= 0
β >= 0
α + β < 1
```

Persistência:

```text
persistência = α + β
```

Meia-vida do choque:

```text
meia_vida = ln(0,5) / ln(α + β)
```

Só calcular meia-vida quando:

```text
0 < α + β < 1
```

### 3.3. Volatilidade condicional em pontos

Se `σ_t` é a volatilidade em retorno logarítmico:

```text
σ_pontos ≈ referência × σ_t
```

Para banda exata em log:

```text
Banda superior kσ = referência × exp(k × σ_t)
Banda inferior kσ = referência × exp(-k × σ_t)
```

Para WDO, arredondar ao tick:

```text
preço_arredondado = Math.Round(preço / tickSize) × tickSize
```

### 3.4. Bandas recomendadas

Implementar estas bandas por padrão:

```text
±0,5σ
±1,0σ
±1,5σ
±2,0σ
±2,5σ
±3,0σ opcional
```

Na tela, destacar:

```text
±0,5σ = zona de atenção / rotação
±1,0σ = zona estatística relevante
±1,5σ = zona de operação com confirmação
±2,0σ = extremo intraday / possível exaustão
±2,5σ = evento extremo / risco de tendência forte
```

### 3.5. Z-score condicional

Z-score contra referência:

```text
z_preço = ln(preço_atual / referência) / σ_t
```

A forma aproximada em pontos também pode ser exibida:

```text
z_pontos = (preço_atual - referência) / σ_pontos
```

A tela deve mostrar os dois contextos:

```text
z diário: preço atual contra referência D-1 usando σ diária GARCH
z intraday: preço atual contra VWAP/POC/abertura usando σ intraday GARCH
```

### 3.6. Forecast de volatilidade h-passos

Volatilidade de longo prazo:

```text
σ²_LP = ω / (1 - α - β)
```

Forecast h-passos:

```text
σ²_{t+h} = σ²_LP + (α + β)^(h-1) × (σ²_{t+1} - σ²_LP)
```

Usar para:

```text
1 candle à frente
5 candles à frente
15 candles à frente
fim do pregão, quando houver intraday suficiente
próximo pregão, no diário
```

### 3.7. Volume e MDH

Criar leituras que cruzam volatilidade condicional com volume:

```text
Volume_z = (ln(volume_atual) - média_ln_volume) / desvio_ln_volume
```

Para intraday:

```text
Volume_z_barra = z-score do volume do candle atual contra barras do mesmo timeframe.
```

Leitura:

```text
|z_preço| alto + Volume_z alto + rejeição = exaustão/reversão provável
|z_preço| alto + Volume_z alto + aceitação fora da banda + delta a favor = continuação provável
|z_preço| alto + Volume_z baixo = rompimento suspeito / falso rompimento possível
```

Isso encaixa com a hipótese MDH: volume e número de negócios carregam informação sobre a mistura de distribuições e ajudam a explicar a variação da volatilidade.

---

## 4. Modelo de dados a adicionar

Criar arquivo:

```text
src/RtdDolarNative/Quant/GarchModels.cs
```

### 4.1. Classes recomendadas

```csharp
namespace RtdDolarNative.Quant
{
    public sealed class GarchConfig
    {
        public bool Enabled { get; set; }
        public int DailyWindowDays { get; set; }
        public int DailyMinSamples { get; set; }
        public int IntradayTimeframeSeconds { get; set; }
        public int IntradayMinBars { get; set; }
        public int MaxIntradayBars { get; set; }
        public int RefitIntradaySeconds { get; set; }
        public string MeanMode { get; set; }              // Zero ou Constant
        public string Distribution { get; set; }          // Normal inicialmente
        public string DailyReferenceMode { get; set; }    // PriorAdjustmentThenClose
        public string IntradayReferenceMode { get; set; } // VwapThenPocThenOpen
        public double StationarityCap { get; set; }
        public int MaxIterations { get; set; }
        public double Tolerance { get; set; }
        public double[] BandMultipliers { get; set; }
        public double WinsorizeZ { get; set; }
        public double ReversionMinAbsZDaily { get; set; }
        public double ReversionMinAbsZIntraday { get; set; }
        public double ExtremeAbsZIntraday { get; set; }
        public double VolumeZMin { get; set; }
        public int MaxEntryDistanceTicks { get; set; }
    }
}
```

```csharp
public sealed class GarchFitResult
{
    public bool Success { get; set; }
    public string Status { get; set; }
    public string Scope { get; set; } // Daily ou Intraday
    public int Samples { get; set; }
    public double Mu { get; set; }
    public double Omega { get; set; }
    public double Alpha { get; set; }
    public double Beta { get; set; }
    public double Persistence { get; set; }
    public double HalfLifePeriods { get; set; }
    public double LongRunVariance { get; set; }
    public double LongRunSigma { get; set; }
    public double LastVariance { get; set; }
    public double NextVariance { get; set; }
    public double NextSigma { get; set; }
    public double NegativeLogLikelihood { get; set; }
    public int Iterations { get; set; }
    public DateTimeOffset CalculatedAt { get; set; }
    public string Warning { get; set; }
}
```

```csharp
public sealed class GarchBandLevel
{
    public string Scope { get; set; }       // Daily ou Intraday
    public string ReferenceName { get; set; }
    public decimal ReferencePrice { get; set; }
    public double Sigma { get; set; }       // multiplicador: -2, -1.5, etc.
    public decimal Price { get; set; }
    public decimal DistanceCurrent { get; set; }
    public decimal DistanceReference { get; set; }
    public string Side { get; set; }        // Compra, Venda, Centro
    public string Label { get; set; }
    public string Read { get; set; }
    public int ScoreHint { get; set; }
}
```

```csharp
public sealed class GarchSignal
{
    public string Scope { get; set; }
    public string Setup { get; set; }
    public string Direction { get; set; }   // Buy ou Sell
    public decimal Price { get; set; }
    public int Score { get; set; }
    public string Robustness { get; set; }
    public string LevelName { get; set; }
    public decimal? LevelPrice { get; set; }
    public double ZDaily { get; set; }
    public double ZIntraday { get; set; }
    public decimal? StopPrice { get; set; }
    public decimal? Target1 { get; set; }
    public decimal? Target2 { get; set; }
    public decimal? RiskPoints { get; set; }
    public decimal? RewardPoints { get; set; }
    public decimal? RiskReward { get; set; }
    public string Confirmation { get; set; }
    public string Gate { get; set; }
    public string Reasons { get; set; }
}
```

```csharp
public sealed class GarchSnapshot
{
    public GarchSnapshot()
    {
        DailyBands = new List<GarchBandLevel>();
        IntradayBands = new List<GarchBandLevel>();
        Signals = new List<GarchSignal>();
        Backtest = new List<GarchBacktestRow>();
        Warnings = new List<string>();
    }

    public GarchFitResult DailyFit { get; set; }
    public GarchFitResult IntradayFit { get; set; }
    public decimal DailyReference { get; set; }
    public string DailyReferenceName { get; set; }
    public decimal IntradayReference { get; set; }
    public string IntradayReferenceName { get; set; }
    public decimal CurrentPrice { get; set; }
    public double ZDaily { get; set; }
    public double ZIntraday { get; set; }
    public decimal DailySigmaPoints { get; set; }
    public decimal IntradaySigmaPoints { get; set; }
    public string DailyRegime { get; set; }
    public string IntradayRegime { get; set; }
    public string CombinedRead { get; set; }
    public List<GarchBandLevel> DailyBands { get; set; }
    public List<GarchBandLevel> IntradayBands { get; set; }
    public List<GarchSignal> Signals { get; set; }
    public List<GarchBacktestRow> Backtest { get; set; }
    public List<string> Warnings { get; set; }
}
```

```csharp
public sealed class GarchBacktestRow
{
    public string Scope { get; set; }
    public string Direction { get; set; }
    public double Sigma { get; set; }
    public int Samples { get; set; }
    public int Touches { get; set; }
    public int Reversals { get; set; }
    public int Continuations { get; set; }
    public double ReversalRate { get; set; }
    public decimal AverageMfePoints { get; set; }
    public decimal AverageMaePoints { get; set; }
    public decimal ExpectancyPoints { get; set; }
    public double ProfitFactor { get; set; }
    public double Confidence { get; set; }
    public decimal RiskReward { get; set; }
    public double EdgeScore { get; set; }
    public string Read { get; set; }
}
```

### 4.2. Alteração em `QuantResult`

No arquivo:

```text
src/RtdDolarNative/Quant/QuantModels.cs
```

Adicionar:

```csharp
public GarchSnapshot Garch { get; set; }
```

No construtor de `QuantResult`:

```csharp
Garch = new GarchSnapshot();
```

Alternativa menos invasiva: manter `_garch` separado no `MainWindow.xaml.cs`. Minha recomendação é híbrida:

1. Criar `_garch` separado para a tela GARCH.
2. Também permitir que `QuantResult.Garch` receba o snapshot para o gráfico e oportunidades.

---

## 5. Engine GARCH a adicionar

Criar arquivo:

```text
src/RtdDolarNative/Quant/GarchEngine.cs
```

### 5.1. API principal

```csharp
public static class GarchEngine
{
    public static GarchSnapshot Build(GarchBuildInput input)
    {
        // 1. Validar dados.
        // 2. Estimar GARCH diário.
        // 3. Estimar ou aproximar GARCH intraday.
        // 4. Calcular bandas.
        // 5. Calcular z-scores.
        // 6. Gerar sinais.
        // 7. Gerar backtest proxy.
    }
}
```

Criar também:

```csharp
public sealed class GarchBuildInput
{
    public List<DailyBar> DailyBars { get; set; }
    public List<IntradayBar> IntradayBars { get; set; }
    public MarketSnapshot Snapshot { get; set; }
    public FlowMetrics FlowMetrics { get; set; }
    public decimal TickSize { get; set; }
    public GarchConfig Config { get; set; }
    public QuantResult QuantResult { get; set; }
}
```

### 5.2. Estimação diária

Usar `_dailyBars`:

```text
_dailyBars -> DailyBar.Close -> retornos log -> EstimateGarch11
```

Regras:

```text
Mínimo absoluto: 63 retornos.
Mínimo recomendado: 126 retornos.
Ideal: 252+ retornos.
Janela default: 252 ou, se o usuário preferir a janela atual do sistema, usar `Ui.CalculationDays` como visão curta e `Garch.DailyWindowDays` como visão GARCH.
```

Recomendação personalizada:

```text
Manter `Ui.CalculationDays = 45` para indicadores existentes.
Criar `Garch.DailyWindowDays = 252` para GARCH diário.
```

Motivo: GARCH precisa de mais amostra que RSI/ATR curto.

### 5.3. Estimação intraday

Criar barras intraday a partir dos ticks:

```text
TickEvent -> IntradayBarAggregator -> barras de 1min/5min -> retornos log -> EstimateGarch11
```

Se ainda não houver amostra suficiente:

```text
Usar fallback: σ_intraday = σ_diária × sqrt(timeframeSeconds / sessionSeconds)
```

Exibir claramente na tela:

```text
Intraday GARCH: estimado
ou
Intraday GARCH: fallback diário escalado
```

Nunca ocultar o fallback.

### 5.4. Otimizador Marquardt

Implementar um otimizador Marquardt/damped Newton simples em C# puro.

Função objetivo para distribuição normal:

```text
NLL = 0,5 × Σ [ ln(2π) + ln(h_t) + ε²_t / h_t ]
```

Onde:

```text
h_t = σ²_t
ε_t = r_t - μ
```

Parâmetros otimizados em espaço transformado:

```text
p[0] = μ
p[1] = log(ω)
p[2] = raw_alpha
p[3] = raw_beta
```

Transformação recomendada para garantir estacionariedade:

```csharp
omega = Math.Exp(p[1]);

double ea = Math.Exp(p[2]);
double eb = Math.Exp(p[3]);
double denom = 1.0 + ea + eb;
alpha = stationarityCap * ea / denom;
beta  = stationarityCap * eb / denom;
```

Com:

```text
stationarityCap = 0,995 ou 0,999
```

Isso garante:

```text
α >= 0
β >= 0
α + β < stationarityCap
```

Algoritmo Marquardt:

```text
1. Calcular NLL atual.
2. Calcular gradiente numérico por diferença central.
3. Calcular Hessiana numérica ou aproximação quasi-Hessiana.
4. Resolver: (H + λ diag(H)) step = -g.
5. Testar p_new = p + step.
6. Se NLL melhora: aceitar, reduzir λ.
7. Se NLL piora: rejeitar, aumentar λ.
8. Parar por tolerância, máximo de iterações ou step pequeno.
```

Fallbacks obrigatórios:

```text
Se Hessiana falhar: usar passo de gradiente amortecido.
Se NLL virar NaN/Infinity: rejeitar passo.
Se parâmetros ruins: fallback para EWMA/GARCH fixo.
Se retornos insuficientes: retornar Success=false com Warning.
```

### 5.5. Inicialização dos parâmetros

```csharp
double variance = SampleVariance(returns);
double mu = meanMode == "Zero" ? 0.0 : Average(returns);
double alpha0 = 0.08;
double beta0 = 0.88;
double omega0 = variance * Math.Max(1e-6, 1.0 - alpha0 - beta0);
```

Se a série for muito curta ou instável:

```text
alpha0 = 0,06
beta0 = 0,90
omega0 = variance × 0,04
```

### 5.6. Tratamento robusto de retornos

Antes de estimar:

1. Remover retornos `NaN`, `Infinity`, zero inválido.
2. Winsorizar retornos extremos acima de `WinsorizeZ`, default `6`.
3. Não remover eventos reais do mercado sem registrar aviso. Se winsorizar, exibir:

```text
Retornos extremos winsorizados: N
```

### 5.7. Arredondamento de bandas

Usar tick size:

```csharp
private static decimal RoundToTick(decimal value, decimal tickSize)
{
    if (tickSize <= 0m) tickSize = 0.5m;
    return Math.Round(value / tickSize, 0, MidpointRounding.AwayFromZero) * tickSize;
}
```

---

## 6. Barra intraday: nova estrutura necessária

Criar arquivo:

```text
src/RtdDolarNative/MarketData/IntradayBar.cs
```

```csharp
public sealed class IntradayBar
{
    public string Asset { get; set; }
    public DateTimeOffset Start { get; set; }
    public int Seconds { get; set; }
    public decimal Open { get; set; }
    public decimal High { get; set; }
    public decimal Low { get; set; }
    public decimal Close { get; set; }
    public decimal Volume { get; set; }
    public decimal Quantity { get; set; }
    public int TradeCount { get; set; }
    public decimal Delta { get; set; }
}
```

Criar arquivo:

```text
src/RtdDolarNative/MarketData/IntradayBarAggregator.cs
```

Responsabilidade:

```text
Receber TickEvent.
Agrupar por ativo e timeframe.
Manter barras recentes de 1min e 5min.
Não travar a UI.
Não depender de SQLite inicialmente.
```

API sugerida:

```csharp
public sealed class IntradayBarAggregator
{
    public IntradayBarAggregator(int maxBars, params int[] timeframesSeconds)
    public void Add(TickEvent tick)
    public void AddFromSnapshot(MarketSnapshot snapshot) // fallback se não houver ticks reais
    public List<IntradayBar> GetBars(string asset, int seconds)
    public void ResetAsset(string asset)
}
```

Integração no `MainWindow.xaml.cs`:

```csharp
private readonly IntradayBarAggregator _intradayBars;
```

No construtor:

```csharp
_intradayBars = new IntradayBarAggregator(_config.Garch.MaxIntradayBars, 60, 300);
```

No método atual:

```csharp
private void ProbeService_TickReceived(TickEvent tick)
{
    _ticks.Add(tick);
    _intradayBars.Add(tick);
}
```

No `ProbeService_SnapshotReceived`, se não houver Times real, pode alimentar fallback quando `ULT` mudar:

```csharp
if (previousSnapshot == null || quoteChanged)
{
    _flowProcessor.Post(snapshot);
    _intradayBars.AddFromSnapshot(snapshot); // fallback controlado por deduplicação
}
```

A classe deve deduplicar preço repetido no mesmo timestamp para não criar barras artificiais demais.

---

## 7. Configuração nova em `AppConfig`

### 7.1. Alterar `AppConfig.cs`

Adicionar propriedade em `AppConfig`:

```csharp
public GarchConfig Garch { get; set; }
```

No construtor/default:

```csharp
Garch = new GarchConfig();
```

Criar classe em `Config/AppConfig.cs` ou em `Quant/GarchModels.cs`. Melhor em `Config/AppConfig.cs` se for serializada pelo appsettings.

### 7.2. Exemplo de `appsettings.json`

Adicionar:

```json
"Garch": {
  "Enabled": true,
  "DailyWindowDays": 252,
  "DailyMinSamples": 126,
  "IntradayTimeframeSeconds": 60,
  "IntradayMinBars": 90,
  "MaxIntradayBars": 1200,
  "RefitIntradaySeconds": 30,
  "MeanMode": "Constant",
  "Distribution": "Normal",
  "DailyReferenceMode": "PriorAdjustmentThenClose",
  "IntradayReferenceMode": "VwapThenPocThenOpen",
  "StationarityCap": 0.995,
  "MaxIterations": 120,
  "Tolerance": 0.00000001,
  "BandMultipliers": [0.5, 1.0, 1.5, 2.0, 2.5],
  "WinsorizeZ": 6.0,
  "ReversionMinAbsZDaily": 0.75,
  "ReversionMinAbsZIntraday": 1.5,
  "ExtremeAbsZIntraday": 2.0,
  "VolumeZMin": 1.0,
  "MaxEntryDistanceTicks": 6
}
```

### 7.3. Normalização

Adicionar método `Normalize()` em `GarchConfig`:

```text
DailyWindowDays entre 63 e 1000.
DailyMinSamples entre 63 e DailyWindowDays.
IntradayTimeframeSeconds permitido: 60, 300, 900.
IntradayMinBars mínimo 45, ideal 90.
MaxIntradayBars mínimo 300.
StationarityCap entre 0,90 e 0,999.
BandMultipliers default se vazio.
MaxIterations entre 20 e 500.
Tolerance > 0.
```

---

## 8. Integração com `QuantEngine`

### 8.1. Opção recomendada

No curto prazo, implementar GARCH como engine separada e chamar no `MainWindow.Recalculate()`.

Atual:

```csharp
_result = QuantEngine.Build(_dailyBars, calcSnapshot, _config.Rtd.TickSize, SelectedCalculationDays());
```

Novo:

```csharp
_result = QuantEngine.Build(_dailyBars, calcSnapshot, _config.Rtd.TickSize, SelectedCalculationDays());

_garch = GarchEngine.Build(new GarchBuildInput
{
    DailyBars = _dailyBars,
    IntradayBars = _intradayBars.GetBars(FocusedAsset(), _config.Garch.IntradayTimeframeSeconds),
    Snapshot = calcSnapshot,
    FlowMetrics = _flowProcessor.GetMetrics(FocusedAsset()),
    TickSize = _config.Rtd.TickSize,
    Config = _config.Garch,
    QuantResult = _result
});

_result.Garch = _garch;
AppendGarchLevelsToQuantResult(_result, _garch);
```

### 8.2. Função para converter bandas GARCH em `KeyLevel`

No `MainWindow.xaml.cs` ou no `GarchEngine`:

```csharp
private void AppendGarchLevelsToQuantResult(QuantResult result, GarchSnapshot garch)
{
    if (result == null || garch == null)
        return;

    foreach (GarchBandLevel band in garch.DailyBands.Concat(garch.IntradayBands))
    {
        KeyLevel level = new KeyLevel();
        level.Price = band.Price;
        level.Label = band.Label;
        level.Type = band.Side == "Compra" ? "Suporte" : band.Side == "Venda" ? "Resistencia" : "Valor";
        level.Source = band.Scope == "Daily" ? "GARCH-D" : "GARCH-I";
        level.Score = band.ScoreHint;
        level.Evidence = band.Read;
        level.Direction = band.Side;
        level.Distance = result.Intraday == null ? 0m : band.Price - result.Intraday.Price;
        level.Tags = "GARCH;" + band.Scope + ";" + band.Sigma.ToString("N1", _ptBr);
        result.KeyLevels.Add(level);
    }

    result.Confluence = MergeInterestLevels(result.KeyLevels, result.Intraday.Price, _config.Rtd.TickSize);
}
```

Se `MergeInterestLevels` for privado em `QuantEngine`, não chamar diretamente. Alternativas:

1. Adicionar bandas antes do `QuantEngine` montar confluência, mais invasivo.
2. Criar lista separada para gráfico e tela GARCH.
3. Usar `result.KeyLevels.Add(...)` e deixar o gráfico desenhar `KeyLevels`, mesmo sem entrar na confluência.

Recomendação inicial:

```text
Adicionar bandas GARCH a `result.KeyLevels`.
Não mexer na confluência no primeiro commit.
Depois integrar no score.
```

---

## 9. Nova tela GARCH

### 9.1. Índice de aba

Hoje existem constantes até:

```csharp
private const int TabRtdComplete = 22;
```

Adicionar:

```csharp
private const int TabGarch = 23;
```

Adicionar `TabItem Header="GARCH"` no final do `MainTabs`, para não quebrar índices atuais.

### 9.2. Menu superior

Adicionar em `MainWindow.xaml` nos menus de `Analise` e `Janelas`:

```xml
<MenuItem Header="GARCH" InputGestureText="Ctrl+Shift+G" Tag="23" Click="NavigateMenuItem_Click" />
```

Adicionar botão rápido na barra de análise:

```xml
<Button Content="GARCH" ToolTip="GARCH 1:1" Tag="23" Click="TopNavButton_Click" Style="{StaticResource TopNavButtonStyle}" MinWidth="66" />
```

Adicionar atalho em `MainWindow_KeyDown`, se existir switch de atalhos:

```text
Ctrl+Shift+G -> NavigateToTab(TabGarch)
```

### 9.3. Layout recomendado da tela

A tela deve ser específica, não apenas mais uma grade em `Indicadores`.

Estrutura visual:

```text
┌──────────────────────────────────────────────────────────────────────────────┐
│ GARCH 1:1 | Ativo | RTD | CSV | σ D1 | σ Intraday | zD | zI | Sinal atual     │
├──────────────────────────────────────────────────────────────────────────────┤
│ [Cards Diário]        [Cards Intraday]        [Leitura / Score / Gate]       │
├──────────────────────────────────────────────────────────────────────────────┤
│ [Gráfico com bandas GARCH D/I]             │ [Oportunidades GARCH]          │
│                                             │ Compra/Venda/Stop/Alvo/Gate   │
├──────────────────────────────────────────────────────────────────────────────┤
│ [Bandas Diário] [Bandas Intraday] [Parâmetros] [Backtest/Auditoria]          │
└──────────────────────────────────────────────────────────────────────────────┘
```

### 9.4. Controles da tela

Adicionar nomes de controles:

```xml
<TextBlock x:Name="GarchStateText" />
<TextBlock x:Name="GarchAssetText" />
<TextBlock x:Name="GarchDailySigmaText" />
<TextBlock x:Name="GarchIntradaySigmaText" />
<TextBlock x:Name="GarchZDailyText" />
<TextBlock x:Name="GarchZIntradayText" />
<TextBlock x:Name="GarchSignalText" />
<TextBlock x:Name="GarchQualityText" />

<ComboBox x:Name="GarchIntradayTimeframeCombo" />
<ComboBox x:Name="GarchDailyReferenceCombo" />
<ComboBox x:Name="GarchIntradayReferenceCombo" />
<Button Content="Recalcular" Click="RefreshGarchButton_Click" />
<Button Content="Oportunidades" Tag="16" Click="TopNavButton_Click" />

<charts:NativeChartControl x:Name="GarchChartControl" />

<DataGrid x:Name="GarchDailyBandsGrid" />
<DataGrid x:Name="GarchIntradayBandsGrid" />
<DataGrid x:Name="GarchSignalsGrid" />
<DataGrid x:Name="GarchParametersGrid" />
<DataGrid x:Name="GarchBacktestGrid" />
<DataGrid x:Name="GarchAuditGrid" />
```

Se não quiser criar novo `NativeChartControl`, pode reaproveitar o `ChartControl` apenas na aba `Grafico`. Mas para a tela GARCH é melhor ter um `GarchChartControl` próprio, recebendo os mesmos dados.

### 9.5. Cards superiores

Cards recomendados:

| Card | Conteúdo |
|---|---|
| Ativo | `FocusedAsset()` |
| CSV | `_dailyBars.Count` pregões |
| RTD | `_probeService.Status`, idade do snapshot |
| Referência diária | AJA/FEC/AJU/Close D-1 + preço |
| σ diária GARCH | pontos e percentual |
| z diário | valor e leitura |
| Referência intraday | VWAP/POC/Abertura + preço |
| σ intraday | pontos e timeframe |
| z intraday | valor e leitura |
| Regime | normal, alerta, extremo, fallback |
| Sinal | compra, venda, aguardar, continuação |

### 9.6. Grids

#### `GarchDailyBandsGrid`

Colunas:

```text
Lado | Sigma | Preço | Dist. Atual | Dist. Ref. | Leitura | Score
```

Exemplo:

```text
Venda | +1,0σ | 5.270,00 | +74,0 | +46,5 | resistência estatística diária | 78
Compra | -1,0σ | 5.177,50 | -18,5 | -46,0 | suporte estatístico diário | 82
```

#### `GarchIntradayBandsGrid`

Colunas:

```text
Lado | Sigma | Preço | Dist. Atual | Referência | Leitura | Aceitação
```

#### `GarchSignalsGrid`

Colunas:

```text
Setup | Direção | Preço | Score | Robustez | Nível | Stop | Alvo 1 | Alvo 2 | R/R | Confirmação | Gate | Motivos
```

#### `GarchParametersGrid`

Colunas:

```text
Escopo | μ | ω | α | β | α+β | Meia-vida | σ próxima | σ LP | NLL | Iterações | Status
```

#### `GarchBacktestGrid`

Colunas:

```text
Escopo | Dir | Sigma | Amostras | Toques | Reversão | Continuação | Exp | PF | Conf | R/R | Edge | Leitura
```

#### `GarchAuditGrid`

Colunas:

```text
Item | Valor | Detalhe
```

Exemplos de auditoria:

```text
Fonte diária | CSV WDOFUT_F_0 | 498 pregões
Fonte intraday | Times real / fallback snapshot | 94 barras 1min
Referência diária | AJA | 5223,50
Referência intraday | VWAP/MED | 5189,45
Otimização | Marquardt normal | 37 iterações
Fallback | Não | -
Warnings | 0 | -
```

---

## 10. Regras de compra e venda baseadas em GARCH

### 10.1. Compra de reversão

Setup:

```text
Preço esticado para baixo + rejeição + fluxo não confirma mais venda.
```

Condições base:

```text
zDaily <= -0,75
ou
zIntraday <= -1,50
```

Condições fortes:

```text
zIntraday <= -2,00
Preço perto de banda inferior GARCH intraday
Preço perto de banda inferior GARCH diária
Preço abaixo de VAL/LVN e volta para dentro
Delta vendedor perde força
Imbalance começa a favorecer compra
Candle/tick recupera mínima anterior ou volta para dentro da banda
Volume_z >= 1,0 em região extrema
```

Gatilho:

```text
1. Tocar banda inferior.
2. Não aceitar abaixo.
3. Voltar para dentro da banda.
4. Romper máxima do candle/barra de rejeição.
```

Stop:

```text
Stop técnico = mínima da rejeição - max(2 ticks, 0,25σ_intraday)
```

Alvos:

```text
Alvo 1 = banda -1σ ou VWAP/POC, o que estiver mais perto.
Alvo 2 = referência intraday.
Alvo 3 = banda oposta ou referência diária, somente se fluxo confirmar.
```

### 10.2. Venda de reversão

Setup:

```text
Preço esticado para cima + rejeição + fluxo não confirma mais compra.
```

Condições base:

```text
zDaily >= +0,75
ou
zIntraday >= +1,50
```

Condições fortes:

```text
zIntraday >= +2,00
Preço perto de banda superior GARCH intraday
Preço perto de banda superior GARCH diária
Preço acima de VAH/LVN e volta para dentro
Delta comprador perde força
Imbalance começa a favorecer venda
Candle/tick perde mínima da barra de rejeição
Volume_z >= 1,0 em região extrema
```

Stop:

```text
Stop técnico = máxima da rejeição + max(2 ticks, 0,25σ_intraday)
```

Alvos:

```text
Alvo 1 = banda +1σ ou VWAP/POC, o que estiver mais perto.
Alvo 2 = referência intraday.
Alvo 3 = banda oposta ou referência diária, somente se fluxo confirmar.
```

### 10.3. Continuação por aceitação fora da banda

Nem todo toque em banda é reversão. O sistema deve reconhecer continuação.

Compra de continuação:

```text
Preço rompe banda superior +2σ intraday.
Fecha/permanece acima.
Delta comprador positivo.
Imbalance comprador.
Pullback respeita a banda rompida.
Volume_z alto.
```

Venda de continuação:

```text
Preço rompe banda inferior -2σ intraday.
Permanece abaixo.
Delta vendedor negativo.
Imbalance vendedor.
Pullback respeita banda rompida por baixo.
Volume_z alto.
```

Tela deve mostrar:

```text
Tipo: Continuação, não reversão.
Gate: aceitar fora da banda / aguardar pullback.
```

### 10.4. Zona sem operação

Se:

```text
|zDaily| < 0,50
|zIntraday| < 1,00
Preço perto do POC/VWAP
```

Mostrar:

```text
Leitura: miolo estatístico / sem assimetria GARCH.
Ação: aguardar bordas.
```

---

## 11. Score GARCH personalizado para seu sistema

O projeto já usa `Robusto`, `Acionavel`, `Monitorar`, `Fraco`, `Bloqueado`. O GARCH deve seguir o mesmo padrão.

### 11.1. Score base

```text
Score inicial: 45
```

Adicionar:

| Condição | Pontos |
|---|---:|
| `|zDaily| >= 0,75` | +6 |
| `|zDaily| >= 1,00` | +8 adicionais |
| `|zIntraday| >= 1,50` | +10 |
| `|zIntraday| >= 2,00` | +10 adicionais |
| Preço até `MaxEntryDistanceTicks` da banda | +6 |
| Banda diária e intraday apontam mesmo lado | +10 |
| Perto de POC/VAH/VAL/HVN/LVN relevante | +5 |
| Rejeição detectada | +12 |
| Volume_z >= 1,0 | +5 |
| Delta/imbalance confirmam direção | +8 |
| Backtest direcional favorável | +5 |
| Parâmetros GARCH estacionários e fit bom | +4 |

Subtrair:

| Condição | Penalidade |
|---|---:|
| Fluxo conflita | -12 |
| Sem CSV suficiente | -15 |
| Intraday fallback diário escalado | -6 |
| Snapshot atrasado | -10 a -25 |
| Tape derivado | aplicar cap |
| TopOfBookOnly | aplicar cap |
| `α + β >= 0,995` | -8 e aviso |
| Otimização falhou | -15 e usar fallback |
| Preço no miolo `|zIntraday| < 1` | cap 55 |

### 11.2. Caps de robustez

```text
Sem RTD ULT: cap 45
CSV < 63 retornos: cap 55
CSV < 126 retornos: cap 72
Intraday sem barras suficientes: cap 78
TopOfBookOnly: cap Flow.TopOfBookOnlyScoreCap
DerivedTape: cap Flow.DerivedTapeScoreCap
FullTimesAndTrades ou FullDepth: cap até 95
GARCH fallback: cap 74
Fluxo conflitando: cap 72
```

### 11.3. Classes de robustez

```text
Robusto:
score >= 85
cap >= 90
GARCH diário válido
GARCH intraday válido ou proxy muito bem identificado
fluxo real confirma
dados frescos
edge/backtest não bloqueia

Acionável:
score >= 75
pelo menos 3 confirmações
sem conflito grave

Monitorar:
score >= 60
falta gatilho ou confirmação

Fraco:
score < 60

Bloqueado:
dados insuficientes, snapshot ausente ou fit inválido sem fallback confiável
```

---

## 12. Integração com Oportunidades e Scanner

### 12.1. Nova origem de oportunidade

Adicionar as oportunidades GARCH dentro de `BuildOpportunityRows()`.

Hoje o método junta:

```text
FlowSignal
QuantSignal
```

Adicionar:

```text
GarchSignal
```

Opção simples:

1. Converter `GarchSignal` em `OpportunityRow` dentro de um novo método:

```csharp
private List<OpportunityRow> BuildGarchOpportunityRows(RtdAssetConfig asset, MarketSnapshot snapshot, FlowMetrics metrics)
```

2. Chamar em `BuildOpportunityRows()` após `BuildQuantOpportunityRows()`.

Exemplo:

```csharp
List<OpportunityRow> garchRows = BuildGarchOpportunityRows(asset, snapshot, metrics);
rows.AddRange(garchRows);
```

### 12.2. Matching com fluxo

Criar função:

```csharp
private FlowSignal FindMatchingFlowSignal(List<FlowSignal> flowSignals, GarchSignal garchSignal)
```

Mesma lógica do `FindMatchingFlowSignal` para `QuantSignal`:

```text
mesma direção
nível/preço próximo dentro de max(16 ticks, 4 pontos)
maior score
mais recente
```

### 12.3. Matching com QuantSignal

Criar:

```csharp
private QuantSignal FindMatchingQuantSignal(GarchSignal garchSignal)
```

Motivo: se um sinal GARCH de venda em +2σ também coincide com Bollinger mean reversion ou reversão estatística já existente, aumentar score.

### 12.4. Exibição na tela Oportunidades

`OpportunityRow.Setup` deve mostrar:

```text
GARCH reversão compra
GARCH reversão venda
GARCH continuação compra
GARCH continuação venda
```

`OpportunityRow.Level`:

```text
GARCH-I -2,0σ @ 5.179,00
GARCH-D -1,0σ @ 5.176,50
```

`OpportunityRow.Reasons`:

```text
zD -0,98; zI -2,12; rejeição em banda; volume_z 1,4; delta virou comprador; referência diária AJA 5223,50; σD 46,9 pts; σI 5,2 pts
```

---

## 13. Integração com gráfico

### 13.1. Caminho mais simples

O `NativeChartControl` já desenha níveis de `QuantResult.KeyLevels`. Então:

```text
Adicionar bandas GARCH como KeyLevel.
Source = GARCH-D ou GARCH-I.
Type = Suporte / Resistencia / Valor.
Label = GARCH D -1,0σ, GARCH I +2,0σ etc.
```

O gráfico passará a mostrar as linhas sem grande alteração.

### 13.2. Melhorias futuras no `NativeChartControl`

Adicionar filtros:

```csharp
private bool _showGarchLevels = true;
public bool ShowGarchLevels { get; set; }
```

Em `IsLevelCategoryEnabled` adicionar:

```csharp
bool hasGarch = tokens.Any(x => x.IndexOf("GARCH", StringComparison.OrdinalIgnoreCase) >= 0);
```

Se `hasGarch`, usar `ShowGarchLevels`.

Adicionar checkbox no topo:

```xml
<CheckBox x:Name="ChartShowGarchCheckBox" Content="GARCH" ... />
```

Mas para o primeiro commit, pode usar `ShowKeyLevels` já existente.

---

## 14. Backtest GARCH

### 14.1. Backtest diário proxy

Criar em `GarchEngine`:

```csharp
private static List<GarchBacktestRow> BacktestDailyGarch(List<DailyBar> bars, GarchConfig config, decimal tickSize)
```

Lógica:

```text
Para cada dia t depois de uma janela mínima:
1. Estimar GARCH usando dados até t-1.
2. Referência = close/ajuste de t-1.
3. Calcular bandas de ±1σ, ±1,5σ e ±2σ.
4. Verificar se high/low do dia t tocou a banda.
5. Medir reversão conservadora.
```

Para compra:

```text
Touch: Low_t <= banda inferior
Entry proxy: banda inferior
Reversal: Close_t > Entry ou High_t atinge pelo menos Entry + target
Continuation: Close_t abaixo da banda ou Low_t avança contra stop
MFE: High_t - Entry
MAE: Entry - Low_t
```

Para venda:

```text
Touch: High_t >= banda superior
Entry proxy: banda superior
Reversal: Close_t < Entry ou Low_t atinge Entry - target
Continuation: Close_t acima da banda ou High_t avança contra stop
MFE: Entry - Low_t
MAE: High_t - Entry
```

Ser conservador porque OHLC diário não mostra a ordem intradiária.

### 14.2. Backtest intraday

Fase 1: sessão atual somente.

```text
Usar IntradayBarAggregator.
Medir toques nas bandas intraday e resultado nos próximos N candles.
```

Fase 2: persistir barras intraday em SQLite.

Criar futuro arquivo:

```text
src/RtdDolarNative/MarketData/IntradayBarSqliteStore.cs
```

Tabela:

```sql
CREATE TABLE intraday_bars (
  asset TEXT NOT NULL,
  timeframe_seconds INTEGER NOT NULL,
  start_utc TEXT NOT NULL,
  open REAL NOT NULL,
  high REAL NOT NULL,
  low REAL NOT NULL,
  close REAL NOT NULL,
  volume REAL,
  quantity REAL,
  trade_count INTEGER,
  delta REAL,
  PRIMARY KEY(asset, timeframe_seconds, start_utc)
);
```

---

## 15. Linhas de implementação por arquivo

### 15.1. `src/RtdDolarNative/RtdDolarNative.csproj`

Adicionar:

```xml
<Compile Include="Quant\GarchModels.cs" />
<Compile Include="Quant\GarchEngine.cs" />
<Compile Include="MarketData\IntradayBar.cs" />
<Compile Include="MarketData\IntradayBarAggregator.cs" />
```

Se criar otimizador separado:

```xml
<Compile Include="Quant\MarquardtOptimizer.cs" />
```

### 15.2. `src/RtdDolarNative/Config/AppConfig.cs`

Adicionar:

```csharp
public GarchConfig Garch { get; set; }
```

E classe serializável `GarchConfig` se não ficar em `Quant/GarchModels.cs`.

### 15.3. `src/RtdDolarNative/appsettings.json`

Adicionar seção `Garch` conforme item 7.2.

### 15.4. `src/RtdDolarNative/Quant/QuantModels.cs`

Adicionar:

```csharp
public GarchSnapshot Garch { get; set; }
```

No construtor:

```csharp
Garch = new GarchSnapshot();
```

### 15.5. `src/RtdDolarNative/Quant/GarchEngine.cs`

Implementar:

```text
Build
BuildDaily
BuildIntraday
EstimateGarch11
NegativeLogLikelihood
ForecastNextVariance
BuildBands
BuildSignals
BuildDailyBacktestProxy
RoundToTick
ResolveDailyReference
ResolveIntradayReference
VolumeZScore
ClassifyRegime
```

### 15.6. `src/RtdDolarNative/MainWindow.xaml.cs`

Adicionar campos:

```csharp
private const int TabGarch = 23;
private readonly IntradayBarAggregator _intradayBarAggregator;
private GarchSnapshot _garch;
private DateTimeOffset _lastGarchIntradayFit = DateTimeOffset.MinValue;
```

No construtor:

```csharp
_intradayBarAggregator = new IntradayBarAggregator(_config.Garch.MaxIntradayBars, 60, 300);
```

No `ProbeService_TickReceived`:

```csharp
_ticks.Add(tick);
_intradayBarAggregator.Add(tick);
```

No `ProbeService_SnapshotReceived`, opcional:

```csharp
_intradayBarAggregator.AddFromSnapshot(snapshot);
```

No `Recalculate()`:

```csharp
_result = QuantEngine.Build(...);
_garch = GarchEngine.Build(...);
_result.Garch = _garch;
AppendGarchLevelsToQuantResult(_result, _garch);
RenderResult(calcSnapshot);
```

No render ativo:

```csharp
bool showGarch = selectedTab == TabGarch;
...
if (showGarch) RenderGarch(snapshot);
```

Adicionar método:

```csharp
private void RenderGarch(MarketSnapshot snapshot)
```

Adicionar botão:

```csharp
private void RefreshGarchButton_Click(object sender, RoutedEventArgs e)
{
    Recalculate();
    RenderGarch(FocusedSnapshot() ?? _lastSnapshot);
}
```

Adicionar rows:

```csharp
private sealed class GarchBandRow { ... }
private sealed class GarchSignalRow { ... }
private sealed class GarchParameterRow { ... }
private sealed class GarchAuditRow { ... }
private sealed class GarchBacktestViewRow { ... }
```

### 15.7. `src/RtdDolarNative/MainWindow.xaml`

Adicionar menu, botão e `TabItem Header="GARCH"`.

### 15.8. `tests/QuantEngineTests/Program.cs`

Adicionar testes:

```text
GarchEstimateIsStationary
GarchBandsUsePriorAdjustmentReference
GarchBandsRoundToTick
GarchDailySignalAppearsAtLowerBand
GarchIntradayFallsBackWhenInsufficientBars
GarchDoesNotCreateSignalInMiddleOfRange
GarchBacktestProducesDirectionalRows
```

---

## 16. Pseudocódigo do `GarchEngine.Build`

```csharp
public static GarchSnapshot Build(GarchBuildInput input)
{
    GarchSnapshot output = new GarchSnapshot();

    if (input == null || input.Config == null || !input.Config.Enabled)
    {
        output.Warnings.Add("GARCH desabilitado.");
        return output;
    }

    MarketSnapshot snapshot = input.Snapshot;
    decimal current = ResolveCurrentPrice(snapshot, input.DailyBars);
    output.CurrentPrice = current;

    // Diário
    List<double> dailyReturns = BuildDailyReturns(input.DailyBars, input.Config);
    output.DailyReference = ResolveDailyReference(snapshot, input.DailyBars, out string dailyRefName);
    output.DailyReferenceName = dailyRefName;

    output.DailyFit = EstimateGarch11(dailyReturns, "Daily", input.Config);

    if (output.DailyFit.Success)
    {
        output.DailySigmaPoints = ToPoints(output.DailyReference, output.DailyFit.NextSigma);
        output.ZDaily = ZLog(current, output.DailyReference, output.DailyFit.NextSigma);
        output.DailyBands = BuildBands("Daily", dailyRefName, output.DailyReference, current, output.DailyFit.NextSigma, input.TickSize, input.Config);
        output.DailyRegime = ClassifyZ(output.ZDaily);
    }
    else
    {
        output.Warnings.Add(output.DailyFit.Warning);
    }

    // Intraday
    List<IntradayBar> intraday = input.IntradayBars ?? new List<IntradayBar>();
    output.IntradayReference = ResolveIntradayReference(snapshot, input.FlowMetrics, input.QuantResult, out string intradayRefName);
    output.IntradayReferenceName = intradayRefName;

    if (intraday.Count >= input.Config.IntradayMinBars)
    {
        List<double> intradayReturns = BuildIntradayReturns(intraday, input.Config);
        output.IntradayFit = EstimateGarch11(intradayReturns, "Intraday", input.Config);
    }
    else
    {
        output.IntradayFit = BuildIntradayFallbackFromDaily(output.DailyFit, input.Config);
        output.Warnings.Add("Intraday com poucas barras; usando fallback diário escalado.");
    }

    if (output.IntradayFit != null && output.IntradayFit.Success)
    {
        output.IntradaySigmaPoints = ToPoints(output.IntradayReference, output.IntradayFit.NextSigma);
        output.ZIntraday = ZLog(current, output.IntradayReference, output.IntradayFit.NextSigma);
        output.IntradayBands = BuildBands("Intraday", intradayRefName, output.IntradayReference, current, output.IntradayFit.NextSigma, input.TickSize, input.Config);
        output.IntradayRegime = ClassifyZ(output.ZIntraday);
    }

    output.Signals = BuildSignals(output, input);
    output.Backtest = BuildBacktest(input.DailyBars, input.Config, input.TickSize);
    output.CombinedRead = BuildCombinedRead(output, input);
    return output;
}
```

---

## 17. Tela GARCH: exemplo de leitura esperada

Exemplo baseado na lógica que discutimos durante a análise do WDO:

```text
Ajuste D-1: 5223,50
Preço atual: 5188,50
σ diária GARCH: 46,9 pts
z diário: -0,75

Referência intraday: POC/VWAP 5189,45
σ intraday: 5,2 pts
z intraday: -0,18

Leitura:
Preço está deslocado contra D-1, mas está no miolo intraday. Sem assimetria para nova entrada. Aguardar bordas: 5199/5202 para venda de rejeição ou 5184/5179 para compra de defesa.
```

Quando preço toca banda superior:

```text
Preço: 5202,00
z intraday: +2,4
Região: acima da banda +2σ intraday
Fluxo: delta comprador perdeu força
Volume: alto
Sinal: GARCH reversão venda
Stop: acima da máxima da rejeição
Alvo 1: +1σ / 5195
Alvo 2: POC/VWAP / 5190
Robustez: Acionável se fluxo real confirmar
```

Quando preço toca banda inferior:

```text
Preço: 5177,50
z diário: próximo de -1σ
z intraday: abaixo de -2σ
Sinal: GARCH reversão compra
Confirmação: voltar para dentro da banda e romper máxima da barra de rejeição
Alvo 1: 5184/5188
Alvo 2: POC/VWAP
```

---

## 18. Validação funcional

### 18.1. Validação sem Profit aberto

1. Abrir app sem Profit.
2. Carregar CSV diário.
3. Entrar na tela GARCH.
4. Deve mostrar GARCH diário se houver CSV suficiente.
5. Deve mostrar aviso de RTD ausente.
6. Não deve travar.
7. Intraday deve aparecer como indisponível/fallback, nunca como estimado real.

### 18.2. Validação com Profit aberto e somente cotação

1. Ligar `Cotacao`.
2. Confirmar `ULT`, `ABE`, `MAX`, `MIN`, `FEC`, `AJU/AJA`, `MED`, `VOL`.
3. Tela GARCH deve calcular:

```text
referência diária
bandas diárias
z diário
bandas intraday por fallback ou snapshot
```

4. Score deve ser capado se `Book`/`Times` estiverem desligados.

### 18.3. Validação com Times e Book

1. Ligar `Book` e `Times`.
2. Confirmar `FlowMetrics.DataQuality` como `FullTimesAndTrades` ou `FullDepth` quando disponível.
3. Confirmar barras intraday sendo criadas.
4. GARCH intraday passa de fallback para estimado quando atingir `IntradayMinBars`.
5. Sinais robustos só aparecem com:

```text
RTD fresco
CSV suficiente
fluxo confirmando
garch válido
edge/backtest não bloqueando
```

### 18.4. Validação das bandas

Testar manualmente com valores conhecidos:

```text
referência = 5223,50
σ = 0,00898
k = 1
banda inferior log = 5223,50 × exp(-0,00898) ≈ 5176,8
banda superior log = 5223,50 × exp(+0,00898) ≈ 5270,6
```

Arredondar ao tick `0,5`.

### 18.5. Validação de performance

Regras:

```text
RTD thread não pode chamar GarchEngine.Estimate.
GarchEngine deve rodar no timer quant ou por ação manual.
Intraday MLE deve ser refeito no máximo a cada 30 segundos por padrão.
Tela deve apenas renderizar o último `_garch` calculado.
```

---

## 19. Testes automatizados propostos

Adicionar ao `tests/QuantEngineTests/Program.cs`.

### 19.1. `GarchEstimateIsStationary`

```text
Dado uma série sintética de retornos,
quando EstimateGarch11 roda,
então alpha >= 0, beta >= 0 e alpha + beta < 1.
```

### 19.2. `GarchBandsUsePriorAdjustmentReference`

```text
Snapshot com AJA = 5223,5 e FEC = 5200.
Bandas diárias devem usar AJA quando DailyReferenceMode = PriorAdjustmentThenClose.
```

### 19.3. `GarchBandsRoundToTick`

```text
TickSize = 0,5.
Todas as bandas devem ter preço múltiplo de 0,5.
```

### 19.4. `GarchNoSignalInMiddle`

```text
Preço perto da referência.
zDaily < 0,5 e zIntraday < 1,0.
Não deve gerar compra/venda.
```

### 19.5. `GarchBuySignalAtLowerBand`

```text
Preço abaixo de -2σ intraday.
Fluxo neutro/confirmando.
Deve gerar sinal Buy Monitorar/Acionável.
```

### 19.6. `GarchSellSignalAtUpperBand`

```text
Preço acima de +2σ intraday.
Fluxo neutro/confirmando venda.
Deve gerar sinal Sell Monitorar/Acionável.
```

### 19.7. `GarchIntradayFallbackIsCapped`

```text
Com poucas barras intraday,
GARCH intraday usa fallback e score máximo deve ser capado.
```

---

## 20. Plano de execução por etapas para Codex 5.3 Spark

### Etapa 0 — Preparação

1. Criar branch/commit de backup antes de alterar.
2. Confirmar build atual.
3. Confirmar se o projeto deve ficar em `.NET Framework 4.6.1` ou migrar para `4.8`. Para menor risco, não migrar agora.
4. Não mexer no instalador nesta etapa.

### Etapa 1 — Modelos e configuração

Implementar:

```text
GarchModels.cs
GarchConfig
AppConfig.Garch
appsettings.json com seção Garch
csproj com novos Compile Include
```

Critério de aceite:

```text
App abre.
Config carrega.
Sem alteração visual ainda.
```

### Etapa 2 — Engine diária

Implementar:

```text
BuildDailyReturns
EstimateGarch11
NegativeLogLikelihood normal
Marquardt optimizer
ResolveDailyReference
BuildDailyBands
ZDaily
DailyRegime
```

Critério de aceite:

```text
Com CSV carregado, GarchEngine.Build retorna DailyFit.Success=true.
Bandas são múltiplos de tick.
α + β < 1.
```

### Etapa 3 — Intraday bars

Implementar:

```text
IntradayBar
IntradayBarAggregator
integração com TickReceived
fallback AddFromSnapshot
GetBars por ativo/timeframe
```

Critério de aceite:

```text
Com RTD rodando, barras intraday são acumuladas.
Sem RTD real, app não quebra.
```

### Etapa 4 — GARCH intraday

Implementar:

```text
BuildIntradayReturns
Estimate intraday se houver barras suficientes
Fallback diário escalado se não houver
ZIntraday
IntradayBands
IntradayRegime
```

Critério de aceite:

```text
Tela/log indica estimado ou fallback.
Score capado quando fallback.
```

### Etapa 5 — Sinais GARCH

Implementar:

```text
BuildSignals
Compra reversão
Venda reversão
Continuação compra
Continuação venda
Gate/Reasons/Stop/Targets
```

Critério de aceite:

```text
Sinal não aparece no miolo.
Sinal aparece perto das bandas com gatilho.
Gate mostra o que falta.
```

### Etapa 6 — Tela GARCH

Implementar:

```text
TabGarch = 23
menu e botão superior
TabItem Header="GARCH"
RenderGarch
grids e cards
botão Recalcular
```

Critério de aceite:

```text
Ctrl+Shift+G abre a tela.
Tela mostra bandas, parâmetros, z-scores, sinais e auditoria.
Não trava com CSV ausente.
```

### Etapa 7 — Integração com gráfico e oportunidades

Implementar:

```text
AppendGarchLevelsToQuantResult
GARCH no NativeChartControl via KeyLevel
BuildGarchOpportunityRows
Oportunidades mostram sinais GARCH
Scanner considera melhor sinal GARCH quando aplicável
```

Critério de aceite:

```text
Bandas aparecem no gráfico.
Oportunidades exibem GARCH com qualidade e motivos.
Robustez respeita caps.
```

### Etapa 8 — Backtest e testes

Implementar:

```text
GarchBacktestDailyProxy
GarchBacktestGrid
Testes automatizados no projeto QuantEngineTests
```

Critério de aceite:

```text
Testes passam.
Backtest não promete resultado, apenas audita toques/reversão.
```

### Etapa 9 — Documentação

Atualizar:

```text
README.md
docs/quant_trading.md
docs/validacao.md
docs/arquitetura.md
```

Adicionar seção:

```text
Tela GARCH
Referência D-1 vs Intraday
Interpretação das bandas
Limitações
```

---

## 21. Prompt pronto para Codex 5.3 Spark

Use este prompt no Codex 5.3 Spark:

```text
Você está trabalhando no projeto WPF C# `poin-dolar-windows-main`, uma plataforma nativa de análise do WDO via RTD do Profit. Implemente uma tela completa de GARCH(1,1) conforme o arquivo `plano_garch_poin_dolar_windows.md`.

Regras obrigatórias:
1. Não criar envio de ordens.
2. Não rodar estimação GARCH na thread RTD.
3. Não travar a UI.
4. Não adicionar dependências externas desnecessárias.
5. Manter compatibilidade com o estilo atual do projeto e com .NET Framework usado no csproj.
6. Implementar incrementalmente, preservando abas existentes e seus índices.
7. Adicionar a nova aba no final com `TabGarch = 23`.
8. Usar `AJA`/ajuste anterior ou fechamento D-1 como referência principal do GARCH diário.
9. Usar VWAP/POC/abertura como referência intraday.
10. Mostrar quando o intraday for estimado e quando for fallback.

Tarefas:
1. Criar `Quant/GarchModels.cs`.
2. Criar `Quant/GarchEngine.cs` com GARCH(1,1), NLL normal e otimizador Marquardt/damped Newton.
3. Criar `MarketData/IntradayBar.cs` e `MarketData/IntradayBarAggregator.cs`.
4. Adicionar `GarchConfig` em `AppConfig` e `appsettings.json`.
5. Integrar `_intradayBarAggregator` ao `ProbeService_TickReceived` e fallback em `ProbeService_SnapshotReceived`.
6. Integrar `_garch = GarchEngine.Build(...)` no `Recalculate()`.
7. Converter bandas GARCH em `KeyLevel` para aparecerem no gráfico.
8. Criar `TabItem Header="GARCH"` com cards, gráfico, grids de bandas, sinais, parâmetros, backtest e auditoria.
9. Criar `RenderGarch()` e rows privadas no `MainWindow.xaml.cs`.
10. Integrar sinais GARCH em `Oportunidades` sem quebrar sinais quant/fluxo existentes.
11. Adicionar testes no `tests/QuantEngineTests/Program.cs`.
12. Atualizar documentação.

Critérios de aceite:
- Compila em Debug|x64.
- Abre sem Profit e sem CSV sem quebrar.
- Com CSV diário, mostra GARCH diário, parâmetros, σ em pontos, bandas e z diário.
- Com RTD/ticks, acumula barras intraday e mostra GARCH intraday ou fallback claramente.
- Sinais GARCH não aparecem no miolo estatístico.
- Sinais aparecem em bandas extremas com score/gate/motivos.
- Oportunidades e gráfico mostram bandas GARCH.
- Robustez é capada por qualidade de dados, fallback, CSV insuficiente e fluxo conflitante.
```

---

## 22. Checklist final de aceite operacional

Antes de considerar pronto:

```text
[ ] Build Debug|x64 passa.
[ ] App abre em idle.
[ ] CSV diário carrega.
[ ] Tela GARCH abre por menu e Ctrl+Shift+G.
[ ] Sem CSV: tela mostra aviso, não exceção.
[ ] Com CSV: GARCH diário mostra α, β, α+β, meia-vida, σ, bandas.
[ ] Bandas usam referência D-1, preferencialmente AJA/FEC.
[ ] Bandas são arredondadas ao tick 0,5.
[ ] Intraday mostra estimado ou fallback.
[ ] Z diário e z intraday aparecem.
[ ] Miolo estatístico não gera oportunidade.
[ ] Toque em banda extrema gera monitoramento/acionável apenas com confirmação.
[ ] Stop e alvo aparecem em pontos.
[ ] Oportunidades recebem sinais GARCH.
[ ] Gráfico mostra bandas GARCH.
[ ] Scores respeitam cap por dados ruins.
[ ] `Robusto` não aparece com TopOfBookOnly, tape derivado, CSV baixo, fluxo conflitante ou GARCH fallback fraco.
[ ] Testes GARCH passam.
[ ] Documentação atualizada.
```

---

## 23. Prioridade recomendada

A ordem mais segura é:

```text
1. GARCH diário + tela simples.
2. Bandas no gráfico.
3. Intraday aggregator + fallback.
4. GARCH intraday real.
5. Sinais GARCH.
6. Oportunidades.
7. Backtest e refinamento.
```

Não tente implementar tudo em um único commit gigante. O projeto já é grande, principalmente `MainWindow.xaml.cs`; commits menores reduzem risco.

---

## 24. Resultado esperado para o usuário final

A tela GARCH deve permitir ao trader olhar rapidamente e entender:

```text
- Onde estão as bandas estatísticas do dia contra D-1.
- Onde estão as bandas estatísticas intraday contra VWAP/POC/abertura.
- Se o preço está no miolo, em zona de atenção ou em extremo.
- Se o ponto é de compra, venda, continuação ou apenas monitoramento.
- Qual é o stop técnico em pontos.
- Quais são os alvos naturais.
- Se o fluxo confirma, conflita ou ainda está neutro.
- Se a leitura é robusta ou limitada pela qualidade dos dados.
```

A filosofia da implementação deve ser:

```text
GARCH = régua estatística de volatilidade.
Volume Profile = mapa de aceitação/rejeição.
Order Flow = confirmação operacional.
Score = síntese auditável, não promessa de acerto.
```
