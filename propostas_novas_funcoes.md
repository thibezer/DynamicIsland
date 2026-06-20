# 🏝️ Propostas de Novas Funcionalidades — DynamicIsland

> **Documento de Pesquisa e Ideação**
> Gerado em: 18 de Junho de 2026
> Baseado na análise completa do código-fonte e pesquisa de mercado

---

## Sumário Executivo

Este relatório apresenta **15 propostas de novas funcionalidades** para o projeto DynamicIsland, priorizadas por impacto no dia a dia do usuário. Cada proposta foi avaliada considerando:

- O que já existe no projeto (para evitar redundância)
- O que os concorrentes oferecem (Notchify, DockBar, DynamicWin)
- Tendências de UX/UI para widgets de desktop em 2025-2026
- Viabilidade técnica dentro da arquitetura WPF/.NET 10 atual

### O que já existe no DynamicIsland (para referência)

| Categoria | Funcionalidades Implementadas |
|:---|:---|
| **Controles Rápidos** | Wi-Fi, Bluetooth, Modo Avião, Luz Noturna, Hotspot, Projetar, Transmitir, Legendas, Acessibilidade, Economia |
| **Mídia** | Play/Pause, Próxima faixa, Exibição de capa/título/artista (GSMTC) |
| **Hardware** | Volume (slider + mute), Brilho (slider com throttle) |
| **Notificações** | Leitura da última notificação do Windows (Toast Listener) |
| **Drag & Drop** | Arrastar arquivos para a ilha → Enviar hiperlink para Excel |
| **Personalização** | Cor de destaque, fundo e botões inativos (predefinidas + hexadecimal) |
| **Comportamento** | Topmost, anti-Win+D, ocultação em tela cheia, instância única, início com Windows |

---

## 📋 Propostas de Funcionalidades

---

### 1. ⏱️ Timer / Pomodoro Flutuante

**Descrição:** Um cronômetro e timer Pomodoro integrado na ilha. O usuário define o tempo (ex: 25min trabalho + 5min pausa) e a pílula compacta exibe a contagem regressiva em tempo real, substituindo temporariamente a área de notificações. Ao finalizar, a ilha pulsa com uma animação de alerta.

**Integração Visual:**
- No modo compacto: o timer aparece no lugar do título da notificação (ex: `🍅 18:32 restantes`)
- No modo expandido: um painel dedicado com botões Iniciar/Pausar/Resetar e seleção de tempo (15, 25, 45, 60 min)
- A borda da ilha pode ter um efeito de progresso circular ou preenchimento gradual usando a cor de destaque

**Complexidade:** 🟡 Média
- Usa `DispatcherTimer` (já utilizado no projeto)
- Requer novo painel XAML e estado na máquina de estados
- Persistência do timer ativo no `island_config.json`

**Inspiração:** DockBar (Steam) oferece Pomodoro integrado; Focus To-Do e PomyTimer são referências de UX para timers flutuantes sempre visíveis.

---

### 2. 📊 Monitor de Performance do Sistema

**Descrição:** Exibir o uso de CPU, RAM e opcionalmente GPU em tempo real na pílula compacta ou no painel expandido, com barras de progresso ou gráficos mini (sparklines).

**Integração Visual:**
- No modo compacto (toggle configurável): `CPU 23% | RAM 67%` com mini-barras coloridas
- No painel expandido: um card dedicado com barras de progresso estilo Fluent Design, similar aos sliders de volume/brilho já existentes
- Alertas visuais (borda vermelha pulsante) quando CPU > 90% ou RAM > 85%

**Complexidade:** 🟡 Média
- CPU: `System.Diagnostics.PerformanceCounter` ou WMI
- RAM: `System.Diagnostics.Process` / `GC.GetGCMemoryInfo()` / WMI
- GPU: Requer bibliotecas adicionais (ex: LibreHardwareMonitor)
- Atualização a cada 2-3 segundos via `DispatcherTimer`

**Inspiração:** Notchify e DockBar mostram CPU/RAM em tempo real; Rainmeter é a referência clássica; Witals oferece design moderno para o Widget Board do Windows.

---

### 3. 📋 Histórico de Área de Transferência (Clipboard)

**Descrição:** Monitorar a área de transferência do Windows e manter um histórico dos últimos N itens copiados (texto, caminhos de arquivo, imagens). O usuário pode clicar em qualquer item do histórico para re-copiá-lo.

