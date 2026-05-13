using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Paddings;
using Org.BouncyCastle.Crypto.Parameters;
using PandoCast.Core.Models;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PandoCast.Core
{
    // API CLIENT
    public class PandoraRestApi
    {
        private readonly HttpClient _httpClient;
        private readonly CookieContainer _restCookies = new();
        private readonly HttpClient _restClient;
        private readonly SemaphoreSlim _csrfLock = new(1, 1);
        private readonly SemaphoreSlim _stationCacheLock = new(1, 1);
        private PandoraStation[] _cachedStations = [];
        private string _stationListChecksum = string.Empty;
        private string _csrfToken = string.Empty;

        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

        // Pandora Static Encryption Keys
        private const string ReqKey = "6#26FRL$ZWD";
        private const string ResKey = "R=U!LH$O2B#";
        private const string PandoraWebRoot = "https://www.pandora.com/";

        private long _timeOffset = 0;
        public string AuthToken { get; private set; } = string.Empty;
        public string PartnerId { get; private set; } = string.Empty;
        public string PartnerAuthToken { get; private set; } = string.Empty;
        public string UserId { get; private set; } = string.Empty;

        public PandoraRestApi()
        {
            _httpClient = new HttpClient
            {
                BaseAddress = new Uri("https://android-tuner.pandora.com/")
            };

            _httpClient.DefaultRequestHeaders.Add("User-Agent", "Pandora/1811.1 Android/7.1.1");

            var restHandler = new HttpClientHandler
            {
                CookieContainer = _restCookies,
                UseCookies = true,
                AllowAutoRedirect = true,
                AutomaticDecompression = DecompressionMethods.All
            };

            _restClient = new HttpClient(restHandler)
            {
                BaseAddress = new Uri("https://www.pandora.com/api/"),
                Timeout = TimeSpan.FromSeconds(60)
            };

            _restClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36 PandoCast/1.0");
            _restClient.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/plain, */*");
            _restClient.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
            _restClient.DefaultRequestHeaders.Referrer = new Uri(PandoraWebRoot);
            _restClient.DefaultRequestHeaders.TryAddWithoutValidation("Origin", PandoraWebRoot.TrimEnd('/'));
            _restClient.DefaultRequestHeaders.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
        }

        public PandoraStation[] GetCachedStations()
        {
            return _cachedStations;
        }

        public async Task<PandoraStation[]> GetStationsCachedAsync()
        {
            if (string.IsNullOrEmpty(AuthToken)) return [];

            await _stationCacheLock.WaitAsync();
            try
            {
                if (_cachedStations.Length == 0 || string.IsNullOrEmpty(_stationListChecksum))
                {
                    return await RefreshStationCacheAsync();
                }

                string currentChecksum = await GetStationListChecksumCoreAsync();

                if (string.IsNullOrEmpty(currentChecksum))
                {
                    return await RefreshStationCacheAsync();
                }

                if (currentChecksum == _stationListChecksum)
                {
                    return _cachedStations;
                }

                return await RefreshStationCacheAsync();
            }
            finally
            {
                _stationCacheLock.Release();
            }
        }

        public async Task<string> GetStationListChecksumAsync()
        {
            if (string.IsNullOrEmpty(AuthToken)) return string.Empty;

            return await GetStationListChecksumCoreAsync();
        }

        public async Task<PandoraStation[]> GetStationsAsync()
        {
            if (string.IsNullOrEmpty(AuthToken)) return [];

            StationListResult? result = await GetStationListResultAsync();

            if (result == null) return [];

            await _stationCacheLock.WaitAsync();
            try
            {
                UpdateStationCache(result);
            }
            finally
            {
                _stationCacheLock.Release();
            }

            return result.Stations;
        }

        public async Task<PandoraTrack[]> GetPlaylistAsync(
            string stationToken,
            bool stationIsStarting = false,
            string fragmentRequestReason = "Normal",
            string? lastPlayedTrackToken = null)
        {
            if (string.IsNullOrEmpty(AuthToken) || string.IsNullOrWhiteSpace(stationToken)) return [];

            try
            {
                var payload = new
                {
                    stationId = stationToken,
                    isStationStart = stationIsStarting,
                    fragmentRequestReason,
                    audioFormat = "aacplus",
                    startingAtTrackId = (string?)null,
                    lastPlayedTrackToken,
                    onDemandArtistMessageArtistUidHex = (string?)null,
                    onDemandArtistMessageIdHex = (string?)null
                };

                RestPlaylistResult? data = await InvokeRestApiAsync<RestPlaylistResult>("v1/playlist/getFragment", payload);
                return data?.Tracks.Select(track => track.ToPandoraTrack()).ToArray() ?? [];
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[PANDORA REST PLAYLIST EXCEPTION] {ex.Message}");
                return [];
            }
        }

        public async Task<PandoraStationModesResult?> GetAvailableStationModesAsync(string stationToken)
        {
            if (string.IsNullOrEmpty(AuthToken) || string.IsNullOrWhiteSpace(stationToken)) return null;

            try
            {
                var payload = new
                {
                    stationId = stationToken
                };

                return await InvokeRestApiAsync<PandoraStationModesResult>("v1/interactiveradio/getAvailableModesSimple", payload);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[PANDORA REST STATION MODES EXCEPTION] {ex.Message}");
                return null;
            }
        }

        public async Task<PandoraStationModesResult?> SetStationModeAsync(string stationToken, int modeId, int previousModeId)
        {
            if (string.IsNullOrEmpty(AuthToken) || string.IsNullOrWhiteSpace(stationToken)) return null;

            try
            {
                var payload = new
                {
                    stationId = stationToken,
                    modeId,
                    previousModeId
                };

                return await InvokeRestApiAsync<PandoraStationModesResult>("v1/interactiveradio/setAndGetAvailableModes", payload);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[PANDORA REST SET STATION MODE EXCEPTION] {ex.Message}");
                return null;
            }
        }

        private async Task<PandoraStation[]> RefreshStationCacheAsync()
        {
            StationListResult? result = await GetStationListResultAsync();

            if (result == null)
            {
                return _cachedStations;
            }

            UpdateStationCache(result);
            return _cachedStations;
        }

        private void UpdateStationCache(StationListResult result)
        {
            _cachedStations = result.Stations;
            _stationListChecksum = result.Checksum;
        }

        private void ClearStationCache()
        {
            _cachedStations = [];
            _stationListChecksum = string.Empty;
        }

        private async Task<StationListResult?> GetStationListResultAsync()
        {
            if (string.IsNullOrEmpty(AuthToken)) return null;

            try
            {
                List<PandoraStation> stations = [];
                int startIndex = 0;
                const int pageSize = 250;
                int totalStations = int.MaxValue;

                while (stations.Count < totalStations)
                {
                    var payload = new
                    {
                        pageSize,
                        startIndex
                    };

                    RestStationListResult? page = await InvokeRestApiAsync<RestStationListResult>("v1/station/getStations", payload);
                    if (page == null) return null;

                    totalStations = page.TotalStations <= 0 ? page.Stations.Length : page.TotalStations;
                    if (page.Stations.Length == 0) break;

                    stations.AddRange(page.Stations.Select(station => station.ToPandoraStation()));
                    startIndex += page.Stations.Length;
                }

                PandoraStation[] stationArray = stations.ToArray();
                return new StationListResult
                {
                    Stations = stationArray,
                    Checksum = ComputeStationChecksum(stationArray)
                };
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[PANDORA REST STATION EXCEPTION] {ex.Message}");
                return null;
            }
        }

        private async Task<string> GetStationListChecksumCoreAsync()
        {
            try
            {
                StationListResult? result = await GetStationListResultAsync();
                return result?.Checksum ?? string.Empty;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[PANDORA REST STATION CHECKSUM EXCEPTION] {ex.Message}");
                return string.Empty;
            }
        }

        private async Task<T?> InvokeRestApiAsync<T>(string endpoint, object payload)
        {
            if (string.IsNullOrWhiteSpace(AuthToken)) return default;

            await EnsureCsrfTokenAsync();

            using HttpResponseMessage response = await SendRestRequestAsync(endpoint, payload);
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
            {
                _csrfToken = string.Empty;
                await EnsureCsrfTokenAsync();

                using HttpResponseMessage retryResponse = await SendRestRequestAsync(endpoint, payload);
                return await ReadRestResponseAsync<T>(endpoint, retryResponse);
            }

            return await ReadRestResponseAsync<T>(endpoint, response);
        }

        private async Task<HttpResponseMessage> SendRestRequestAsync(string endpoint, object payload)
        {
            string json = JsonSerializer.Serialize(payload, JsonOptions);
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };

            request.Headers.TryAddWithoutValidation("X-AuthToken", AuthToken);
            request.Headers.TryAddWithoutValidation("X-CsrfToken", _csrfToken);
            request.Headers.Referrer = new Uri(PandoraWebRoot);

            return await _restClient.SendAsync(request);
        }

        private static async Task<T?> ReadRestResponseAsync<T>(string endpoint, HttpResponseMessage response)
        {
            string rawResponse = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                System.Diagnostics.Debug.WriteLine($"[PANDORA REST HTTP ERROR] {endpoint}: {(int)response.StatusCode} {response.ReasonPhrase}");
                System.Diagnostics.Debug.WriteLine(rawResponse);
                return default;
            }

            if (string.IsNullOrWhiteSpace(rawResponse)) return default;

            try
            {
                return JsonSerializer.Deserialize<T>(rawResponse, JsonOptions);
            }
            catch (JsonException ex)
            {
                System.Diagnostics.Debug.WriteLine($"[PANDORA REST JSON ERROR] {endpoint}: {ex.Message}");
                System.Diagnostics.Debug.WriteLine(rawResponse);
                return default;
            }
        }

        private async Task EnsureCsrfTokenAsync()
        {
            if (!string.IsNullOrWhiteSpace(_csrfToken)) return;

            await _csrfLock.WaitAsync();
            try
            {
                if (!string.IsNullOrWhiteSpace(_csrfToken)) return;

                await BootstrapCsrfCookieAsync(HttpMethod.Head);
                _csrfToken = FindCookie("csrftoken") ?? string.Empty;

                if (string.IsNullOrWhiteSpace(_csrfToken))
                {
                    await BootstrapCsrfCookieAsync(HttpMethod.Get);
                    _csrfToken = FindCookie("csrftoken") ?? string.Empty;
                }

                if (string.IsNullOrWhiteSpace(_csrfToken))
                {
                    throw new InvalidOperationException("Pandora did not provide a csrftoken cookie.");
                }
            }
            finally
            {
                _csrfLock.Release();
            }
        }

        private async Task BootstrapCsrfCookieAsync(HttpMethod method)
        {
            using var request = new HttpRequestMessage(method, PandoraWebRoot);
            request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };

            using HttpResponseMessage response = await _restClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            if (response.StatusCode == HttpStatusCode.MethodNotAllowed && method == HttpMethod.Head)
            {
                return;
            }

            if ((int)response.StatusCode >= 500)
            {
                throw new HttpRequestException($"{method.Method} {PandoraWebRoot} returned {(int)response.StatusCode} {response.ReasonPhrase}");
            }
        }

        private string? FindCookie(string name)
        {
            Uri[] cookieUris =
            [
                new(PandoraWebRoot),
                new("https://www.pandora.com/api/"),
                new("https://pandora.com/")
            ];

            foreach (Uri uri in cookieUris)
            {
                foreach (Cookie cookie in _restCookies.GetCookies(uri))
                {
                    if (cookie.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    {
                        return cookie.Value;
                    }
                }
            }

            return null;
        }

        private static string ComputeStationChecksum(PandoraStation[] stations)
        {
            string stationFingerprint = string.Join("\n", stations.Select(station => $"{station.StationToken}\t{station.StationName}\t{station.ArtUrl}"));
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(stationFingerprint));
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        public async Task<bool> AuthenticateAsync(string email, string password)
        {
            try
            {
                // STEP 1: PARTNER LOGIN (Plaintext JSON)
                var partnerPayload = new
                {
                    username = "android",
                    password = "AC7IBG09A3DTSYM4R41UJWL07VLN8JI7",
                    deviceModel = "android-generic",
                    version = "5"
                };

                var pRes = await _httpClient.PostAsJsonAsync("services/json/?v=5&method=auth.partnerLogin", partnerPayload);
                if (!pRes.IsSuccessStatusCode) return false;

                var pData = await pRes.Content.ReadFromJsonAsync<PandoraJsonResponse<PartnerLoginResult>>();
                if (pData?.Stat != "ok" || pData.Result == null) return false;

                PartnerId = pData.Result.PartnerId;
                PartnerAuthToken = pData.Result.PartnerAuthToken;

                // We must decrypt the server time and save the offset to prevent replay-attack errors
                long serverTime = DecryptSyncTime(pData.Result.SyncTime);
                _timeOffset = serverTime - DateTimeOffset.UtcNow.ToUnixTimeSeconds();

                // STEP 2: USER LOGIN (Blowfish Encrypted)
                var userPayload = new
                {
                    loginType = "user",
                    username = email,
                    password = password,
                    partnerAuthToken = PartnerAuthToken,
                    syncTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + _timeOffset
                };

                // Convert to JSON and Encrypt it
                string rawJson = JsonSerializer.Serialize(userPayload);
                string encryptedBody = EncryptPayload(rawJson);

                // Send it to the specific auth token URL
                string url = $"services/json/?v=5&method=auth.userLogin&auth_token={Uri.EscapeDataString(PartnerAuthToken)}&partner_id={Uri.EscapeDataString(PartnerId)}";

                var content = new StringContent(encryptedBody, Encoding.UTF8, "text/plain");
                var uRes = await _httpClient.PostAsync(url, content);

                if (!uRes.IsSuccessStatusCode) return false;

                // The User Login RESPONSE is always plaintext JSON
                var uData = await uRes.Content.ReadFromJsonAsync<PandoraJsonResponse<UserLoginResult>>();
                if (uData?.Stat != "ok" || uData.Result == null) return false;

                AuthToken = uData.Result.UserAuthToken;
                UserId = uData.Result.UserId;
                ClearStationCache();
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[PANDORA JSON API ERROR] {ex.Message}");
                return false;
            }
        }

        // BLOWFISH HELPER METHODS

        private long DecryptSyncTime(string hexSyncTime)
        {
            var engine = new BlowfishEngine();
            var cipherMode = new EcbBlockCipher(engine);
            var cipher = new BufferedBlockCipher(cipherMode);

            cipher.Init(false, new KeyParameter(Encoding.ASCII.GetBytes(ResKey)));

            byte[] input = Convert.FromHexString(hexSyncTime);
            byte[] output = new byte[cipher.GetOutputSize(input.Length)];
            int len = cipher.ProcessBytes(input, 0, input.Length, output, 0);
            cipher.DoFinal(output, len);

            // Pandora adds 4 bytes of garbage data to the front.
            string rawTimeStr = Encoding.ASCII.GetString(output, 4, output.Length - 4);

            // EXTRACT THE NUMBERS ONLY: Stop reading the moment we hit invisible block padding
            var cleanTime = new StringBuilder();
            foreach (char c in rawTimeStr)
            {
                if (char.IsDigit(c))
                    cleanTime.Append(c);
                else
                    break;
            }

            return long.Parse(cleanTime.ToString());
        }

        private string EncryptPayload(string json)
        {
            var engine = new BlowfishEngine();

            // Explicitly wrap the engine in ECB mode for BouncyCastle v2+
            var cipherMode = new EcbBlockCipher(engine);
            var cipher = new PaddedBufferedBlockCipher(cipherMode); // Defaults to PKCS7

            cipher.Init(true, new KeyParameter(Encoding.ASCII.GetBytes(ReqKey)));

            byte[] input = Encoding.UTF8.GetBytes(json);
            byte[] output = new byte[cipher.GetOutputSize(input.Length)];
            int len = cipher.ProcessBytes(input, 0, input.Length, output, 0);
            cipher.DoFinal(output, len);

            // Convert to Hex and enforce lowercase as required by Pandora
            return Convert.ToHexString(output).ToLowerInvariant();
        }
    }
}
