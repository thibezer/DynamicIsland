using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Windows.Media;
using Windows.Media.Control;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace DynamicIslandWindows.Services
{
    public class MediaNotificationState
    {
        public string Mode { get; set; } = "notification";
        public string Title { get; set; } = string.Empty;
        public string Subtitle { get; set; } = string.Empty;
        public string Thumbnail { get; set; } = string.Empty;
        public bool IsPlaying { get; set; } = false;
    }

    public class MediaNotificationService : IDisposable
    {
        public event EventHandler? StateChanged;
        private bool _listenerSetup = false;
        private bool _disposed = false;

        private GlobalSystemMediaTransportControlsSessionManager? _sessionManager;
        private GlobalSystemMediaTransportControlsSession? _currentSession;

        // Cache de metadados e imagem de música
        private string _cachedMusicTitle = string.Empty;
        private string _cachedMusicArtist = string.Empty;
        private string _cachedMusicThumbnailBase64 = string.Empty;
        private bool _cachedMusicIsPlaying = false;

        // Cache de metadados e ícone de notificações
        private string _cachedNotifTitle = string.Empty;
        private string _cachedNotifContent = string.Empty;
        private string _cachedNotifAppId = string.Empty;
        private string _cachedNotifAppIconBase64 = string.Empty;

        public async Task InitializeAsync()
        {
            try
            {
                if (_sessionManager == null)
                {
                    _sessionManager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
                    if (_sessionManager != null)
                    {
                        _sessionManager.CurrentSessionChanged += OnCurrentSessionChanged;
                        UpdateCurrentSession();
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MediaManagerInit] {ex.Message}");
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
                Debug.WriteLine($"[Notifications] {ex.Message}");
            }
        }

        private void OnCurrentSessionChanged(GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args)
        {
            UpdateCurrentSession();
            StateChanged?.Invoke(this, EventArgs.Empty);
        }

        private void UpdateCurrentSession()
        {
            if (_sessionManager == null) return;

            // Desinscrever da sessão anterior
            if (_currentSession != null)
            {
                try
                {
                    _currentSession.PlaybackInfoChanged -= OnSessionPlaybackInfoChanged;
                    _currentSession.MediaPropertiesChanged -= OnSessionMediaPropertiesChanged;
                }
                catch { }
            }

            _currentSession = _sessionManager.GetCurrentSession();

            // Inscrever na nova sessão
            if (_currentSession != null)
            {
                try
                {
                    _currentSession.PlaybackInfoChanged += OnSessionPlaybackInfoChanged;
                    _currentSession.MediaPropertiesChanged += OnSessionMediaPropertiesChanged;
                }
                catch { }
            }
        }

        private void OnSessionPlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args)
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
        }

        private void OnSessionMediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args)
        {
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
                if (_sessionManager == null)
                {
                    _sessionManager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
                    if (_sessionManager != null)
                    {
                        _sessionManager.CurrentSessionChanged += OnCurrentSessionChanged;
                        UpdateCurrentSession();
                    }
                }

                var session = _currentSession;
                if (session != null)
                {
                    GlobalSystemMediaTransportControlsSessionPlaybackStatus playbackStatus = GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed;
                    try
                    {
                        var playbackInfo = session.GetPlaybackInfo();
                        playbackStatus = playbackInfo?.PlaybackStatus ?? GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed;
                    }
                    catch { }

                    if (playbackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Stopped ||
                        playbackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed)
                    {
                        state.Mode = "notification";
                        var (notifTitle, notifContent, appIcon) = await GetLatestNotificationAsync();
                        state.Title = notifTitle;
                        state.Subtitle = notifContent;
                        state.Thumbnail = appIcon;
                    }
                    else
                    {
                        state.Mode = "music";
                        state.IsPlaying = playbackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

                        var info = await session.TryGetMediaPropertiesAsync();
                        var title = string.IsNullOrEmpty(info.Title) ? "Nenhuma música" : info.Title;
                        var artist = string.IsNullOrEmpty(info.Artist) ? "Nenhum artista" : info.Artist;

                        state.Title = title;
                        state.Subtitle = artist;

                        if (title == _cachedMusicTitle && artist == _cachedMusicArtist)
                        {
                            // A música é a mesma, logo o thumbnail é idêntico e o pegamos do cache instantaneamente
                            state.Thumbnail = _cachedMusicThumbnailBase64;
                        }
                        else
                        {
                            // Nova música! Carrega o thumbnail
                            state.Thumbnail = string.Empty;

                            if (info.Thumbnail != null)
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
                                        state.Thumbnail = Convert.ToBase64String(bytes);
                                    }
                                }
                                catch (Exception ex)
                                {
                                    Debug.WriteLine($"[Thumbnail] {ex.Message}");
                                }
                            }

                            _cachedMusicThumbnailBase64 = state.Thumbnail;
                        }

                        // Atualiza o cache geral da música (incluindo estado de execução)
                        _cachedMusicTitle = title;
                        _cachedMusicArtist = artist;
                        _cachedMusicIsPlaying = state.IsPlaying;
                    }
                }
                else
                {
                    state.Mode = "notification";
                    var (notifTitle, notifContent, appIcon) = await GetLatestNotificationAsync();
                    state.Title = notifTitle;
                    state.Subtitle = notifContent;
                    state.Thumbnail = appIcon;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[UpdateMedia] {ex.Message}");
                state.Title = "Erro";
                state.Subtitle = ex.Message;
            }

            return state;
        }

        private async Task<(string title, string content, string appIcon)> GetLatestNotificationAsync()
        {
            const string defaultTitle   = "Sem notificações";
            const string defaultContent = "Tudo limpo por aqui";
            try
            {
                var listener     = UserNotificationListener.Current;
                var accessStatus = await listener.RequestAccessAsync();
                if (accessStatus != UserNotificationListenerAccessStatus.Allowed)
                    return (defaultTitle, defaultContent, string.Empty);

                var notifications = await listener.GetNotificationsAsync(NotificationKinds.Toast);
                int count = notifications.Count;
                if (count == 0) return (defaultTitle, defaultContent, string.Empty);

                var last     = notifications[count - 1];
                var bindings = last.Notification.Visual.Bindings;
                if (bindings.Count == 0) return (defaultTitle, defaultContent, string.Empty);

                var elems = bindings[0].GetTextElements();
                int ec    = elems.Count;

                string title = defaultTitle;
                string content = defaultContent;

                if (ec >= 3)
                {
                    title = elems[1].Text;
                    content = elems[2].Text;
                }
                else if (ec == 2)
                {
                    title = elems[0].Text;
                    content = elems[1].Text;
                }
                else if (ec == 1)
                {
                    title = elems[0].Text;
                }

                string appName = last.AppInfo.AppUserModelId ?? string.Empty;

                // Se título, conteúdo e aplicativo forem idênticos ao cache, retornamos o cache
                if (title == _cachedNotifTitle && content == _cachedNotifContent && appName == _cachedNotifAppId)
                {
                    return (_cachedNotifTitle, _cachedNotifContent, _cachedNotifAppIconBase64);
                }

                string appIcon = string.Empty;

                // Se o aplicativo mudou ou não temos o ícone dele no cache, carregamos
                if (appName != _cachedNotifAppId || string.IsNullOrEmpty(_cachedNotifAppIconBase64))
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
                                appIcon = Convert.ToBase64String(bytes);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[AppLogo] {ex.Message}");
                    }
                }
                else
                {
                    // Se o app for o mesmo, reaproveita o ícone cacheado
                    appIcon = _cachedNotifAppIconBase64;
                }

                // Atualiza o cache de notificações
                _cachedNotifTitle = title;
                _cachedNotifContent = content;
                _cachedNotifAppId = appName;
                _cachedNotifAppIconBase64 = appIcon;

                return (title, content, appIcon);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Notifications] {ex.Message}");
                return (defaultTitle, defaultContent, string.Empty);
            }
        }

        public async Task HandleMediaCommandAsync(string? command)
        {
            if (string.IsNullOrEmpty(command)) return;
            try
            {
                var session = _currentSession;
                if (session == null)
                {
                    if (_sessionManager == null)
                    {
                        _sessionManager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
                    }
                    session = _sessionManager?.GetCurrentSession();
                }
                if (session == null) return;

                switch (command)
                {
                    case "playPause":
                        var status = session.GetPlaybackInfo().PlaybackStatus;
                        if (status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                            await session.TryPauseAsync();
                        else
                            await session.TryPlayAsync();
                        break;
                    case "next":
                        await session.TrySkipNextAsync();
                        break;
                    case "previous":
                        await session.TrySkipPreviousAsync();
                        break;
                    case "shuffle":
                        try
                        {
                            var playback = session.GetPlaybackInfo();
                            if (playback != null)
                            {
                                bool isShuffle = playback.IsShuffleActive ?? false;
                                await session.TryChangeShuffleActiveAsync(!isShuffle);
                            }
                        }
                        catch { }
                        break;
                    case "repeat":
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
                        catch { }
                        break;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MediaCmd] {ex.Message}");
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            if (_currentSession != null)
            {
                try
                {
                    _currentSession.PlaybackInfoChanged -= OnSessionPlaybackInfoChanged;
                    _currentSession.MediaPropertiesChanged -= OnSessionMediaPropertiesChanged;
                }
                catch { }
                _currentSession = null;
            }

            if (_sessionManager != null)
            {
                try
                {
                    _sessionManager.CurrentSessionChanged -= OnCurrentSessionChanged;
                }
                catch { }
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
                    Debug.WriteLine($"[NotificationsDispose] {ex.Message}");
                }
                _listenerSetup = false;
            }
            GC.SuppressFinalize(this);
        }
    }
}
