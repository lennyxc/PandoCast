using System.Text.Json;
using System.Text.Json.Serialization;

namespace PandoCast.Core.Models
{
    public class PandoraPlaylistResult
    {
        [JsonPropertyName("items")]
        public PandoraTrack[] Items { get; set; } = [];
    }

    public class RestPlaylistResult
    {
        [JsonPropertyName("tracks")]
        public RestPandoraTrack[] Tracks { get; set; } = [];
    }

    public class RestPandoraTrack
    {
        [JsonPropertyName("trackToken")]
        public string TrackToken { get; set; } = string.Empty;

        [JsonPropertyName("songTitle")]
        public string SongTitle { get; set; } = string.Empty;

        [JsonPropertyName("artistName")]
        public string ArtistName { get; set; } = string.Empty;

        [JsonPropertyName("albumTitle")]
        public string AlbumTitle { get; set; } = string.Empty;

        [JsonPropertyName("albumArt")]
        public PandoraArt[] AlbumArt { get; set; } = [];

        [JsonPropertyName("audioURL")]
        public string AudioUrl { get; set; } = string.Empty;

        [JsonPropertyName("audioEncoding")]
        public string AudioEncoding { get; set; } = string.Empty;

        public PandoraTrack ToPandoraTrack()
        {
            Dictionary<string, PandoraAudioUrl> audioUrlMap = [];
            if (!string.IsNullOrWhiteSpace(AudioUrl))
            {
                audioUrlMap["highQuality"] = new PandoraAudioUrl
                {
                    AudioUrl = AudioUrl,
                    Encoding = AudioEncoding,
                    Protocol = "HTTP"
                };
            }

            return new PandoraTrack
            {
                TrackToken = TrackToken,
                SongName = SongTitle,
                ArtistName = ArtistName,
                AlbumName = AlbumTitle,
                AlbumArtUrl = RestPandoraStation.GetPreferredArtUrl(AlbumArt, preferredSize: 500),
                AudioUrlMap = audioUrlMap
            };
        }
    }

    public class PandoraTrack
    {
        [JsonPropertyName("trackToken")]
        public string TrackToken { get; set; } = string.Empty;

        [JsonPropertyName("songName")]
        public string SongName { get; set; } = string.Empty;

        [JsonPropertyName("artistName")]
        public string ArtistName { get; set; } = string.Empty;

        [JsonPropertyName("albumName")]
        public string AlbumName { get; set; } = string.Empty;

        [JsonPropertyName("albumArtUrl")]
        public string AlbumArtUrl { get; set; } = string.Empty;

        [JsonPropertyName("audioUrlMap")]
        public Dictionary<string, PandoraAudioUrl> AudioUrlMap { get; set; } = [];

        [JsonPropertyName("additionalAudioUrl")]
        public JsonElement AdditionalAudioUrl { get; set; }

        public bool IsPlayable => !string.IsNullOrWhiteSpace(TrackToken) && !string.IsNullOrWhiteSpace(GetPlayableAudioUrl());

        public string DisplayTitle => string.IsNullOrWhiteSpace(SongName) ? "Unknown Track" : SongName;

        public string DisplaySubtitle
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(ArtistName) && !string.IsNullOrWhiteSpace(AlbumName))
                {
                    return $"{ArtistName} - {AlbumName}";
                }

                if (!string.IsNullOrWhiteSpace(ArtistName)) return ArtistName;
                if (!string.IsNullOrWhiteSpace(AlbumName)) return AlbumName;

                return "Pandora Station Track";
            }
        }

        public string GetPlayableAudioUrl()
        {
            string additionalUrl = GetAdditionalAudioUrl();
            if (!string.IsNullOrWhiteSpace(additionalUrl)) return additionalUrl;

            foreach (string quality in new[] { "highQuality", "mediumQuality", "lowQuality" })
            {
                if (AudioUrlMap.TryGetValue(quality, out var audioUrl) && !string.IsNullOrWhiteSpace(audioUrl.AudioUrl))
                {
                    return audioUrl.AudioUrl;
                }
            }

            foreach (var audioUrl in AudioUrlMap.Values)
            {
                if (!string.IsNullOrWhiteSpace(audioUrl.AudioUrl)) return audioUrl.AudioUrl;
            }

            return string.Empty;
        }

        public string GetPreferredContentType()
        {
            string audioUrl = GetPlayableAudioUrl();

            if (audioUrl.Contains(".mp3", StringComparison.OrdinalIgnoreCase)) return "audio/mpeg";
            if (audioUrl.Contains(".aac", StringComparison.OrdinalIgnoreCase)) return "audio/aac";
            if (audioUrl.Contains(".m4a", StringComparison.OrdinalIgnoreCase) || audioUrl.Contains(".mp4", StringComparison.OrdinalIgnoreCase)) return "audio/mp4";

            foreach (var audioUrlMapItem in AudioUrlMap.Values)
            {
                if (string.Equals(audioUrlMapItem.AudioUrl, audioUrl, StringComparison.OrdinalIgnoreCase))
                {
                    if (audioUrlMapItem.Encoding.Contains("mp3", StringComparison.OrdinalIgnoreCase)) return "audio/mpeg";
                    if (audioUrlMapItem.Encoding.Contains("aac", StringComparison.OrdinalIgnoreCase)) return "audio/aac";
                }
            }

            return "audio/mp4";
        }

        private string GetAdditionalAudioUrl()
        {
            if (AdditionalAudioUrl.ValueKind == JsonValueKind.String)
            {
                return AdditionalAudioUrl.GetString() ?? string.Empty;
            }

            if (AdditionalAudioUrl.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in AdditionalAudioUrl.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String) continue;

                    string? url = item.GetString();
                    if (!string.IsNullOrWhiteSpace(url)) return url;
                }
            }

            return string.Empty;
        }
    }

    public class PandoraAudioUrl
    {
        [JsonPropertyName("bitrate")]
        public string Bitrate { get; set; } = string.Empty;

        [JsonPropertyName("encoding")]
        public string Encoding { get; set; } = string.Empty;

        [JsonPropertyName("audioUrl")]
        public string AudioUrl { get; set; } = string.Empty;

        [JsonPropertyName("protocol")]
        public string Protocol { get; set; } = string.Empty;
    }
}
