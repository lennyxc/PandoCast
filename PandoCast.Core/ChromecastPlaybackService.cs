using Microsoft.Extensions.Logging.Abstractions;
using PandoCast.Core.Models;
using Sharpcaster;
using Sharpcaster.Models;
using Sharpcaster.Models.Media;

namespace PandoCast.Core
{
    public enum CastPlaybackEventKind
    {
        Connected,
        Playing,
        Paused,
        Buffering,
        Finished,
        Idle,
        Error,
        Disconnected
    }

    public class CastPlaybackEventArgs : EventArgs
    {
        public CastPlaybackEventArgs(CastPlaybackEventKind kind, string message)
        {
            Kind = kind;
            Message = message;
        }

        public CastPlaybackEventKind Kind { get; }
        public string Message { get; }
    }

    public sealed class ChromecastPlaybackService : IAsyncDisposable
    {
        private const string DefaultMediaReceiverAppId = "CC1AD845";
        private readonly SemaphoreSlim _connectionLock = new(1, 1);
        private ChromecastClient? _client;
        private ChromecastReceiver? _receiver;

        public event EventHandler<CastPlaybackEventArgs>? PlaybackEvent;
        public event EventHandler<double>? VolumeChanged;

        public ChromecastReceiver? Receiver => _receiver;
        public string PreferredReceiverName { get; set; } = string.Empty;

        public async Task<ChromecastReceiver[]> DiscoverReceiversAsync(CancellationToken cancellationToken = default)
        {
            using var locator = new ChromecastLocator(NullLogger<ChromecastLocator>.Instance);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));

