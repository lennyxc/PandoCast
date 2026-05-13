namespace PandoCast.Core
{
    public class ChromecastReceiverInfo
    {
        public string Name { get; init; } = string.Empty;
        public string Model { get; init; } = string.Empty;
        public string DeviceUri { get; init; } = string.Empty;

        public string DisplayName
        {
            get
            {
                if (string.IsNullOrWhiteSpace(Name)) return "Auto select first Chromecast";
                if (string.IsNullOrWhiteSpace(Model)) return Name;

                return $"{Name} ({Model})";
            }
        }
    }
}
