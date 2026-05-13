### System Architecture: Pandora Native Proxy Cast (Hybrid Legacy Auth + REST API)

#### 1. High-Level Concept
The application is a "Headless-First" Windows background service. It runs primarily in the Windows System Tray, authenticating with Pandora's legacy JSON v5 API and then using Pandora's modern REST API for station and playlist retrieval where possible. When a user requests playback to a soundbar, the app acts as a local HTTP proxy bridge, converting Pandora's tokenized URLs into a raw audio stream readable by the Chromecast's default media receiver.

#### 2. Core Components & Tech Stack
**Target Runtime:** .NET 10. Native AOT remains a possible future packaging goal, but the current projects build as standard .NET assemblies.

**A. The UI Layer (WPF)**
* **Framework:** WPF (Windows Presentation Foundation).
* **Key Libraries:** `Hardcodet.NotifyIcon.Wpf` handles the SysTray icon, right-click context menus, and global hotkeys. Custom XAML handles the bottom-right borderless Window flyout.
* **Responsibility:** Displaying login forms, showing current station/track metadata (Album Art, Artist, Song), and handling basic playback controls (Play, Pause, Skip, Stop Casting).

**B. The API Layer (Hybrid Pandora Client)**
* **Framework:** `System.Net.Http.HttpClient` + Custom Cryptography Implementation + Cookie-aware REST client
* **References:** Legacy JSON v5 API (`https://6xq.net/pandora-apidoc/json/`) and modern REST API (`https://6xq.net/pandora-apidoc/rest/`).
* **Responsibility:** Handling authentication through the legacy encrypted API, then using the modern REST API for normal station and playback operations.
* **Legacy Partner Authentication:** Calling `auth.partnerLogin` (TLS, unencrypted POST body) to obtain the `partnerAuthToken` and an encrypted `syncTime`.
* **Legacy User Authentication:** Calling `auth.userLogin` (TLS, Blowfish-encrypted POST body) to obtain the active `userAuthToken`.
* **REST Session Setup:** Making a lightweight request to `https://www.pandora.com/` to obtain the `csrftoken` cookie and sending that same value as `X-CsrfToken`.
* **REST Data Retrieval:** Fetching stations from `/api/v1/station/getStations` and tracks from `/api/v1/playlist/getFragment` using the legacy `userAuthToken` in the `X-AuthToken` header.
* **Memory:** Holds the current station list, `userAuthToken`, CSRF token, REST cookies, and track queue in memory. Does not download full audio files.

**Legacy JSON v5 details retained for authentication:**
* **Cryptography:** Encrypting outgoing JSON POST bodies using Blowfish (ECB mode) and converting them to lowercase hexadecimal notation.
* **Time Synchronization:** Decrypting `syncTime` returned during `auth.partnerLogin` and applying the server offset to the encrypted `auth.userLogin` request.

