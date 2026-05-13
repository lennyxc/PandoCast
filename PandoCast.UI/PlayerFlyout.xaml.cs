using PandoCast.Core;
using PandoCast.Core.Models;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;

namespace PandoCast.UI
{
    public partial class PlayerFlyout : UserControl
    {
        private readonly ObservableCollection<StationListItemViewModel> _stations = [];
        private readonly ObservableCollection<PandoraStationMode> _stationModes = [];
        private readonly Dictionary<string, StationListItemViewModel> _stationItemsByKey = [];
        private PandoraStation[] _displayedStations = [];
        private PandoraStation? _modeStation;
        private PandoraStation? _pendingStation;
        private PandoraStationMode? _selectedMode;
        private int _currentModeId;
        private int _modeRequestVersion;
        private bool _isRefreshingStations;
        private bool _isLoadingModes;
        private bool _isApplyingMode;
        private bool _isUpdatingModeSelection;
        private bool _isUpdatingVolumeUi;
        private string _pendingStationMessage = string.Empty;
        private CancellationTokenSource? _volumeChangeDebounce;

        public PlayerFlyout()
        {
            InitializeComponent();
            StationListBox.ItemsSource = _stations;
            ModeListBox.ItemsSource = _stationModes;
            App.Playback.StateChanged += Playback_StateChanged;
            UpdatePlaybackState(App.Playback.State);
        }

        private async void UserControl_Loaded(object sender, RoutedEventArgs e)
        {
            if (!App.IsLoggedIn)
            {
                HeaderTextBlock.Text = "Please log in via Settings.";
                ShowStations([]);
                TrackTitleTextBlock.Text = "Please log in via Settings.";
                TrackSubtitleTextBlock.Text = "";
                PlaybackStatusTextBlock.Text = "";
                ReceiverTextBlock.Text = "";
                HideStationModes();
                PlayPauseButton.IsEnabled = false;
                VolumeSlider.IsEnabled = false;
                return;
            }

            var cachedStations = App.PandoraApi.GetCachedStations();
            if (cachedStations.Length > 0)
            {
                ShowStations(cachedStations);
                HeaderTextBlock.Text = "Your Stations";
            }
            else
            {
                HeaderTextBlock.Text = "Loading Stations...";
            }

            if (_isRefreshingStations) return;

            _isRefreshingStations = true;
            try
            {
                var stations = await App.PandoraApi.GetStationsCachedAsync();

                if (stations.Length > 0)
                {
                    ShowStations(stations);
                    HeaderTextBlock.Text = "Your Stations";
                }
                else
                {
                    ShowStations([]);
                    HeaderTextBlock.Text = "No Stations Found";
                }
            }
            finally
            {
                _isRefreshingStations = false;
            }
        }

        private void ShowStations(PandoraStation[] stations)
        {
            if (ReferenceEquals(_displayedStations, stations)) return;

            _displayedStations = stations;
            _stations.Clear();

            var activeKeys = new HashSet<string>();

            foreach (var station in stations)
            {
                string key = GetStationKey(station);
                activeKeys.Add(key);

                if (!_stationItemsByKey.TryGetValue(key, out var item))
                {
                    item = new StationListItemViewModel(station);
                    _stationItemsByKey[key] = item;
                }
                else
                {
                    item.UpdateStation(station);
                }

                _stations.Add(item);
                _ = item.LoadArtAsync();
            }

            foreach (var key in _stationItemsByKey.Keys.Except(activeKeys).ToArray())
            {
                _stationItemsByKey.Remove(key);
            }
        }

        private static string GetStationKey(PandoraStation station)
        {
            return string.IsNullOrWhiteSpace(station.StationToken)
                ? station.StationName
                : station.StationToken;
        }

        private async void StationListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (StationListBox.SelectedItem is StationListItemViewModel selectedItem)
            {
                var selectedStation = selectedItem.Station;
                StationListBox.SelectedItem = null;

                await LoadStationModesAsync(selectedStation);
            }
        }

