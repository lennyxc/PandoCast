using System.Text.Json.Serialization;

namespace PandoCast.Core.Models
{
    public class StationListResult
    {
        [JsonPropertyName("stations")]
        public PandoraStation[] Stations { get; set; } = [];

        [JsonPropertyName("checksum")]
        public string Checksum { get; set; } = string.Empty;
    }

    public class StationListChecksumResult
    {
        [JsonPropertyName("checksum")]
        public string Checksum { get; set; } = string.Empty;
    }

    public class RestStationListResult
    {
        [JsonPropertyName("totalStations")]
        public int TotalStations { get; set; }

        [JsonPropertyName("index")]
        public int Index { get; set; }

        [JsonPropertyName("stations")]
        public RestPandoraStation[] Stations { get; set; } = [];
    }

    public class RestPandoraStation
    {
        [JsonPropertyName("stationId")]
        public string StationId { get; set; } = string.Empty;

        [JsonPropertyName("pandoraId")]
        public string PandoraId { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("isShuffle")]
        public bool IsShuffle { get; set; }

        [JsonPropertyName("art")]
        public PandoraArt[] Art { get; set; } = [];

        public PandoraStation ToPandoraStation()
        {
            return new PandoraStation
            {
                StationToken = string.IsNullOrWhiteSpace(StationId) ? PandoraId : StationId,
                StationName = Name,
                IsQuickMix = IsShuffle,
                ArtUrl = GetPreferredArtUrl(Art, preferredSize: 130)
            };
        }

        internal static string GetPreferredArtUrl(PandoraArt[] art, int preferredSize)
        {
            if (art.Length == 0) return string.Empty;

            return art
                .OrderBy(item => Math.Abs(item.Size - preferredSize))
                .ThenBy(item => item.Size)
                .FirstOrDefault(item => !string.IsNullOrWhiteSpace(item.Url))
                ?.Url ?? string.Empty;
        }
    }

    public class PandoraArt
    {
        [JsonPropertyName("url")]
        public string Url { get; set; } = string.Empty;

        [JsonPropertyName("size")]
        public int Size { get; set; }
    }

    public class PandoraStation
    {
        [JsonPropertyName("stationToken")]
        public string StationToken { get; set; } = string.Empty;

        [JsonPropertyName("stationName")]
        public string StationName { get; set; } = string.Empty;

        [JsonPropertyName("isQuickMix")]
        public bool IsQuickMix { get; set; }

        [JsonPropertyName("artUrl")]
        public string ArtUrl { get; set; } = string.Empty;
    }

    public class PandoraStationModesResult
    {
        [JsonPropertyName("interactiveRadioAvailable")]
        public bool InteractiveRadioAvailable { get; set; }

        [JsonPropertyName("currentModeId")]
        public int CurrentModeId { get; set; }

        [JsonPropertyName("availableModesHeader")]
        public string AvailableModesHeader { get; set; } = string.Empty;

        [JsonPropertyName("takeoverModesHeader")]
        public string TakeoverModesHeader { get; set; } = string.Empty;

        [JsonPropertyName("availableModes")]
        public PandoraStationMode[] AvailableModes { get; set; } = [];

        [JsonPropertyName("hasTakeoverModes")]
        public bool HasTakeoverModes { get; set; }

        public PandoraStationMode? CurrentMode => AvailableModes.FirstOrDefault(mode => mode.ModeId == CurrentModeId);

        public PandoraStationMode[] SelectableModes => AvailableModes.Where(mode => mode.IsModeAvailable).ToArray();
    }

    public class PandoraStationMode
    {
        [JsonPropertyName("modeId")]
        public int ModeId { get; set; }

        [JsonPropertyName("modePandoraId")]
        public string ModePandoraId { get; set; } = string.Empty;

        [JsonPropertyName("modeName")]
        public string ModeName { get; set; } = string.Empty;

        [JsonPropertyName("modeButtonText")]
        public string ModeButtonText { get; set; } = string.Empty;

        [JsonPropertyName("modeDescription")]
        public string ModeDescription { get; set; } = string.Empty;

        [JsonPropertyName("isPremiumOnly")]
        public bool IsPremiumOnly { get; set; }

        [JsonPropertyName("isModeAvailable")]
        public bool IsModeAvailable { get; set; }

        [JsonPropertyName("isTakeoverMode")]
        public bool IsTakeoverMode { get; set; }

        [JsonPropertyName("isAlgorithmicMode")]
        public bool IsAlgorithmicMode { get; set; }

        [JsonPropertyName("isInitialMode")]
        public bool IsInitialMode { get; set; }

        public string DisplayName => string.IsNullOrWhiteSpace(ModeButtonText) ? ModeName : ModeButtonText;

        public string DisplayDescription
        {
            get
            {
                if (IsPremiumOnly && !string.IsNullOrWhiteSpace(ModeDescription)) return $"{ModeDescription} Premium only.";
                if (IsPremiumOnly) return "Premium only.";

                return ModeDescription;
            }
        }
    }
}
