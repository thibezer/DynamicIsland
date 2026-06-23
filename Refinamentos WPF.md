# Diretrizes de Animação Fluid (Apple HIG) — Implementação WPF
**Versão 2.0 — Revisada e corrigida**

---

## 1. A Física de Mola Ativa vs. Curvas de Bézier Tradicionais

Curvas tradicionais (`EaseInOut`, Bézier cúbicas) calculam o movimento puramente por tempo e velocidade relativa. O resultado é uma aceleração artificial no início e uma desaceleração abrupta no final — claramente sintético ao olhar humano.

A Apple usa exclusivamente **Spring Physics (Física de Mola)** no SwiftUI e na Dynamic Island. O elemento é tratado como uma massa física presa a uma mola com amortecimento por atrito (sistema massa-mola-amortecedor). Isso permite reações dinâmicas com peso, momento e um *overshoot* natural antes de estabilizar.

### Parâmetros fundamentais

| Parâmetro | Símbolo | Descrição |
|---|---|---|
| **Damping Ratio** | `ζ` (zeta) | Coeficiente de amortecimento. Controla elasticidade. |
| **Response** | `T` | Tempo de resposta da mola em segundos. Define rigidez. |

**Valores da Apple para a Dynamic Island:**

| Cenário | DampingRatio | Response | Duration calculada |
|---|---|---|---|
| Expansão/recolhimento | `0.85` | `0.55s` | `~710ms` |
| Snappy (clique rápido) | `0.85` | `0.35s` | `~450ms` |
| Mola visível (bouncy) | `0.60` | `0.40s` | `~730ms` |

> **Por que `DampingRatio = 0.85`?** Produz sistema *subamortecido* com overshoot de apenas ~0.6% do tamanho-alvo — perceptível como "vivo" mas sem parecer borrachudo.

---

## 2. A Equação Diferencial de Movimento

Para um sistema subamortecido (`ζ < 1`), partindo do repouso (`x(0) = 0`, `ẋ(0) = 0`) com alvo em `1.0`:

```
x(t) = 1 − e^(−αt) · [cos(ωd·t) + (α/ωd)·sin(ωd·t)]
```

Onde:
- `ωn = 2π / T` — frequência natural
- `α  = ζ · ωn` — fator de decaimento exponencial  
- `ωd = ωn · √(1 − ζ²)` — frequência angular amortecida

### Cálculo da Duration correta

O WPF exige uma `Duration` fixa para o `DoubleAnimation`. O tempo de estabilização real da mola (threshold 0.1%) é:

```
settleTime = −ln(0.001) / α  =  (6.908 · T) / (2π · ζ)
```

Para os presets da Apple:
- `ζ=0.85, T=0.55s` → `settleTime ≈ 711ms`
- `ζ=0.85, T=0.35s` → `settleTime ≈ 453ms`

> ⚠️ **Não use `550ms` hardcoded** — esse valor não acompanha mudanças em `Response`. Use o método `GetSettleTime()` da classe abaixo.

---

## 3. `SpringEase.cs` — Classe Refinada

> **Correções em relação à versão 1.0:**
> - ✅ `DependencyProperty` para `DampingRatio` e `Response` (necessário para XAML)
> - ✅ Override de `CreateInstanceCore()` (obrigatório — `EasingFunctionBase` é `Freezable`)
> - ✅ Guarda contra divisão por zero quando `DampingRatio` próximo de 1.0
> - ✅ `settle_factor` calculado dinamicamente (não mais `* 5.0` fixo)
> - ✅ Construtor força `EasingMode = EasingMode.EaseOut` por padrão
> - ✅ `GetSettleTime()` público para calcular a `Duration` correta automaticamente

