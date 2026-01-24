using System;
using System.IO;
using System.Text.Json;
using System.Windows;

namespace WatchdogGUI
{
    public partial class ConfigEditorWindow : Window
    {
        private ArtworkConfig? _config;
        private string _configPath = "";

        public ConfigEditorWindow(string gamePath)
        {
            InitializeComponent();
            
            var gameDir = Path.GetDirectoryName(gamePath);
            if (!string.IsNullOrEmpty(gameDir))
            {
                _configPath = Path.Combine(gameDir, "Config", "ArtworkConfig.json");
                LoadConfig();
            }
        }

        private void LoadConfig_Click(object sender, RoutedEventArgs e)
        {
            LoadConfig();
        }

        private void LoadConfig()
        {
            try
            {
                if (!File.Exists(_configPath))
                {
                    MessageBox.Show($"Config file not found at:\n{_configPath}", "File Not Found");
                    return;
                }

                var json = File.ReadAllText(_configPath);
                _config = JsonSerializer.Deserialize<ArtworkConfig>(json);
                
                if (_config != null)
                {
                    ResolutionWidthBox.Text = _config.ResolutionWidth.ToString();
                    ResolutionHeightBox.Text = _config.ResolutionHeight.ToString();
                    
                    IdleFadeDurationSlider.Value = _config.IdleFadeDuration;
                    IdlePrepareLeadSlider.Value = _config.IdlePrepareLead;
                    IdleMinFadeSlider.Value = _config.IdleMinFade;
                    CrossfadeIdleToggle.IsChecked = _config.CrossfadeIdle;
                    
                    ActiveFadeInSlider.Value = _config.ActiveFadeIn;
                    ActiveFadeOutSlider.Value = _config.ActiveFadeOut;
                    ActiveUseFixedWindowToggle.IsChecked = _config.ActiveUseFixedWindow;
                   ActiveFixedMidHoldSlider.Value = _config.ActiveFixedMidHoldSeconds;
                    ActiveEndHoldSlider.Value = _config.ActiveEndHoldSeconds;
                    ReturnFadeSlider.Value = _config.ReturnFade;
                    ReturnBlackHoldSlider.Value = _config.ReturnBlackHold;
                    RandomizeIdleStartToggle.IsChecked = _config.RandomizeIdleStartOnReturn;
                    
                    MusicVolumeSlider.Value = _config.MusicVolume;
                    MusicRampInSlider.Value = _config.MusicRampIn;
                    MusicFadeInSlider.Value = _config.MusicFadeIn;
                    MusicRampOutSlider.Value = _config.MusicRampOut;
                    MusicSyncToggle.IsChecked = _config.MusicSyncWithActiveFade;
                    
                    PanelToggleKeyBox.Text = _config.PanelToggleKey.ToString();
                    IgnoreAmbientToggle.IsChecked = _config.IgnoreAmbientWhileActive;
                    ActiveClickCancelsToggle.IsChecked = _config.ActiveClickCancelsToAmbient;
                    AutoLoadMusicToggle.IsChecked = _config.AutoLoadMusicFromStreaming;
                    MusicSubfolderBox.Text = _config.MusicSubfolder;
                    EnableStartupPanelsToggle.IsChecked = _config.EnableStartupPanels;
                    
                    MessageBox.Show("Configuration loaded successfully!", "Success");
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error loading config:\n{ex.Message}", "Error");
            }
        }

        private void SaveConfig_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_config == null)
                {
                    MessageBox.Show("Please load the config first", "No Config Loaded");
                    return;
                }

                if (int.TryParse(ResolutionWidthBox.Text, out int resWidth)) _config.ResolutionWidth = resWidth;
                if (int.TryParse(ResolutionHeightBox.Text, out int resHeight)) _config.ResolutionHeight = resHeight;
                
                _config.IdleFadeDuration = IdleFadeDurationSlider.Value;
                _config.IdlePrepareLead = IdlePrepareLeadSlider.Value;
                _config.IdleMinFade = IdleMinFadeSlider.Value;
                _config.CrossfadeIdle = CrossfadeIdleToggle.IsChecked ?? false;
                
                _config.ActiveFadeIn = ActiveFadeInSlider.Value;
                _config.ActiveFadeOut = ActiveFadeOutSlider.Value;
                _config.ActiveUseFixedWindow = ActiveUseFixedWindowToggle.IsChecked ?? false;
                _config.ActiveFixedMidHoldSeconds = ActiveFixedMidHoldSlider.Value;
                _config.ActiveEndHoldSeconds = ActiveEndHoldSlider.Value;
                _config.ReturnFade = ReturnFadeSlider.Value;
                _config.ReturnBlackHold = ReturnBlackHoldSlider.Value;
                _config.RandomizeIdleStartOnReturn = RandomizeIdleStartToggle.IsChecked ?? false;
                
                _config.MusicVolume = MusicVolumeSlider.Value;
                _config.MusicRampIn = MusicRampInSlider.Value;
                _config.MusicFadeIn = MusicFadeInSlider.Value;
                _config.MusicRampOut = MusicRampOutSlider.Value;
                _config.MusicSyncWithActiveFade = MusicSyncToggle.IsChecked ?? false;
                
                if (int.TryParse(PanelToggleKeyBox.Text, out int keyCode)) _config.PanelToggleKey = keyCode;
                _config.IgnoreAmbientWhileActive = IgnoreAmbientToggle.IsChecked ?? false;
                _config.ActiveClickCancelsToAmbient = ActiveClickCancelsToggle.IsChecked ?? false;
                _config.AutoLoadMusicFromStreaming = AutoLoadMusicToggle.IsChecked ?? false;
                _config.MusicSubfolder = MusicSubfolderBox.Text;
                _config.EnableStartupPanels = EnableStartupPanelsToggle.IsChecked ?? false;

                var options = new JsonSerializerOptions { WriteIndented = true };
                var json = JsonSerializer.Serialize(_config, options);
                File.WriteAllText(_configPath, json);
                
                MessageBox.Show("Configuration saved successfully!", "Success");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error saving config:\n{ex.Message}", "Error");
            }
        }
    }
}
