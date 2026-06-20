# 🎬 Relatório de Animações — Dynamic Island Windows

> **Autor:** AnimationExpert · **Data:** 18/06/2026  
> **Benchmark:** Dynamic Island do iPhone (iOS 16+)  
> **Arquivos Analisados:** `NativeIslandWindow.xaml` (757 linhas) · `NativeIslandWindow.xaml.cs` (1667 linhas)

---

## 📋 Sumário Executivo

O projeto possui **4 categorias de animação** com um total de **~12 animações distintas**. O nível técnico atual é **bom**, mas há oportunidades significativas para atingir o patamar premium da Dynamic Island do iPhone. As principais lacunas são:

1. Todas as transições de painel usam `Visibility.Collapsed`/`Visible` sem fade ou slide — resultado: **corte abrupto**
2. A função `AnimateBorder` é sólida, mas usa apenas `CubicEase` — a Dynamic Island usa curvas **spring/bounce** que dão a sensação orgânica
3. Não há animações de conteúdo **dentro** da ilha (os textos, ícones e thumbnails aparecem/somem instantaneamente)
4. O efeito de drop shadow pulsante no modo Drag & Drop é bom, mas pode ser refinado
5. Faltam micro-interações nos toggles (a troca de cor é instantânea, sem transição)

---

## 1️⃣ Inventário Completo de Animações

### 1.1 — `TactileBorderButtonStyle` (XAML, Linhas 155-180)

| Evento | Propriedade | De → Para | Duração | Curva |
|--------|-------------|-----------|---------|-------|
| `MouseEnter` | ScaleX/Y | 1.0 → 1.03 | 120ms | DecelerationRatio=0.8 |
| `MouseLeave` | ScaleX/Y | 1.03 → 1.0 | 180ms | DecelerationRatio=0.8 |
| `PreviewMouseDown` | ScaleX/Y | → 0.94 | 70ms | DecelerationRatio=0.8 |
| `PreviewMouseUp` | ScaleX/Y | 0.94 → 1.03 | 90ms | DecelerationRatio=0.8 |

**Avaliação:** ⭐⭐⭐⭐ (4/5)  
- ✅ O padrão hover → press → release é excelente e segue a lógica do iOS
- ✅ DecelerationRatio=0.8 funciona como uma boa curva ease-out
- ⚠️ A escala de 3% no hover é boa, mas o press de 6% (1.0→0.94) poderia ser reduzido para 4% (0.96) para ficar mais sutil como no iOS
- ⚠️ Faltaria uma curva **spring** no release para dar aquele "quique" característico

### 1.2 — `AnimateBorder()` — Motor Principal (C#, Linhas 372-387)

```csharp
// CÓDIGO ATUAL
private void AnimateBorder(double targetWidth, double targetHeight, Action? onCompleted = null)
{
    var duration = TimeSpan.FromMilliseconds(300);
    var easing = new CubicEase { EasingMode = EasingMode.EaseOut };

    var widthAnim = new DoubleAnimation(islandBorder.Width, targetWidth, duration) { EasingFunction = easing };
    var heightAnim = new DoubleAnimation(islandBorder.Height, targetHeight, duration) { EasingFunction = easing };
    // ...
}
```

| Propriedade | Tipo | Duração | Curva |
|-------------|------|---------|-------|
| `Width` | DoubleAnimation | 300ms | CubicEase(EaseOut) |
| `Height` | DoubleAnimation | 300ms | CubicEase(EaseOut) |

**Avaliação:** ⭐⭐⭐ (3/5)  
- ✅ CubicEase é funcional e suave
- ⚠️ 300ms é ligeiramente lento — a Dynamic Island do iOS usa ~250ms para abrir e ~200ms para fechar
- ❌ Não há diferenciação de duração entre abertura e fechamento
- ❌ Falta a curva **spring** que dá a identidade orgânica do iOS (overshoot sutil)
- ❌ A mesma animação é usada para todas as transições (expandir, fechar, subpainéis, drop zone)

### 1.3 — Efeito de Drop Shadow Pulsante (C#, Linhas 472-483)

```csharp
// CÓDIGO ATUAL
var dropShadow = new DropShadowEffect {
    Color = Color.FromRgb(0x4C, 0xC2, 0xFF),
    ShadowDepth = 0, Opacity = 1.0, BlurRadius = 25
};
islandBorder.Effect = dropShadow;
DoubleAnimation blurAnim = new DoubleAnimation(15, 35, 
    new Duration(TimeSpan.FromSeconds(1.5))) 
    { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever };
```

**Avaliação:** ⭐⭐⭐⭐ (4/5)  
- ✅ O conceito de pulsação via BlurRadius é excelente
- ✅ AutoReverse + RepeatBehavior.Forever cria o efeito "respiro"
- ⚠️ 1.5s por ciclo é um pouco lento — 0.8-1.0s seria mais alerta/urgente
- ⚠️ A animação é linear (sem easing) — adicionar EaseInOut a tornaria mais orgânica
- ⚠️ Falta animar também a opacidade para um efeito mais rico