```csharp
using System;
using System.Windows;
using System.Windows.Media.Animation;

namespace DynamicIslandWindows
{
    /// <summary>
    /// Easing function que replica com precisão matemática a física de mola (Spring Physics)
    /// usada pelo SwiftUI / Dynamic Island da Apple.
    ///
    /// USO OBRIGATÓRIO:
    ///   - EasingMode deve ser EasingMode.EaseOut (definido no construtor por padrão).
    ///   - A Duration do DoubleAnimation deve ser GetSettleTime(), não um valor fixo.
    ///
    /// XAML:
    ///   <anim:SpringEase DampingRatio="0.85" Response="0.55"/>
    /// </summary>
    public class SpringEase : EasingFunctionBase
    {
        // ── DependencyProperties (obrigatório para uso em XAML e Storyboard) ─────────

        public static readonly DependencyProperty DampingRatioProperty =
            DependencyProperty.Register(
                nameof(DampingRatio),
                typeof(double),
                typeof(SpringEase),
                new PropertyMetadata(0.85));

        public static readonly DependencyProperty ResponseProperty =
            DependencyProperty.Register(
                nameof(Response),
                typeof(double),
                typeof(SpringEase),
                new PropertyMetadata(0.55));

        // ── Propriedades ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Coeficiente de amortecimento (ζ).
        /// 0.85 = padrão Apple (Dynamic Island). Range válido: [0.01, 0.999].
        /// Não use 1.0 — causa divisão por zero na fórmula de ωd.
        /// </summary>
        public double DampingRatio
        {
            get => (double)GetValue(DampingRatioProperty);
            set => SetValue(DampingRatioProperty, Math.Clamp(value, 0.01, 0.999));
        }

        /// <summary>
        /// Tempo de resposta da mola em segundos (T).
        /// 0.55 = expansão fluida | 0.35 = snappy (clique). Range válido: > 0.
        /// </summary>
        public double Response
        {
            get => (double)GetValue(ResponseProperty);
            set => SetValue(ResponseProperty, Math.Max(value, 0.01));
        }

        // ── Construtor ────────────────────────────────────────────────────────────────

        public SpringEase()
        {
            // EaseOut é o único modo fisicamente correto para uma mola.
            // EaseIn produziria movimento invertido (WPF aplica f_out(t) = 1 - f_in(1-t)).
            EasingMode = EasingMode.EaseOut;
        }

        // ── Núcleo da equação diferencial ─────────────────────────────────────────────

        protected override double EaseInCore(double normalizedTime)
        {
            double t = normalizedTime;

            // Guards de boundary — WPF pode chamar com valores ligeiramente fora de [0,1]
            if (t <= 0.0) return 0.0;
            if (t >= 1.0) return 1.0;

            double zeta    = DampingRatio;
            double T       = Response;

            // Frequência natural (rad/s)
            double omegaN  = (2.0 * Math.PI) / T;

            // Frequência amortecida — max() previne raiz negativa por erro de ponto flutuante
            double omegaD  = omegaN * Math.Sqrt(Math.Max(0.0, 1.0 - zeta * zeta));

            // Fator de decaimento exponencial
            double alpha   = zeta * omegaN;

            // Settle factor: quantas "vezes T" são necessárias para estabilizar a 0.1%.
            // Derivado de: settleTime = -ln(0.001) / alpha = 6.908 / (2π·ζ) * T
            // Logo settle_factor = 6.908 / (2π·ζ) — independente de T.
            double settleFactor = -Math.Log(0.001) / (2.0 * Math.PI * zeta);

            // Mapeia t ∈ [0,1] para o tempo físico real que cobre o ciclo de estabilização
            double physicalTime = t * T * settleFactor;

            // Resolução da equação diferencial para sistema subamortecido
            double envelope = Math.Exp(-alpha * physicalTime);
            double cosTerm  = Math.Cos(omegaD * physicalTime);
            double sinTerm  = (omegaD > 1e-10)
                ? (alpha / omegaD) * Math.Sin(omegaD * physicalTime)
                : 0.0;  // Guarda: omegaD → 0 quando zeta → 1

            return 1.0 - envelope * (cosTerm + sinTerm);
        }

        // ── Obrigatório: Freezable / clone do WPF ────────────────────────────────────

        protected override Freezable CreateInstanceCore()
        {
            // O sistema de animação do WPF clona easing functions durante Storyboard.Begin().
            // Sem este override, InvalidOperationException em tempo de execução.
            return new SpringEase
            {
                DampingRatio = this.DampingRatio,
                Response     = this.Response,
                EasingMode   = this.EasingMode
            };
        }

        // ── Utilitário ────────────────────────────────────────────────────────────────

        /// <summary>
        /// Retorna o TimeSpan ideal para usar como Duration no DoubleAnimation,
        /// calculado a partir dos parâmetros físicos da mola (threshold 0.1%).
        ///
        /// Uso:
        ///   var anim = new DoubleAnimation { Duration = appleSpring.GetSettleTime() };
        /// </summary>
        public TimeSpan GetSettleTime()
        {
            double omegaN   = (2.0 * Math.PI) / Response;
            double alpha    = DampingRatio * omegaN;
            double settleSeconds = -Math.Log(0.001) / alpha;
            return TimeSpan.FromSeconds(settleSeconds);
        }
    }
}
```

