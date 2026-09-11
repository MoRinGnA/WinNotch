using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using Windows.Media.Control;
using Windows.Storage.Streams;
using WinNotch.Models;

namespace WinNotch.Services
{
    public class MediaService : IDisposable
    {
        private GlobalSystemMediaTransportControlsSessionManager? _sessionManager;
        private GlobalSystemMediaTransportControlsSession? _currentSession;
        private readonly object _sessionLock = new();
        private bool _disposed;

        public event Action<MediaMetadata?>? MediaChanged;
        public event Action<bool>? PlaybackStatusChanged;
        public event Action<TimeSpan, TimeSpan>? TimelineChanged;

        public MediaMetadata? CurrentMedia { get; private set; }

        public async Task InitializeAsync()
        {
            try
            {
                _sessionManager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
                if (_sessionManager != null)
                {
                    _sessionManager.CurrentSessionChanged += SessionManager_CurrentSessionChanged;
                }
                await RefreshSessionAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"MediaService Initialize error: {ex.Message}");
            }
        }

        private async void SessionManager_CurrentSessionChanged(GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args)
        {
            await RefreshSessionAsync();
        }

        private async Task RefreshSessionAsync()
        {
            if (_disposed) return;

            GlobalSystemMediaTransportControlsSession? newSession = null;
            try
            {
                newSession = _sessionManager?.GetCurrentSession();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"GetCurrentSession error: {ex.Message}");
            }

            lock (_sessionLock)
            {
                if (_currentSession != null)
                {
                    _currentSession.MediaPropertiesChanged -= CurrentSession_MediaPropertiesChanged;
                    _currentSession.PlaybackInfoChanged -= CurrentSession_PlaybackInfoChanged;
                    _currentSession.TimelinePropertiesChanged -= CurrentSession_TimelinePropertiesChanged;
                }

                _currentSession = newSession;

                if (_currentSession != null)
                {
                    _currentSession.MediaPropertiesChanged += CurrentSession_MediaPropertiesChanged;
                    _currentSession.PlaybackInfoChanged += CurrentSession_PlaybackInfoChanged;
                    _currentSession.TimelinePropertiesChanged += CurrentSession_TimelinePropertiesChanged;
                }
            }