**Integração Visual:**
- No painel expandido: um novo sub-painel (similar ao wifiPanel) com lista rolável dos últimos 10 itens
- Cada item mostra preview truncado do texto ou miniatura da imagem
- Botão "Limpar Histórico" no topo
- Toggle para ativar/desativar o monitoramento

**Complexidade:** 🟡 Média
- Usa API Win32 `AddClipboardFormatListener` para escutar `WM_CLIPBOARDUPDATE`
- Armazena em `ObservableCollection` na memória (ou SQLite para persistência)
- Precisa de hook via `HwndSource` (padrão já utilizado no projeto para `WndProc`)

**Inspiração:** PowerToys Clipboard History; Raycast (Windows Beta); DynamicWin File Tray.

---

### 4. 🎵 Player de Mídia Expandido

**Descrição:** Evoluir os controles de mídia compactos (que hoje são apenas Play/Pause e Next) para um mini-player completo quando a ilha está expandida, com capa grande, barra de progresso da música, botões de anterior/shuffle/repeat.

**Integração Visual:**
- No painel expandido, substituir ou complementar a grade de toggles com um card de mídia:
  - Capa do álbum em tamanho maior (80x80px) com bordas arredondadas
  - Título + Artista com fonte maior
  - Barra de progresso da música (posição/duração total)
  - Botões: ⏮ Anterior | ⏯ Play/Pause | ⏭ Próximo | 🔀 Shuffle | 🔁 Repeat
- Transição suave entre o modo de toggles e o modo de mídia

**Complexidade:** 🟡 Média
- Já tem a infraestrutura via `MediaNotificationService` (GSMTC)
- As APIs `TrySkipPreviousAsync`, `TryChangeShuffleActiveAsync`, `TryChangeAutoRepeatModeAsync` já estão implementadas no serviço mas **não estão expostas na UI**
- Barra de progresso: `GetTimelineProperties()` da API GSMTC

**Inspiração:** DockBar exibe capa grande + barra de progresso; Apple Dynamic Island no iOS mostra controles completos ao expandir.

---

### 5. 📅 Relógio + Data + Próximo Compromisso

**Descrição:** Exibir relógio digital, data formatada e, opcionalmente, o próximo evento do calendário do Windows/Outlook na pílula compacta ou no topo do painel expandido.

**Integração Visual:**
- No modo compacto: quando não há mídia tocando nem notificações, mostrar `Qui, 18 Jun • 23:45` em vez de "Sem notificações"
- No painel expandido: card com relógio grande no estilo do Windows 11, data completa e próximo compromisso (se disponível)
- Clique no relógio abre o calendário flutuante nativo do Windows

**Complexidade:** 🟢 Baixa (relógio/data) / 🔴 Alta (integração com calendário)
- Relógio/Data: Trivial com `DateTime.Now` e `DispatcherTimer` de 1 segundo
- Calendário: Requer integração com API do Windows Calendar ou Microsoft Graph (OAuth2)

**Inspiração:** Notchify exibe data/hora; widgets nativos do Windows 11 mostram calendário; macOS Menu Bar widgets.

---

### 6. 🔒 Indicador de Privacidade (Câmera/Microfone)

**Descrição:** Detectar quando a câmera ou microfone estão em uso por algum aplicativo e mostrar um indicador visual proeminente na ilha (ponto verde para câmera, laranja para microfone), similar ao que o iOS faz nativamente.

**Integração Visual:**
- Na pílula compacta: um ponto colorido animado (pulso suave) na extremidade direita da ilha
  - 🟢 Verde = Câmera ativa
  - 🟠 Laranja = Microfone ativo
  - 🔴 Vermelho = Ambos ativos
- Ao expandir: nome do aplicativo que está usando o dispositivo
- Animação de entrada/saída com fade suave

**Complexidade:** 🟡 Média
- Câmera: Monitorar via registro do Windows (`HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\webcam`)
- Microfone: Idem com chave `microphone`
- Ou usar `Windows.Devices.Enumeration.DeviceWatcher`

**Inspiração:** iOS Dynamic Island com ponto verde/laranja; Windows 11 já tem indicadores nativos na taskbar mas são pouco visíveis; Notchify implementou isso.

---

### 7. 🚀 Lançador Rápido de Aplicativos