---

## 4. Integração no Code-Behind (`NativeIslandWindow.xaml.cs`)

### 4.1. Animação de Redimensionamento

```csharp
private void AnimateBorder(double targetWidth, double targetHeight)
{
    var appleSpring = new SpringEase
    {
        DampingRatio = 0.85,
        Response     = 0.55
    };

    // Duration calculada pela própria mola — nunca hardcoded
    var duration = appleSpring.GetSettleTime();

    var widthAnim = new DoubleAnimation(islandBorder.Width, targetWidth, duration)
    {
        EasingFunction = appleSpring
    };

    // Altura converge ~15% mais rápido — comportamento real da Dynamic Island
    var heightSpring = new SpringEase
    {
        DampingRatio = 0.85,
        Response     = 0.45  // response menor = mais rígida/rápida
    };

    var heightAnim = new DoubleAnimation(islandBorder.Height, targetHeight, heightSpring.GetSettleTime())
    {
        EasingFunction = heightSpring
    };

    // BeginAnimation substitui animações anteriores — seguro para chamadas rápidas
    islandBorder.BeginAnimation(Border.WidthProperty,  widthAnim);
    islandBorder.BeginAnimation(Border.HeightProperty, heightAnim);
}
```

> **Por que `Response` diferente para altura?**  
> A Dynamic Island expande a largura e a altura com molas independentes. A largura lidera e a altura "segue" ligeiramente mais rápida. Isso cria a sensação de squash-and-stretch orgânico.

### 4.2. Transição Suave de Cores nos Toggles

```csharp
private void UpdateToggleButton(Border border, TextBlock icon, bool isOn)
{
    var duration = TimeSpan.FromMilliseconds(350);

    Color targetBg     = isOn ? Color.FromArgb(0x33, 0xF6, 0xA7, 0x33)
                               : Color.FromArgb(0x33, 0x50, 0x50, 0x50);
    Color targetBorder = isOn ? Color.FromArgb(0x66, 0xF6, 0xA7, 0x33)
                               : Color.FromArgb(0x66, 0x80, 0x80, 0x80);
    Color targetIcon   = isOn ? Color.FromRgb(0xF6, 0xA7, 0x33)
                               : Colors.White;

    AnimateColor(border, Border.BackgroundProperty,   targetBg,     duration);
    AnimateColor(border, Border.BorderBrushProperty,  targetBorder, duration);
    AnimateColor(icon,   TextBlock.ForegroundProperty, targetIcon,   duration);
}

/// <summary>
/// Anima a cor de qualquer SolidColorBrush em qualquer FrameworkElement.
/// Cria o brush automaticamente caso o elemento use null ou não-SolidColorBrush.
/// </summary>
private static void AnimateColor(
    FrameworkElement element,
    DependencyProperty brushProperty,
    Color targetColor,
    TimeSpan duration)
{
    if (element == null) return;

    // Garante que o brush é mutável (SolidColorBrush não-frozen)
    var currentBrush = element.GetValue(brushProperty) as SolidColorBrush;
    Color initialColor = (currentBrush != null && !currentBrush.IsFrozen)
        ? currentBrush.Color
        : (currentBrush?.Color ?? Colors.Transparent);

    if (currentBrush == null || currentBrush.IsFrozen)
    {
        var newBrush = new SolidColorBrush(initialColor);
        element.SetValue(brushProperty, newBrush);
        currentBrush = newBrush;
    }

    var anim = new ColorAnimation(targetColor, duration)
    {
        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
    };

    currentBrush.BeginAnimation(SolidColorBrush.ColorProperty, anim);
}
```

