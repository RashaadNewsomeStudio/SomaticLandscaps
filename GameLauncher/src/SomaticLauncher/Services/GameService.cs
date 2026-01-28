using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SomaticLauncher.Models;

namespace SomaticLauncher.Services
{
    public class GameService
    {
        private readonly StatusService _statusService;
        private Process _gameProcess;
        private string _currentExePath;
        private CancellationTokenSource _watchdogCts;
        private DateTime _startTime;
        private readonly List<DateTime> _crashHistory = new();

        public bool IsRunning => _gameProcess != null && !_gameProcess.HasExited;
        public string CurrentStatus { get; private set; } = "OFF";
        
        public event Action<string> OnStatusChanged;
        public event Action<string, string> OnLog; // msg, level

        public GameService(StatusService statusService)
        {
            _statusService = statusService;
        }

        public long UptimeSeconds 
        {
            get
            {
                if (!IsRunning) return 0;
                return (long)(DateTime.Now - _startTime).TotalSeconds;
            }
        }

        public async Task StartGameAsync(string exePath, bool isRestart = false)
        {
            if (File.Exists(exePath) == false)
            {
                OnLog?.Invoke("Game executable not found.", "ERROR");
                return;
            }

            if (IsRunning)
            {
                OnLog?.Invoke("Game is already running.", "WARN");
                return;
            }

            // Check crash loop
            PruneCrashHistory();
            if (isRestart)
            {
                _crashHistory.Add(DateTime.Now);
                if (_crashHistory.Count >= 5)
                {
                    OnLog?.Invoke("Crash loop detected! stopping watchdog.", "ERROR");
                    CurrentStatus = "OFF"; 
                    OnStatusChanged?.Invoke(CurrentStatus);
                    return;
                }
            }

            _currentExePath = exePath;
            var startInfo = new ProcessStartInfo
            {
                FileName = exePath,
                WorkingDirectory = Path.GetDirectoryName(exePath),
                UseShellExecute = false
            };

            try
            {
                _gameProcess = new Process { StartInfo = startInfo };
                _gameProcess.EnableRaisingEvents = true;
                _gameProcess.Exited += OnProcessExited;

                if (_gameProcess.Start())
                {
                    _startTime = DateTime.Now;
                    CurrentStatus = "LIVE";
                    OnStatusChanged?.Invoke(CurrentStatus);

                    var eventType = isRestart ? EventType.Relaunched : EventType.Launched;
                    var reason = isRestart ? EventReason.Watchdog : EventReason.User;

                    OnLog?.Invoke(isRestart ? "Game relaunched (watchdog)" : "Game launched!", "INFO");
                    
                    _statusService.QueueEvent(eventType, reason, _currentExePath, _gameProcess.Id, "LIVE", 0);

                    // Start Watchdog Poll as backup
                    _watchdogCts?.Cancel();
                    _watchdogCts = new CancellationTokenSource();
                    _ = WatchdogPollLoop(_watchdogCts.Token);
                }
            }
            catch (Exception ex)
            {
                 OnLog?.Invoke($"Failed to start game: {ex.Message}", "ERROR");
            }
        }

        public async Task StopGameAsync()
        {
            // Cancel watchdog first so we don't trigger restart
            _watchdogCts?.Cancel();
            
            if (_gameProcess == null || _gameProcess.HasExited)
            {
                CurrentStatus = "OFF";
                OnStatusChanged?.Invoke(CurrentStatus);
                return;
            }

            OnLog?.Invoke("Stopping game (user)...", "INFO");

            try
            {
                // Graceful close
                _gameProcess.Exited -= OnProcessExited; // Detach event to avoid restart trigger
                
                if (_gameProcess.MainWindowHandle != IntPtr.Zero)
                {
                    _gameProcess.CloseMainWindow();
                    // Wait up to 2 seconds
                    if (!_gameProcess.WaitForExit(2000))
                    {
                        KillProcessTree();
                    }
                }
                else
                {
                     KillProcessTree();
                }

                CurrentStatus = "OFF";
                OnStatusChanged?.Invoke(CurrentStatus);
                _statusService.QueueEvent(EventType.Stopped, EventReason.User, _currentExePath, 0, "OFF", UptimeSeconds);
                OnLog?.Invoke("Game stopped.", "INFO");
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"Error stopping game: {ex.Message}", "ERROR");
            }
            finally
            {
                _gameProcess = null;
            }
        }

        private void KillProcessTree()
        {
            if (_gameProcess != null && !_gameProcess.HasExited)
            {
                _gameProcess.Kill(true); // .NET 8 supports killing entire tree
            }
        }

        private void OnProcessExited(object sender, EventArgs e)
         {
            // If this fires, it means unexpected exit (since we detach on StopGame)
            if (CurrentStatus == "LIVE")
            {
                var exitCode = _gameProcess?.ExitCode ?? -1;
                OnLog?.Invoke($"Game crashed (Exit Code: {exitCode})", "ERROR");
                _statusService.QueueEvent(EventType.Crashed, EventReason.Unknown, _currentExePath, _gameProcess?.Id ?? 0, "OFF", UptimeSeconds);

                // Restart
                _ = Task.Run(async () => 
                {
                    await Task.Delay(200); // Slight delay
                    await StartGameAsync(_currentExePath, isRestart: true);
                });
            }
        }

        private async Task WatchdogPollLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                if (_gameProcess != null && _gameProcess.HasExited && CurrentStatus == "LIVE")
                {
                    // Fallback if event didn't fire (rare but possible)
                    OnProcessExited(null, null); 
                    break; 
                }
                await Task.Delay(200, token);
            }
        }

        private void PruneCrashHistory()
        {
            // Remove crashes older than 2 minutes
            var cutoff = DateTime.Now.AddMinutes(-2);
            _crashHistory.RemoveAll(t => t < cutoff);
        }
    }
}