**C. The Local Streaming Proxy Layer**
* **Framework:** `System.Net.HttpListener` (Native C#)
* **Responsibility:** The critical bridge. It spins up a lightweight web server on your local machine (e.g., `http://[YOUR_IP]:8080/stream`).
* **Execution:** It waits for the Chromecast to connect to the `/stream` endpoint, reads the current playing track's Pandora URL, opens a streaming `HttpResponseMessage` to Pandora, and writes the incoming byte stream directly to the outgoing `HttpListenerResponse.OutputStream` pointing to the Chromecast.

**D. The Cast Controller Layer**
* **Framework:** `SharpCaster` (NuGet Package) or similar CastV2 protocol implementation.
* **Responsibility:** Running mDNS scans on your local network to find the soundbar, connecting to the Chromecast using the DefaultMediaReceiver App ID (`CC1AD845`), and sending the JSON payload that tells the soundbar to play your proxy URL.

#### 3. Execution & Data Flow

**Step 1: Boot & Authentication**
1. User launches the app; it hides instantly and pins to the SysTray.
2. User clicks the SysTray icon, opening the login flyout.
3. App POSTs partner credentials (username, password, deviceModel) to `?method=auth.partnerLogin`.
4. App takes the returned encrypted `syncTime`, decrypts it using the partner password via Blowfish (skipping the first 4 bytes of garbage data), and calculates the server time offset.
5. App POSTs user credentials to `?method=auth.userLogin`, encrypting the JSON payload with Blowfish.
6. App saves the returned `userAuthToken` in memory.
7. App obtains a REST `csrftoken` cookie from `https://www.pandora.com/` before the first modern REST call.

**Step 2: Queueing a Station**
1. User selects a station from the UI.
2. App calls `/api/v1/playlist/getFragment`, passing the REST station ID and `X-AuthToken`/`X-CsrfToken` headers.
3. Pandora returns a JSON array of tracks. Each track object contains an `audioURL`, `audioEncoding`, and `trackToken`.
4. The UI updates to show the Album Art URL and Song Name.

**Step 3: The Proxy Handshake (Casting)**
1. User clicks the "Cast to Soundbar" button.
2. The Cast Controller finds the soundbar on the Wi-Fi.
3. The Proxy Layer starts listening on `http://192.168.1.X:8080/cast.m4a`.
4. The Cast Controller sends a command to the soundbar: "Play `http://192.168.1.X:8080/cast.m4a`".
5. The Soundbar pings your Proxy Layer.

**Step 4: Stream Piping & Next Track**
1. The Proxy Layer receives the request from the Soundbar.
2. The Proxy Layer requests the actual track URL from Pandora.
3. The data pipes directly through your PC's RAM (in small chunks) to the Soundbar.
4. When the stream ends, the Cast Controller intercepts the `MediaStatus.Idle` event from the Soundbar, pops the next song from the API queue, and restarts Step 3.

#### 4. Suggested Project Structure
To keep the UI snappy and isolate the complex cryptography and background networking, separate the logic clearly:

```text
PandoCast.slnx
│
├── PandoCast.UI/ (WPF App)
│   ├── App.xaml / App.xaml.cs (Startup & SysTray initialization)
│   ├── SettingsWindow.xaml / SettingsWindow.xaml.cs
│   └── PlayerFlyout.xaml / PlayerFlyout.xaml.cs
│
├── PandoCast.Core/ (Class Library)
│   ├── PandoraRestApi.cs (Handles legacy auth, REST calls, token/cookie lifecycle)
│   ├── PlaybackCoordinator.cs (Maintains playback state and the track queue)
│   ├── HttpStreamProxy.cs (The HttpListener piping logic)
│   ├── ChromecastPlaybackService.cs (mDNS discovery and CastV2 messaging)
│   └── Models/ (C# POCOs representing legacy auth and REST responses)
│
└── PandoCast.PandoraProbe/ (Temporary REST proof-of-concept utility)
```

#### 5. Other Considerations

* **Blowfish Encryption:** The legacy JSON v5 API still requires Blowfish ECB encryption for `auth.userLogin`. This is now limited to authentication; station and playlist data should prefer REST endpoints.
* **Time Synchronization (`syncTime`):** Pandora strictly enforces request expiration for legacy encrypted requests. The app must decrypt the `syncTime` returned during `auth.partnerLogin`, calculate the delta between Pandora's servers and the local system clock, and apply that offset to the encrypted `auth.userLogin` payload.
* **REST CSRF/Cookies:** Modern REST endpoints require a matching `csrftoken` cookie and `X-CsrfToken` header, plus `X-AuthToken` containing the legacy `userAuthToken`. If REST calls begin returning 401/403, refresh the CSRF cookie and retry once.
* **Playlist Fragment Size:** `/api/v1/playlist/getFragment` returns a limited fragment of tracks. The app must actively monitor the queue and request another fragment before playback runs out.
* **Firewall:** Because you are acting as an HTTP server (`HttpListener`), Windows Defender will block incoming connections from your soundbar on the first run. You must ensure your app prompts the user for Local Network firewall permissions during setup.
