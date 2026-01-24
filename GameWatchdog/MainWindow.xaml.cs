using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using System.Windows.Threading;
using Microsoft.Win32;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;

namespace WatchdogGUI
{
    public partial class MainWindow : Window
    {
        private Process? _watchdogProcess;
        private DispatcherTimer _updateTimer;
        private DateTime _startTime;
        private string _watchdogExePath = "";
        private string _watchdogLogPath = "";
        private ArtworkConfig? _artworkConfig;
        private int _lastKnownPid = 0;
        private bool _isManualStop = false;
        private static readonly HttpClient _httpClient = new HttpClient();
        private DateTime _lastHeartbeat = DateTime.MinValue;

        static MainWindow()
        {
            _httpClient.DefaultRequestHeaders.Add("x-api-key", "somatic-secure-key-2026-v1");
        }

        public MainWindow()
        {
            InitializeComponent();
            
            try
            {
                // Find the "Somatic Landscapes" root directory
                var baseDir = AppDomain.CurrentDomain.BaseDirectory;
                var currentDir = new DirectoryInfo(baseDir);
                
                // Navigate up until we find the "Somatic Landscapes" folder
                while (currentDir != null && currentDir.Name != "Somatic Landscapes")
                {
                    currentDir = currentDir.Parent;
                }
                
                if (currentDir != null)
                {
                    // Dynamically search for GameWatchdog.exe in the project directory
                    _watchdogExePath = FindGameWatchdogExe(currentDir.FullName);
                    
                    if (string.IsNullOrEmpty(_watchdogExePath))
                    {
                        throw new FileNotFoundException("GameWatchdog.exe not found in project directory");
                    }
                    
                    _watchdogLogPath = Path.Combine(Path.GetDirectoryName(_watchdogExePath) ?? baseDir, "watchdog.log");
                }
                else
                {
                    // Fallback: search from base directory
                    _watchdogExePath = FindGameWatchdogExe(baseDir);
                    if (string.IsNullOrEmpty(_watchdogExePath))
                    {
                        throw new FileNotFoundException("GameWatchdog.exe not found");
                    }
                    _watchdogLogPath = Path.Combine(Path.GetDirectoryName(_watchdogExePath) ?? baseDir, "watchdog.log");
                }
                
                _updateTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                _updateTimer.Tick += (s, e) =>
                {
                    UptimeText.Text = $"{(int)(DateTime.Now - _startTime).TotalHours:D2}:{(DateTime.Now - _startTime).Minutes:D2}:{(DateTime.Now - _startTime).Seconds:D2}";
                    
                    if (_watchdogProcess != null && _watchdogProcess.HasExited)
                    {
                        if (!_isManualStop)
                        {
                            LogActivity("⚠️ Process crashed/exited. Restarting...");
                            SendLog("⚠️ Game Crashed - Restarting...");
                            StartGameProcess();
                        }
                        else
                        {
                            StopWatchdogUI();
                        }
                    }
                    UpdateStats();

                    // Heartbeat every 30s
                    if (_watchdogProcess != null && !_watchdogProcess.HasExited && (DateTime.Now - _lastHeartbeat).TotalSeconds > 30)
                    {
                        SendStatus("running");
                        _lastHeartbeat = DateTime.Now;
                    }
                };
                
                 LogActivity("✅ Somatic Landscapes Launcher Ready");
                
                // Load saved config
                if (File.Exists("launcher_config.json"))
                {
                    try
                    {
                        var json = File.ReadAllText("launcher_config.json");
                        var config = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                        if (config != null)
                        {
                            if (config.ContainsKey("GamePath")) GamePathTextBox.Text = config["GamePath"];
                            if (config.ContainsKey("GameArgs")) GameArgsTextBox.Text = config["GameArgs"];
                        }
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Init Error: {ex.Message}";
            }
        }

        /// <summary>
        /// Recursively searches for GameWatchdog.exe in the given directory
        /// </summary>
        private string FindGameWatchdogExe(string rootPath)
        {
            try
            {
                // Search patterns in order of preference
                var searchPatterns = new[]
                {
                    "**/GameWatchdog/bin/Release/**/GameWatchdog.exe",
                    "**/GameWatchdog/bin/Debug/**/GameWatchdog.exe",
                    "**/bin/Release/**/GameWatchdog.exe",
                    "**/bin/Debug/**/GameWatchdog.exe"
                };

                // Try to find the executable
                var files = Directory.GetFiles(rootPath, "GameWatchdog.exe", SearchOption.AllDirectories);
                
                if (files.Length > 0)
                {
                    // Prefer Release builds over Debug builds
                    var releaseFile = files.FirstOrDefault(f => f.Contains("Release"));
                    if (releaseFile != null) return releaseFile;
                    
                    // Return first found if no Release build
                    return files[0];
                }
            }
            catch (Exception ex)
            {
                LogActivity($"⚠️ Search error: {ex.Message}");
            }
            
            return string.Empty;
        }


        private void StartButton_Click(object sender, RoutedEventArgs e)
        {
            _isManualStop = false;
            StartGameProcess();
        }

        private void StartGameProcess()
        {
            if (string.IsNullOrWhiteSpace(GamePathTextBox.Text)) { MessageBox.Show("Enter game path"); return; }
            
            // Save config
            try
            {
                var config = new Dictionary<string, string>
                {
                    ["GamePath"] = GamePathTextBox.Text,
                    ["GameArgs"] = GameArgsTextBox.Text
                };
                File.WriteAllText("launcher_config.json", JsonSerializer.Serialize(config));
            }
            catch { }

            try
            {
                var args = GameArgsTextBox.Text;
                
                // Webhook is now hardcoded in the backend

                _watchdogProcess = Process.Start(new ProcessStartInfo
                {
                    FileName = GamePathTextBox.Text,
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true,     
                    RedirectStandardOutput = true,
                    WorkingDirectory = Path.GetDirectoryName(GamePathTextBox.Text)
                });

                if (_watchdogProcess != null)
                {
                    _startTime = DateTime.Now;
                    _watchdogProcess.OutputDataReceived += (s, ev) => { if (!string.IsNullOrEmpty(ev.Data)) Dispatcher.Invoke(() => LogActivity(ev.Data)); };
                    _watchdogProcess.BeginOutputReadLine();
                    if (!_updateTimer.IsEnabled) _updateTimer.Start();
                    
                    StartButton.IsEnabled = false;
                    StopButton.IsEnabled = true;
                    StatusIndicator.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#4CAF50")!);
                    StatusText.Text = "Running";
                    StatusText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#4CAF50")!);
                    StatusText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#4CAF50")!);
                    LogActivity($"✅ Started (PID: {_watchdogProcess.Id})");
                    SendLog($"✅ Game Started (PID: {_watchdogProcess.Id})");
                    SendStatus("running");
                }
            }
            catch (Exception ex) { LogActivity($"Start failed: {ex.Message}"); }
        }

        private void StopButton_Click(object sender, RoutedEventArgs e)
        {
            _isManualStop = true;
            StopWatchdog();
        }

        private void StopWatchdog()
        {
            if (_watchdogProcess != null && !_watchdogProcess.HasExited) 
            { 
                try { _watchdogProcess.Kill(true); _watchdogProcess.WaitForExit(2000); } catch {}
            }
            StopWatchdogUI();
        }

        private void StopWatchdogUI()
        {
            _updateTimer.Stop();
            _watchdogProcess = null;
            StartButton.IsEnabled = true;
            StopButton.IsEnabled = false;
            StatusIndicator.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#858585")!);
            StatusText.Text = "Stopped";
            StatusText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#858585")!);
            LogActivity("⏹️ Stopped");
            SendLog("⏹️ Game Stopped by User");
            SendStatus("stopped");
            
            // Reset Graph
             _heartbeatPoints.Clear();
             HeartbeatPolyline.Points.Clear();
        }

        private void KillSwitchButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                File.WriteAllText(Path.Combine(Path.GetDirectoryName(_watchdogExePath) ?? "", "STOP.txt"), $"Kill: {DateTime.Now}");
                LogActivity("🛑 Kill switch");
                MessageBox.Show("Kill switch activated", "Success");
            }
            catch { }
        }

