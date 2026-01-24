using UnityEngine;
using UnityEngine.Networking;
using System.Collections;
using System.Text;
using System.IO;

/// <summary>
/// CrashReporter - Sends Unity logs and crash reports to Firebase Firestore.
/// Instructions:
/// 1. Create a Firebase Project at console.firebase.google.com
/// 2. Enable Firestore Database (Test Mode)
/// 3. Paste your Project ID below.
/// </summary>
public class CrashReporter : MonoBehaviour
{
    [Header("Configuration")]
    [Tooltip("Target Firebase Project ID (e.g. 'somatic-landscapes-123')")]
    public string projectId = "somaticlandscaps"; // User confirmed this is the ID
    
    [Tooltip("Upload the full Player-prev.log from the last session on startup?")]
    public bool uploadPreviousSessionLog = true;

    private string firestoreUrl;
    private bool isUploading = false;

    // Struct to match Firestore JSON format
    [System.Serializable]
    private class FirestoreDocument
    {
        public Fields fields;
    }

    [System.Serializable]
    private class Fields
    {
        public StringValue message;
        public StringValue stackTrace;
        public StringValue type;
        public StringValue timestamp;
        public StringValue deviceParams;
        public StringValue sessionID;
    }

    [System.Serializable]
    private class StringValue { public string stringValue; }

    void Awake()
    {
        // FAILSAFE: Explicitly disable CrashReporter as per request
        Debug.Log("[CrashReporter] ⚠️ Service DISABLED by user request.");
        enabled = false;
        return; 

        /* Unreachable code due to explicit disable above
        // Auto-fix empty inspector value to code default
        if (string.IsNullOrWhiteSpace(projectId) || projectId == "YOUR_PROJECT_ID_HERE")
        {
            projectId = "somaticlandscaps";
        }

        projectId = projectId.Trim(); 
        
        if (string.IsNullOrEmpty(projectId))
        {
            Debug.LogError("[CrashReporter] ❌ Project ID is MISSING! Please set it in Inspector.");
            return;
        }

        firestoreUrl = $"https://firestore.googleapis.com/v1/projects/{projectId}/databases/(default)/documents/game_logs";
        
        // Listen for logs
        Application.logMessageReceived += HandleLog;
        
        // Report specific startup event
        LogToFirebase("Game Started", "", LogType.Log);

        if (uploadPreviousSessionLog)
        {
            StartCoroutine(UploadPreviousLog());
        }
        */
    }

    void OnDestroy()
    {
        Application.logMessageReceived -= HandleLog;
    }

    /// <summary>
    /// Captures logs and filters for important ones (Exceptions/Errors)
    /// </summary>
    void HandleLog(string logString, string stackTrace, LogType type)
    {
        // Only upload Errors, Exceptions, or critical Assertions. 
        // We skip normal Logs to save bandwidth, unless you specifically want them.
        // Guard: Prevent infinite recursion if CrashReporter itself generates an error
        if (!string.IsNullOrEmpty(stackTrace) && stackTrace.Contains("CrashReporter")) return;
        if (!string.IsNullOrEmpty(logString) && logString.Contains("CrashReporter")) return;

        if (type == LogType.Exception || type == LogType.Error || type == LogType.Assert)
        {
            LogToFirebase(logString, stackTrace, type);
        }
    }

    void Update()
    {
        // Manual Test Button
        if (Input.GetKeyDown(KeyCode.F8))
        {
            Debug.Log("[CrashReporter] 🛠️ Manual Test Triggered via F8");
            LogToFirebase("Manual Test Log (F8)", "No Stack Trace", LogType.Log);
        }
    }

    /// <summary>
    /// Prepares the UnityWebRequest to send data to Firestore
    /// </summary>
    private void LogToFirebase(string message, string stackTrace, LogType type)
    {
        StartCoroutine(SendLogRoutine(message, stackTrace, type));
    }

    IEnumerator SendLogRoutine(string message, string stackTrace, LogType type)
    {
        // Safety: Don't flood
        if (isUploading) yield return new WaitForSeconds(0.1f);
        isUploading = true;

        string DeviceInfo = $"OS: {SystemInfo.operatingSystem} | RAM: {SystemInfo.systemMemorySize}MB | GPU: {SystemInfo.graphicsDeviceName}";

        // Construct JSON Payload manually or via JsonUtility
        // Firestore REST API structure is verbose, so we build it carefully.
        
        var payload = new FirestoreDocument
        {
            fields = new Fields
            {
                message = new StringValue { stringValue = message },
                stackTrace = new StringValue { stringValue = stackTrace },
                type = new StringValue { stringValue = type.ToString() },
                timestamp = new StringValue { stringValue = System.DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss") },
                deviceParams = new StringValue { stringValue = DeviceInfo },
                sessionID = new StringValue { stringValue = System.Guid.NewGuid().ToString() } // Unique per log entry really, but we can group if needed
            }
        };

        string json = JsonUtility.ToJson(payload);

        using (UnityWebRequest request = new UnityWebRequest(firestoreUrl, "POST"))
        {
            byte[] bodyRaw = Encoding.UTF8.GetBytes(json);
            request.uploadHandler = new UploadHandlerRaw(bodyRaw);
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json");

            yield return request.SendWebRequest();

            if (request.result != UnityWebRequest.Result.Success)
            {
                // ENABLED DEBUGGING - Use standard Log not LogError to prevent recursion!
                Debug.Log($"[CrashReporter] ❌ Upload FAILED: {request.error}\nURL: {firestoreUrl}\nResponse: {request.downloadHandler.text}"); 
            }
            else
            {
                Debug.Log($"[CrashReporter] ✅ Upload Success! ({type})");
            }
        }
        
        isUploading = false;
    }

    /// <summary>
    /// Reads the previous session's log file (Player-prev.log) and uploads it as a single huge string.
    /// </summary>
    IEnumerator UploadPreviousLog()
    {
        string logPath = Path.Combine(Application.persistentDataPath, "Player-prev.log");
        
        if (File.Exists(logPath))
        {
            string fullLog = "";
            try
            {
                fullLog = File.ReadAllText(logPath);
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[CrashReporter] Could not read prev log: {e.Message}");
                yield break;
            }

            if (string.IsNullOrEmpty(fullLog)) yield break;

            // Firestore has a 1MB limit per document. Truncate if needed.
            if (fullLog.Length > 900000) 
            {
                fullLog = fullLog.Substring(fullLog.Length - 900000); // Keep last ~900KB
            }

            LogToFirebase("PREVIOUS SESSION LOG", fullLog, LogType.Warning);
        }
    }
}