> **Atenção:** O método original usava três métodos separados (`AnimateBorderColor`, `AnimateBorderBrushColor`, `AnimateTextForeColor`) com código duplicado. O método unificado `AnimateColor` cobre todos os casos via `DependencyProperty`.

---

## 5. Uso em XAML (com DependencyProperty)

Com as `DependencyProperty` implementadas, a `SpringEase` pode ser usada diretamente em XAML, sem code-behind:

```xml
<!-- Namespace no topo da janela: -->
<!-- xmlns:anim="clr-namespace:DynamicIslandWindows" -->

<Border x:Name="island" Width="160" Height="40" CornerRadius="20">
  <Border.Triggers>
    <EventTrigger RoutedEvent="MouseLeftButtonDown">
      <BeginStoryboard>
        <Storyboard>

          <!-- Largura: mola padrão Apple -->
          <DoubleAnimation
              Storyboard.TargetName="island"
              Storyboard.TargetProperty="Width"
              To="380"
              Duration="0:0:0.711">
            <DoubleAnimation.EasingFunction>
              <anim:SpringEase DampingRatio="0.85" Response="0.55"/>
            </DoubleAnimation.EasingFunction>
          </DoubleAnimation>

          <!-- Altura: mola ligeiramente mais rígida -->
          <DoubleAnimation
              Storyboard.TargetName="island"
              Storyboard.TargetProperty="Height"
              To="120"
              Duration="0:0:0.582">
            <DoubleAnimation.EasingFunction>
              <anim:SpringEase DampingRatio="0.85" Response="0.45"/>
            </DoubleAnimation.EasingFunction>
          </DoubleAnimation>

          <!-- Conteúdo: aparece após a expansão iniciar (~40% da duração) -->
          <DoubleAnimation
              Storyboard.TargetName="islandContent"
              Storyboard.TargetProperty="Opacity"
              From="0" To="1"
              BeginTime="0:0:0.28"
              Duration="0:0:0.22"/>

        </Storyboard>
      </BeginStoryboard>
    </EventTrigger>
  </Border.Triggers>
</Border>
```

---

## 6. Presets Prontos

```csharp
/// <summary>Presets de mola para cenários comuns da Dynamic Island.</summary>
public static class SpringPresets
{
    /// <summary>Expansão/recolhimento padrão — estilo Dynamic Island.</summary>
    public static SpringEase Expansion => new SpringEase { DampingRatio = 0.85, Response = 0.55 };

    /// <summary>Resposta rápida para cliques e toggles.</summary>
    public static SpringEase Snappy => new SpringEase { DampingRatio = 0.85, Response = 0.35 };

    /// <summary>Mola com bounce visível — alertas e notificações.</summary>
    public static SpringEase Bouncy => new SpringEase { DampingRatio = 0.60, Response = 0.40 };

    /// <summary>Transição suave sem bounce — mudanças de conteúdo internas.</summary>
    public static SpringEase Smooth => new SpringEase { DampingRatio = 0.95, Response = 0.50 };
}

// Uso:
// var spring = SpringPresets.Expansion;
// var anim   = new DoubleAnimation(from, to, spring.GetSettleTime())
//              { EasingFunction = spring };
```

---

## 7. Fluxo Pomodoro na Dynamic Island

```
[Estado Idle] ──(click Pomodoro)──> [Estado Foco — 25min]
                                           │
                          (cronômetro em tempo real na pílula)
                                           │
                   [Alerta: fim do foco] <─┘
                           │
                   [Estado Descanso — 5min]
                           │
                   [Retorna ao Foco] ──────┘
```

- Ícone MDL2 para cronômetro: `\uE916`
- Transição entre estados: `SpringPresets.Snappy` (`Response = 0.35`)
- Atualização do timer: `DispatcherTimer` com interval de `1s`

---

## 9. Diretrizes de Design — WWDC 2025 (Liquid Glass)
> **Fonte:** "What's New in Design" — Apple WWDC 2025, sessão 356  
> Filtrado para o que é diretamente aplicável ao Dynamic Island WPF.

