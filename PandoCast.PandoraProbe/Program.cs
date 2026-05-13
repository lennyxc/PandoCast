using PandoCast.Core;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

const string PandoraWebRoot = "https://www.pandora.com/";
const string PandoraApiRoot = "https://www.pandora.com/api/";

Console.WriteLine("PandoCast Pandora REST probe");
Console.WriteLine("Authenticates with legacy /services/json, then sends cookie/CSRF-aware requests to modern /api endpoints.");
Console.WriteLine();

string email = GetCredential(args, 0, ["PANDOCAST_PANDORA_EMAIL", "PANDORA_EMAIL"], "Pandora email: ");
string password = GetCredential(args, 1, ["PANDOCAST_PANDORA_PASSWORD", "PANDORA_PASSWORD"], "Pandora password: ", secret: true);

var legacyApi = new PandoraRestApi();

Console.WriteLine();
Console.WriteLine("Authenticating with legacy /services/json...");
if (!await legacyApi.AuthenticateAsync(email, password))
{
    Console.Error.WriteLine("Legacy authentication failed.");
    return 1;
}

Console.WriteLine($"Legacy authentication succeeded. userId={legacyApi.UserId}, authToken={Redact(legacyApi.AuthToken)}");

var cookies = new CookieContainer();
using var handler = new HttpClientHandler
{
    CookieContainer = cookies,
    UseCookies = true,
    AllowAutoRedirect = true,
    AutomaticDecompression = DecompressionMethods.All
};

using var restClient = new HttpClient(handler)
{
    BaseAddress = new Uri(PandoraApiRoot),
    Timeout = TimeSpan.FromSeconds(60)
};

ConfigureDefaultHeaders(restClient);

Console.WriteLine("Obtaining csrftoken cookie from www.pandora.com...");
string csrfToken;
try
{
    csrfToken = await RefreshCsrfTokenAsync(restClient, cookies);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Failed to obtain CSRF token: {ex.Message}");
    return 1;
}

Console.WriteLine($"CSRF token acquired: {Redact(csrfToken)}");
PrintHelp();