**Descrição:** Um painel com ícones dos aplicativos favoritos do usuário (configuráveis) para abrir com um clique. Funciona como uma dock simplificada acessível sem precisar ir à barra de tarefas.

**Integração Visual:**
- No painel expandido: uma fila de ícones circulares (6-8 apps) no rodapé, entre os sliders e os botões de configuração
- Cada ícone tem efeito tátil (`TactileBorderButtonStyle` já existente)
- Configuração via drag & drop de atalhos .lnk ou seleção manual via diálogo

**Complexidade:** 🟡 Média
- Lançar apps: `Process.Start()` (já utilizado no projeto)
- Extrair ícones: `System.Drawing.Icon.ExtractAssociatedIcon()` ou API Shell32
- Persistência da lista de apps no `island_config.json`

**Inspiração:** DockBar permite lançamento rápido de apps; macOS Dock; PowerToys Run.

---

### 8. 🌤️ Widget de Clima

**Descrição:** Exibir a condição climática atual (temperatura, ícone de condição, cidade) na pílula compacta como modo alternativo quando não há notificações, e detalhes expandidos (previsão de 3 dias, umidade, vento) no painel.

**Integração Visual:**
- No modo compacto: `São Paulo • 24°C ☁️` com ícone Segoe MDL2
- No painel expandido: card com previsão detalhada, ícones animados ou estáticos
- Atualização a cada 30 minutos para economia de recursos

**Complexidade:** 🟡 Média
- API gratuita: OpenWeatherMap, WeatherAPI ou wttr.in
- Requer `HttpClient` para chamadas REST
- Geolocalização: API WinRT `Windows.Devices.Geolocation` (já usa WinRT no projeto)
- Cache agressivo para evitar chamadas desnecessárias

**Inspiração:** Notchify exibe clima; DockBar tem widget de clima; widgets nativos do Windows 11.

---

### 9. 📝 Notas Rápidas (Sticky Notes)

**Descrição:** Um campo de texto simples para anotações rápidas que fica acessível a qualquer momento pelo painel expandido. Ideal para anotar um número de telefone, uma ideia rápida ou um lembrete sem abrir nenhum aplicativo.

**Integração Visual:**
- No painel expandido: um sub-painel com TextBox multi-linha estilizado (dark theme, similar ao `HexTextBoxStyle` existente)
- Texto salvo automaticamente no `island_config.json` ou arquivo `.txt` separado
- Limite visual de 500 caracteres com contador
- Botões: "Copiar tudo" e "Limpar"

**Complexidade:** 🟢 Baixa
- TextBox com `AcceptsReturn="True"` e `TextWrapping="Wrap"`
- Salvar via `File.WriteAllText` no mesmo diretório de configuração
- Reaproveitamento dos estilos existentes (`HexTextBoxStyle`)

**Inspiração:** DockBar Quick Notes; Sticky Notes do Windows; Themia Notes Widget.

---

### 10. 🔊 Seletor de Dispositivo de Áudio

**Descrição:** Permitir ao usuário alternar rapidamente entre dispositivos de saída de áudio (fones, caixas de som, monitores HDMI) sem ir às Configurações do Windows. Essencial para quem usa múltiplos dispositivos.

**Integração Visual:**
- No painel expandido: botão ao lado do slider de volume que abre uma lista dos dispositivos de áudio disponíveis
- Cada item mostra: nome do dispositivo + ícone + indicador de "ativo"
- Clique para trocar o dispositivo padrão instantaneamente

**Complexidade:** 🟡 Média
- Listar dispositivos: `NAudio.CoreAudioApi.MMDeviceEnumerator.EnumerateAudioEndPoints()` (NAudio já está no projeto)
- Trocar dispositivo padrão: requer P/Invoke da `IPolicyConfig` (API COM não documentada oficialmente, mas amplamente usada por EarTrumpet, SoundSwitch, etc.)

**Inspiração:** EarTrumpet; Centro de Ações do Windows 11; macOS audio switcher na menu bar.

---

### 11. ⌨️ Atalhos Globais de Teclado

**Descrição:** Permitir que o usuário abra/feche a ilha ou acione funcionalidades específicas via combinações de teclas globais (ex: `Win+Shift+I` abre a ilha, `Win+Shift+M` mute rápido).

