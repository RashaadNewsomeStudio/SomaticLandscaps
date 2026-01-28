using System;
using System.Collections.Concurrent;
using UnityEngine;

/// <summary>
/// A simple global dispatcher to marshal actions to the main thread.
/// Guaranteed to exist if initialized early (e.g. via RuntimeInitializeOnLoadMethod or Auto-Creation).
/// </summary>
public class GlobalMainThreadDispatcher : MonoBehaviour
{
    private static GlobalMainThreadDispatcher _instance;
    private static readonly ConcurrentQueue<Action> _queue = new ConcurrentQueue<Action>();

    public static GlobalMainThreadDispatcher Instance
    {
        get
        {
            if (_instance == null)
            {
                // Try find existing
                _instance = FindFirstObjectByType<GlobalMainThreadDispatcher>();
                // If not found, we can't create it safely from background thread.
                // Caller must check for null.
            }
            return _instance;
        }
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void Initialize()
    {
        if (_instance == null)
        {
            var go = new GameObject("GlobalMainThreadDispatcher");
            _instance = go.AddComponent<GlobalMainThreadDispatcher>();
            DontDestroyOnLoad(go);
        }
    }

    public static void Enqueue(Action action)
    {
        _queue.Enqueue(action);
    }

    void Update()
    {
        while (_queue.TryDequeue(out var action))
        {
            try { action?.Invoke(); }
            catch (Exception ex) { Debug.LogError($"Dispatcher Error: {ex}"); }
        }
    }
}