while (true)
{
    Console.WriteLine();
    Console.Write("endpoint> ");
    string? endpointInput = Console.ReadLine();

    if (string.IsNullOrWhiteSpace(endpointInput))
    {
        continue;
    }

    string command = endpointInput.Trim();
    if (command.Equals("exit", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("quit", StringComparison.OrdinalIgnoreCase))
    {
        return 0;
    }

    if (command.Equals("help", StringComparison.OrdinalIgnoreCase))
    {
        PrintHelp();
        continue;
    }

    if (command.Equals("auth", StringComparison.OrdinalIgnoreCase))
    {
        Console.WriteLine($"userId={legacyApi.UserId}");
        Console.WriteLine($"partnerId={legacyApi.PartnerId}");
        Console.WriteLine($"authToken={Redact(legacyApi.AuthToken)}");
        Console.WriteLine($"csrfToken={Redact(csrfToken)}");
        continue;
    }

    if (command.Equals("csrf", StringComparison.OrdinalIgnoreCase))
    {
        csrfToken = await RefreshCsrfTokenAsync(restClient, cookies);
        Console.WriteLine($"CSRF token refreshed: {Redact(csrfToken)}");
        continue;
    }

    if (command.Equals("legacy-stations", StringComparison.OrdinalIgnoreCase))
    {
        await PrintLegacyStationsAsync(legacyApi);
        continue;
    }

    Uri requestUri;
    try
    {
        requestUri = BuildPandoraApiUri(command);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Invalid endpoint: {ex.Message}");
        continue;
    }

    HttpMethod method = ReadHttpMethod();
    string body = ReadJsonBody(method);

    try
    {
        using var request = new HttpRequestMessage(method, requestUri);
        AddPandoraRestHeaders(request, legacyApi.AuthToken, csrfToken);

        if (!string.IsNullOrWhiteSpace(body))
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        Console.WriteLine($"Sending {method.Method} {requestUri}");
        using HttpResponseMessage response = await restClient.SendAsync(request);

        string? updatedCsrfToken = FindCookie(cookies, "csrftoken");
        if (!string.IsNullOrWhiteSpace(updatedCsrfToken) && updatedCsrfToken != csrfToken)
        {
            csrfToken = updatedCsrfToken;
            Console.WriteLine($"CSRF token updated from response cookies: {Redact(csrfToken)}");
        }

        await PrintResponseAsync(response);

        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
        {
            Console.WriteLine("Hint: run `csrf` to refresh the CSRF cookie, then retry. Some endpoints may also require browser-specific request arguments.");
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Request failed: {ex.Message}");
    }
}

static void ConfigureDefaultHeaders(HttpClient client)
{
    client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36 PandoCastProbe/1.0");
    client.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/plain, */*");
    client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
    client.DefaultRequestHeaders.Referrer = new Uri(PandoraWebRoot);
    client.DefaultRequestHeaders.TryAddWithoutValidation("Origin", PandoraWebRoot.TrimEnd('/'));
    client.DefaultRequestHeaders.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
}

static void AddPandoraRestHeaders(HttpRequestMessage request, string authToken, string csrfToken)
{
    request.Headers.TryAddWithoutValidation("X-AuthToken", authToken);
    request.Headers.TryAddWithoutValidation("X-CsrfToken", csrfToken);
    request.Headers.Referrer = new Uri(PandoraWebRoot);
}

static async Task<string> RefreshCsrfTokenAsync(HttpClient client, CookieContainer cookies)
{
    await SendCsrfBootstrapRequestAsync(client, HttpMethod.Head);
    string? token = FindCookie(cookies, "csrftoken");
    if (!string.IsNullOrWhiteSpace(token))
    {
        return token;
    }

    await SendCsrfBootstrapRequestAsync(client, HttpMethod.Get);
    token = FindCookie(cookies, "csrftoken");
    if (!string.IsNullOrWhiteSpace(token))
    {
        return token;
    }

    throw new InvalidOperationException("No csrftoken cookie was set by www.pandora.com.");
}

static async Task SendCsrfBootstrapRequestAsync(HttpClient client, HttpMethod method)
{
    using var request = new HttpRequestMessage(method, PandoraWebRoot);
    request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };

    using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
    if (response.StatusCode == HttpStatusCode.MethodNotAllowed && method == HttpMethod.Head)
    {
        return;
    }

    if ((int)response.StatusCode >= 500)
    {
        throw new HttpRequestException($"{method.Method} {PandoraWebRoot} returned {(int)response.StatusCode} {response.ReasonPhrase}");
    }
}

static string? FindCookie(CookieContainer cookies, string name)
{
    Uri[] cookieUris =
    [
        new(PandoraWebRoot),
        new(PandoraApiRoot),
        new("https://pandora.com/")
    ];

    foreach (Uri uri in cookieUris)
    {
        foreach (Cookie cookie in cookies.GetCookies(uri))
        {
            if (cookie.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return cookie.Value;
            }
        }
    }

    return null;
}

static Uri BuildPandoraApiUri(string endpoint)
{
    string trimmed = endpoint.Trim();

    if (Uri.TryCreate(trimmed, UriKind.Absolute, out Uri? absoluteUri))
    {
        if (!absoluteUri.Host.EndsWith("pandora.com", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Only pandora.com URLs are allowed.");
        }

        return absoluteUri;
    }

    trimmed = trimmed.TrimStart('/');
    if (trimmed.StartsWith("api/", StringComparison.OrdinalIgnoreCase))
    {
        trimmed = trimmed[4..];
    }

    return new Uri(new Uri(PandoraApiRoot), trimmed);
}

static HttpMethod ReadHttpMethod()
{
    Console.Write("method [POST]> ");
    string? input = Console.ReadLine();
    string method = string.IsNullOrWhiteSpace(input) ? "POST" : input.Trim().ToUpperInvariant();

    return method switch
    {
        "GET" => HttpMethod.Get,
        "POST" => HttpMethod.Post,
        "PUT" => HttpMethod.Put,
        "PATCH" => HttpMethod.Patch,
        "DELETE" => HttpMethod.Delete,
        "HEAD" => HttpMethod.Head,
        _ => new HttpMethod(method)
    };
}

static string ReadJsonBody(HttpMethod method)
{
    if (method == HttpMethod.Get || method == HttpMethod.Head)
    {
        return string.Empty;
    }

    Console.WriteLine("JSON body. Submit a blank line for `{}`. Prefix with @ to read a JSON file.");
    Console.Write("json> ");
    string? firstLine = Console.ReadLine();

    if (string.IsNullOrWhiteSpace(firstLine))
    {
        return "{}";
    }

    if (firstLine.TrimStart().StartsWith('@'))
    {
        string path = firstLine.Trim()[1..].Trim('"');
        return File.ReadAllText(path);
    }

    if (IsCompleteJson(firstLine))
    {
        return firstLine;
    }

    var lines = new List<string> { firstLine };
    while (true)
    {
        Console.Write("json> ");
        string? line = Console.ReadLine();
        if (string.IsNullOrWhiteSpace(line))
        {
            break;
        }

        lines.Add(line);
    }

    string body = string.Join(Environment.NewLine, lines);

    try
    {
        using JsonDocument _ = JsonDocument.Parse(body);
    }
    catch (JsonException ex)
    {
        Console.WriteLine($"Warning: JSON did not parse locally: {ex.Message}");
    }

    return body;
}

static bool IsCompleteJson(string value)
{
    try
    {
        using JsonDocument _ = JsonDocument.Parse(value);
        return true;
    }
    catch (JsonException)
    {
        return false;
    }
}

static async Task PrintResponseAsync(HttpResponseMessage response)
{
    Console.WriteLine($"Status: {(int)response.StatusCode} {response.ReasonPhrase}");
    Console.WriteLine($"Content-Type: {response.Content.Headers.ContentType}");

    foreach (var header in response.Headers)
    {
        if (header.Key.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase))
        {
            continue;
        }

        if (header.Key.StartsWith("X-", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine($"{header.Key}: {string.Join(", ", header.Value)}");
        }
    }

    string content = await response.Content.ReadAsStringAsync();
    if (string.IsNullOrWhiteSpace(content))
    {
        Console.WriteLine("<empty response body>");
        return;
    }

    Console.WriteLine();
    Console.WriteLine(PrettyJsonOrRaw(content));
}

static string PrettyJsonOrRaw(string content)
{
    try
    {
        using JsonDocument document = JsonDocument.Parse(content);
        return JsonSerializer.Serialize(document.RootElement, new JsonSerializerOptions { WriteIndented = true });
    }
    catch (JsonException)
    {
        return content;
    }
}

static async Task PrintLegacyStationsAsync(PandoraRestApi legacyApi)
{
    var stations = await legacyApi.GetStationsAsync();
    Console.WriteLine($"Legacy station count: {stations.Length}");

    foreach (var station in stations.Take(50))
    {
        Console.WriteLine($"{station.StationToken}\t{station.StationName}");
    }

    if (stations.Length > 50)
    {
        Console.WriteLine($"...{stations.Length - 50} more");
    }
}

static string GetCredential(string[] args, int index, string[] environmentNames, string prompt, bool secret = false)
{
    if (args.Length > index && !string.IsNullOrWhiteSpace(args[index]))
    {
        return args[index];
    }

    foreach (string environmentName in environmentNames)
    {
        string? value = Environment.GetEnvironmentVariable(environmentName);
        if (!string.IsNullOrWhiteSpace(value))
        {
            return value;
        }
    }

    Console.Write(prompt);
    return secret ? ReadSecret() : Console.ReadLine() ?? string.Empty;
}

static string ReadSecret()
{
    var value = new StringBuilder();

    while (true)
    {
        ConsoleKeyInfo key = Console.ReadKey(intercept: true);
        if (key.Key == ConsoleKey.Enter)
        {
            Console.WriteLine();
            return value.ToString();
        }

        if (key.Key == ConsoleKey.Backspace)
        {
            if (value.Length > 0)
            {
                value.Length--;
            }

            continue;
        }

        if (!char.IsControl(key.KeyChar))
        {
            value.Append(key.KeyChar);
        }
    }
}

static string Redact(string value)
{
    if (string.IsNullOrWhiteSpace(value))
    {
        return "<empty>";
    }

    if (value.Length <= 10)
    {
        return "<redacted>";
    }

    return $"{value[..4]}...{value[^4..]}";
}

static void PrintHelp()
{
    Console.WriteLine();
    Console.WriteLine("Commands:");
    Console.WriteLine("  help             Show this help.");
    Console.WriteLine("  auth             Show redacted auth and CSRF state.");
    Console.WriteLine("  csrf             Refresh the csrftoken cookie.");
    Console.WriteLine("  legacy-stations  Print station tokens from the legacy API.");
    Console.WriteLine("  exit             Quit.");
    Console.WriteLine();
    Console.WriteLine("Endpoint input examples:");
    Console.WriteLine("  v1/station/getStations");
    Console.WriteLine("  /api/v1/station/getStations");
    Console.WriteLine("  https://www.pandora.com/api/v1/station/getStations");
    Console.WriteLine();
    Console.WriteLine("Default request behavior: POST with X-AuthToken, X-CsrfToken, Origin, Referer, cookies, and application/json body.");
}