            try
            {
                IEnumerable<ChromecastReceiver> receivers = await locator.FindReceiversAsync(
                    TimeSpan.FromSeconds(1),
                    TimeSpan.FromSeconds(3),
                    TimeSpan.FromSeconds(8)).ConfigureAwait(false);

                return receivers.ToArray();
            }
            catch (OperationCanceledException)
            {
                return [];
            }
        }

        public async Task<ChromecastReceiver> EnsureConnectedAsync(CancellationToken cancellationToken = default)
        {
            if (_client != null && _receiver != null && IsPreferredReceiver(_receiver)) return _receiver;

            await _connectionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_client != null && _receiver != null && IsPreferredReceiver(_receiver)) return _receiver;

                if (_client != null)
                {
                    await DisconnectClientAsync().ConfigureAwait(false);
                }

                ChromecastReceiver[] receivers = await DiscoverReceiversAsync(cancellationToken).ConfigureAwait(false);
                _receiver = SelectReceiver(receivers);

                _client = new ChromecastClient(NullLogger<ChromecastClient>.Instance);
                _client.Disconnected += Client_Disconnected;
                _client.MediaChannel.StatusChanged += MediaChannel_StatusChanged;
                _client.ReceiverChannel.ReceiverStatusChanged += ReceiverChannel_ReceiverStatusChanged;
                _client.MediaChannel.LoadFailed += (_, _) => RaiseEvent(CastPlaybackEventKind.Error, "Chromecast failed to load the audio stream.");
                _client.MediaChannel.LoadCancelled += (_, _) => RaiseEvent(CastPlaybackEventKind.Error, "Chromecast cancelled the audio load request.");
                _client.MediaChannel.ErrorHappened += (_, _) => RaiseEvent(CastPlaybackEventKind.Error, "Chromecast reported a media error.");

                var status = await _client.ConnectChromecast(_receiver).ConfigureAwait(false);
                RaiseVolumeChanged(status?.Volume?.Level);
                RaiseEvent(CastPlaybackEventKind.Connected, "Connected to Chromecast.");

                return _receiver;
            }
            finally
            {
                _connectionLock.Release();
            }
        }

        private ChromecastReceiver SelectReceiver(ChromecastReceiver[] receivers)
        {
            if (receivers.Length == 0) throw new InvalidOperationException("No Chromecast devices found on the local network.");

            if (string.IsNullOrWhiteSpace(PreferredReceiverName)) return receivers[0];

            return receivers.FirstOrDefault(receiver => string.Equals(receiver.Name, PreferredReceiverName, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Selected Chromecast receiver '{PreferredReceiverName}' was not found on the local network.");
        }

        private bool IsPreferredReceiver(ChromecastReceiver receiver)
        {
            return string.IsNullOrWhiteSpace(PreferredReceiverName)
                || string.Equals(receiver.Name, PreferredReceiverName, StringComparison.OrdinalIgnoreCase);
        }

        public async Task CastTrackAsync(PandoraTrack track, Uri streamUri, CancellationToken cancellationToken = default)
        {
            await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);

            if (_client == null) throw new InvalidOperationException("Chromecast is not connected.");

            await _client.LaunchApplicationAsync(DefaultMediaReceiverAppId, true).ConfigureAwait(false);

            var media = new Media
            {
                ContentId = streamUri.ToString(),
                ContentUrl = streamUri.ToString(),
                ContentType = track.GetPreferredContentType(),
                StreamType = StreamType.Buffered,
                Metadata = new MusicTrackMetadata
                {
                    MetadataType = MetadataType.Music,
                    Title = track.DisplayTitle,
                    SongName = track.SongName,
                    Artist = track.ArtistName,
                    AlbumName = track.AlbumName,
                    SubTitle = track.ArtistName,
                    Images = string.IsNullOrWhiteSpace(track.AlbumArtUrl)
                        ? []
                        : [new Image { Url = track.AlbumArtUrl }]
                }
            };

            await _client.MediaChannel.LoadAsync(media, true, []).ConfigureAwait(false);
        }

        public async Task ResumeAsync()
        {
            if (_client == null) return;

            await _client.MediaChannel.PlayAsync().ConfigureAwait(false);
        }

        public async Task PauseAsync()
        {
            if (_client == null) return;

            await _client.MediaChannel.PauseAsync().ConfigureAwait(false);
        }

        public async Task StopAsync()
        {
            if (_client == null) return;

            await _client.MediaChannel.StopAsync().ConfigureAwait(false);
        }

        public async Task SetVolumeAsync(double level)
        {
            if (_client == null) return;

            double normalizedLevel = Math.Clamp(level, 0, 1);
            var status = await _client.ReceiverChannel.SetVolume(normalizedLevel).ConfigureAwait(false);
            RaiseVolumeChanged(status?.Volume?.Level ?? normalizedLevel);
        }

        public async ValueTask DisposeAsync()
        {
            await DisconnectClientAsync().ConfigureAwait(false);
            _connectionLock.Dispose();
        }

        private async Task DisconnectClientAsync()
        {
            ChromecastClient? client = _client;
            _client = null;
            _receiver = null;

            if (client == null) return;

            client.Disconnected -= Client_Disconnected;
            client.MediaChannel.StatusChanged -= MediaChannel_StatusChanged;
            client.ReceiverChannel.ReceiverStatusChanged -= ReceiverChannel_ReceiverStatusChanged;

            try
            {
                await client.DisconnectAsync().ConfigureAwait(false);
            }
            catch
            {
            }

            await client.Dispose().ConfigureAwait(false);
        }

        private void MediaChannel_StatusChanged(object? sender, MediaStatus status)
        {
            switch (status.PlayerState)
            {
                case PlayerStateType.Playing:
                    RaiseEvent(CastPlaybackEventKind.Playing, "Casting.");
                    break;
                case PlayerStateType.Paused:
                    RaiseEvent(CastPlaybackEventKind.Paused, "Paused.");
                    break;
                case PlayerStateType.Buffering:
                case PlayerStateType.Loading:
                    RaiseEvent(CastPlaybackEventKind.Buffering, "Buffering on Chromecast.");
                    break;
                case PlayerStateType.Idle when string.Equals(status.IdleReason, "FINISHED", StringComparison.OrdinalIgnoreCase):
                    RaiseEvent(CastPlaybackEventKind.Finished, "Track finished.");
                    break;
                case PlayerStateType.Idle when string.Equals(status.IdleReason, "ERROR", StringComparison.OrdinalIgnoreCase):
                    RaiseEvent(CastPlaybackEventKind.Error, "Chromecast could not continue playback.");
                    break;
                case PlayerStateType.Idle:
                    RaiseEvent(CastPlaybackEventKind.Idle, "Chromecast is idle.");
                    break;
            }
        }

        private void Client_Disconnected(object? sender, EventArgs e)
        {
            _client = null;
            _receiver = null;
            RaiseEvent(CastPlaybackEventKind.Disconnected, "Chromecast disconnected.");
        }

        private void ReceiverChannel_ReceiverStatusChanged(object? sender, Sharpcaster.Models.ChromecastStatus.ChromecastStatus status)
        {
            RaiseVolumeChanged(status.Volume?.Level);
        }

        private void RaiseEvent(CastPlaybackEventKind kind, string message)
        {
            PlaybackEvent?.Invoke(this, new CastPlaybackEventArgs(kind, message));
        }

        private void RaiseVolumeChanged(double? level)
        {
            if (!level.HasValue) return;

            VolumeChanged?.Invoke(this, Math.Clamp(level.Value, 0, 1));
        }
    }
}
