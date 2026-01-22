// OSCDispatcher.cs
using System;

/// <summary>
/// Global dispatcher to share OSC data between scripts safely.
/// Prevents multiple port listeners.
/// </summary>
public static class OSCDispatcher
{
    public static event Action<string, int> OnIntReceived;

    /// <summary>
    /// Call this when you receive an OSC int message.
    /// </summary>
    public static void DispatchInt(string address, int value)
    {
        OnIntReceived?.Invoke(address, value);
    }
}