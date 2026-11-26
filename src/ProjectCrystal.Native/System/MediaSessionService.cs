using ProjectCrystal.Core.Diagnostics;
using ProjectCrystal.Core.Models;
using Windows.Media.Control;

namespace ProjectCrystal.Native.System;

public sealed class MediaSessionService
{
    private GlobalSystemMediaTransportControlsSessionManager? _manager;

    public async Task InitializeAsync()
    {
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        }
        catch (Exception ex)
        {
            CrystalLogger.Warn("Media", $"SMTC unavailable: {ex.Message}");
        }
    }

    public async Task<MediaSessionInfo> GetCurrentAsync()
    {
        if (_manager is null)
            return new MediaSessionInfo(false, "", "No Media Playing", "", "", false, false, false, false, null);

        try
        {
            var session = _manager.GetCurrentSession();
            if (session is null)
                return new MediaSessionInfo(false, "", "No Media Playing", "", "", false, false, false, false, null);

            var props = await session.TryGetMediaPropertiesAsync();
            var playback = session.GetPlaybackInfo();
            bool playing = playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            var controls = playback.Controls;

            byte[]? thumb = null;
            if (props.Thumbnail != null)
            {
                try
                {
                    using var stream = await props.Thumbnail.OpenReadAsync();
                    using var netStream = stream.AsStreamForRead();
                    using var ms = new MemoryStream();
                    await netStream.CopyToAsync(ms);
                    thumb = ms.ToArray();
                }
                catch { }
            }

            return new MediaSessionInfo(
                HasSession: true,
                SourceApp: session.SourceAppUserModelId ?? "Media",
                Title: string.IsNullOrWhiteSpace(props.Title) ? "Active Media" : props.Title,
                Artist: props.Artist ?? "",
                Album: props.AlbumTitle ?? "",
                IsPlaying: playing,
                CanPlayPause: controls.IsPauseEnabled || controls.IsPlayEnabled,
                CanNext: controls.IsNextEnabled,
                CanPrevious: controls.IsPreviousEnabled,
                ThumbnailPng: thumb);
        }
        catch
        {
            return new MediaSessionInfo(false, "", "No Media Playing", "", "", false, false, false, false, null);
        }
    }

    public async Task TogglePlayPauseAsync()
    {
        try { var s = _manager?.GetCurrentSession(); if (s != null) await s.TryTogglePlayPauseAsync(); } catch { }
    }

    public async Task NextAsync()
    {
        try { var s = _manager?.GetCurrentSession(); if (s != null) await s.TrySkipNextAsync(); } catch { }
    }

    public async Task PreviousAsync()
    {
        try { var s = _manager?.GetCurrentSession(); if (s != null) await s.TrySkipPreviousAsync(); } catch { }
    }
}