        private async Task LoadStationModesAsync(PandoraStation station)
        {
            int requestVersion = Interlocked.Increment(ref _modeRequestVersion);

            _modeStation = station;
            _pendingStation = station;
            _selectedMode = null;
            _currentModeId = 0;
            _isLoadingModes = true;
            _isApplyingMode = false;
            _pendingStationMessage = "Loading station modes...";

            _stationModes.Clear();
            ModePanel.Visibility = Visibility.Visible;
            ModeHeaderTextBlock.Text = $"Loading modes for {station.StationName}...";
            UpdatePlaybackState(App.Playback.State);

            try
            {
                PandoraStationModesResult? result = await App.PandoraApi.GetAvailableStationModesAsync(station.StationToken);
                if (requestVersion != _modeRequestVersion) return;

                if (result?.InteractiveRadioAvailable == true && result.SelectableModes.Length > 0)
                {
                    ShowStationModes(result);
                    _pendingStationMessage = "Choose a mode, then press Play to cast.";
                }
                else
                {
                    ModeHeaderTextBlock.Text = "No station modes available. Default playback will be used.";
                    _pendingStationMessage = "Press Play to cast this station.";
                }
            }
            catch (Exception ex)
            {
                if (requestVersion != _modeRequestVersion) return;

                ModeHeaderTextBlock.Text = "Could not load station modes. Default playback will be used.";
                _pendingStationMessage = $"Mode lookup failed: {ex.Message}";
            }
            finally
            {
                if (requestVersion == _modeRequestVersion)
                {
                    _isLoadingModes = false;
                    UpdatePlaybackState(App.Playback.State);
                }
            }
        }

        private void ShowStationModes(PandoraStationModesResult result)
        {
            PandoraStationMode[] modes = result.SelectableModes;
            PandoraStationMode selectedMode = result.CurrentMode
                ?? modes.FirstOrDefault(mode => mode.IsInitialMode)
                ?? modes[0];

            _stationModes.Clear();
            foreach (PandoraStationMode mode in modes)
            {
                _stationModes.Add(mode);
            }

            _currentModeId = selectedMode.ModeId;
            _selectedMode = _stationModes.FirstOrDefault(mode => mode.ModeId == selectedMode.ModeId) ?? selectedMode;
            ModeHeaderTextBlock.Text = string.IsNullOrWhiteSpace(result.AvailableModesHeader)
                ? "Choose a mode to fine-tune your station"
                : result.AvailableModesHeader;

            _isUpdatingModeSelection = true;
            try
            {
                ModeListBox.SelectedItem = _selectedMode;
            }
            finally
            {
                _isUpdatingModeSelection = false;
            }
        }

