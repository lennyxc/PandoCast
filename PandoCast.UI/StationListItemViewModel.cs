using PandoCast.Core.Models;
using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PandoCast.UI
{
    public class StationListItemViewModel : INotifyPropertyChanged
    {
        private static readonly HttpClient ImageClient = new();
        private ImageSource? _artSource;
        private Task? _artLoadTask;

        public StationListItemViewModel(PandoraStation station)
        {
            Station = station;
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public PandoraStation Station { get; private set; }
        public string StationName => Station.StationName;
        public string ArtUrl => Station.ArtUrl;

        public ImageSource? ArtSource
        {
            get => _artSource;
            private set
            {
                if (_artSource == value) return;

                _artSource = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ArtSource)));
            }
        }

        public void UpdateStation(PandoraStation station)
        {
            string previousName = StationName;
            string previousArtUrl = ArtUrl;

            Station = station;

            if (previousName != StationName)
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StationName)));
            }

            if (previousArtUrl != ArtUrl)
            {
                ArtSource = null;
                _artLoadTask = null;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ArtUrl)));
            }
        }

        public Task LoadArtAsync()
        {
            _artLoadTask ??= LoadArtCoreAsync();
            return _artLoadTask;
        }

        private async Task LoadArtCoreAsync()
        {
            string artUrl = ArtUrl;
            if (string.IsNullOrWhiteSpace(artUrl) || ArtSource != null) return;

            try
            {
                byte[] imageBytes = await ImageClient.GetByteArrayAsync(artUrl);

                using var stream = new MemoryStream(imageBytes);
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.DecodePixelWidth = 45;
                bitmap.StreamSource = stream;
                bitmap.EndInit();
                bitmap.Freeze();

                if (ArtUrl == artUrl)
                {
                    ArtSource = bitmap;
                }
            }
            catch
            {
                // Keep the placeholder visible when artwork is unavailable.
            }
        }
    }
}
