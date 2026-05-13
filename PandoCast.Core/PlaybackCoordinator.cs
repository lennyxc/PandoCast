using PandoCast.Core.Models;

namespace PandoCast.Core
{
    public enum PlaybackStatus
    {
        Stopped,
        Loading,
        Ready,
        Buffering,
        Playing,
        Paused,
        Failed
    }

    public class PlaybackState
    {
        public PlaybackStatus Status { get; init; } = PlaybackStatus.Stopped;
        public PandoraStation? Station { get; init; }
        public PandoraStationMode? StationMode { get; init; }
        public PandoraTrack? CurrentTrack { get; init; }
        public IReadOnlyList<PandoraTrack> Queue { get; init; } = [];
        public string Message { get; init; } = "Select a station to play.";
        public string ReceiverName { get; init; } = string.Empty;
        public double VolumeLevel { get; init; } = 50;

        public bool HasPlayableTrack => CurrentTrack != null;
        public bool CanTogglePlayPause => HasPlayableTrack && Status is (PlaybackStatus.Ready or PlaybackStatus.Playing or PlaybackStatus.Paused or PlaybackStatus.Failed);
        public bool CanAdjustVolume => !string.IsNullOrWhiteSpace(ReceiverName) && Status is (PlaybackStatus.Playing or PlaybackStatus.Paused or PlaybackStatus.Buffering);

        public PlaybackState WithStatus(PlaybackStatus status, string message)
        {
            return new PlaybackState
            {
                Status = status,
                Station = Station,
                StationMode = StationMode,
                CurrentTrack = CurrentTrack,
                Queue = Queue,
                Message = message,
                ReceiverName = ReceiverName,
                VolumeLevel = VolumeLevel
            };
        }
    }

    public sealed class PlaybackCoordinator : IAsyncDisposable
    {
        private readonly PandoraRestApi _pandoraApi;
        private readonly ChromecastPlaybackService _castService = new();
        private readonly HttpStreamProxy _streamProxy;
        private readonly SemaphoreSlim _commandLock = new(1, 1);
        private readonly object _stateGate = new();
        private int _stationRequestVersion;
        private int _currentTrackIndex;
        private int _isAdvancingTrack;

        public PlaybackCoordinator(PandoraRestApi pandoraApi)
        {
            _pandoraApi = pandoraApi;
            _streamProxy = new HttpStreamProxy(() => State.CurrentTrack);
            _castService.PlaybackEvent += CastService_PlaybackEvent;
            _castService.VolumeChanged += CastService_VolumeChanged;
        }

        public event EventHandler<PlaybackState>? StateChanged;

        public PlaybackState State { get; private set; } = new();

        public string PreferredReceiverName
        {
            get => _castService.PreferredReceiverName;
            set => _castService.PreferredReceiverName = value.Trim();
        }

