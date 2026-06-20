# Manifesto do Projeto DynamicIsland

Este manifesto detalha a arquitetura, estrutura, funcionamento e as interações do projeto **DynamicIsland** (uma aplicação WPF em C# para Windows). O DynamicIsland simula o conceito de "Ilha Dinâmica" no Windows, oferecendo controles rápidos do sistema, monitoramento de mídia e notificações e integração de arquivos por Drag & Drop.

---

## 1. Árvore de Diretórios e Estrutura de Arquivos

Abaixo está o mapeamento dos principais componentes do projeto e suas respectivas responsabilidades:

```
DynamicIsland/
│
├── Services/                                 # Camada de Serviços da Aplicação
│   ├── ExcelService.cs                       # Integração COM com o Microsoft Excel
│   ├── HardwareService.cs                    # Controle e leitura de áudio, brilho, Wi-Fi e energia
│   └── MediaNotificationService.cs           # Sincronização de mídia GSMTC e escuta de notificações
│
├── App.xaml                                  # Declaração global do aplicativo WPF
├── App.xaml.cs                               # Lógica de controle de ciclo de vida e instância única
├── AssemblyInfo.cs                           # Metadados do assembly do projeto
├── DynamicIsland.csproj                      # Configuração do compilador, framework e pacotes NuGet
├── DynamicIsland.sln                         # Solução do Visual Studio para gerenciar o projeto
├── NativeIslandWindow.xaml                   # Layout e marcação XAML do painel flutuante
├── NativeIslandWindow.xaml.cs                # Lógica e comportamento em código-por-trás (code-behind)
└── manifesto.md                              # Este manifesto explicativo do projeto
```

---

## 2. Configurações do Sistema e Ciclo de Vida

### Configuração do Projeto (`DynamicIsland.csproj`)
- **SDK**: `Microsoft.NET.Sdk` compilando como uma aplicação executável do Windows (`WinExe`).
- **Framework de Destino**: `.NET 10.0-windows10.0.19041.0` — o que permite chamar APIs nativas da plataforma WinRT / Windows Runtime de forma integrada (ex: Wi-Fi, Rádios, Notificações).
- **Recursos Habilitados**: `UseWPF` habilitado, suporte a tipos anuláveis (`Nullable`) e usos implícitos (`ImplicitUsings`).
- **Dependências externas**:
  - `NAudio (v2.2.1)`: Usado para interagir com as APIs de áudio CoreAudio do Windows.
  - `System.Management (v8.0.0)`: Usado para realizar consultas e edições via WMI (Windows Management Instrumentation) (ex: leitura e ajuste do brilho da tela).

### Inicialização e Instância Única (`App.xaml` e `App.xaml.cs`)
O arquivo `App.xaml` define a janela padrão de inicialização com a propriedade `StartupUri="NativeIslandWindow.xaml"`.
O arquivo `App.xaml.cs` estende a inicialização do WPF para garantir que **apenas uma instância** da aplicação possa ser executada ao mesmo tempo (Padrão Single Instance). Ele faz isso por meio de um Mutex global do sistema operacional (`Global\DynamicIslandWindows_SingleInstance_Mutex`):
1. **`OnStartup`**: Tenta adquirir a posse do Mutex nomeado. Se o Mutex já foi criado por outro processo, a aplicação fecha silenciosamente com `Current.Shutdown()`. Caso contrário, prossegue com a inicialização normal.
2. **`OnExit`**: Quando o aplicativo é fechado, o Mutex é liberado com segurança (`_mutex.ReleaseMutex()`) e descartado.

---

## 3. Arquitetura de Software e Serviços (`Services/`)

O projeto separa as regras de manipulação do sistema operacional do fluxo de apresentação visual (UI), distribuindo-as em três serviços principais na pasta `Services`:

### A. ExcelService (`Services/ExcelService.cs`)
Implementa automação de escritório utilizando interoperabilidade via objetos COM (Component Object Model) do Microsoft Excel de forma dinâmica (Reflection):
- **Objetivo**: Conectar a uma sessão ativa do Excel e gerar hiperlinks na planilha ativa.
- **Funcionamento**: Importa funções clássicas do Win32 (`oleaut32.dll` e `ole32.dll`) como `GetActiveObject` e `CLSIDFromProgID` para encontrar uma janela ativa do Excel.
- **Processamento**: Se a janela existir, o serviço recupera a planilha ativa (`ActiveSheet`), a célula selecionada (`ActiveCell`) e a lista de hiperlinks (`Hyperlinks`) por meio de reflexão dinâmica para evitar dependências fortes. Em seguida, invoca o método `Add` para registrar o hiperlink do arquivo arrastado.
- **Segurança**: Garante a limpeza correta de recursos COM no bloco `finally` utilizando `Marshal.ReleaseComObject`, impedindo que processos órfãos do `excel.exe` fiquem presos na memória.

### B. HardwareService (`Services/HardwareService.cs`)
Interage com componentes físicos da máquina e configurações do Windows através de APIs nativas do Win32, UWP (WinRT) e WMI:
1. **Áudio**: Utiliza a API `NAudio.CoreAudioApi` (`MMDeviceEnumerator`) para ler o nível do volume (`GetCurrentVolume`), aplicar um novo nível (`SetVolume`) ou inverter o status de mudo (`ToggleMute`).
2. **Brilho**: Busca o brilho do monitor por WMI (`WmiMonitorBrightness`). O método `GetCurrentBrightness` executa a consulta em uma thread de background (`Task.Run`) protegida por um limite de tempo (timeout) de 500ms para evitar que o widget trave caso o serviço do WMI do Windows fique indisponível. Para atualizar o brilho, o método `SetBrightness` utiliza uma fila inteligente amortecida que evita gargalos ao enviar comandos repetidos para o driver de vídeo.
3. **Luz Noturna**: Executado diretamente na GPU através de chamadas do GDI32.dll (`SetDeviceGammaRamp` e `GetDeviceGammaRamp`). Salva a rampa de cor original da tela do usuário e aplica um filtro de cor quente personalizado (reduzindo a luz verde em 15% e a azul em 40%) para simular a luz noturna.
4. **Controle de Rádios (Wi-Fi e Bluetooth)**: Utiliza a API WinRT `Windows.Devices.Radios` para ativar e desativar os transmissores.
5. **Varredura e Conexão de Rede Wi-Fi**: Utiliza a biblioteca WinRT `Windows.Devices.WiFi.WiFiAdapter` para varrer redes sem fio locais (`GetAvailableNetworksAsync`) e conectar a elas (`ConnectToNetworkAsync`) fornecendo as credenciais de segurança.
6. **Legendas ao Vivo**: Utiliza chamadas Win32 `keybd_event` para simular o acionamento físico do atalho global `Win + Ctrl + L` no Windows.

### C. MediaNotificationService (`Services/MediaNotificationService.cs`)
Realiza a ponte entre a mídia em reprodução no sistema operacional, as notificações do Windows e a ilha de exibição:
1. **Controle de Mídia de Sistema (GSMTC)**: Utiliza as APIs de transporte de mídia do Windows (`GlobalSystemMediaTransportControlsSessionManager`) para obter informações sobre a música que está tocando em aplicativos como Spotify, Chrome, Edge, VLC, etc.
   - Escuta eventos de alteração de faixa, alteração de metadados e mudança de status de reprodução.
   - Decodifica a imagem de capa (thumbnail) da música enviada pelo SO para uma string em formato Base64.
   - Resolve o problema de sessões "zumbi" (aplicativos que foram fechados ou pausados em segundo plano, mas permanecem na fila do Windows).
2. **Notificações do Windows (Toast Listener)**: Utiliza a classe `UserNotificationListener` para monitorar as notificações ativas que chegam na Central de Ações do Windows.
   - Caso não haja nenhuma mídia tocando, o serviço entra em modo `"notification"` e expõe as informações da última notificação recebida (remetente, título, conteúdo e ícone do aplicativo).

---

## 4. Estrutura e Estilização da Interface de Usuário (`NativeIslandWindow.xaml`)

A janela do aplicativo (`NativeIslandWindow.xaml`) foi desenhada para se comportar como uma interface nativa do Windows 11 (Fluent Design).

### Características da Janela
- **Transparência e Bordas**: `AllowsTransparency="True"`, `Background="Transparent"` e `WindowStyle="None"`.
- **Topmost**: Sempre fixada por cima das outras janelas (`Topmost="True"`).
- **Sem Barra de Tarefas**: Não exibe ícone na barra de tarefas principal do Windows (`ShowInTaskbar="False"`).
- **Ancoragem**: Mantém uma ancoragem estática elegante no canto inferior esquerdo da tela física (logo acima do menu iniciar / relógio, adaptando-se às variações de DPI do monitor).

### Estilos Customizados Importantes
- `TactileBorderButtonStyle`: Aplica uma transformação de escala animada que responde ao mouse. Aumenta o tamanho em 3% (`ScaleX/Y = 1.03`) em hover e diminui para 94% (`0.94`) ao clicar, criando um efeito tátil tridimensional de clique.
- `ModernFluentSliderStyle`: Redefine o controle de `Slider` do WPF para ficar idêntico ao Centro de Controle do Windows 11. Remove o botão deslizante (thumb) clássico e engrossa a trilha ativa para 12px com extremidades arredondadas (estilo pílula). O thumb é ocultado visualmente, mas mantém uma área de colisão invisível ampliada de 24px para facilitar o arrasto.
- `HexTextBoxStyle`: Estilo plano escuro para os inputs de configurações de cores da ilha.

---

## 5. Mapeamento de Elementos Interativos e Eventos (`NativeIslandWindow.xaml.cs`)

O código-por-trás da janela principal gerencia a máquina de estados que alterna a visualização da ilha entre os seus diferentes painéis e lida com os eventos de clique e arrasto:

### A. Lógica da Janela Focada no Comportamento Nativo (Anti-ocultação e Tela Cheia)
- **Bloqueio Win+D**: Registra uma escuta nativa (`WndProc`) interceptando a mensagem `WM_WINDOWPOSCHANGING`. Impede que a janela seja minimizada ou ocultada por atalhos globais de limpar área de trabalho (como `Win+D`), garantindo que o widget permaneça fixado em cima (`HWND_TOPMOST`).
- **Foco inteligente**: Aplica a flag `WS_EX_NOACTIVATE` via P/Invoke da biblioteca `user32.dll` para que os cliques do usuário na ilha não tirem o foco de digitação da janela em que ele estava trabalhando no momento.
- **Detecção de Tela Cheia**: A cada 2 segundos, o timer `_fullscreenTimer` faz uma consulta ao estado de notificações do Windows (`SHQueryUserNotificationState`). Se o usuário estiver rodando um jogo ou fazendo uma apresentação em tela cheia, a ilha é ocultada temporariamente para não atrapalhar, reaparecendo quando o usuário retornar ao desktop comum.

### B. Transições de Tamanho e Animações Fluidas
As transições físicas de dimensão do widget ocorrem em tempo de execução via aceleração por hardware através do método `AnimateBorder(width, height)`. O tamanho da pílula compacta é calculado de maneira adaptativa (`GetDynamicCompactWidth`) com base na largura dos textos ativos de notificação ou música para evitar barras pretas desproporcionais.

---

## 6. Tabela de Ações e Elementos Interativos (Botões)

Abaixo estão descritos todos os botões e áreas de interação e o que acontece quando são acionados pelo usuário:

| Elemento / Botão | Identificador XAML | Evento C# | Ação e Comportamento |
| :--- | :--- | :--- | :--- |
| **Área da Ilha Compacta** | `islandBorder` | `MouseLeftButtonDown` | Quando a ilha está fechada, clicar nela expande a janela para o painel principal de configurações e atalhos rápidos (`OpenIsland`). |
| **Área da Ilha (Arrasto)** | `islandBorder` | `DragEnter` / `DragOver` / `Drop` | Permite arrastar qualquer arquivo do Windows Explorer e soltar em cima do widget para abrir as opções de compartilhamento (Drop Zone). |
| **Modo Mídia: Play/Pause** | `miniMediaControls` | `PlayPause_Click` | Envia o comando de reproduzir ou pausar para o tocador de mídia ativo no sistema. |
| **Modo Mídia: Próxima** | `miniMediaControls` | `Next_Click` | Avança para a próxima faixa de música no tocador ativo. |
| **Ícone de Volume (Mute)** | `txtVolumeIcon` | `MuteToggle_Click` | Clicar no ícone de alto-falante silencia (mute) ou ativa (unmute) o áudio geral do sistema. |
| **Controle de Volume** | `volumeSlider` | `VolumeSlider_ValueChanged` | Controle deslizante para alterar o volume do Windows. Ao deslizar, desmuta automaticamente se estiver silenciado. |
| **Controle de Brilho** | `brightnessSlider` | `BrightnessSlider_ValueChanged` | Altera o brilho do monitor através de um controle dinâmico. O envio dos valores é amortecido a cada 150ms para evitar travamentos. |
| **Botão Split Wi-Fi (Esquerda)** | `btnWifi` | `Toggle_Click` (Tag: `wifi`) | Alterna o estado de ativação física do Wi-Fi (Liga/Desliga). |
| **Botão Split Wi-Fi (Direita)** | `btnWifiMenu` | `WifiMenuOpen_Click` | Oculta o painel de atalhos rápidos e exibe o menu interno de conexões Wi-Fi, disparando uma varredura de redes locais. |
| **Toggle Bluetooth** | `btnBluetooth` | `Toggle_Click` (Tag: `bluetooth`) | Liga ou desliga o rádio Bluetooth do computador. |
| **Toggle Modo Avião** | `btnAirplane` | `Toggle_Click` (Tag: `airplane`) | Ativa ou desativa o Modo Avião (liga/desliga todos os rádios de conectividade sem fio simultaneamente). |
| **Toggle Acessibilidade** | `btnAccessibility` | `Toggle_Click` (Tag: `accessibility`) | Abre a tela de acessibilidade e exibição do painel de configurações do Windows (`ms-settings:easeofaccess-display`). |
| **Toggle Economia** | `btnBatterySaver` | `Toggle_Click` (Tag: `battery_saver`) | Abre a página de configuração de bateria e economia de energia do Windows (`ms-settings:batterysaver`). |
| **Toggle Legendas** | `btnLiveCaptions` | `Toggle_Click` (Tag: `live_captions`) | Emite o atalho de teclado global do Windows `Win + Ctrl + L` para iniciar a ferramenta nativa de Legendas em Tempo Real. |
| **Toggle Luz Noturna** | `btnNightLight` | `Toggle_Click` (Tag: `night_light`) | Ativa ou desativa a rampa de calibração de cor quente direta na GPU (filtro de luz azul). |
| **Toggle Hotspot** | `btnMobileHotspot` | `Toggle_Click` (Tag: `mobile_hotspot`) | Abre as configurações de Hotspot Móvel do Windows para compartilhamento de internet (`ms-settings:network-mobilehotspot`). |
| **Toggle Partilhar** | `btnNearbyShare` | `Toggle_Click` (Tag: `nearby_share`) | Abre as configurações de compartilhamento de arquivos por proximidade do Windows (`ms-settings:crossdevice`). |
| **Toggle Transmitir** | `btnCast` | `Toggle_Click` (Tag: `cast`) | Abre o menu lateral rápido de conexões e telas sem fio (`ms-settings-connectabledevices:devicediscovery`). |
| **Toggle Projetar** | `btnProject` | `Toggle_Click` (Tag: `project`) | Executa a ferramenta nativa `DisplaySwitch.exe` do Windows para alternar modos de múltiplos monitores (Duplicar, Estender, etc.). |
| **Toggle Ficheiros** | `btnFilePicker` | `Toggle_Click` (Tag: `file_picker`) | Fecha a ilha e abre uma janela tradicional de seleção de arquivos (`OpenFileDialog`), simulando a ação de arrastar um arquivo. |
| **Botão Configurações PC** | `btnAllSettings` | `OpenSettings_Click` | Abre a central geral de configurações do Windows 10/11 (`ms-settings:`). |
| **Botão Aparência Ilha** | `btnAppearanceSettings`| `AppearanceSettings_Click` | Oculta os atalhos rápidos e abre o painel interno de personalização cromática da ilha (`appearancePanel`). |
| **Ação Excel (Drop)** | Painel Drop | `ExcelAction_Click` | Envia o caminho completo do arquivo selecionado/arrastado para a célula ativa na janela do Excel que estiver aberta em segundo plano. |
| **Cancelar Drop** | Painel Drop / X | `CancelDrop_Click` | Cancela a operação de drag & drop, descarta o arquivo e recolhe a ilha para o estado compacto padrão. |
| **Botão Voltar** | `appearancePanel` / `wifiPanel` | `BackToMainPanel_Click` | Retorna dos subpainéis (Aparência ou Wi-Fi) para a tela principal de atalhos rápidos da ilha. |
| **Cores Predefinidas** | Blocos de cores | `SelectColor_Click` / `SelectBgColor_Click` / `SelectInactColor_Click` | Define imediatamente as cores predefinidas para Destaque, Fundo ou Botão Inativo da ilha e grava a nova configuração no JSON local. |
| **Confirmar Hexadecimal** | Botões com ícone de check | `ApplyAccentHex_Click` / `ApplyBgHex_Click` / `ApplyInactHex_Click` | Valida se a cor informada no TextBox é um código hexadecimal válido (ex: `#FF1E1E20`) e, se for, aplica-a e salva no arquivo JSON. |
| **Switch Geral Wi-Fi** | `btnWifiToggleHeader` | `WifiToggleHeader_Click` | Ativa ou desativa o Wi-Fi no topo da lista interna de redes Wi-Fi e atualiza a exibição. |
| **Item de Rede Wi-Fi** | Lista de redes | `WifiNetworkItem_Click` | Seleciona a rede sem fio desejada. Se for aberta, inicia a conexão direta. Se for protegida, abre o formulário de inserção de senha. |
| **Confirmar Conexão Wi-Fi**| `wifiPasswordPanel` | `ConnectWifi_Click` | Inicia a tentativa de conexão assíncrona com a senha digitada no TextBox e exibe o feedback de progresso, sucesso ou falha. |
| **Cancelar Conexão Wi-Fi** | `wifiPasswordPanel` | `CancelWifiConnection_Click` | Fecha o formulário de solicitação de senha do Wi-Fi e retorna à lista geral de redes encontradas. |

---

## 7. Persistência de Dados e Configurações

O aplicativo armazena as escolhas estéticas do usuário em um arquivo JSON local chamado `island_config.json`, localizado no caminho:
`%localappdata%\DynamicIslandWindows\island_config.json`

O arquivo salva três valores de cores em hexadecimal:
- **`ThemeColor`**: Cor de realce dos botões ativados e do preenchimento de trilha dos sliders (padrão Coral: `#FFF6A733`).
- **`BackgroundColor`**: Cor de fundo da ilha (padrão Preto: `#FF000000`).
- **`InactiveColor`**: Cor de fundo dos botões que não estão ativados (padrão Cinza Escuro: `#FF1E1E20`).

Exemplo do arquivo de configuração:
```json
{
  "ThemeColor": "#FFF6A733",
  "BackgroundColor": "#FF000000",
  "InactiveColor": "#FF1E1E20"
}
```

O salvamento e o carregamento são gerenciados automaticamente através dos métodos `SaveConfig` e `LoadConfig` utilizando a biblioteca `System.Text.Json`. A inicialização automática com o Windows é configurada inserindo um registro na pasta de execução do usuário (`HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Run`) com a chave `"DynamicIsland"`.