---

### 9.1. Sistema de Formas (Shape System)

A Apple define três tipos de forma, e o Dynamic Island usa todos eles em estados diferentes:

| Tipo | Definição | Uso no Dynamic Island |
|---|---|---|
| **Fixed** | `CornerRadius` constante | Nunca — formas fixas não se adaptam |
| **Capsule** | `CornerRadius = Height / 2` | Estado compacto (pílula em repouso) |
| **Concentric** | `CornerRadius = parent_radius − padding` | Estado expandido (conteúdo interno) |

A regra fundamental é que o `CornerRadius` do conteúdo interno nunca deve ser estimado — ele é *calculado* a partir do container pai:

```
radius_conteúdo = radius_island - margin_interna
```

**Implementação em WPF:**

```csharp
/// <summary>
/// Calcula o CornerRadius concêntrico para um elemento filho,
/// seguindo a regra geométrica da Apple (WWDC 2025).
/// </summary>
/// <param name="parentRadius">CornerRadius atual do islandBorder.</param>
/// <param name="innerMargin">Padding/margem do filho em relação ao pai.</param>
public static CornerRadius GetConcentricRadius(double parentRadius, double innerMargin)
{
    double r = Math.Max(0, parentRadius - innerMargin);
    return new CornerRadius(r);
}

// Uso durante a animação de expansão:
// double currentIslandRadius = islandBorder.CornerRadius.TopLeft;
// innerContent.CornerRadius = GetConcentricRadius(currentIslandRadius, 8.0);
```

**No estado compacto (pílula):**

```csharp
// Capsule pura: radius = altura / 2
// Sempre que Height mudar, CornerRadius deve seguir:
islandBorder.CornerRadius = new CornerRadius(islandBorder.Height / 2.0);
```

> **Por que isso importa visualmente?** Cantos que não respeitam a concentricidade criam "tensão óptica" — parecem *apertados demais* ou *alargados* em relação ao container. A Apple chama isso de "pinched or flared corners", e é o erro mais comum em layouts aninhados.

---

### 9.2. Animação Ancorada na Fonte (Source-Anchored Animation)

O WWDC 2025 descreve que elementos como a Action Sheet agora "nascem" do ponto de origem da ação, não de uma posição fixa na tela. O mesmo princípio se aplica ao island expandindo:

> *"It springs from the action itself, which serves as the source."*

**Regra prática:** `BeginAnimation` sempre deve usar `From = valor_atual`, nunca `From = 0` ou um tamanho fixo. Caso contrário, a animação "pula" para um ponto de partida artificial.

```csharp
private void AnimateBorder(double targetWidth, double targetHeight)
{
    var appleSpring = new SpringEase { DampingRatio = 0.85, Response = 0.55 };
    var duration    = appleSpring.GetSettleTime();

    // ✅ CORRETO: From = tamanho atual — a expansão parte de onde está
    var widthAnim = new DoubleAnimation(
        fromValue: islandBorder.Width,   // captura o estado atual
        toValue:   targetWidth,
        duration:  duration)
    { EasingFunction = appleSpring };

    // ❌ ERRADO: From = valor fixo — cria "salto" indesejado
    // var widthAnim = new DoubleAnimation(from: 160, to: targetWidth, ...);

    islandBorder.BeginAnimation(Border.WidthProperty,  widthAnim);
    islandBorder.BeginAnimation(Border.HeightProperty,
        new DoubleAnimation(islandBorder.Height, targetHeight, duration)
        { EasingFunction = new SpringEase { DampingRatio = 0.85, Response = 0.45 } });
}
```

> **Atenção com animações interrompidas:** Se `BeginAnimation` for chamado enquanto uma animação anterior ainda está em curso, o WPF usa o *valor animado atual* (não o `Width` da propriedade base) como ponto de partida. Para capturar o valor real no meio da animação, use:
> ```csharp
> double currentWidth = islandBorder.RenderSize.Width; // valor visual real, não o da propriedade
> ```

---

### 9.3. Hierarquia de Estado via Tamanho e Opacidade