        private async void ModeListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isUpdatingModeSelection) return;
            PandoraStation? station = _modeStation ?? _pendingStation;
            if (station == null) return;
            if (ModeListBox.SelectedItem is not PandoraStationMode selectedMode) return;

            _selectedMode = selectedMode;
            _pendingStationMessage = _pendingStation == null
                ? $"Switching to {selectedMode.DisplayName}..."
                : $"{selectedMode.DisplayName} selected. Press Play to cast.";
            UpdatePlaybackState(App.Playback.State);

            if (selectedMode.ModeId == _currentModeId) return;

            int requestVersion = _modeRequestVersion;
            int previousModeId = _currentModeId;

            try
            {
                _isApplyingMode = true;
                ModeHeaderTextBlock.Text = $"Switching to {selectedMode.DisplayName}...";
                _pendingStationMessage = $"Switching to {selectedMode.DisplayName}...";
                UpdatePlaybackState(App.Playback.State);

                PandoraStationModesResult? result = await App.PandoraApi.SetStationModeAsync(station.StationToken, selectedMode.ModeId, previousModeId);
                if (requestVersion != _modeRequestVersion) return;

                if (result?.InteractiveRadioAvailable == true && result.SelectableModes.Length > 0)
                {
                    ShowStationModes(result);
                    selectedMode = _selectedMode ?? selectedMode;
                    _pendingStationMessage = _pendingStation == null
                        ? $"{selectedMode.DisplayName} loaded."
                        : $"{selectedMode.DisplayName} selected. Press Play to cast.";
                }
                else
                {
                    _currentModeId = selectedMode.ModeId;
                    ModeHeaderTextBlock.Text = "Choose a mode to fine-tune your station";
                }

                if (_pendingStation == null && IsPlaybackStation(station))
                {
                    await App.Playback.ChangeStationModeAsync(selectedMode);
                }
            }
            catch (Exception ex)
            {
                if (requestVersion != _modeRequestVersion) return;

                _pendingStationMessage = $"Mode switch failed: {ex.Message}";
            }
            finally
            {
                if (requestVersion == _modeRequestVersion)
                {
                    _isApplyingMode = false;
                    UpdatePlaybackState(App.Playback.State);
                }
            }
        }

        private async void PlayPauseButton_Click(object sender, RoutedEventArgs e)
        {
            if (_pendingStation != null)
            {
                await PlayPendingStationAsync();
                return;
            }

            if (App.Playback.State.Status == PlaybackStatus.Playing)
            {
                await App.Playback.PauseAsync();
                return;
            }

            await App.Playback.PlayAsync();
        }

        private async Task PlayPendingStationAsync()
        {
            PandoraStation station = _pendingStation ?? throw new InvalidOperationException("No station is selected.");
            PandoraStationMode? selectedMode = _selectedMode ?? _stationModes.FirstOrDefault();

            if (selectedMode != null && selectedMode.ModeId != _currentModeId)
            {
                PandoraStationModesResult? result = await App.PandoraApi.SetStationModeAsync(station.StationToken, selectedMode.ModeId, _currentModeId);
                if (result?.InteractiveRadioAvailable == true && result.CurrentMode != null)
                {
                    selectedMode = result.CurrentMode;
                }
            }

            _modeStation = station;
            _pendingStation = null;
            _pendingStationMessage = string.Empty;
            UpdatePlaybackState(App.Playback.State);

            await App.Playback.PlayStationAsync(station, selectedMode);
            await App.Playback.PlayAsync();
        }

        private static bool IsSameStation(PandoraStation first, PandoraStation second)
        {
            return string.Equals(GetStationKey(first), GetStationKey(second), StringComparison.OrdinalIgnoreCase);
        }

        private bool IsPlaybackStation(PandoraStation station)
        {
            PandoraStation? playbackStation = App.Playback.State.Station;
            return playbackStation != null && IsSameStation(station, playbackStation);
        }

        private void Playback_StateChanged(object? sender, PlaybackState state)
        {
            Dispatcher.Invoke(() => UpdatePlaybackState(state));
        }

        private void UpdatePlaybackState(PlaybackState state)
        {
            if (_pendingStation != null)
            {
                TrackTitleTextBlock.Text = _pendingStation.StationName;
                TrackSubtitleTextBlock.Text = _selectedMode == null
                    ? "Select a mode or press Play for the default station mode."
                    : $"Mode: {_selectedMode.DisplayName}";
            }
            else if (state.CurrentTrack != null)
            {
                TrackTitleTextBlock.Text = state.CurrentTrack.DisplayTitle;
                TrackSubtitleTextBlock.Text = state.CurrentTrack.DisplaySubtitle;
            }
            else if (state.Station != null)
            {
                TrackTitleTextBlock.Text = state.Station.StationName;
                TrackSubtitleTextBlock.Text = state.StationMode == null
                    ? "Pandora will choose the tracks for this station."
                    : $"Mode: {state.StationMode.DisplayName}";
            }
            else
            {
                TrackTitleTextBlock.Text = "Select a station to play...";
                TrackSubtitleTextBlock.Text = "Pandora will choose the tracks for the station.";
            }

            PlaybackStatusTextBlock.Text = _isApplyingMode || _pendingStation != null
                ? _pendingStationMessage
                : state.Message;
            ReceiverTextBlock.Text = string.IsNullOrWhiteSpace(state.ReceiverName)
                ? ""
                : $"Streaming to: {state.ReceiverName}";

            _isUpdatingVolumeUi = true;
            VolumeSlider.Value = state.VolumeLevel;
            VolumeTextBlock.Text = $"{Math.Round(state.VolumeLevel):0}%";
            VolumeSlider.IsEnabled = state.CanAdjustVolume;
            _isUpdatingVolumeUi = false;

            if (_pendingStation != null)
            {
                PlayPauseButton.IsEnabled = !_isLoadingModes && !_isApplyingMode;
                PlayPauseButton.Content = _isLoadingModes || _isApplyingMode ? "Loading" : "Play";
                return;
            }

            PlayPauseButton.IsEnabled = state.CanTogglePlayPause;
            PlayPauseButton.Content = state.Status switch
            {
                PlaybackStatus.Loading => "Loading",
                PlaybackStatus.Buffering => "Wait",
                PlaybackStatus.Playing => "Pause",
                PlaybackStatus.Paused => "Resume",
                _ => "Play"
            };
        }

        private void HideStationModes()
        {
            Interlocked.Increment(ref _modeRequestVersion);
            _modeStation = null;
            _pendingStation = null;
            _selectedMode = null;
            _currentModeId = 0;
            _isLoadingModes = false;
            _isApplyingMode = false;
            _pendingStationMessage = string.Empty;
            _stationModes.Clear();

            if (ModePanel != null)
            {
                ModePanel.Visibility = Visibility.Collapsed;
            }
        }

        private async void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isUpdatingVolumeUi || VolumeTextBlock == null) return;

            double volumeLevel = Math.Round(e.NewValue);
            VolumeTextBlock.Text = $"{volumeLevel:0}%";

            _volumeChangeDebounce?.Cancel();
            _volumeChangeDebounce?.Dispose();
            _volumeChangeDebounce = new CancellationTokenSource();
            CancellationToken cancellationToken = _volumeChangeDebounce.Token;

            try
            {
                await Task.Delay(150, cancellationToken);
                await App.Playback.SetVolumeAsync(volumeLevel);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }
}
