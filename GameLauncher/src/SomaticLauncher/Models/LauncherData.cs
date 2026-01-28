using System;
using System.Text.Json.Serialization;

namespace SomaticLauncher.Models
{
    public enum EventType
    {
        [JsonPropertyName("selected")]
        Selected,
        [JsonPropertyName("launched")]
        Launched,
        [JsonPropertyName("stopped")]
        Stopped,
        [JsonPropertyName("crashed")]
        Crashed,
        [JsonPropertyName("relaunched")]
        Relaunched,
        [JsonPropertyName("heartbeat")]
        Heartbeat,
        [JsonPropertyName("launcher_online")]
        LauncherOnline
    }

    public enum EventReason
    {
        [JsonPropertyName("user")]
        User,
        [JsonPropertyName("watchdog")]
        Watchdog,
        [JsonPropertyName("unknown")]
        Unknown
    }

    public class LogEvent
    {
        public DateTime Timestamp { get; set; }
        public string Message { get; set; }
        public string Level { get; set; } // INFO, WARN, ERROR
    }

    public class ServerPayload
    {
        [JsonPropertyName("machineId")]
        public string MachineId { get; set; } = Environment.MachineName;

        [JsonPropertyName("timestampUtc")]
        public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;

        [JsonPropertyName("app")]
        public string App { get; set; } = "SomaticLauncher";

        [JsonPropertyName("game")]
        public GamePayload Game { get; set; }

        [JsonPropertyName("event")]
        public EventPayload Event { get; set; }
    }

    public class GamePayload
    {
        [JsonPropertyName("exeName")]
        public string ExeName { get; set; }

        [JsonPropertyName("exePath")]
        public string ExePath { get; set; }

        [JsonPropertyName("pid")]
        public int Pid { get; set; }

        [JsonPropertyName("state")]
        public string State { get; set; } // LIVE, OFF

        [JsonPropertyName("uptimeSeconds")]
        public long UptimeSeconds { get; set; }
    }

    public class EventPayload
    {
        [JsonPropertyName("type")]
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public EventType Type { get; set; }

        [JsonPropertyName("reason")]
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public EventReason Reason { get; set; }
    }
}
