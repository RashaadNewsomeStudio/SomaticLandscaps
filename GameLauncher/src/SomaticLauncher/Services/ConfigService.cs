using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using SomaticLauncher.Models;

namespace SomaticLauncher.Services
{
    public class ConfigService
    {
        private JsonNode _rootNode;
        private string _currentConfigPath;

        public ArtworkConfig CurrentConfig { get; private set; }
        public bool IsConfigLoaded => CurrentConfig != null;
        public string ConfigPath => _currentConfigPath;

        public async Task<bool> LoadConfigAsync(string exePath)
        {
            CurrentConfig = null;
            _rootNode = null;
            _currentConfigPath = null;

            if (string.IsNullOrEmpty(exePath)) return false;

            var gameDir = Path.GetDirectoryName(exePath);
            var configPath = Path.Combine(gameDir, "config", "ArtworkConfig.json");
            _currentConfigPath = configPath;

            if (!File.Exists(configPath))
            {
                return false;
            }

            try
            {
                using var stream = File.OpenRead(configPath);
                _rootNode = await JsonNode.ParseAsync(stream);
                
                // Deserialize strictly for the typed model, but keep _rootNode for saving
                CurrentConfig = _rootNode.Deserialize<ArtworkConfig>(new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
                
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error loading config: {ex.Message}");
                return false; 
            }
        }

        public async Task SaveConfigAsync()
        {
            if (_rootNode == null || CurrentConfig == null || string.IsNullOrEmpty(_currentConfigPath))
                return;

            // Update the JsonNode with values from CurrentConfig to preserve comments/_other fields
            UpdateNodeFromModel(_rootNode, CurrentConfig);

            var options = new JsonSerializerOptions { WriteIndented = true };
            await File.WriteAllTextAsync(_currentConfigPath, _rootNode.ToJsonString(options));
        }

        public void CreateDefaultConfig(string exePath)
        {
            if (string.IsNullOrEmpty(exePath)) return;
             var gameDir = Path.GetDirectoryName(exePath);
            var configDir = Path.Combine(gameDir, "config");
            _currentConfigPath = Path.Combine(configDir, "ArtworkConfig.json");

            if (!Directory.Exists(configDir))
            {
                Directory.CreateDirectory(configDir);
            }

            CurrentConfig = new ArtworkConfig();
            _rootNode = JsonSerializer.SerializeToNode(CurrentConfig);
        }

        private void UpdateNodeFromModel(JsonNode node, ArtworkConfig config)
        {
            // Helper to update node property if it exists, or add it
            void SetVal<T>(string key, T val) 
            {
                node[key] = JsonValue.Create(val);
            }

            SetVal(nameof(config.ResolutionWidth), config.ResolutionWidth);
            SetVal(nameof(config.ResolutionHeight), config.ResolutionHeight);

            SetVal(nameof(config.IdleFadeDuration), config.IdleFadeDuration);
            SetVal(nameof(config.IdlePrepareLead), config.IdlePrepareLead);
            SetVal(nameof(config.IdleMinFade), config.IdleMinFade);
            SetVal(nameof(config.CrossfadeIdle), config.CrossfadeIdle);
            SetVal(nameof(config.RandomizeIdleStartOnReturn), config.RandomizeIdleStartOnReturn);

            SetVal(nameof(config.ActiveFadeIn), config.ActiveFadeIn);
            SetVal(nameof(config.ActiveFadeOut), config.ActiveFadeOut);
            SetVal(nameof(config.ActiveUseFixedWindow), config.ActiveUseFixedWindow);
            SetVal(nameof(config.ActiveFixedMidHoldSeconds), config.ActiveFixedMidHoldSeconds);
            SetVal(nameof(config.ActiveEndHoldSeconds), config.ActiveEndHoldSeconds);

            SetVal(nameof(config.ReturnFade), config.ReturnFade);
            SetVal(nameof(config.ReturnBlackHold), config.ReturnBlackHold);

            SetVal(nameof(config.MusicVolume), config.MusicVolume);
            SetVal(nameof(config.MusicRampIn), config.MusicRampIn);
            SetVal(nameof(config.MusicFadeIn), config.MusicFadeIn);
            SetVal(nameof(config.MusicRampOut), config.MusicRampOut);
            SetVal(nameof(config.MusicSyncWithActiveFade), config.MusicSyncWithActiveFade);
            SetVal(nameof(config.AutoLoadMusicFromStreaming), config.AutoLoadMusicFromStreaming);
            SetVal(nameof(config.MusicSubfolder), config.MusicSubfolder);

            SetVal(nameof(config.PanelToggleKey), config.PanelToggleKey);
            SetVal(nameof(config.IgnoreAmbientWhileActive), config.IgnoreAmbientWhileActive);
            SetVal(nameof(config.ActiveClickCancelsToAmbient), config.ActiveClickCancelsToAmbient);
            SetVal(nameof(config.EnableStartupPanels), config.EnableStartupPanels);
        }
    }
}