        public async Task SetPreferredReceiverNameAsync(string receiverName, bool retargetActivePlayback = true)
        {
            await _commandLock.WaitAsync().ConfigureAwait(false);
            try
            {
                _castService.PreferredReceiverName = receiverName.Trim();

                if (!retargetActivePlayback || State.CurrentTrack == null)
                {
                    return;
                }

                if (State.Status is not (PlaybackStatus.Playing or PlaybackStatus.Paused or PlaybackStatus.Buffering))
                {
                    return;
                }

                bool shouldPauseAfterRetarget = State.Status == PlaybackStatus.Paused;

                try
                {
                    SetState(State.WithReceiver(string.Empty).WithStatus(PlaybackStatus.Loading, "Switching Chromecast receiver..."));
                    await _castService.StopAsync().ConfigureAwait(false);
                    await CastCurrentTrackAsync("Finding selected Chromecast...").ConfigureAwait(false);

                    if (shouldPauseAfterRetarget)
                    {
                        SetState(State.WithStatus(PlaybackStatus.Buffering, "Pausing Chromecast playback..."));
                        await _castService.PauseAsync().ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    SetState(State.WithReceiver(string.Empty).WithStatus(PlaybackStatus.Failed, $"Receiver switch failed: {ex.Message}"));
                }
            }
            finally
            {
                _commandLock.Release();
            }
        }

        public async Task<ChromecastReceiverInfo[]> DiscoverReceiversAsync(CancellationToken cancellationToken = default)
        {
            var receivers = await _castService.DiscoverReceiversAsync(cancellationToken).ConfigureAwait(false);

            return receivers.Select(receiver => new ChromecastReceiverInfo
            {
                Name = receiver.Name,
                Model = receiver.Model,
                DeviceUri = receiver.DeviceUri?.ToString() ?? string.Empty
            }).ToArray();
        }

        public async Task PlayStationAsync(PandoraStation station, PandoraStationMode? stationMode = null)
        {
            await _commandLock.WaitAsync().ConfigureAwait(false);
            try
            {
                int requestVersion = Interlocked.Increment(ref _stationRequestVersion);
                _currentTrackIndex = 0;

                if (State.Status is PlaybackStatus.Playing or PlaybackStatus.Paused or PlaybackStatus.Buffering)
                {
                    await _castService.StopAsync().ConfigureAwait(false);
                    await _streamProxy.StopAsync().ConfigureAwait(false);
                }

                SetState(new PlaybackState
                {
                    Status = PlaybackStatus.Loading,
                    Station = station,
                    StationMode = stationMode,
                    Message = stationMode == null
                        ? $"Loading {station.StationName}..."
                        : $"Loading {station.StationName} - {stationMode.DisplayName}..."
                });

                PandoraTrack[] playlist = await _pandoraApi.GetPlaylistAsync(station.StationToken, stationIsStarting: true);
                if (requestVersion != _stationRequestVersion) return;

                PandoraTrack[] playableTracks = playlist.Where(track => track.IsPlayable).ToArray();
                PandoraTrack? currentTrack = playableTracks.FirstOrDefault();

                if (currentTrack == null)
                {
                    SetState(new PlaybackState
                    {
                        Status = PlaybackStatus.Failed,
                        Station = station,
                        StationMode = stationMode,
                        Message = "Pandora did not return a playable track for this station."
                    });

                    return;
                }

                SetState(new PlaybackState
                {
                    Status = PlaybackStatus.Ready,
                    Station = station,
                    StationMode = stationMode,
                    CurrentTrack = currentTrack,
                    Queue = playableTracks,
                    Message = stationMode == null
                        ? "Track loaded. Ready to cast."
                        : $"{stationMode.DisplayName} loaded. Ready to cast."
                });
            }
            finally
            {
                _commandLock.Release();
            }
        }

        public Task PlayAsync()
        {
            return PlayCoreAsync();
        }

        public Task PauseAsync()
        {
            return PauseCoreAsync();
        }

        public async Task SetVolumeAsync(double volumeLevel)
        {
            double clampedVolumeLevel = Math.Clamp(volumeLevel, 0, 100);
            SetState(State.WithVolume(clampedVolumeLevel));

            if (!State.CanAdjustVolume) return;

            await _castService.SetVolumeAsync(clampedVolumeLevel / 100).ConfigureAwait(false);
        }

        public async Task ChangeStationModeAsync(PandoraStationMode stationMode)
        {
            await _commandLock.WaitAsync().ConfigureAwait(false);
            try
            {
                PlaybackState state = State;
                PandoraStation? station = state.Station;
                if (station == null) return;

                int requestVersion = Interlocked.Increment(ref _stationRequestVersion);
                bool shouldCastAfterReload = state.Status is PlaybackStatus.Playing or PlaybackStatus.Buffering;
                bool shouldPauseAfterReload = state.Status == PlaybackStatus.Paused;

                SetState(new PlaybackState
                {
                    Status = PlaybackStatus.Loading,
                    Station = station,
                    StationMode = stationMode,
                    CurrentTrack = state.CurrentTrack,
                    Queue = state.Queue,
                    Message = $"Loading {stationMode.DisplayName}...",
                    ReceiverName = state.ReceiverName,
                    VolumeLevel = state.VolumeLevel
                });

                PandoraTrack[] playlist = await _pandoraApi.GetPlaylistAsync(
                    station.StationToken,
                    fragmentRequestReason: "changeMode",
                    lastPlayedTrackToken: state.CurrentTrack?.TrackToken).ConfigureAwait(false);

                if (requestVersion != _stationRequestVersion) return;

                PandoraTrack[] playableTracks = playlist.Where(track => track.IsPlayable).ToArray();
                PandoraTrack? currentTrack = playableTracks.FirstOrDefault();
                if (currentTrack == null)
                {
                    SetState(State.WithStatus(PlaybackStatus.Failed, $"Pandora did not return a playable track for {stationMode.DisplayName}."));
                    return;
                }

                _currentTrackIndex = 0;
                SetState(new PlaybackState
                {
                    Status = PlaybackStatus.Ready,
                    Station = station,
                    StationMode = stationMode,
                    CurrentTrack = currentTrack,
                    Queue = playableTracks,
                    Message = $"{stationMode.DisplayName} loaded. Ready to cast.",
                    ReceiverName = state.ReceiverName,
                    VolumeLevel = state.VolumeLevel
                });

                if (!shouldCastAfterReload && !shouldPauseAfterReload) return;

                await CastCurrentTrackAsync($"Casting {stationMode.DisplayName}...").ConfigureAwait(false);

                if (shouldPauseAfterReload)
                {
                    SetState(State.WithStatus(PlaybackStatus.Buffering, "Pausing Chromecast playback..."));
                    await _castService.PauseAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                _commandLock.Release();
            }
        }

        public async Task StopAsync()
        {
            await _commandLock.WaitAsync().ConfigureAwait(false);
            try
            {
                Interlocked.Increment(ref _stationRequestVersion);
                await _castService.StopAsync().ConfigureAwait(false);
                await _streamProxy.StopAsync().ConfigureAwait(false);
                SetState(new PlaybackState { Status = PlaybackStatus.Stopped });
            }
            finally
            {
                _commandLock.Release();
            }
        }

        public async ValueTask DisposeAsync()
        {
            _castService.PlaybackEvent -= CastService_PlaybackEvent;
            _castService.VolumeChanged -= CastService_VolumeChanged;
            await _streamProxy.DisposeAsync().ConfigureAwait(false);
            await _castService.DisposeAsync().ConfigureAwait(false);
            _commandLock.Dispose();
        }

        private async Task PlayCoreAsync()
        {
            await _commandLock.WaitAsync().ConfigureAwait(false);
            try
            {
                PlaybackState state = State;
                if (!state.HasPlayableTrack) return;

                try
                {
                    if (state.Status == PlaybackStatus.Paused)
                    {
                        SetState(state.WithStatus(PlaybackStatus.Buffering, "Resuming Chromecast playback..."));
                        await _castService.ResumeAsync().ConfigureAwait(false);
                        return;
                    }

                    if (state.Status is not PlaybackStatus.Ready and not PlaybackStatus.Failed) return;

                    await CastCurrentTrackAsync("Finding Chromecast...").ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    SetState(State.WithStatus(PlaybackStatus.Failed, $"Cast failed: {ex.Message}"));
                }
            }
            finally
            {
                _commandLock.Release();
            }
        }

        private async Task PauseCoreAsync()
        {
            await _commandLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (State.Status != PlaybackStatus.Playing) return;

                SetState(State.WithStatus(PlaybackStatus.Buffering, "Pausing Chromecast playback..."));
                await _castService.PauseAsync().ConfigureAwait(false);
            }
            finally
            {
                _commandLock.Release();
            }
        }

        private async Task CastCurrentTrackAsync(string startMessage)
        {
            PlaybackState state = State;
            PandoraTrack currentTrack = state.CurrentTrack ?? throw new InvalidOperationException("No playable track is selected.");

            SetState(state.WithStatus(PlaybackStatus.Loading, startMessage));

            await _streamProxy.StartAsync().ConfigureAwait(false);
            var receiver = await _castService.EnsureConnectedAsync().ConfigureAwait(false);
            Uri streamUri = _streamProxy.GetStreamUri(receiver.DeviceUri, currentTrack);

            SetState(State.WithReceiver(receiver.Name).WithStatus(PlaybackStatus.Buffering, "Sending audio to Chromecast..."));
            await _castService.CastTrackAsync(currentTrack, streamUri).ConfigureAwait(false);
            SetState(State.WithReceiver(receiver.Name).WithStatus(PlaybackStatus.Playing, "Casting."));
        }

        private void SetState(PlaybackState state)
        {
            lock (_stateGate)
            {
                State = state;
            }

            StateChanged?.Invoke(this, State);
        }

        private void CastService_PlaybackEvent(object? sender, CastPlaybackEventArgs e)
        {
            switch (e.Kind)
            {
                case CastPlaybackEventKind.Finished:
                    _ = AdvanceToNextTrackAsync();
                    break;
                case CastPlaybackEventKind.Playing:
                    if (State.Status != PlaybackStatus.Playing)
                    {
                        SetState(State.WithStatus(PlaybackStatus.Playing, e.Message));
                    }
                    break;
                case CastPlaybackEventKind.Paused:
                    SetState(State.WithStatus(PlaybackStatus.Paused, e.Message));
                    break;
                case CastPlaybackEventKind.Buffering:
                    if (State.Status is PlaybackStatus.Playing or PlaybackStatus.Buffering or PlaybackStatus.Loading)
                    {
                        SetState(State.WithStatus(PlaybackStatus.Buffering, e.Message));
                    }
                    break;
                case CastPlaybackEventKind.Error:
                case CastPlaybackEventKind.Disconnected:
                    if (State.Status != PlaybackStatus.Stopped)
                    {
                        SetState(State.WithStatus(PlaybackStatus.Failed, e.Message));
                    }
                    break;
            }
        }

        private async Task AdvanceToNextTrackAsync()
        {
            if (Interlocked.Exchange(ref _isAdvancingTrack, 1) == 1) return;

            await _commandLock.WaitAsync().ConfigureAwait(false);
            try
            {
                PlaybackState state = State;
                PandoraStation? station = state.Station;
                if (station == null || state.CurrentTrack == null) return;

                List<PandoraTrack> queue = state.Queue.ToList();
                int nextIndex = _currentTrackIndex + 1;

                if (nextIndex >= queue.Count)
                {
                    SetState(state.WithStatus(PlaybackStatus.Loading, "Requesting more tracks from Pandora..."));
                    await AppendMoreTracksAsync(station, queue).ConfigureAwait(false);
                }

                if (nextIndex >= queue.Count)
                {
                    SetState(state.WithStatus(PlaybackStatus.Failed, "Pandora did not return another playable track."));
                    return;
                }

                _currentTrackIndex = nextIndex;
                PandoraTrack nextTrack = queue[nextIndex];

                SetState(new PlaybackState
                {
                    Status = PlaybackStatus.Loading,
                    Station = station,
                    StationMode = state.StationMode,
                    CurrentTrack = nextTrack,
                    Queue = queue,
                    Message = "Loading next track...",
                    ReceiverName = state.ReceiverName,
                    VolumeLevel = state.VolumeLevel
                });

                if (queue.Count - _currentTrackIndex <= 2)
                {
                    await AppendMoreTracksAsync(station, queue).ConfigureAwait(false);
                    SetState(new PlaybackState
                    {
                        Status = State.Status,
                        Station = station,
                        StationMode = State.StationMode,
                        CurrentTrack = nextTrack,
                        Queue = queue,
                        Message = State.Message,
                        ReceiverName = State.ReceiverName,
                        VolumeLevel = State.VolumeLevel
                    });
                }

                await CastCurrentTrackAsync("Casting next track...").ConfigureAwait(false);
            }
            finally
            {
                _commandLock.Release();
                Interlocked.Exchange(ref _isAdvancingTrack, 0);
            }
        }

        private async Task AppendMoreTracksAsync(PandoraStation station, List<PandoraTrack> queue)
        {
            PandoraTrack[] moreTracks = await _pandoraApi.GetPlaylistAsync(station.StationToken).ConfigureAwait(false);
            var existingTokens = new HashSet<string>(queue.Select(track => track.TrackToken));

            foreach (PandoraTrack track in moreTracks.Where(track => track.IsPlayable))
            {
                if (existingTokens.Add(track.TrackToken))
                {
                    queue.Add(track);
                }
            }
        }

        private void CastService_VolumeChanged(object? sender, double volumeLevel)
        {
            SetState(State.WithVolume(volumeLevel * 100));
        }
    }

    internal static class PlaybackStateReceiverExtensions
    {
        public static PlaybackState WithReceiver(this PlaybackState state, string receiverName)
        {
            return new PlaybackState
            {
                Status = state.Status,
                Station = state.Station,
                StationMode = state.StationMode,
                CurrentTrack = state.CurrentTrack,
                Queue = state.Queue,
                Message = state.Message,
                ReceiverName = receiverName,
                VolumeLevel = state.VolumeLevel
            };
        }

        public static PlaybackState WithVolume(this PlaybackState state, double volumeLevel)
        {
            return new PlaybackState
            {
                Status = state.Status,
                Station = state.Station,
                StationMode = state.StationMode,
                CurrentTrack = state.CurrentTrack,
                Queue = state.Queue,
                Message = state.Message,
                ReceiverName = state.ReceiverName,
                VolumeLevel = Math.Clamp(volumeLevel, 0, 100)
            };
        }
    }

}
