using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using SomaticLauncher.Models;

namespace SomaticLauncher.Services
{
    public class StatusService
    {
        private readonly HttpClient _httpClient;
        private readonly ConcurrentQueue<ServerPayload> _packetQueue = new();
        private bool _isSending = false;

        private const string ENDPOINT = "https://somaticstatusserver.onrender.com/update";

        // Expose an event so UI can log server status
        public event Action<string, bool> OnServerStatus; // message, isError

        public StatusService(IHttpClientFactory httpClientFactory)
        {
            _httpClient = httpClientFactory.CreateClient("StatusClient");
        }

        public void QueueEvent(EventType type, EventReason reason, string exePath, int pid, string state, long uptimeSeconds)
        {
            var payload = new ServerPayload
            {
                TimestampUtc = DateTime.UtcNow,
                Game = new GamePayload
                {
                    ExeName = System.IO.Path.GetFileName(exePath),
                    ExePath = exePath,
                    Pid = pid,
                    State = state,
                    UptimeSeconds = uptimeSeconds
                },
                Event = new EventPayload
                {
                    Type = type,
                    Reason = reason
                }
            };

            _packetQueue.Enqueue(payload);
            
            // Fire and forget send task
            _ = ProcessQueueAsync();
        }

        private async Task ProcessQueueAsync()
        {
            if (_isSending) return;
            _isSending = true;

            try
            {
                while (_packetQueue.TryPeek(out _))
                {
                    if (_packetQueue.TryDequeue(out var payload))
                    {
                        await SendPayloadAsync(payload);
                    }
                }
            }
            finally
            {
                _isSending = false;
            }
        }

        private async Task SendPayloadAsync(ServerPayload payload)
        {
            try
            {
                var options = new JsonSerializerOptions
                {
                   DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                   Converters = { new JsonStringEnumConverter() } 
                };

                // Simple retry logic (very basic for MVP)
                int retries = 0;
                while (retries < 3)
                {
                    try
                    {
                        var response = await _httpClient.PostAsJsonAsync(ENDPOINT, payload, options);
                        if (response.IsSuccessStatusCode)
                        {
                            OnServerStatus?.Invoke($"Server update success ({payload.Event.Type})", false);
                            break;
                        }
                        else
                        {
                            OnServerStatus?.Invoke($"Server returned {response.StatusCode}", true);
                        }
                    }
                    catch (Exception)
                    {
                        if (retries == 2) throw;
                    }
                    retries++;
                    await Task.Delay(1000 * retries);
                }
            }
            catch (Exception ex)
            {
                OnServerStatus?.Invoke($"Server send failed: {ex.Message}", true);
                // For MVP, if it fails after retries, we lose the packet or could requeue. 
                // We'll just log it to avoid infinite loops.
            }
        }
    }
}
