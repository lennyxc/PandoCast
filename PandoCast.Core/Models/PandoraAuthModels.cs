using System.Text.Json.Serialization;

namespace PandoCast.Core.Models
{
    // A generic wrapper because ALL responses from v5 are wrapped in this stat/result format
    public class PandoraJsonResponse<T>
    {
        [JsonPropertyName("stat")]
        public string Stat { get; set; } = string.Empty;

        [JsonPropertyName("result")]
        public T? Result { get; set; }
    }

    public class PartnerLoginResult
    {
        [JsonPropertyName("partnerId")]
        public string PartnerId { get; set; } = string.Empty;

        [JsonPropertyName("partnerAuthToken")]
        public string PartnerAuthToken { get; set; } = string.Empty;

        [JsonPropertyName("syncTime")]
        public string SyncTime { get; set; } = string.Empty;
    }

    public class UserLoginResult
    {
        [JsonPropertyName("userAuthToken")]
        public string UserAuthToken { get; set; } = string.Empty;

        [JsonPropertyName("userId")]
        public string UserId { get; set; } = string.Empty;
    }
}