        private void BrowseButton_Click(object sender, RoutedEventArgs e)
        {
            var d = new OpenFileDialog { Filter = "Executables (*.exe)|*.exe" };
            if (d.ShowDialog() == true) GamePathTextBox.Text = d.FileName;
        }


        private List<double> _heartbeatPoints = new List<double>();
        private Random _rnd = new Random();
        private double _phase = 0;

        private void UpdateHeartbeatGraph()
        {
            // Canvas size (approx)
            double width = HeartbeatCanvas.ActualWidth;
            double height = HeartbeatCanvas.ActualHeight;
            if (width <= 0 || height <= 0) return;

            // Generate next point
            // Simulating a "heartbeat" pattern: flat... pulse... flat
            
            double baseLine = height / 2;
            double val = baseLine;

            if (_watchdogProcess != null && !_watchdogProcess.HasExited)
            {
                 // Pulse logic
                 _phase += 0.2;
                 if (_phase > Math.PI * 2) _phase = 0;

                 // Create a "QRS" complex look occasionally
                 if (_rnd.NextDouble() > 0.8)
                 {
                     val = baseLine + (_rnd.Next(-30, 30));
                 }
                 else
                 {
                     val = baseLine + Math.Sin(_phase) * 5;
                 }
            }
            else
            {
                // Flatline
                val = baseLine;
            }

            _heartbeatPoints.Add(val);

            // Cap points
            int maxPoints = (int)(width / 5); // 5px spacing
            if (_heartbeatPoints.Count > maxPoints)
            {
                _heartbeatPoints.RemoveAt(0);
            }

            // Draw
            var points = new PointCollection();
            for (int i = 0; i < _heartbeatPoints.Count; i++)
            {
                points.Add(new Point(i * 5, _heartbeatPoints[i]));
            }
            HeartbeatPolyline.Points = points;
        }


