using PandoCast.Core;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;

namespace PandoCast.UI
{
    public partial class SettingsWindow : Window
    {
        private readonly ChromecastReceiverInfo _autoReceiver = new();
        private bool _hasLoaded;
        private bool _isUpdatingReceiverList;

        public SettingsWindow()
        {
            InitializeComponent();
            LoadCredentialsIntoUI();
            LoadReceiverIntoUI();
        }

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            if (_hasLoaded) return;

            _hasLoaded = true;
            await RefreshReceiversAsync();
        }

        private async void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            string email = EmailTextBox.Text;
            string password = PasswordBox.Password;

            if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
            {
                MessageBox.Show("Please enter both your email and password.", "Missing Info", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Disable button while network request happens
            SaveButton.IsEnabled = false;
            SaveButton.Content = "Logging in...";

            // 1. Try to log in using the GLOBAL API instance we made in App.xaml.cs
            bool success = await App.PandoraApi.AuthenticateAsync(email, password);

            if (success)
            {
                // 1. Update the global logged-in flag
                App.IsLoggedIn = true;

                // 2. Grab the running instance of the App and update the tray text
                _ = App.PandoraApi.GetStationsCachedAsync();
                ((App)Application.Current).UpdateTrayStatus("PandoCast - Ready to Cast!");

                SaveCredentialsToDisk(email, password);
                await SaveSelectedReceiverAsync(retargetActivePlayback: false);
                MessageBox.Show("Login Successful! Ready to cast.", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                this.Close();
            }
            else
            {
                MessageBox.Show("Login failed. Please check your email and password.", "Authentication Error", MessageBoxButton.OK, MessageBoxImage.Error);
                SaveButton.IsEnabled = true;
                SaveButton.Content = "Save & Login";
            }
        }

        private void SaveCredentialsToDisk(string email, string password)
        {
            string rawData = $"{email}|{password}";
            byte[] rawBytes = Encoding.UTF8.GetBytes(rawData);
            byte[] encryptedBytes = ProtectedData.Protect(rawBytes, null, DataProtectionScope.CurrentUser);

            Directory.CreateDirectory(Path.GetDirectoryName(App.ConfigPath)!);
            File.WriteAllBytes(App.ConfigPath, encryptedBytes);
        }

        private void LoadCredentialsIntoUI()
        {
            if (!File.Exists(App.ConfigPath)) return;

            try
            {
                byte[] encryptedBytes = File.ReadAllBytes(App.ConfigPath);
                byte[] decryptedBytes = ProtectedData.Unprotect(encryptedBytes, null, DataProtectionScope.CurrentUser);
                string rawData = Encoding.UTF8.GetString(decryptedBytes);
                string[] parts = rawData.Split('|', 2);

                if (parts.Length == 2)
                {
                    EmailTextBox.Text = parts[0];
                    PasswordBox.Password = parts[1]; // Note: PasswordBox isn't fully bindable, so we set it directly
                }
            }
            catch { /* Silently fail UI population if decryption breaks */ }
        }

        private void LoadReceiverIntoUI()
        {
            ReceiverComboBox.ItemsSource = new[] { _autoReceiver };
            ReceiverComboBox.SelectedIndex = 0;

            ReceiverStatusTextBlock.Text = string.IsNullOrWhiteSpace(App.PreferredReceiverName)
                ? "Auto select is enabled. Refresh to choose a specific receiver."
                : $"Saved receiver: {App.PreferredReceiverName}. Refresh to verify availability.";
        }

        private async void RefreshReceiversButton_Click(object sender, RoutedEventArgs e)
        {
            await RefreshReceiversAsync();
        }

        private async Task RefreshReceiversAsync()
        {
            string savedReceiverName = App.PreferredReceiverName;
            RefreshReceiversButton.IsEnabled = false;
            SaveReceiverButton.IsEnabled = false;
            ReceiverStatusTextBlock.Text = "Searching for Chromecast receivers...";

            try
            {
                ChromecastReceiverInfo[] discoveredReceivers = await App.Playback.DiscoverReceiversAsync();
                var receiverItems = new List<ChromecastReceiverInfo> { _autoReceiver };
                receiverItems.AddRange(discoveredReceivers.OrderBy(receiver => receiver.Name));

                _isUpdatingReceiverList = true;
                ReceiverComboBox.ItemsSource = receiverItems;

                ChromecastReceiverInfo? selectedReceiver = receiverItems.FirstOrDefault(receiver =>
                    string.Equals(receiver.Name, savedReceiverName, StringComparison.OrdinalIgnoreCase));

                ReceiverComboBox.SelectedItem = selectedReceiver ?? _autoReceiver;
                _isUpdatingReceiverList = false;

                if (discoveredReceivers.Length == 0)
                {
                    ReceiverStatusTextBlock.Text = "No Chromecast receivers found. Confirm the device is on the same network.";
                }
                else if (!string.IsNullOrWhiteSpace(savedReceiverName) && selectedReceiver == null)
                {
                    ReceiverStatusTextBlock.Text = $"Found {discoveredReceivers.Length} receiver(s), but saved receiver '{savedReceiverName}' was not found.";
                }
                else if (selectedReceiver == null || string.IsNullOrWhiteSpace(selectedReceiver.Name))
                {
                    ReceiverStatusTextBlock.Text = $"Found {discoveredReceivers.Length} receiver(s). Auto select will use the first available receiver.";
                }
                else
                {
                    ReceiverStatusTextBlock.Text = $"Selected receiver is available: {selectedReceiver.DisplayName}.";
                }
            }
            catch (Exception ex)
            {
                _isUpdatingReceiverList = false;
                ReceiverStatusTextBlock.Text = $"Receiver discovery failed: {ex.Message}";
            }
            finally
            {
                RefreshReceiversButton.IsEnabled = true;
                SaveReceiverButton.IsEnabled = true;
            }
        }

        private async void SaveReceiverButton_Click(object sender, RoutedEventArgs e)
        {
            await SaveSelectedReceiverAsync(retargetActivePlayback: true);

            ReceiverStatusTextBlock.Text = string.IsNullOrWhiteSpace(App.PreferredReceiverName)
                ? "Saved receiver preference: Auto select first available receiver."
                : $"Saved receiver preference: {App.PreferredReceiverName}.";
        }

        private async void ReceiverComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_isUpdatingReceiverList || !_hasLoaded) return;

            await SaveSelectedReceiverAsync(retargetActivePlayback: true);
        }

        private async Task SaveSelectedReceiverAsync(bool retargetActivePlayback)
        {
            if (ReceiverComboBox.SelectedItem is not ChromecastReceiverInfo selectedReceiver) return;

            SaveReceiverButton.IsEnabled = false;
            ReceiverStatusTextBlock.Text = string.IsNullOrWhiteSpace(selectedReceiver.Name)
                ? "Applying receiver preference: Auto select."
                : $"Applying receiver preference: {selectedReceiver.DisplayName}.";

            try
            {
                await App.SavePreferredReceiverNameAsync(selectedReceiver.Name, retargetActivePlayback);
                ReceiverStatusTextBlock.Text = string.IsNullOrWhiteSpace(App.PreferredReceiverName)
                    ? "Receiver preference applied: Auto select first available receiver."
                    : $"Receiver preference applied: {App.PreferredReceiverName}.";
            }
            catch (Exception ex)
            {
                ReceiverStatusTextBlock.Text = $"Receiver preference failed: {ex.Message}";
            }
            finally
            {
                SaveReceiverButton.IsEnabled = true;
            }
        }
    }
}
