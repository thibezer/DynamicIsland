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

    public class MediaNotificationService
    {
        public event EventHandler? StateChanged;
        private bool _listenerSetup = false;

        public async Task InitializeAsync()
        {
            if (_listenerSetup) return;
            try
            {
                var listener = UserNotificationListener.Current;
                var accessStatus = await listener.RequestAccessAsync();
                if (accessStatus == UserNotificationListenerAccessStatus.Allowed)
                {
                    listener.NotificationChanged += (s, e) => StateChanged?.Invoke(this, EventArgs.Empty);
                    _listenerSetup = true;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Notifications] {ex.Message}");
            }
        }

        public async Task<MediaNotificationState> GetCurrentStateAsync()
        {
            var state = new MediaNotificationState();
            try
            {
                var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
                var session = manager.GetCurrentSession();

                if (session != null)
                {
                    var info = await session.TryGetMediaPropertiesAsync();
                    state.Title = string.IsNullOrEmpty(info.Title) ? "Nenhuma música" : info.Title;
                    state.Subtitle = string.IsNullOrEmpty(info.Artist) ? "Nenhum artista" : info.Artist;
                    state.Thumbnail = string.Empty;

                    try
                    {
                        var playbackInfo = session.GetPlaybackInfo();
                        state.IsPlaying = playbackInfo?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                    }
                    catch { state.IsPlaying = false; }

                    if (info.Thumbnail != null)
                    {
                        try
                        {
                            // BLINDAGEM: Usando 'using' nas três camadas de stream nativo para evitar vazamento
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

                    var playbackStatus = session.GetPlaybackInfo()?.PlaybackStatus;
                    if (playbackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Stopped ||
                        playbackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed)
                    {
                        state.Mode = "notification";
                        var (notifTitle, notifContent) = await GetLatestNotificationAsync();
                        state.Title = notifTitle;
                        state.Subtitle = notifContent;
                        state.Thumbnail = string.Empty;
                    }
                    else
                    {
                        state.Mode = "music";
                    }
                }
                else
                {
                    state.Mode = "notification";
                    var (notifTitle, notifContent) = await GetLatestNotificationAsync();
                    state.Title = notifTitle;
                    state.Subtitle = notifContent;
                    state.Thumbnail = string.Empty;
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

        private async Task<(string title, string content)> GetLatestNotificationAsync()
        {
            const string defaultTitle   = "Sem notificações";
            const string defaultContent = "Tudo limpo por aqui";
            try
            {
                var listener     = UserNotificationListener.Current;
                var accessStatus = await listener.RequestAccessAsync();
                if (accessStatus != UserNotificationListenerAccessStatus.Allowed)
                    return (defaultTitle, defaultContent);

                var notifications = await listener.GetNotificationsAsync(NotificationKinds.Toast);
                int count = notifications.Count;
                if (count == 0) return (defaultTitle, defaultContent);

                var last     = notifications[count - 1];
                var bindings = last.Notification.Visual.Bindings;
                if (bindings.Count == 0) return (defaultTitle, defaultContent);

                var elems = bindings[0].GetTextElements();
                int ec    = elems.Count;

                string title   = ec >= 2 ? elems[1].Text : (ec >= 1 ? elems[0].Text : defaultTitle);
                string content = ec >= 3 ? elems[2].Text : (ec >= 2 ? elems[1].Text : defaultContent);
                return (title, content);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Notifications] {ex.Message}");
                return (defaultTitle, defaultContent);
            }
        }

        public async Task HandleMediaCommandAsync(string? command)
        {
            if (string.IsNullOrEmpty(command)) return;
            try
            {
                var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
                var session = manager.GetCurrentSession();
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
    }
}