**Integração Visual:**
- Configuração no painel de Aparência ou em um novo sub-painel "Atalhos"
- Lista editável de atalhos com seus respectivos comandos
- Feedback visual (a ilha pisca rapidamente) ao acionar um atalho

**Complexidade:** 🟡 Média
- API Win32 `RegisterHotKey` / `UnregisterHotKey`
- Hook via `HwndSource` (estrutura já existe no projeto)
- Persistência dos atalhos no `island_config.json`

**Inspiração:** PowerToys Run usa `Alt+Space`; Raycast usa `Option+Space`; Notion usa atalhos globais.

---

### 12. 📁 Drag & Drop Expandido (Multi-ação)

**Descrição:** Expandir o sistema de drag & drop que hoje só tem a opção "Enviar para Excel" para incluir múltiplas ações: Copiar Caminho, Abrir Com..., Compartilhar via E-mail, Mover para Pasta Rápida, Gerar Link de Compartilhamento.

**Integração Visual:**
- O `dropZonePanel` existente ganha mais cards de ação (similar ao layout atual do ExcelAction)
- Cada ação tem ícone, título e subtítulo descritivo
- Ações configuráveis pelo usuário (quais aparecem e em qual ordem)

**Complexidade:** 🟢 Baixa a 🟡 Média
- Copiar caminho: `Clipboard.SetText(path)` — trivial
- Abrir com: `Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true })` — trivial
- Mover para pasta: `File.Move()` com seleção de destino
- Compartilhar: API WinRT `Windows.ApplicationModel.DataTransfer`

**Inspiração:** macOS Finder Quick Actions; DynamicWin File Tray; ShareX.

---

### 13. 🔋 Indicador de Bateria com Detalhes

**Descrição:** Para laptops: exibir o nível de bateria, estado de carregamento e tempo estimado restante na pílula compacta e com mais detalhes no painel expandido.

**Integração Visual:**
- No modo compacto: ícone de bateria com porcentagem ao lado da área de notificação/mídia
- No painel expandido: card com barra de progresso da bateria, indicador "Carregando ⚡" ou "Na bateria", e tempo estimado restante
- Cores adaptativas: verde (>60%), amarelo (20-60%), vermelho (<20%)

**Complexidade:** 🟢 Baixa
- `System.Windows.Forms.SystemInformation.PowerStatus` ou WMI `Win32_Battery`
- Dados nativos do Windows, sem bibliotecas externas
- Atualização a cada 30 segundos

**Inspiração:** Widget de bateria do iOS; indicadores de bateria do macOS Menu Bar; Notchify battery widget.

---

### 14. 🌍 Posição Adaptável da Ilha

**Descrição:** Permitir que o usuário reposicione a ilha em diferentes cantos/bordas da tela (inferior esquerdo, inferior central, inferior direito, superior central — como no iPhone), adaptando-se a diferentes configurações de monitor.

**Integração Visual:**
- No painel de Aparência: um mini-mapa da tela com 6 posições clicáveis
- A ilha se move com animação suave para a nova posição
- Suporte a múltiplos monitores (escolher em qual tela a ilha aparece)

**Complexidade:** 🟡 Média
- Requer recalcular `this.Left` e `this.Top` baseado no `WorkArea` e na posição escolhida
- Alterar `HorizontalAlignment` e `VerticalAlignment` do `islandBorder`
- A animação `AnimateBorder` e o layout de expansão precisariam se adaptar à direção (expandir para cima vs. para baixo)
- Persistência da posição no `island_config.json`

**Inspiração:** DockBar permite posicionamento flexível; Rainmeter permite skins em qualquer lugar da tela; macOS permite reorganizar itens da menu bar.

---

### 15. 🎨 Temas Predefinidos Completos

**Descrição:** Em vez de configurar 3 cores individualmente, oferecer temas completos predefinidos (Midnight Blue, Forest Green, Sunset Orange, Monochrome, Neon Purple, etc.) que aplicam combinações harmônicas de cores de uma só vez.

**Integração Visual:**
- No painel de Aparência: uma fila horizontal de cartões de tema, cada um com preview das 3 cores combinadas
- Clique único aplica todas as 3 cores instantaneamente
- Ainda mantém a opção de personalização manual para quem preferir
- Possibilidade futura de importar/exportar temas como JSON

