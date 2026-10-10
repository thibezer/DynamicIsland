using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Windows.Media;
using Windows.Media.Control;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace DynamicIslandWindows.Services
{
    /// <summary>
    /// Estado consolidado de reprodução de mídia ou notificação ativa.
    /// </summary>
    public enum MediaMode
    {
        Notification,
        Music
    }

    public enum MediaCommand
    {
        PlayPause,
        Next,
        Previous,
        Shuffle,
        Repeat
    }

    public class MediaNotificationState
    {
        public MediaMode Mode { get; set; } = MediaMode.Notification;
        public string Title { get; set; } = string.Empty;
        public string Subtitle { get; set; } = string.Empty;
        public byte[]? ThumbnailBytes { get; set; }
        public string Thumbnail => ThumbnailBytes != null && ThumbnailBytes.Length > 0 ? Convert.ToBase64String(ThumbnailBytes) : string.Empty;
        public bool IsPlaying { get; set; } = false;
    }

    /// <summary>
    /// Serviço de monitoramento de sessões de mídia (GSMTC) e notificações de sistema (Toast).
    /// </summary>
    public class MediaNotificationService : IDisposable
    {
        private static readonly char[] LineSeparators = { '\r', '\n' };

        public event EventHandler? StateChanged;
        private bool _listenerSetup = false;
        private bool _disposed = false;

        private GlobalSystemMediaTransportControlsSessionManager? _sessionManager;
        private GlobalSystemMediaTransportControlsSession? _currentSession;

        // Os eventos do GSMTC chegam em threads de background, enquanto GetCurrentStateAsync roda na UI
        private readonly object _sessionLock = new();

        // Cache de metadados e imagem de música
        private string _cachedMusicTitle = string.Empty;
        private string _cachedMusicArtist = string.Empty;
        private byte[]? _cachedMusicThumbnailBytes;
        private bool _noThumbnailAvailable = false;
        private bool _cachedMusicIsPlaying = false;
        private string _lastPlayingAppId = string.Empty;

        // Cache de metadados e ícone de notificações
        private string _cachedNotifTitle = string.Empty;
        private string _cachedNotifContent = string.Empty;
        private string _cachedNotifAppId = string.Empty;
        private byte[]? _cachedNotifAppIconBytes;

        /// <summary>Obtém o gerenciador de sessões (uma única vez) e assina seus eventos.</summary>
        private async Task EnsureSessionManagerAsync()
        {
            if (_sessionManager != null) return;

            var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            if (manager == null) return;

            lock (_sessionLock)
            {
                if (_sessionManager != null || _disposed) return; // outra chamada concorrente já inicializou
                _sessionManager = manager;
                _sessionManager.CurrentSessionChanged += OnCurrentSessionChanged;
                _sessionManager.SessionsChanged += OnSessionsChanged;
            }
            UpdateCurrentSession();
        }

        public async Task InitializeAsync()
        {
            try
            {
                await EnsureSessionManagerAsync();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MediaNotificationService.InitializeAsync] MediaManager: {ex.Message}");
            }

            if (_listenerSetup) return;
            try
            {
                var listener = UserNotificationListener.Current;
                var accessStatus = await listener.RequestAccessAsync();
                if (accessStatus == UserNotificationListenerAccessStatus.Allowed)
                {
                    listener.NotificationChanged += OnNotificationChanged;
                    _listenerSetup = true;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MediaNotificationService.InitializeAsync] Notifications: {ex.Message}");
            }
        }

        private void OnCurrentSessionChanged(GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args)
        {
            UpdateCurrentSession();
            StateChanged?.Invoke(this, EventArgs.Empty);
        }

        private void OnSessionsChanged(GlobalSystemMediaTransportControlsSessionManager sender, SessionsChangedEventArgs args)
        {
            // A lista de sessões mudou (ex.: o player foi fechado e reaberto). O app pode ter criado uma
            // sessão nova com o mesmo AppUserModelId, então reassina os eventos incondicionalmente.
            UpdateCurrentSession(force: true);
            StateChanged?.Invoke(this, EventArgs.Empty);
        }

        private void UpdateCurrentSession(bool force = false)
        {
            lock (_sessionLock)
            {
                UpdateCurrentSessionCore(force);
            }
        }

        private void UpdateCurrentSessionCore(bool force)
        {
            if (_sessionManager == null || _disposed) return;

            var newSession = _sessionManager.GetCurrentSession();

            // Verifica se a sessão realmente mudou
            bool sessionChanged = force;
            if (_currentSession == null && newSession != null)
            {
                sessionChanged = true;
            }
            else if (_currentSession != null && newSession == null)
            {
                sessionChanged = true;
            }
            else if (_currentSession != null && newSession != null)
            {
                sessionChanged |= _currentSession.SourceAppUserModelId != newSession.SourceAppUserModelId;
            }

            if (!sessionChanged) return;

            // Desinscrever da sessão anterior
            if (_currentSession != null)
            {
                try
                {
                    _currentSession.PlaybackInfoChanged -= OnSessionPlaybackInfoChanged;
                    _currentSession.MediaPropertiesChanged -= OnSessionMediaPropertiesChanged;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[MediaNotificationService.UpdateCurrentSession] Desinscrição: {ex.Message}");
                }
            }

            _currentSession = newSession;

            // Inscrever na nova sessão
            if (_currentSession != null)
            {
                try
                {
                    _currentSession.PlaybackInfoChanged += OnSessionPlaybackInfoChanged;
                    _currentSession.MediaPropertiesChanged += OnSessionMediaPropertiesChanged;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[MediaNotificationService.UpdateCurrentSession] Inscrição: {ex.Message}");
                }
            }
        }

        private void OnSessionPlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args)
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
        }

        private void OnSessionMediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args)
        {
            _cachedMusicThumbnailBytes = null;
            _noThumbnailAvailable = false;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }

        private void OnNotificationChanged(UserNotificationListener sender, UserNotificationChangedEventArgs args)
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
        }

        public async Task<MediaNotificationState> GetCurrentStateAsync()
        {
            var state = new MediaNotificationState();
            try
            {
                await EnsureSessionManagerAsync();
                UpdateCurrentSession();

                GlobalSystemMediaTransportControlsSession? session;
                lock (_sessionLock) { session = _currentSession; }
                if (session != null)
                {
                    GlobalSystemMediaTransportControlsSessionPlaybackStatus playbackStatus = GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed;
                    try
                    {
                        var playbackInfo = session.GetPlaybackInfo();
                        playbackStatus = playbackInfo?.PlaybackStatus ?? GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed;
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[MediaNotificationService.GetCurrentStateAsync] PlaybackStatus: {ex.Message}");
                    }

                    string currentAppId = string.Empty;
                    try
                    {
                        currentAppId = session.SourceAppUserModelId;
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[MediaNotificationService.GetCurrentStateAsync] SourceAppId: {ex.Message}");
                    }

                    if (playbackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                    {
                        _lastPlayingAppId = currentAppId;
                    }

                    // Sessão pausada de outro app que não o último a tocar é "zumbi". Sem histórico
                    // (app aberto com a música já pausada), a sessão é exibida normalmente.
                    bool isZombieSession = playbackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused &&
                                           !string.IsNullOrEmpty(_lastPlayingAppId) && currentAppId != _lastPlayingAppId;

                    if (playbackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Stopped ||
                        playbackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed ||
                        isZombieSession)
                    {
                        state.Mode = MediaMode.Notification;
                        var (notifTitle, notifContent, appIconBytes) = await GetLatestNotificationAsync();
                        state.Title = notifTitle;
                        state.Subtitle = notifContent;
                        state.ThumbnailBytes = appIconBytes;
                    }
                    else
                    {
                        state.Mode = MediaMode.Music;
                        state.IsPlaying = playbackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

                        var info = await session.TryGetMediaPropertiesAsync();
                        var title = string.IsNullOrEmpty(info?.Title) ? "Nenhuma música" : info.Title;
                        var artist = string.IsNullOrEmpty(info?.Artist) ? "Nenhum artista" : info.Artist;

                        state.Title = title;
                        state.Subtitle = artist;

                        if (title != _cachedMusicTitle || artist != _cachedMusicArtist)
                        {
                            _cachedMusicThumbnailBytes = null;
                            _noThumbnailAvailable = false;
                        }

                        if (_cachedMusicThumbnailBytes != null)
                        {
                            state.ThumbnailBytes = _cachedMusicThumbnailBytes;
                        }
                        else if (!_noThumbnailAvailable && info?.Thumbnail != null)
                        {
                            try
                            {
                                using var stream = await info.Thumbnail.OpenReadAsync();
                                if (stream.Size > 0)
                                {
                                    using var inputStream = stream.GetInputStreamAt(0);
                                    using var reader = new Windows.Storage.Streams.DataReader(inputStream);
                                    await reader.LoadAsync((uint)stream.Size);
                                    byte[] bytes = new byte[stream.Size];
                                    reader.ReadBytes(bytes);
                                    state.ThumbnailBytes = bytes;
                                    _cachedMusicThumbnailBytes = bytes;
                                }
                                else
                                {
                                    _noThumbnailAvailable = true;
                                }
                            }
                            catch (Exception ex)
                            {
                                Debug.WriteLine($"[MediaNotificationService.GetCurrentStateAsync] ThumbnailRead: {ex.Message}");
                            }
                        }
                        else
                        {
                            _noThumbnailAvailable = true;
                        }

                        _cachedMusicTitle = title;
                        _cachedMusicArtist = artist;
                        _cachedMusicIsPlaying = state.IsPlaying;
                    }
                }
                else
                {
                    state.Mode = MediaMode.Notification;
                    var (notifTitle, notifContent, appIconBytes) = await GetLatestNotificationAsync();
                    state.Title = notifTitle;
                    state.Subtitle = notifContent;
                    state.ThumbnailBytes = appIconBytes;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MediaNotificationService.GetCurrentStateAsync] Erro geral: {ex.Message}");
                state.Title = "Erro";
                state.Subtitle = ex.Message;
            }

            return state;
        }

        private async Task<(string title, string content, byte[]? appIconBytes)> GetLatestNotificationAsync()
        {
            const string defaultTitle   = "Sem notificações";
            const string defaultContent = "Tudo limpo por aqui";
            try
            {
                var listener = UserNotificationListener.Current;
                var accessStatus = await listener.RequestAccessAsync();
                if (accessStatus != UserNotificationListenerAccessStatus.Allowed)
                    return (defaultTitle, defaultContent, null);

                var notifications = await listener.GetNotificationsAsync(NotificationKinds.Toast);
                if (notifications.Count == 0) return (defaultTitle, defaultContent, null);

                // A ordem retornada não é garantida: escolhe explicitamente a mais recente
                var last = notifications.OrderByDescending(n => n.CreationTime).First();
                var bindings = last.Notification.Visual.Bindings;
                if (bindings.Count == 0) return (defaultTitle, defaultContent, null);

                var elems = bindings[0].GetTextElements();
                int ec = elems.Count;

                string title = defaultTitle;
                string content = defaultContent;

                string GetFirstLine(string text)
                {
                    if (string.IsNullOrEmpty(text)) return text;
                    var index = text.IndexOfAny(LineSeparators);
                    if (index >= 0)
                    {
                        return text.Substring(0, index).Trim();
                    }
                    return text.Trim();
                }

                // Em toasts ToastGeneric o 1º texto é o título e os seguintes são o corpo
                if (ec >= 1 && !string.IsNullOrWhiteSpace(elems[0].Text))
                {
                    title = GetFirstLine(elems[0].Text);
                }
                for (int i = 1; i < ec; i++)
                {
                    if (!string.IsNullOrWhiteSpace(elems[i].Text))
                    {
                        content = GetFirstLine(elems[i].Text);
                        break;
                    }
                }

                string appName = last.AppInfo.AppUserModelId ?? string.Empty;

                if (title == _cachedNotifTitle && content == _cachedNotifContent && appName == _cachedNotifAppId)
                {
                    return (_cachedNotifTitle, _cachedNotifContent, _cachedNotifAppIconBytes);
                }

                byte[]? appIconBytes = null;

                if (appName != _cachedNotifAppId || _cachedNotifAppIconBytes == null)
                {
                    try
                    {
                        var logoStreamRef = last.AppInfo.DisplayInfo.GetLogo(new Windows.Foundation.Size(30, 30));
                        if (logoStreamRef != null)
                        {
                            using var stream = await logoStreamRef.OpenReadAsync();
                            if (stream.Size > 0)
                            {
                                using var inputStream = stream.GetInputStreamAt(0);
                                using var reader = new Windows.Storage.Streams.DataReader(inputStream);
                                await reader.LoadAsync((uint)stream.Size);
                                byte[] bytes = new byte[stream.Size];
                                reader.ReadBytes(bytes);
                                appIconBytes = bytes;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[MediaNotificationService.GetLatestNotificationAsync] AppLogo: {ex.Message}");
                    }
                }
                else
                {
                    appIconBytes = _cachedNotifAppIconBytes;
                }

                _cachedNotifTitle = title;
                _cachedNotifContent = content;
                _cachedNotifAppId = appName;
                _cachedNotifAppIconBytes = appIconBytes;

                return (title, content, appIconBytes);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MediaNotificationService.GetLatestNotificationAsync] {ex.Message}");
                return (defaultTitle, defaultContent, null);
            }
        }

        public async Task HandleMediaCommandAsync(MediaCommand command)
        {
            try
            {
                GlobalSystemMediaTransportControlsSession? session;
                lock (_sessionLock) { session = _currentSession; }
                if (session == null)
                {
                    await EnsureSessionManagerAsync();
                    session = _sessionManager?.GetCurrentSession();
                }
                if (session == null) return;

                switch (command)
                {
                    case MediaCommand.PlayPause:
                        var status = session.GetPlaybackInfo()?.PlaybackStatus;
                        if (status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                            await session.TryPauseAsync();
                        else
                            await session.TryPlayAsync();
                        break;
                    case MediaCommand.Next:
                        await session.TrySkipNextAsync();
                        break;
                    case MediaCommand.Previous:
                        await session.TrySkipPreviousAsync();
                        break;
                    case MediaCommand.Shuffle:
                        try
                        {
                            var playback = session.GetPlaybackInfo();
                            if (playback != null)
                            {
                                bool isShuffle = playback.IsShuffleActive ?? false;
                                await session.TryChangeShuffleActiveAsync(!isShuffle);
                            }
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"[MediaNotificationService.HandleMediaCommandAsync] Shuffle: {ex.Message}");
                        }
                        break;
                    case MediaCommand.Repeat:
                        try
                        {
                            var playback = session.GetPlaybackInfo();
                            if (playback != null)
                            {
                                var repeatMode = playback.AutoRepeatMode ?? MediaPlaybackAutoRepeatMode.None;
                                var nextMode = repeatMode == MediaPlaybackAutoRepeatMode.None 
                                    ? MediaPlaybackAutoRepeatMode.List 
                                    : MediaPlaybackAutoRepeatMode.None;
                                await session.TryChangeAutoRepeatModeAsync(nextMode);
                            }
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"[MediaNotificationService.HandleMediaCommandAsync] Repeat: {ex.Message}");
                        }
                        break;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MediaNotificationService.HandleMediaCommandAsync] {ex.Message}");
            }
        }

        public void Dispose()
        {
            lock (_sessionLock)
            {
                if (_disposed) return;
                _disposed = true; // Impede que eventos atrasados reassinem sessões
            }

            if (_currentSession != null)
            {
                try
                {
                    _currentSession.PlaybackInfoChanged -= OnSessionPlaybackInfoChanged;
                    _currentSession.MediaPropertiesChanged -= OnSessionMediaPropertiesChanged;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[MediaNotificationService.Dispose] Session: {ex.Message}");
                }
                _currentSession = null;
            }

            if (_sessionManager != null)
            {
                try
                {
                    _sessionManager.CurrentSessionChanged -= OnCurrentSessionChanged;
                    _sessionManager.SessionsChanged -= OnSessionsChanged;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[MediaNotificationService.Dispose] SessionManager: {ex.Message}");
                }
                _sessionManager = null;
            }

            if (_listenerSetup)
            {
                try
                {
                    var listener = UserNotificationListener.Current;
                    listener.NotificationChanged -= OnNotificationChanged;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[MediaNotificationService.Dispose] Listener: {ex.Message}");
                }
                _listenerSetup = false;
            }
            GC.SuppressFinalize(this);
        }
    }
}
