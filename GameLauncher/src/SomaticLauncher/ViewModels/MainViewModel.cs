using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using SomaticLauncher.Models;
using SomaticLauncher.Services;

namespace SomaticLauncher.ViewModels
{
    public partial class MainViewModel : ObservableObject
    {
        private readonly GameService _gameService;
        private readonly ConfigService _configService;
        private readonly StatusService _statusService;

        [ObservableProperty]
        private string _gamePath;

        [ObservableProperty]
        private string _gameWorkDir;

        [ObservableProperty]
        private string _statusText = "OFFLINE";

        [ObservableProperty]
        private string _uptimeText = "00:00:00";

        [ObservableProperty]
        private bool _isGameRunning;

        [ObservableProperty]
        private bool _isConfigLoaded;

        [ObservableProperty]
        private bool _isConfigMissing = true;

        [ObservableProperty]
        private double _windowOpacity = 0.95;

        [ObservableProperty]
        private ArtworkConfig _editableConfig;

        public ObservableCollection<string> Logs { get; } = new();

        public MainViewModel(GameService gameService, ConfigService configService, StatusService statusService)
        {
            _gameService = gameService;
            _configService = configService;
            _statusService = statusService;

            // Subscribe to events
            _gameService.OnStatusChanged += OnGameStatusChanged;
            _gameService.OnLog += AddLog;
            _statusService.OnServerStatus += (msg, isError) => AddLog(msg, isError ? "ERROR" : "INFO");

            // Timer for UI updates (uptime)
            var timer = new System.Windows.Threading.DispatcherTimer();
            timer.Interval = TimeSpan.FromSeconds(1);
            timer.Tick += (s, e) => UpdateRuntime();
            timer.Start();

            AddLog("Launcher initialized.", "INFO");
        }

        private void OnGameStatusChanged(string status)
        {
            StatusText = status;
            IsGameRunning = status == "LIVE";
        }

        private void UpdateRuntime()
        {
            if (IsGameRunning)
            {
                var ts = TimeSpan.FromSeconds(_gameService.UptimeSeconds);
                UptimeText = ts.ToString(@"hh\:mm\:ss");
            }
            else
            {
                UptimeText = "00:00:00";
            }
        }

        [RelayCommand]
        private void SelectGame()
        {
            var dialog = new OpenFileDialog
            {
                Filter = "Executables (*.exe)|*.exe",
                Title = "Select Game Executable"
            };

            if (dialog.ShowDialog() == true)
            {
                GamePath = dialog.FileName;
                GameWorkDir = Path.GetDirectoryName(GamePath);
                AddLog($"Selected game: {Path.GetFileName(GamePath)}", "INFO");
                
                 _statusService.QueueEvent(EventType.Selected, EventReason.User, GamePath, 0, "OFF", 0);

                LoadConfigForGame();
            }
        }

        private async void LoadConfigForGame()
        {
            var success = await _configService.LoadConfigAsync(GamePath);
            IsConfigLoaded = success;
            IsConfigMissing = !success;

            if (success)
            {
                EditableConfig = _configService.CurrentConfig;
                AddLog("Config loaded successfully.", "INFO");
            }
            else
            {
                AddLog("Config missing or invalid.", "WARN");
                EditableConfig = null;
            }
        }

        [RelayCommand]
        private async Task CreateConfig()
        {
            if (string.IsNullOrEmpty(GamePath)) return;
            _configService.CreateDefaultConfig(GamePath);
            await _configService.SaveConfigAsync(); // Save default to disk
            AddLog("Default config created.", "INFO");
            LoadConfigForGame();
        }

        [RelayCommand]
        private async Task StartGame()
        {
            if (string.IsNullOrEmpty(GamePath)) return;
            await _gameService.StartGameAsync(GamePath);
        }

        [RelayCommand]
        private async Task StopGame()
        {
            await _gameService.StopGameAsync();
        }

        [RelayCommand]
        private async Task SaveConfig()
        {
            if (IsConfigLoaded)
            {
                await _configService.SaveConfigAsync();
                AddLog("Config saved.", "INFO");
            }
        }

        private void AddLog(string message, string level)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                var time = DateTime.Now.ToString("HH:mm:ss");
                Logs.Insert(0, $"[{time}] {message}");
                // Limit logs
                if (Logs.Count > 100) Logs.RemoveAt(Logs.Count - 1);
            });
        }
    }
}