**Complexidade:** 🟢 Baixa
- Apenas arrays de configurações pré-montadas
- Reutiliza `ApplyThemeColor`, `ApplyBgColor` e `ApplyInactiveColor` existentes
- Sem novas dependências

**Inspiração:** Windows Terminal themes; VS Code themes; Discord themes.

---

## 📊 Matriz de Priorização

| # | Funcionalidade | Impacto no Usuário | Complexidade | Prioridade Sugerida |
|:---:|:---|:---:|:---:|:---:|
| 1 | Timer / Pomodoro | 🔥 Alto | 🟡 Média | ⭐ P1 |
| 4 | Player de Mídia Expandido | 🔥 Alto | 🟡 Média | ⭐ P1 |
| 5 | Relógio + Data | 🔥 Alto | 🟢 Baixa | ⭐ P1 |
| 9 | Notas Rápidas | 🔥 Alto | 🟢 Baixa | ⭐ P1 |
| 13 | Indicador de Bateria | 🔥 Alto | 🟢 Baixa | ⭐ P1 |
| 15 | Temas Predefinidos | 🔶 Médio | 🟢 Baixa | ⭐ P1 |
| 2 | Monitor de Performance | 🔶 Médio | 🟡 Média | ⭐ P2 |
| 6 | Indicador de Privacidade | 🔶 Médio | 🟡 Média | ⭐ P2 |
| 10 | Seletor de Áudio | 🔶 Médio | 🟡 Média | ⭐ P2 |
| 12 | Drag & Drop Expandido | 🔶 Médio | 🟢 Baixa | ⭐ P2 |
| 3 | Clipboard History | 🔶 Médio | 🟡 Média | ⭐ P2 |
| 11 | Atalhos Globais | 🔶 Médio | 🟡 Média | ⭐ P3 |
| 7 | Lançador de Apps | 🔵 Baixo | 🟡 Média | ⭐ P3 |
| 8 | Widget de Clima | 🔵 Baixo | 🟡 Média | ⭐ P3 |
| 14 | Posição Adaptável | 🔵 Baixo | 🟡 Média | ⭐ P3 |

---

## 🏆 Recomendação: Primeiros Passos

Sugiro implementar na seguinte ordem para maximizar o valor percebido com o menor esforço:

### Sprint 1 — "Quick Wins" (Baixa complexidade, alto impacto)
1. **Relógio + Data** — Substituir "Sem notificações" por relógio/data quando ocioso
2. **Temas Predefinidos** — Reusa 100% da lógica de cores existente
3. **Indicador de Bateria** — Informação de sobrevivência para laptops

### Sprint 2 — "Core Features" (Média complexidade, alto impacto)
4. **Player de Mídia Expandido** — As APIs já estão implementadas no `MediaNotificationService`, falta apenas a UI
5. **Timer / Pomodoro** — Feature matadora para produtividade
6. **Notas Rápidas** — Funcionalidade simples que resolve uma dor real

### Sprint 3 — "Power User" (Média complexidade, médio impacto)
7. **Monitor de Performance** — Diferencial competitivo
8. **Indicador de Privacidade** — Segurança e consciência do usuário
9. **Drag & Drop Expandido** — Evolução natural do que já existe

---

## 📚 Referências e Projetos Analisados

| Projeto | Plataforma | Destaques |
|:---|:---|:---|
| **Notchify** | Microsoft Store | CPU/RAM, Clima, Timer, Câmera/Mic, Mídia |
| **DockBar** | Steam | Pomodoro, Notas, To-Do, App Launcher, Audio Visualizer |
| **DynamicWin** | GitHub (Open Source) | Mídia, Calendário, File Tray/Clipboard |
| **Rainmeter** | Open Source | Personalização total, Sparklines, Skins |
| **Themia** | Tauri-based | Design moderno, Widgets nativos, Notas, Calendário |
| **PowerToys** | Microsoft | Clipboard History, Run Launcher, Atalhos |
| **EarTrumpet** | Microsoft Store | Audio device switcher, Per-app volume |

---

> **Nota:** Todas as funcionalidades propostas foram validadas quanto à viabilidade dentro da arquitetura atual do projeto (WPF + .NET 10 + WinRT APIs). Nenhuma proposta requer mudança de framework ou refatoração estrutural — todas podem ser adicionadas como novos painéis/serviços seguindo o padrão existente.