            await UpdateMediaPropertiesAsync();
        }

        private async void CurrentSession_MediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args)
        {
            await UpdateMediaPropertiesAsync();
        }

        private void CurrentSession_PlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args)
        {
            try
            {
                var playbackInfo = sender.GetPlaybackInfo();
                bool isPlaying = playbackInfo?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                if (CurrentMedia != null)
                {
                    CurrentMedia.IsPlaying = isPlaying;
                }
                PlaybackStatusChanged?.Invoke(isPlaying);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"PlaybackInfoChanged error: {ex.Message}");
            }
        }

        private void CurrentSession_TimelinePropertiesChanged(GlobalSystemMediaTransportControlsSession sender, TimelinePropertiesChangedEventArgs args)
        {
            try
            {
                var timeline = sender.GetTimelineProperties();
                if (timeline != null && CurrentMedia != null)
                {
                    CurrentMedia.Position = timeline.Position;
                    CurrentMedia.Duration = timeline.EndTime;
                    CurrentMedia.LastUpdatedTime = timeline.LastUpdatedTime;
                    TimelineChanged?.Invoke(timeline.Position, timeline.EndTime);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"TimelinePropertiesChanged error: {ex.Message}");
            }
        }

        public async Task UpdateMediaPropertiesAsync()
        {
            if (_disposed) return;

            GlobalSystemMediaTransportControlsSession? session;
            lock (_sessionLock)
            {
                session = _currentSession;
            }

            if (session == null)
            {
                CurrentMedia = null;
                MediaChanged?.Invoke(null);
                PlaybackStatusChanged?.Invoke(false);
                return;
            }

            try
            {
                var mediaProps = await session.TryGetMediaPropertiesAsync();
                var playbackInfo = session.GetPlaybackInfo();
                var timeline = session.GetTimelineProperties();

                if (mediaProps == null)
                {
                    CurrentMedia = null;
                    MediaChanged?.Invoke(null);
                    PlaybackStatusChanged?.Invoke(false);
                    return;
                }

                var thumbnail = await LoadThumbnailAsync(mediaProps.Thumbnail);
                bool isPlaying = playbackInfo?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

                var meta = new MediaMetadata
                {
                    Title = mediaProps.Title ?? string.Empty,
                    Artist = mediaProps.Artist ?? string.Empty,
                    Thumbnail = thumbnail,
                    IsPlaying = isPlaying,
                    Position = timeline?.Position ?? TimeSpan.Zero,
                    Duration = timeline?.EndTime ?? TimeSpan.Zero,
                    LastUpdatedTime = timeline?.LastUpdatedTime ?? DateTimeOffset.UtcNow
                };

                CurrentMedia = meta;
                MediaChanged?.Invoke(meta);
                PlaybackStatusChanged?.Invoke(isPlaying);
                TimelineChanged?.Invoke(meta.Position, meta.Duration);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"UpdateMediaPropertiesAsync error: {ex.Message}");
            }
        }

        private static async Task<BitmapImage?> LoadThumbnailAsync(IRandomAccessStreamReference? thumbnailRef)
        {
            if (thumbnailRef == null) return null;

            try
            {
                using var stream = await thumbnailRef.OpenReadAsync();
                using var netStream = stream.AsStream();
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.StreamSource = netStream;
                bitmap.EndInit();
                bitmap.Freeze();
                return bitmap;
            }
            catch
            {
                return null;
            }
        }

        public async Task<bool> TryTogglePlayPauseAsync()
        {
            try
            {
                GlobalSystemMediaTransportControlsSession? session;
                lock (_sessionLock) { session = _currentSession; }
                session ??= _sessionManager?.GetCurrentSession();
                if (session != null)
                {
                    return await session.TryTogglePlayPauseAsync();
                }
            }
            catch { }
            return false;
        }

        public async Task<bool> TrySkipNextAsync()
        {
            try
            {
                GlobalSystemMediaTransportControlsSession? session;
                lock (_sessionLock) { session = _currentSession; }
                session ??= _sessionManager?.GetCurrentSession();
                if (session != null)
                {
                    return await session.TrySkipNextAsync();
                }
            }
            catch { }
            return false;
        }

        public async Task<bool> TrySkipPreviousAsync()
        {
            try
            {
                GlobalSystemMediaTransportControlsSession? session;
                lock (_sessionLock) { session = _currentSession; }
                session ??= _sessionManager?.GetCurrentSession();
                if (session != null)
                {
                    return await session.TrySkipPreviousAsync();
                }
            }
            catch { }
            return false;
        }

        public bool GetExactPosition(out TimeSpan currentPos, out TimeSpan duration)
        {
            currentPos = TimeSpan.Zero;
            duration = TimeSpan.Zero;

            try
            {
                GlobalSystemMediaTransportControlsSession? session;
                lock (_sessionLock) { session = _currentSession; }
                session ??= _sessionManager?.GetCurrentSession();

                if (session != null)
                {
                    var timeline = session.GetTimelineProperties();
                    if (timeline != null)
                    {
                        currentPos = timeline.Position;
                        duration = timeline.EndTime;

                        var elapsed = DateTimeOffset.UtcNow - timeline.LastUpdatedTime;
                        if (elapsed > TimeSpan.Zero && elapsed < TimeSpan.FromHours(1))
                        {
                            currentPos += elapsed;
                            if (duration > TimeSpan.Zero && currentPos > duration)
                            {
                                currentPos = duration;
                            }
                        }

                        if (duration > TimeSpan.Zero && currentPos >= duration - TimeSpan.FromMilliseconds(200))
                        {
                            _ = Task.Run(UpdateMediaPropertiesAsync);
                        }

                        if (CurrentMedia != null)
                        {
                            CurrentMedia.Position = currentPos;
                            CurrentMedia.Duration = duration;
                        }

                        return true;
                    }
                }
            }
            catch { }

            if (CurrentMedia != null)
            {
                currentPos = CurrentMedia.CurrentEstimatedPosition;
                duration = CurrentMedia.Duration;
                return true;
            }

            return false;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            lock (_sessionLock)
            {
                if (_currentSession != null)
                {
                    _currentSession.MediaPropertiesChanged -= CurrentSession_MediaPropertiesChanged;
                    _currentSession.PlaybackInfoChanged -= CurrentSession_PlaybackInfoChanged;
                    _currentSession.TimelinePropertiesChanged -= CurrentSession_TimelinePropertiesChanged;
                    _currentSession = null;
                }
            }

            if (_sessionManager != null)
            {
                _sessionManager.CurrentSessionChanged -= SessionManager_CurrentSessionChanged;
                _sessionManager = null;
            }
        }
    }
}