        private void LogActivity(string msg)
        {
            ActivityLogTextBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {msg}\n");
            ActivityLogTextBox.ScrollToEnd();
        }

        private void UpdateStats()
        {
            try
            {
                UpdateHeartbeatGraph();

                if (File.Exists(_watchdogLogPath))
                {
                   // Just read PID from last line if possible, or assume running if process exists
                   if (_watchdogProcess != null && !_watchdogProcess.HasExited)
                   {
                        // Logic simplified for "Pro" mode - we assume green if process matches
                   }
                }
            }
            catch { }
        }

        private void OpenConfigEditor_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(GamePathTextBox.Text))
            {
                MessageBox.Show("Please select a game executable first", "No Game Selected");
                return;
            }

            var configWindow = new ConfigEditorWindow(GamePathTextBox.Text);
            configWindow.ShowDialog();
            LogActivity("⚙️ Config editor closed");
        }

        protected override void OnClosed(EventArgs e) { base.OnClosed(e); StopWatchdog(); }
        private async void SendStatus(string status)
        {
            try
            {
                var payload = new
                {
                    type = "heartbeat",
                    status = status,
                    machine = Environment.MachineName,
                    timestamp = DateTime.UtcNow.ToString("O"),
                    pid = _watchdogProcess?.Id ?? 0
                };

                await _httpClient.PostAsJsonAsync("https://somaticstatusserver.onrender.com/update", payload);
            }
            catch (Exception) { /* Silent fail */ }
        }

        private async void SendLog(string message)
        {
            try
            {
                var payload = new { message = message };
                await _httpClient.PostAsJsonAsync("https://somaticstatusserver.onrender.com/log", payload);
            }
            catch (Exception) { /* Silent fail */ }
        }
    }
}