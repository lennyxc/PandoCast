using Hardcodet.Wpf.TaskbarNotification;
using PandoCast.Core;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;

namespace PandoCast.UI
{
    public partial class App : Application
    {
        private TaskbarIcon? _notifyIcon;
        private SettingsWindow? _settingsWindow;

        public static readonly string ConfigDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PandoCast");

        // The shared path for the credentials file
        public static readonly string ConfigPath = Path.Combine(ConfigDirectory, "credentials.dat");
        public static readonly string ReceiverConfigPath = Path.Combine(ConfigDirectory, "receiver.txt");

        // GLOBAL API INSTANCE: Shared across the whole app
        public static PandoraRestApi PandoraApi { get; } = new PandoraRestApi();
        public static PlaybackCoordinator Playback { get; } = new PlaybackCoordinator(PandoraApi);
        public static bool IsLoggedIn { get; set; } = false;
        public static string PreferredReceiverName { get; private set; } = LoadPreferredReceiverNameFromDisk();

        private async void Application_Startup(object sender, StartupEventArgs e)
        {
            Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Playback.PreferredReceiverName = PreferredReceiverName;

            // Initialize Tray Icon
            _notifyIcon = (TaskbarIcon)FindResource("PandoCastTrayIcon");

            // Start the Silent Boot Process
            await AttemptSilentLoginAsync();
        }

        private async System.Threading.Tasks.Task AttemptSilentLoginAsync()
        {
            if (!File.Exists(ConfigPath))
            {
                UpdateTrayStatus("PandoCast - Requires Login");
                return;
            }

            try
            {
                // Decrypt the file
                byte[] encryptedBytes = File.ReadAllBytes(ConfigPath);
                byte[] decryptedBytes = ProtectedData.Unprotect(encryptedBytes, null, DataProtectionScope.CurrentUser);
                string rawData = Encoding.UTF8.GetString(decryptedBytes);
                string[] parts = rawData.Split('|', 2);

                if (parts.Length == 2)
                {
                    string email = parts[0];
                    string password = parts[1];

                    UpdateTrayStatus("PandoCast - Authenticating...");

                    // Attempt the actual background login
                    IsLoggedIn = await PandoraApi.AuthenticateAsync(email, password);

                    if (IsLoggedIn)
                    {
                        _ = PandoraApi.GetStationsCachedAsync();
                        UpdateTrayStatus("PandoCast - Ready to Cast!");
                    }
                    else
                    {
                        UpdateTrayStatus("PandoCast - Login Failed (Check Settings)");
                    }
                }
            }
            catch
            {
                UpdateTrayStatus("PandoCast - Credential Error");
            }
        }

        public void UpdateTrayStatus(string message)
        {
            if (_notifyIcon != null)
            {
                Dispatcher.Invoke(() => _notifyIcon.ToolTipText = message);
            }
        }

        public static void SavePreferredReceiverName(string receiverName)
        {
            SavePreferredReceiverNameAsync(receiverName).GetAwaiter().GetResult();
        }

        public static async Task SavePreferredReceiverNameAsync(string receiverName, bool retargetActivePlayback = true)
        {
            PreferredReceiverName = receiverName.Trim();

            Directory.CreateDirectory(ConfigDirectory);
            File.WriteAllText(ReceiverConfigPath, PreferredReceiverName, Encoding.UTF8);

            await Playback.SetPreferredReceiverNameAsync(PreferredReceiverName, retargetActivePlayback);
        }

        private static string LoadPreferredReceiverNameFromDisk()
        {
            try
            {
                return File.Exists(ReceiverConfigPath)
                    ? File.ReadAllText(ReceiverConfigPath, Encoding.UTF8).Trim()
                    : string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        public void Settings_Click(object sender, RoutedEventArgs e)
        {
            if (_settingsWindow == null || !_settingsWindow.IsLoaded)
            {
                _settingsWindow = new SettingsWindow();
                _settingsWindow.Show();
            }
            else
            {
                // If it's already open, bring it to the front and make it flash
                if (_settingsWindow.WindowState == WindowState.Minimized)
                    _settingsWindow.WindowState = WindowState.Normal;

                _settingsWindow.Activate();
            }
        }

        public void ExitApplication_Click(object sender, RoutedEventArgs e)
        {
            Current.Shutdown();
        }

        private void Application_Exit(object sender, ExitEventArgs e)
        {
            Playback.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _notifyIcon?.Dispose();
        }
    }
}