### 1.4 — Transições de Painel (Instantâneas via Visibility)

Os painéis **NÃO** possuem animação de transição — apenas alternam entre `Collapsed` e `Visible`:

| Transição | Local (C#) | Animação |
|-----------|------------|----------|
| Compacto → Expandido | `OpenIsland()` L389-408 | ❌ Nenhuma (Visibility direto) |
| Expandido → Compacto | `CloseIsland()` L411-434 | ❌ Nenhuma |
| Expandido → Aparência | `AppearanceSettings_Click()` L942-968 | ❌ Nenhuma |
| Expandido → Wi-Fi | `WifiMenuOpen_Click()` L1631-1644 | ❌ Nenhuma |
| Subpainel → Painel Principal | `BackToMainPanel_Click()` L970-986 | ❌ Nenhuma |
| Compacto → Drop Zone | `ShowDropPanel()` L458-487 | ❌ Nenhuma (apenas border anima) |
| Aparição de senha Wi-Fi | `WifiNetworkItem_Click()` L1550 | ❌ Nenhuma |

**Avaliação:** ⭐ (1/5)  
- ❌ **Este é o ponto mais crítico.** Na Dynamic Island do iOS, o conteúdo faz morph suave — aqui o conteúdo simplesmente aparece/desaparece
- ❌ Quando a ilha abre, o border cresce suavemente mas o conteúdo é mostrado ANTES da animação completar, criando um flash visual

### 1.5 — Toggle de Estado (Background dos Botões)

```csharp
// CÓDIGO ATUAL em UpdateToggleButton() (L592-610)
border.Background = new SolidColorBrush(_themeColor); // INSTANTÂNEO
```

**Avaliação:** ⭐⭐ (2/5)  
- ❌ A troca de cor é instantânea — sem ColorAnimation ou fade
- Na Dynamic Island do iOS, os toggles do Control Center fazem uma transição de cor em ~150ms

### 1.6 — Wi-Fi Switch Indicator (L1358-1373)

```csharp
// CÓDIGO ATUAL
wifiSwitchIndicator.HorizontalAlignment = HorizontalAlignment.Right; // INSTANTÂNEO
```

**Avaliação:** ⭐ (1/5)  
- ❌ O indicador do switch Wi-Fi simplesmente pula de lado — deveria deslizar suavemente com uma animação de TranslateTransform

### 1.7 — Hover nos Botões de Toggle (XAML, Linhas 194-199)

```xml
<!-- CÓDIGO ATUAL -->
<Trigger Property="IsMouseOver" Value="True">
    <Setter Property="Background" Value="#FF444444" />
</Trigger>
```

**Avaliação:** ⭐⭐ (2/5)  
- ❌ Troca de fundo instantânea — deveria ser uma transição animada

### 1.8 — CornerRadius do IslandBorder

O `CornerRadius` permanece fixo em `22.5` durante todas as transições.

**Avaliação:** ⭐⭐⭐ (3/5)  
- ⚠️ Na Dynamic Island real, o corner radius ajusta-se sutilmente com o tamanho — menor quando expandida, maior quando compacta
- No WPF, `CornerRadius` não é animável diretamente, mas pode ser contornado

---

## 2️⃣ Propostas de Melhoria (com Código Exato)

### 🔧 Melhoria 1: Motor AnimateBorder com Spring e Durações Diferenciadas

**Problema:** CubicEase único, mesma duração para tudo, sem caráter orgânico.

```csharp
// ═══════════════════════════════════════════════════════════════════
// SUBSTITUIR AnimateBorder() (Linhas 372-387) POR:
// ═══════════════════════════════════════════════════════════════════
private void AnimateBorder(double targetWidth, double targetHeight, Action? onCompleted = null)
{
    // Detecta se está expandindo ou contraindo
    bool isExpanding = targetWidth > islandBorder.ActualWidth || targetHeight > islandBorder.ActualHeight;
    
    // Durações diferenciadas como no iOS: abertura mais lenta (sensação de expansão),
    // fechamento mais rápido (sensação de snap de volta)
    var duration = isExpanding 
        ? TimeSpan.FromMilliseconds(380)   // Expansão: mais dramática
        : TimeSpan.FromMilliseconds(280);  // Contração: snap rápido

    // Spring-like: ElasticEase com baixa amplitude para overshoot sutil (como iOS)
    IEasingFunction easing;
    if (isExpanding)
    {
        // Overshoot sutil na expansão — como a Dynamic Island que "passa" e volta
        easing = new ElasticEase 
        { 
            EasingMode = EasingMode.EaseOut, 
            Oscillations = 1,     // Apenas 1 oscilação (sem pingar)
            Springiness = 8       // Alta rigidez = overshoot mínimo (~2-3px)
        };
    }
    else
    {
        // Contração: curva suave sem overshoot (BackEase com pull-back mínimo)
        easing = new BackEase 
        { 
            EasingMode = EasingMode.EaseOut, 
            Amplitude = 0.15      // Pull-back sutil antes de estabilizar
        };
    }

    var widthAnim = new DoubleAnimation(islandBorder.ActualWidth, targetWidth, duration) 
    { 
        EasingFunction = easing,
        FillBehavior = FillBehavior.HoldEnd
    };
    
    var heightAnim = new DoubleAnimation(islandBorder.ActualHeight, targetHeight, duration) 
    { 
        EasingFunction = easing,
        FillBehavior = FillBehavior.HoldEnd
    };

    if (onCompleted != null)
    {
        heightAnim.Completed += (s, e) => onCompleted();
    }

    islandBorder.BeginAnimation(Border.WidthProperty, widthAnim);
    islandBorder.BeginAnimation(Border.HeightProperty, heightAnim);
}
```

> **Impacto:** A ilha ganha personalidade orgânica — o overshoot sutil na expansão e o pull-back na contração imitam o comportamento de mola do iOS.

---

### 🔧 Melhoria 2: Fade-In/Out dos Painéis Internos

**Problema:** Conteúdo aparece/desaparece instantaneamente.

```csharp
// ═══════════════════════════════════════════════════════════════════
// ADICIONAR estes métodos auxiliares de animação de conteúdo:
// ═══════════════════════════════════════════════════════════════════

/// <summary>
/// Faz um painel aparecer com fade + slide de baixo para cima (como iOS)
/// </summary>
private void AnimatePanelIn(FrameworkElement panel, double slideDistance = 15, int delayMs = 80)
{
    panel.Visibility = Visibility.Visible;
    panel.Opacity = 0;
    
    // Configura TranslateTransform se não existir
    if (!(panel.RenderTransform is TranslateTransform))
        panel.RenderTransform = new TranslateTransform();
    
    var translate = (TranslateTransform)panel.RenderTransform;
    translate.Y = slideDistance;
    
    var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200))
    {
        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        BeginTime = TimeSpan.FromMilliseconds(delayMs)
    };
    
    var slideUp = new DoubleAnimation(slideDistance, 0, TimeSpan.FromMilliseconds(280))
    {
        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        BeginTime = TimeSpan.FromMilliseconds(delayMs)
    };
    
    panel.BeginAnimation(UIElement.OpacityProperty, fadeIn);
    translate.BeginAnimation(TranslateTransform.YProperty, slideUp);
}

/// <summary>
/// Faz um painel desaparecer com fade-out rápido
/// </summary>
private void AnimatePanelOut(FrameworkElement panel, Action? onCompleted = null)
{
    var fadeOut = new DoubleAnimation(panel.Opacity, 0, TimeSpan.FromMilliseconds(120))
    {
        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
    };
    
    fadeOut.Completed += (s, e) =>
    {
        panel.Visibility = Visibility.Collapsed;
        panel.Opacity = 1; // Reset para próxima abertura
        onCompleted?.Invoke();
    };
    
    panel.BeginAnimation(UIElement.OpacityProperty, fadeOut);
}
```

**Aplicar em `OpenIsland()`:**
```csharp
// ═══════════════════════════════════════════════════════════════════
// SUBSTITUIR OpenIsland() (Linhas 389-409) POR:
// ═══════════════════════════════════════════════════════════════════
private void OpenIsland()
{
    EnsureTopmost();
    if (_isDropZoneActive) DeactivateDropMode();
    _isIslandOpen = true;

    if (mainContentGrid != null)
        mainContentGrid.VerticalAlignment = VerticalAlignment.Bottom;

    if (txtMiniTitle != null) txtMiniTitle.MaxWidth = 260;
    if (txtMiniSubtitle != null) txtMiniSubtitle.MaxWidth = 260;

    miniIslandPanel.Visibility = Visibility.Visible;
    islandSeparator.Visibility = Visibility.Visible;
    dropZonePanel.Visibility = Visibility.Collapsed;

    // 🆕 Painel expandido entra com fade + slide APÓS delay para sincronizar com o border
    AnimatePanelIn(expandedIslandPanel, slideDistance: 20, delayMs: 100);

    AnimateBorder(ISLAND_OPEN_W, ISLAND_OPEN_H);
}
```

**Aplicar em `CloseIsland()`:**
```csharp
// ═══════════════════════════════════════════════════════════════════
// SUBSTITUIR CloseIsland() (Linhas 411-434) POR:
// ═══════════════════════════════════════════════════════════════════
private void CloseIsland()
{
    _isIslandOpen = false;

    if (txtMiniTitle != null) txtMiniTitle.MaxWidth = 220;
    if (txtMiniSubtitle != null) txtMiniSubtitle.MaxWidth = 220;

    // 🆕 Fade-out rápido dos painéis expandidos (não espera terminar)
    AnimatePanelOut(expandedIslandPanel);
    AnimatePanelOut(appearancePanel);
    AnimatePanelOut(wifiPanel);
    
    wifiPasswordPanel.Visibility = Visibility.Collapsed;
    islandSeparator.Visibility = Visibility.Collapsed;
    dropZonePanel.Visibility = Visibility.Collapsed;
    miniIslandPanel.Visibility = Visibility.Visible;

    double dynamicWidth = GetDynamicCompactWidth();
    AnimateBorder(dynamicWidth, _miniBaseHeightLogica, () =>
    {
        if (mainContentGrid != null)
            mainContentGrid.VerticalAlignment = VerticalAlignment.Center;
    });
}
```

> **Impacto:** Os painéis agora deslizam suavemente para dentro com fade, sincronizados com a expansão do border. O fechamento faz fade-out rápido antes do border contrair.

---

### 🔧 Melhoria 3: ColorAnimation nos Toggles

**Problema:** Mudança de cor instantânea nos botões.

```csharp
// ═══════════════════════════════════════════════════════════════════
// SUBSTITUIR UpdateToggleButton() (Linhas 592-610) POR:
// ═══════════════════════════════════════════════════════════════════
private void UpdateToggleButton(Border border, TextBlock icon, bool isOn)
{
    Color targetBg;
    Color targetIcon;
    
    if (isOn)
    {
        targetBg = _themeColor;
        double brightness = (0.299 * _themeColor.R + 0.587 * _themeColor.G + 0.114 * _themeColor.B) / 255;
        targetIcon = brightness > 0.6 ? Colors.Black : Colors.White;
    }
    else
    {
        var brush = this.Resources["IslandInactiveBrush"] as SolidColorBrush;
        targetBg = brush?.Color ?? Color.FromRgb(0x1E, 0x1E, 0x20);
        targetIcon = Colors.White;
    }

    // 🆕 Anima a cor de fundo suavemente (150ms como iOS Control Center)
    var bgAnim = new ColorAnimation(targetBg, TimeSpan.FromMilliseconds(150))
    {
        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
    };
    
    var iconAnim = new ColorAnimation(targetIcon, TimeSpan.FromMilliseconds(150))
    {
        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
    };

    // Garante que o Background é SolidColorBrush animável
    if (!(border.Background is SolidColorBrush bgBrush) || bgBrush.IsFrozen)
    {
        bgBrush = new SolidColorBrush(
            (border.Background as SolidColorBrush)?.Color ?? Colors.Transparent);
        border.Background = bgBrush;
    }
    bgBrush.BeginAnimation(SolidColorBrush.ColorProperty, bgAnim);

    // Garante que o Foreground do ícone é animável
    if (!(icon.Foreground is SolidColorBrush iconBrush) || iconBrush.IsFrozen)
    {
        iconBrush = new SolidColorBrush(
            (icon.Foreground as SolidColorBrush)?.Color ?? Colors.White);
        icon.Foreground = iconBrush;
    }
    iconBrush.BeginAnimation(SolidColorBrush.ColorProperty, iconAnim);

    // BorderBrush do toggle
    if (isOn)
    {
        if (!(border.BorderBrush is SolidColorBrush borderBrush) || borderBrush.IsFrozen)
        {
            borderBrush = new SolidColorBrush(Colors.Transparent);
            border.BorderBrush = borderBrush;
        }
        borderBrush.BeginAnimation(SolidColorBrush.ColorProperty, 
            new ColorAnimation(_themeColor, TimeSpan.FromMilliseconds(150)));
    }
    else
    {
        border.BorderBrush = Brushes.Transparent;
    }
}
```

> **Impacto:** Os toggles agora fazem uma transição de cor suave de 150ms — idêntica ao Control Center do iOS.

---

### 🔧 Melhoria 4: Animação do Wi-Fi Switch Indicator

**Problema:** O indicador do switch pula instantaneamente de lado.

```csharp
// ═══════════════════════════════════════════════════════════════════
// SUBSTITUIR UpdateWifiToggleHeaderUI() (Linhas 1358-1373) POR:
// ═══════════════════════════════════════════════════════════════════
private void UpdateWifiToggleHeaderUI()
{
    if (btnWifiToggleHeader == null || wifiSwitchIndicator == null) return;
    
    // Configura TranslateTransform se necessário
    if (!(wifiSwitchIndicator.RenderTransform is TranslateTransform))
        wifiSwitchIndicator.RenderTransform = new TranslateTransform();
    
    var translate = (TranslateTransform)wifiSwitchIndicator.RenderTransform;
    
    // A pílula tem 44px de largura, o indicador 16px, margem 4px
    // Posição esquerda: 0, Posição direita: ~20px
    double targetX = _wifiOn ? 20 : 0;
    Color targetBg = _wifiOn ? _themeColor : 
        ((this.Resources["IslandInactiveBrush"] as SolidColorBrush)?.Color 
         ?? Color.FromRgb(0x1E, 0x1E, 0x20));
    
    // 🆕 Desliza com spring suave
    var slideAnim = new DoubleAnimation(targetX, TimeSpan.FromMilliseconds(250))
    {
        EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.3 }
    };
    translate.BeginAnimation(TranslateTransform.XProperty, slideAnim);
    
    // Anima a cor de fundo do switch container
    if (!(btnWifiToggleHeader.Background is SolidColorBrush switchBg) || switchBg.IsFrozen)
    {
        switchBg = new SolidColorBrush(
            (btnWifiToggleHeader.Background as SolidColorBrush)?.Color ?? Colors.Gray);
        btnWifiToggleHeader.Background = switchBg;
    }
    switchBg.BeginAnimation(SolidColorBrush.ColorProperty, 
        new ColorAnimation(targetBg, TimeSpan.FromMilliseconds(200))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
    
    // Fixa o indicador à esquerda para que TranslateTransform funcione corretamente
    wifiSwitchIndicator.HorizontalAlignment = HorizontalAlignment.Left;
}
```

> **Nota XAML:** Alterar o `wifiSwitchIndicator` no XAML para ter `HorizontalAlignment="Left"` fixo, pois agora o movimento é via `TranslateTransform`.

---

### 🔧 Melhoria 5: Efeito de Drop Shadow Aprimorado

```csharp
// ═══════════════════════════════════════════════════════════════════
// SUBSTITUIR o trecho de drop shadow em ShowDropPanel() (Linhas 472-483) POR:
// ═══════════════════════════════════════════════════════════════════
if (islandBorder != null)
{
    var dropShadow = new DropShadowEffect
    {
        Color = Color.FromRgb(0x4C, 0xC2, 0xFF),
        ShadowDepth = 0,
        Opacity = 0.8,
        BlurRadius = 20
    };
    islandBorder.Effect = dropShadow;
    islandBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(0x4C, 0xC2, 0xFF));

    // 🆕 Pulsação com EaseInOut para ritmo orgânico + animação de opacidade
    var easing = new SineEase { EasingMode = EasingMode.EaseInOut };
    
    var blurAnim = new DoubleAnimation(18, 40, TimeSpan.FromMilliseconds(900))
    { 
        AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever,
        EasingFunction = easing
    };
    
    var opacityAnim = new DoubleAnimation(0.6, 1.0, TimeSpan.FromMilliseconds(900))
    { 
        AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever,
        EasingFunction = easing
    };
    
    dropShadow.BeginAnimation(DropShadowEffect.BlurRadiusProperty, blurAnim);
    dropShadow.BeginAnimation(DropShadowEffect.OpacityProperty, opacityAnim);
}
```

---

### 🔧 Melhoria 6: TactileBorderButtonStyle com Spring no Release

```xml
<!-- ═══════════════════════════════════════════════════════════════════
     SUBSTITUIR no XAML (Linhas 155-180): Adicionar EasingFunction
     para a animação de mouse-up com leve overshoot
═══════════════════════════════════════════════════════════════════ -->
<Style x:Key="TactileBorderButtonStyle" TargetType="Border">
    <Setter Property="RenderTransformOrigin" Value="0.5,0.5" />
    <Setter Property="RenderTransform">
        <Setter.Value>
            <ScaleTransform ScaleX="1.0" ScaleY="1.0" />
        </Setter.Value>
    </Setter>
    <Style.Triggers>
        <!-- Mouse hover: aumenta escala em 2% (mais sutil que 3%) -->
        <EventTrigger RoutedEvent="Mouse.MouseEnter"><BeginStoryboard><Storyboard>
            <DoubleAnimation Storyboard.TargetProperty="(UIElement.RenderTransform).(ScaleTransform.ScaleX)" To="1.02" Duration="0:0:0.15">
                <DoubleAnimation.EasingFunction><CubicEase EasingMode="EaseOut"/></DoubleAnimation.EasingFunction>
            </DoubleAnimation>
            <DoubleAnimation Storyboard.TargetProperty="(UIElement.RenderTransform).(ScaleTransform.ScaleY)" To="1.02" Duration="0:0:0.15">
                <DoubleAnimation.EasingFunction><CubicEase EasingMode="EaseOut"/></DoubleAnimation.EasingFunction>
            </DoubleAnimation>
        </Storyboard></BeginStoryboard></EventTrigger>

        <!-- Mouse leave: restaura com curva suave -->
        <EventTrigger RoutedEvent="Mouse.MouseLeave"><BeginStoryboard><Storyboard>
            <DoubleAnimation Storyboard.TargetProperty="(UIElement.RenderTransform).(ScaleTransform.ScaleX)" To="1.0" Duration="0:0:0.20">
                <DoubleAnimation.EasingFunction><CubicEase EasingMode="EaseOut"/></DoubleAnimation.EasingFunction>
            </DoubleAnimation>
            <DoubleAnimation Storyboard.TargetProperty="(UIElement.RenderTransform).(ScaleTransform.ScaleY)" To="1.0" Duration="0:0:0.20">
                <DoubleAnimation.EasingFunction><CubicEase EasingMode="EaseOut"/></DoubleAnimation.EasingFunction>
            </DoubleAnimation>
        </Storyboard></BeginStoryboard></EventTrigger>

        <!-- Press: escala para 96% (mais sutil) com snap rápido -->
        <EventTrigger RoutedEvent="UIElement.PreviewMouseLeftButtonDown"><BeginStoryboard><Storyboard>
            <DoubleAnimation Storyboard.TargetProperty="(UIElement.RenderTransform).(ScaleTransform.ScaleX)" To="0.96" Duration="0:0:0.06">
                <DoubleAnimation.EasingFunction><CubicEase EasingMode="EaseOut"/></DoubleAnimation.EasingFunction>
            </DoubleAnimation>
            <DoubleAnimation Storyboard.TargetProperty="(UIElement.RenderTransform).(ScaleTransform.ScaleY)" To="0.96" Duration="0:0:0.06">
                <DoubleAnimation.EasingFunction><CubicEase EasingMode="EaseOut"/></DoubleAnimation.EasingFunction>
            </DoubleAnimation>
        </Storyboard></BeginStoryboard></EventTrigger>

        <!-- Release: bounce back com BackEase (overshoot sutil) -->
        <EventTrigger RoutedEvent="UIElement.PreviewMouseLeftButtonUp"><BeginStoryboard><Storyboard>
            <DoubleAnimation Storyboard.TargetProperty="(UIElement.RenderTransform).(ScaleTransform.ScaleX)" To="1.02" Duration="0:0:0.20">
                <DoubleAnimation.EasingFunction><BackEase EasingMode="EaseOut" Amplitude="0.4"/></DoubleAnimation.EasingFunction>
            </DoubleAnimation>
            <DoubleAnimation Storyboard.TargetProperty="(UIElement.RenderTransform).(ScaleTransform.ScaleY)" To="1.02" Duration="0:0:0.20">
                <DoubleAnimation.EasingFunction><BackEase EasingMode="EaseOut" Amplitude="0.4"/></DoubleAnimation.EasingFunction>
            </DoubleAnimation>
        </Storyboard></BeginStoryboard></EventTrigger>
    </Style.Triggers>
</Style>
```

---

### 🔧 Melhoria 7: Transição Animada entre Sub-Painéis

**Problema:** Trocar de `expandedIslandPanel` para `appearancePanel` ou `wifiPanel` é abrupto.

```csharp
// ═══════════════════════════════════════════════════════════════════
// SUBSTITUIR AppearanceSettings_Click() (Linhas 942-968) POR:
// ═══════════════════════════════════════════════════════════════════
private void AppearanceSettings_Click(object sender, MouseButtonEventArgs e)
{
    e.Handled = true;
    
    // 🆕 Fade-out do painel atual, fade-in do próximo
    AnimatePanelOut(expandedIslandPanel, () =>
    {
        AnimatePanelIn(appearancePanel, slideDistance: 12, delayMs: 0);
    });

    if (mainContentGrid != null)
        mainContentGrid.VerticalAlignment = VerticalAlignment.Bottom;

    appearancePanel.UpdateLayout();
    appearancePanel.Measure(new Size(ISLAND_OPEN_W, double.PositiveInfinity));
    double appearanceHeight = appearancePanel.DesiredSize.Height;

    miniIslandPanel.Measure(new Size(ISLAND_OPEN_W, double.PositiveInfinity));
    double miniHeight = miniIslandPanel.DesiredSize.Height;

    double totalHeight = Math.Min(appearanceHeight + miniHeight + 19, ISLAND_OPEN_H);
    AnimateBorder(ISLAND_OPEN_W, totalHeight);
}

// ═══════════════════════════════════════════════════════════════════
// SUBSTITUIR BackToMainPanel_Click() (Linhas 970-986) POR:
// ═══════════════════════════════════════════════════════════════════
private void BackToMainPanel_Click(object sender, MouseButtonEventArgs e)
{
    e.Handled = true;
    
    // 🆕 Fade-out dos sub-painéis, fade-in do painel principal
    AnimatePanelOut(appearancePanel);
    AnimatePanelOut(wifiPanel);
    wifiPasswordPanel.Visibility = Visibility.Collapsed;
    
    AnimatePanelIn(expandedIslandPanel, slideDistance: 12, delayMs: 100);
    
    if (btnWifi != null && icoWifi != null)
        UpdateToggleButton(btnWifi, icoWifi, _wifiOn);
    if (btnWifiMenu != null && icoWifiMenu != null)
        UpdateToggleButton(btnWifiMenu, icoWifiMenu, _wifiOn);
    
    AnimateBorder(ISLAND_OPEN_W, ISLAND_OPEN_H);
}

// ═══════════════════════════════════════════════════════════════════
// SUBSTITUIR WifiMenuOpen_Click() (Linhas 1631-1644) POR:
// ═══════════════════════════════════════════════════════════════════
private void WifiMenuOpen_Click(object sender, MouseButtonEventArgs e)
{
    e.Handled = true;
    
    AnimatePanelOut(expandedIslandPanel, () =>
    {
        AnimatePanelIn(wifiPanel, slideDistance: 12, delayMs: 0);
    });

    if (mainContentGrid != null)
        mainContentGrid.VerticalAlignment = VerticalAlignment.Bottom;

    UpdateWifiToggleHeaderUI();
    _ = RefreshWifiListAsync();
}
```

---

## 3️⃣ Novas Animações Sugeridas (Valor Premium)

### 🌟 Nova 1: Pulse Sutil de "Respiração" no Modo Compacto

Quando a ilha está compacta e ociosa, uma pulsação muito sutil no CornerRadius (simulada via Opacity do border glow) daria a sensação de que a ilha está "viva".

```csharp
// ═══════════════════════════════════════════════════════════════════
// ADICIONAR após Window_Loaded, depois que a ilha está pronta:
// ═══════════════════════════════════════════════════════════════════
private void StartIdleBreathingAnimation()
{
    // Glow sutil e permanente na borda — como se a ilha "respirasse"
    var breathGlow = new DropShadowEffect
    {
        Color = _themeColor,
        ShadowDepth = 0,
        Opacity = 0,
        BlurRadius = 8
    };
    
    // Só aplica se não há outro efeito ativo
    if (islandBorder.Effect == null)
    {
        islandBorder.Effect = breathGlow;
        
        var breathAnim = new DoubleAnimation(0, 0.15, TimeSpan.FromSeconds(3))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        breathGlow.BeginAnimation(DropShadowEffect.OpacityProperty, breathAnim);
    }
}

private void StopIdleBreathingAnimation()
{
    if (islandBorder.Effect is DropShadowEffect glow && glow.Opacity < 0.2)
    {
        islandBorder.Effect = null;
    }
}
```

> **Onde chamar:** `StartIdleBreathingAnimation()` no final de `CloseIsland()` e `StopIdleBreathingAnimation()` no início de `OpenIsland()`.

---

### 🌟 Nova 2: Animação de Entrada do Thumbnail de Mídia

Quando uma nova música começa e o thumbnail muda, fazer um crossfade.

```csharp
// ═══════════════════════════════════════════════════════════════════
// ADICIONAR método para transição suave de thumbnail:
// ═══════════════════════════════════════════════════════════════════
private void AnimateThumbnailChange(BitmapImage newThumbnail)
{
    // Fade-out rápido
    var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(100));
    fadeOut.Completed += (s, e) =>
    {
        // Troca a imagem
        miniThumbBorder.Background = new ImageBrush(newThumbnail) 
        { 
            Stretch = Stretch.UniformToFill 
        };
        miniThumbBorder.Visibility = Visibility.Visible;
        txtMiniIcon.Visibility = Visibility.Collapsed;
        
        // Fade-in + scale suave
        miniThumbBorder.RenderTransformOrigin = new Point(0.5, 0.5);
        miniThumbBorder.RenderTransform = new ScaleTransform(0.85, 0.85);
        
        var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        var scaleX = new DoubleAnimation(0.85, 1.0, TimeSpan.FromMilliseconds(300))
        {
            EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.2 }
        };
        var scaleY = new DoubleAnimation(0.85, 1.0, TimeSpan.FromMilliseconds(300))
        {
            EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.2 }
        };
        
        miniThumbBorder.BeginAnimation(UIElement.OpacityProperty, fadeIn);
        ((ScaleTransform)miniThumbBorder.RenderTransform).BeginAnimation(
            ScaleTransform.ScaleXProperty, scaleX);
        ((ScaleTransform)miniThumbBorder.RenderTransform).BeginAnimation(
            ScaleTransform.ScaleYProperty, scaleY);
    };
    
    miniThumbBorder.BeginAnimation(UIElement.OpacityProperty, fadeOut);
}
```

---

### 🌟 Nova 3: Animação de Troca de Texto com Slide Vertical

Quando o título/artista muda no modo compacto, o texto antigo desce e o novo desce de cima.

```csharp
// ═══════════════════════════════════════════════════════════════════
// ADICIONAR método para transição de texto:
// ═══════════════════════════════════════════════════════════════════
private void AnimateTextChange(TextBlock textBlock, string newText)
{
    if (textBlock.Text == newText) return;
    
    if (!(textBlock.RenderTransform is TranslateTransform))
        textBlock.RenderTransform = new TranslateTransform();
    
    var translate = (TranslateTransform)textBlock.RenderTransform;
    
    // Fase 1: Slide-out + fade-out do texto atual
    var slideOut = new DoubleAnimation(0, -8, TimeSpan.FromMilliseconds(100));
    var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(100));
    
    fadeOut.Completed += (s, e) =>
    {
        textBlock.Text = newText;
        translate.Y = 8; // Posiciona abaixo
        
        // Fase 2: Slide-in + fade-in do novo texto
        var slideIn = new DoubleAnimation(8, 0, TimeSpan.FromMilliseconds(200))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        
        translate.BeginAnimation(TranslateTransform.YProperty, slideIn);
        textBlock.BeginAnimation(UIElement.OpacityProperty, fadeIn);
    };
    
    translate.BeginAnimation(TranslateTransform.YProperty, slideOut);
    textBlock.BeginAnimation(UIElement.OpacityProperty, fadeOut);
}
```

> **Onde usar:** Em `UpdateMediaInfoAsync()`, substituir `txtMiniTitle.Text = state.Title` por `AnimateTextChange(txtMiniTitle, state.Title)`.

---

### 🌟 Nova 4: Hover Animado nos Itens de Rede Wi-Fi

```csharp
// ═══════════════════════════════════════════════════════════════════
// SUBSTITUIR os handlers inline em RenderWifiNetworks() POR:
// ═══════════════════════════════════════════════════════════════════
itemBorder.MouseEnter += (s, ev) => 
{
    var hoverColor = Color.FromRgb(0x2D, 0x2D, 0x30);
    if (!(itemBorder.Background is SolidColorBrush bg) || bg.IsFrozen)
    {
        bg = new SolidColorBrush(
            (itemBorder.Background as SolidColorBrush)?.Color ?? Colors.Transparent);
        itemBorder.Background = bg;
    }
    bg.BeginAnimation(SolidColorBrush.ColorProperty, 
        new ColorAnimation(hoverColor, TimeSpan.FromMilliseconds(100))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
};

itemBorder.MouseLeave += (s, ev) => 
{
    var normalColor = (this.Resources["IslandInactiveBrush"] as SolidColorBrush)?.Color 
        ?? Color.FromRgb(0x1E, 0x1E, 0x20);
    if (itemBorder.Background is SolidColorBrush bg && !bg.IsFrozen)
    {
        bg.BeginAnimation(SolidColorBrush.ColorProperty, 
            new ColorAnimation(normalColor, TimeSpan.FromMilliseconds(150))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }
};
```

---

## 4️⃣ Resumo de Prioridades

| # | Melhoria | Impacto Visual | Esforço | Prioridade |
|---|----------|----------------|---------|------------|
| 1 | AnimateBorder com Spring | 🔥🔥🔥🔥🔥 | Baixo | **P0 — CRÍTICO** |
| 2 | Fade-In/Out dos Painéis | 🔥🔥🔥🔥🔥 | Médio | **P0 — CRÍTICO** |
| 3 | ColorAnimation nos Toggles | 🔥🔥🔥🔥 | Médio | **P1 — ALTO** |
| 4 | Wi-Fi Switch Deslizante | 🔥🔥🔥 | Baixo | **P1 — ALTO** |
| 5 | Drop Shadow com Easing | 🔥🔥🔥 | Baixo | **P2 — MÉDIO** |
| 6 | TactileBorderButton Spring | 🔥🔥🔥 | Baixo | **P2 — MÉDIO** |
| 7 | Transição entre Sub-Painéis | 🔥🔥🔥🔥 | Médio | **P1 — ALTO** |
| N1 | Pulse de Respiração | 🔥🔥🔥 | Baixo | **P2 — MÉDIO** |
| N2 | Crossfade do Thumbnail | 🔥🔥🔥🔥 | Baixo | **P1 — ALTO** |
| N3 | Slide de Texto | 🔥🔥🔥🔥 | Baixo | **P1 — ALTO** |
| N4 | Hover Animado Wi-Fi | 🔥🔥 | Baixo | **P3 — BAIXO** |

---

## 5️⃣ Referências de Design

| Conceito | Referência |
|----------|------------|
| Curva de mola (spring) | Dynamic Island iOS 16+ — observar o overshoot de ~2px na expansão |
| Morph entre estados | Dynamic Island expandindo da pílula para player de música |
| Toggle transitions | iOS Control Center — toggles com fade de cor de 150ms |
| Breathing glow | Apple Watch face ociosa — pulsação sutil |
| Content crossfade | Spotify Now Playing — thumbnail faz fade ao trocar música |
| Switch slide | iOS toggle switches — indicador desliza com bounce sutil |

---

> [!TIP]
> **Ordem de implementação recomendada:** Comece pelas melhorias **P0** (AnimateBorder + Fade dos Painéis) — estas duas mudanças sozinhas transformam a percepção de qualidade da ilha. Depois aplique os P1 para refinar os detalhes.

> [!IMPORTANT]
> Todas as animações propostas usam classes nativas do WPF (`DoubleAnimation`, `ColorAnimation`, `ElasticEase`, `BackEase`) — **nenhuma dependência externa é necessária**.