O WWDC descreve que ao navegar para um subestado mais profundo, o próprio material Liquid Glass responde — não apenas o conteúdo interno:

> *"Liquid Glass subtly recedes, becoming more opaque and gently growing in size to signal a deeper level of engagement."*

Isso significa que o container do island deve responder ao **nível de profundidade** do estado atual, com uma pequena expansão de tamanho e aumento de opacidade:

```csharp
/// <summary>
/// Aplica os ajustes de hierarquia de profundidade ao island.
/// Chamado quando o estado muda para um subnível (ex: expandido → detalhe).
/// </summary>
private void ApplyDepthHierarchy(IslandDepth depth)
{
    // Pequeno crescimento proporcional ao nível de profundidade
    double sizeGrowth = depth switch
    {
        IslandDepth.Compact  => 0.0,   // pílula em repouso
        IslandDepth.Expanded => 0.0,   // expansão principal
        IslandDepth.Detail   => 4.0,   // +4px width/height — sinaliza subnível
        _                    => 0.0
    };

    double targetOpacity = depth switch
    {
        IslandDepth.Compact  => 0.90,
        IslandDepth.Expanded => 0.92,
        IslandDepth.Detail   => 0.96,  // mais opaco = mais "presente"
        _                    => 0.90
    };

    var quickSpring = SpringPresets.Snappy;

    // Crescimento sutil do container
    if (sizeGrowth > 0)
    {
        islandBorder.BeginAnimation(Border.WidthProperty,
            new DoubleAnimation(
                islandBorder.RenderSize.Width,
                islandBorder.RenderSize.Width + sizeGrowth,
                quickSpring.GetSettleTime())
            { EasingFunction = quickSpring });
    }

    // Transição de opacidade
    islandBorder.BeginAnimation(UIElement.OpacityProperty,
        new DoubleAnimation(targetOpacity, TimeSpan.FromMilliseconds(200))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
}

public enum IslandDepth { Compact, Expanded, Detail }
```

---

### 9.4. Material e Separação Visual

Duas regras do WWDC sobre como o material translúcido deve ser aplicado:

**Regra 1 — Separação obrigatória do conteúdo:**
> *"Elements using Liquid Glass require clear separation from content to maintain legibility."*

No WPF, como o `DwmBlur` foi abandonado, a separação é feita por uma borda sutil. A borda deve estar no `islandBorder`, não suprimida:

```csharp
// Separação mínima recomendada — 1px border translúcido sobre conteúdo
islandBorder.BorderBrush     = new SolidColorBrush(Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF));
islandBorder.BorderThickness = new Thickness(0.5);
```

**Regra 2 — Material no container, não nos filhos:**
> *"Apply the material directly to the control, not its inner views."*

```csharp
// ✅ CORRETO: background no container principal
islandBorder.Background = new SolidColorBrush(Color.FromArgb(0xE0, 0x1C, 0x1C, 0x1E));

// ❌ ERRADO: background nos elementos internos de conteúdo
// iconBorder.Background = ...;
// textPanel.Background  = ...;
```

---

## 10. Problemas Corrigidos em Relação à v1.0

| # | Problema | Versão 1.0 | Versão 2.0 |
|---|---|---|---|
| 1 | `DampingRatio = 1.0` causa divisão por zero em `omegaD` | Clamp até `1.0` | Clamp até `0.999` + guarda `omegaD > 1e-10` |
| 2 | Sem `DependencyProperty` — falha silenciosa em XAML | CLR property simples | `DependencyProperty` registrada |
| 3 | Sem `CreateInstanceCore()` — crash no clone do Storyboard | Ausente | Implementado |
| 4 | `physicalTime` hardcoded (`* 5.0`) — não acompanha damping | `t * response * 5.0` | `t * response * settleFactor(ζ)` |
| 5 | Duration de `550ms` hardcoded | Hardcoded | `GetSettleTime()` dinâmico |
| 6 | Três métodos duplicados para cor | `AnimateBorderColor`, `AnimateBorderBrushColor`, `AnimateTextForeColor` | `AnimateColor` unificado |
| 7 | `EasingMode` não documentado — risco de uso errado | Não documentado | Forçado para `EaseOut` no construtor + comentário |