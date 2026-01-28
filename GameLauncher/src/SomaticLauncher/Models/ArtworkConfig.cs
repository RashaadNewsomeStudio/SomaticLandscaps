using System.Text.Json.Serialization;

namespace SomaticLauncher.Models
{
    public class ArtworkConfig
    {
        // Resolution
        public int ResolutionWidth { get; set; } = 1920;
        public int ResolutionHeight { get; set; } = 1080;

        // Idle
        public float IdleFadeDuration { get; set; } = 1.0f;
        public float IdlePrepareLead { get; set; } = 0.5f;
        public float IdleMinFade { get; set; } = 0.0f;
        public bool CrossfadeIdle { get; set; } = true;
        public bool RandomizeIdleStartOnReturn { get; set; } = false;

        // Active
        public float ActiveFadeIn { get; set; } = 0.5f;
        public float ActiveFadeOut { get; set; } = 0.5f;
        public bool ActiveUseFixedWindow { get; set; } = false;
        public float ActiveFixedMidHoldSeconds { get; set; } = 2.0f;
        public float ActiveEndHoldSeconds { get; set; } = 2.0f;

        // Return
        public float ReturnFade { get; set; } = 1.0f;
        public float ReturnBlackHold { get; set; } = 0.5f;

        // Audio
        public float MusicVolume { get; set; } = 1.0f;
        public float MusicRampIn { get; set; } = 2.0f;
        public float MusicFadeIn { get; set; } = 2.0f;
        public float MusicRampOut { get; set; } = 2.0f;
        public bool MusicSyncWithActiveFade { get; set; } = true;
        public bool AutoLoadMusicFromStreaming { get; set; } = true;
        public string MusicSubfolder { get; set; } = "Music";

        // Other
        public int PanelToggleKey { get; set; } = 27; // Esc
        public bool IgnoreAmbientWhileActive { get; set; } = true;
        public bool ActiveClickCancelsToAmbient { get; set; } = true;
        public bool EnableStartupPanels { get; set; } = true;
    }
}
