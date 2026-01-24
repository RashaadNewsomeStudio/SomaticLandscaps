using System.Text.Json.Serialization;

namespace WatchdogGUI
{
    public class ArtworkConfig
    {
        // Resolution Settings
        [JsonPropertyName("ResolutionWidth")]
        public int ResolutionWidth { get; set; } = 5280;

        [JsonPropertyName("ResolutionHeight")]
        public int ResolutionHeight { get; set; } = 1620;

        // Idle Animation Settings
        [JsonPropertyName("IdleFadeDuration")]
        public double IdleFadeDuration { get; set; } = 1.5;

        [JsonPropertyName("IdlePrepareLead")]
        public double IdlePrepareLead { get; set; } = 2.0;

        [JsonPropertyName("IdleMinFade")]
        public double IdleMinFade { get; set; } = 0.2;

        [JsonPropertyName("CrossfadeIdle")]
        public bool CrossfadeIdle { get; set; } = true;

        // Active Animation Settings
        [JsonPropertyName("ActiveFadeIn")]
        public double ActiveFadeIn { get; set; } = 1.5;

        [JsonPropertyName("ActiveFadeOut")]
        public double ActiveFadeOut { get; set; } = 2.0;

        [JsonPropertyName("ActiveUseFixedWindow")]
        public bool ActiveUseFixedWindow { get; set; } = true;

        [JsonPropertyName("ActiveFixedMidHoldSeconds")]
        public double ActiveFixedMidHoldSeconds { get; set; } = 22.0;

        [JsonPropertyName("ActiveEndHoldSeconds")]
        public double ActiveEndHoldSeconds { get; set; } = 0.15;

        [JsonPropertyName("ReturnFade")]
        public double ReturnFade { get; set; } = 1.5;

        [JsonPropertyName("ReturnBlackHold")]
        public double ReturnBlackHold { get; set; } = 0.5;

        [JsonPropertyName("RandomizeIdleStartOnReturn")]
        public bool RandomizeIdleStartOnReturn { get; set; } = true;

        // Music Settings
        [JsonPropertyName("MusicVolume")]
        public double MusicVolume { get; set; } = 1.0;

        [JsonPropertyName("MusicRampIn")]
        public double MusicRampIn { get; set; } = 1.5;

        [JsonPropertyName("MusicFadeIn")]
        public double MusicFadeIn { get; set; } = 1.5;

        [JsonPropertyName("MusicRampOut")]
        public double MusicRampOut { get; set; } = 1.5;

        [JsonPropertyName("MusicSyncWithActiveFade")]
        public bool MusicSyncWithActiveFade { get; set; } = false;

        // Advanced Settings
        [JsonPropertyName("PanelToggleKey")]
        public int PanelToggleKey { get; set; } = 282;

        [JsonPropertyName("IgnoreAmbientWhileActive")]
        public bool IgnoreAmbientWhileActive { get; set; } = true;

        [JsonPropertyName("ActiveClickCancelsToAmbient")]
        public bool ActiveClickCancelsToAmbient { get; set; } = false;

        [JsonPropertyName("AutoLoadMusicFromStreaming")]
        public bool AutoLoadMusicFromStreaming { get; set; } = true;

        [JsonPropertyName("MusicSubfolder")]
        public string MusicSubfolder { get; set; } = "Music";

        [JsonPropertyName("EnableStartupPanels")]
        public bool EnableStartupPanels { get; set; } = false;
    }
}
