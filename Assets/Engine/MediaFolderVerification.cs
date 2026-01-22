using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;
using TMPro;
using System.Collections;

[DisallowMultipleComponent]
public class MediaFolderVerifier : MonoBehaviour
{
    [Header("UI (on-screen log)")]
    public GameObject logPanel;          // toggled with F4
    public TextMeshProUGUI logText;      // the report text

    [Header("Hotkey")]
    public KeyCode toggleKey = KeyCode.F4;

    [Header("Folders (used if ControllerMain is absent)")]
    public string streamingActiveSubfolder  = "Active";
    public string streamingAmbientSubfolder = "Ambient";

    [Header("Rules")]
    [Tooltip("Active files must start with this prefix (case-insensitive).")]
    public string activeRequiredPrefix = "EC";
    [Tooltip("Allowed video extensions.")]
    public string[] allowedExtensions = new[] { ".mov" };

    [Header("Expected counts (optional)")]
    [Tooltip("Set to -1 to skip the check.")]
    public int expectedAmbientCount = -1;
    [Tooltip("Set to -1 to skip the check.")]
    public int expectedActiveCount  = -1;

    [Header("Scan animation")]
    [Tooltip("Delay after each file line so the user can read the progress.")]
    public float perFileDelaySeconds = 0.15f;  // slow the animation here
    [Tooltip("Width of the ASCII progress bar.")]
    public int progressBarWidth = 12;

    [Header("Report/Behavior")]
    public bool writeReportFile = true;
    public bool logFullReportToConsole = false;
    public bool strict = false;

    // Results
    public bool scanOK { get; private set; }
    public int ambientCountOK { get; private set; }
    public int activeCountOK { get; private set; }
    public string activePathUsed  { get; private set; }
    public string ambientPathUsed { get; private set; }

    // Internal
    private Coroutine _scanCo;
    private readonly StringBuilder _sb = new StringBuilder(4096);

    void Start()
    {
        if (logPanel) logPanel.SetActive(false);
        SafeSetLog("(F4) Press to verify media folders...");
    }

    void Update()
    {
        if (Input.GetKeyDown(toggleKey))
        {
            bool willShow = logPanel ? !logPanel.activeSelf : true;
            if (logPanel) logPanel.SetActive(willShow);
            if (willShow) StartStepByStepScan();
            else StopStepByStepScan();
        }
    }

    [ContextMenu("Verify Now (Step-by-step)")]
    private void StartStepByStepScan()
    {
        StopStepByStepScan();
        _scanCo = StartCoroutine(VerifyNowAsync());
    }

    private void StopStepByStepScan()
    {
        if (_scanCo != null) { StopCoroutine(_scanCo); _scanCo = null; }
    }

    private IEnumerator VerifyNowAsync()
    {
        // reset
        _sb.Length = 0;
        SafeSetLog("Preparing scan...");

        ResolvePaths(out string actPath, out string ambPath);
        activePathUsed  = actPath;
        ambientPathUsed = ambPath;

        _sb.AppendLine("== Media Verification (step by step) ==");
        _sb.AppendLine("Timestamp: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        _sb.AppendLine("Ambient Path: " + ambientPathUsed);
        _sb.AppendLine("Active  Path: " + activePathUsed);
        _sb.AppendLine("Allowed Exts: " + string.Join(", ", allowedExtensions));
        _sb.AppendLine("Active Prefix: \"" + activeRequiredPrefix + "\" (case-insensitive)");
        _sb.AppendLine();
        SafeSetLog(_sb.ToString());
        yield return null;

        // Gather file lists first
        var ambientFiles = SafeGetFiles(ambientPathUsed, allowedExtensions);
        var activeFiles  = SafeGetFiles(activePathUsed, allowedExtensions);

        // AMBIENT: step by step
        var ambientOk = new List<string>();
        var ambientIssues = new List<string>();
        yield return StartCoroutine(ScanFolderStepByStep(
            label: "Ambient",
            files: ambientFiles,
            requirePrefix: false,
            prefix: "",
            allowedExt: allowedExtensions,
            okOut: ambientOk,
            issuesOut: ambientIssues
        ));

        // ACTIVE: step by step
        var activeOk = new List<string>();
        var activeIssues = new List<string>();
        yield return StartCoroutine(ScanFolderStepByStep(
            label: "Active ",
            files: activeFiles,
            requirePrefix: true,
            prefix: activeRequiredPrefix,
            allowedExt: allowedExtensions,
            okOut: activeOk,
            issuesOut: activeIssues
        ));

        // Totals and status
        ambientCountOK = ambientOk.Count;
        activeCountOK  = activeOk.Count;

        var dupes = FindDuplicatesByName(activeOk.Concat(ambientOk));

        bool anyIssues = ambientIssues.Count > 0 || activeIssues.Count > 0 || dupes.Count > 0
                         || (expectedAmbientCount >= 0 && ambientCountOK < expectedAmbientCount)
                         || (expectedActiveCount  >= 0 && activeCountOK  < expectedActiveCount);

        scanOK = !anyIssues || !strict;

        // Final concise summary (no filenames)
        _sb.AppendLine();
        _sb.AppendLine("Summary");
        _sb.AppendLine(SummaryLine("Ambient", ambientCountOK, expectedAmbientCount));
        _sb.AppendLine(SummaryLine("Active ", activeCountOK,  expectedActiveCount));

        if (expectedAmbientCount >= 0 && ambientCountOK < expectedAmbientCount)
            _sb.AppendLine("  - Ambient missing: " + (expectedAmbientCount - ambientCountOK));
        if (expectedActiveCount  >= 0 && activeCountOK  < expectedActiveCount)
            _sb.AppendLine("  - Active missing: " + (expectedActiveCount - activeCountOK));

        if (dupes.Count > 0)
        {
            _sb.AppendLine("  - Duplicate filenames detected: " + dupes.Count);
        }

        _sb.AppendLine();
        _sb.AppendLine("RESULT: " + (scanOK ? "OK" : "PROBLEMS DETECTED"));

        string report = _sb.ToString();
        if (logFullReportToConsole) Debug.Log(report);
        else Debug.Log("[MediaFolderVerifier] Ambient=" + ambientCountOK + ", Active=" + activeCountOK + ", Issues=" + (anyIssues ? "YES" : "NO"));

        SafeSetLog(report);

        if (writeReportFile) TryWriteReport(report);

        _scanCo = null;
    }

    private IEnumerator ScanFolderStepByStep(
        string label,
        string[] files,
        bool requirePrefix,
        string prefix,
        string[] allowedExt,
        List<string> okOut,
        List<string> issuesOut)
    {
        _sb.AppendLine(label + " verification started...");
        SafeSetLog(_sb.ToString());
        yield return null;

        if (files == null || files.Length == 0)
        {
            issuesOut.Add("No files with allowed extensions in: " + (label.Trim() == "Active" ? activePathUsed : ambientPathUsed));
            _sb.AppendLine("  - No files found.");
            SafeSetLog(_sb.ToString());
            yield break;
        }

        for (int i = 0; i < files.Length; i++)
        {
            string path = files[i];
            string name = Path.GetFileName(path);
            string ext  = Path.GetExtension(path);

            bool extOk = allowedExt.Any(e => string.Equals(e, ext, StringComparison.OrdinalIgnoreCase));
            bool prefixOk = !requirePrefix || (!string.IsNullOrEmpty(prefix) && name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

            string prog = BuildProgressBar(i + 1, files.Length, progressBarWidth);

            if (!extOk)
            {
                issuesOut.Add("Bad extension: " + name + " (ext: " + ext + ")");
                _sb.AppendLine("Checking " + label + ": " + (i + 1) + "/" + files.Length + "  " + prog + "  " + name + "  WARN bad extension");
            }
            else if (!prefixOk)
            {
                issuesOut.Add("Naming rule failed: '" + name + "' does not start with '" + prefix + "'");
                _sb.AppendLine("Checking " + label + ": " + (i + 1) + "/" + files.Length + "  " + prog + "  " + name + "  WARN name rule");
            }
            else
            {
                okOut.Add(path);
                _sb.AppendLine("Checking " + label + ": " + (i + 1) + "/" + files.Length + "  " + prog + "  " + name + "  OK");
            }

            SafeSetLog(_sb.ToString());

            // Slow down the "animation" a bit so the operator can see each step
            float delay = Mathf.Max(0f, perFileDelaySeconds);
            if (delay > 0f) yield return new WaitForSecondsRealtime(delay);
            else yield return null;
        }

        if (okOut.Count == 0)
        {
            issuesOut.Add("No valid files passed the rules in: " + (label.Trim() == "Active" ? activePathUsed : ambientPathUsed));
            _sb.AppendLine("  - No valid files passed checks.");
            SafeSetLog(_sb.ToString());
        }

        _sb.AppendLine(label + " verification complete.");
        SafeSetLog(_sb.ToString());
        yield return null;
    }

    private string SummaryLine(string label, int found, int expected)
    {
        if (expected < 0)
            return label + ": " + found + " videos found";
        if (found == expected)
            return label + ": " + found + " videos found (expected " + expected + ") - OK";
        if (found < expected)
            return label + ": " + found + " videos found (expected " + expected + ") - MISSING " + (expected - found);
        return label + ": " + found + " videos found (expected " + expected + ") - EXTRA " + (found - expected);
    }

    private void ResolvePaths(out string active, out string ambient)
    {
        var cm = FindFirstObjectByType<ControllerMain>();
        if (cm != null)
        {
            active  = (!string.IsNullOrEmpty(cm.ActiveStreamPath)  && Directory.Exists(cm.ActiveStreamPath))  ? cm.ActiveStreamPath  : null;
            ambient = (!string.IsNullOrEmpty(cm.AmbientStreamPath) && Directory.Exists(cm.AmbientStreamPath)) ? cm.AmbientStreamPath : null;
        }
        else { active = ambient = null; }

        if (string.IsNullOrEmpty(active))
            active = Path.Combine(Application.streamingAssetsPath, streamingActiveSubfolder);
        if (string.IsNullOrEmpty(ambient))
            ambient = Path.Combine(Application.streamingAssetsPath, streamingAmbientSubfolder);
    }

    private string[] SafeGetFiles(string folder, string[] allowedExt)
    {
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return Array.Empty<string>();
        try
        {
            var list = new List<string>();
            foreach (var ext in allowedExt)
            {
                var pat1 = "*" + ext.ToLowerInvariant();
                var pat2 = "*" + ext.ToUpperInvariant();
                list.AddRange(Directory.GetFiles(folder, pat1));
                if (!string.Equals(pat1, pat2)) list.AddRange(Directory.GetFiles(folder, pat2));
            }
            return list.Distinct(StringComparer.OrdinalIgnoreCase)
                       .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
                       .ToArray();
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[MediaFolderVerifier] GetFiles failed: " + ex.Message);
            return Array.Empty<string>();
        }
    }

    private Dictionary<string, List<string>> FindDuplicatesByName(IEnumerable<string> paths)
    {
        var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in paths)
        {
            var k = Path.GetFileName(p);
            if (!map.TryGetValue(k, out var list)) map[k] = list = new List<string>();
            list.Add(p);
        }
        return map.Where(kv => kv.Value.Count > 1)
                  .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
    }

    private void TryWriteReport(string content)
    {
        string targetPath = null;
        try
        {
            string configDir = Path.GetDirectoryName(ControllerMain.PathInConfig("dummy.txt"));
            if (!string.IsNullOrEmpty(configDir) && Directory.Exists(configDir))
                targetPath = Path.Combine(configDir, "MediaScanReport.txt");
        }
        catch { }

        if (string.IsNullOrEmpty(targetPath))
            targetPath = Path.Combine(Application.persistentDataPath, "MediaScanReport.txt");

        try
        {
            File.WriteAllText(targetPath, content);
            Debug.Log("[MediaFolderVerifier] Report written: " + targetPath);
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[MediaFolderVerifier] Failed to write report: " + ex.Message);
        }
    }

    private string BuildProgressBar(int current, int total, int width)
    {
        float pct = (total <= 0) ? 0f : (float)current / (float)total;
        int filled = Mathf.Clamp(Mathf.RoundToInt(pct * width), 0, width);
        return "[" + new string('#', filled) + new string('.', width - filled) + "]";
    }

    private void SafeSetLog(string text)
    {
        if (logText) logText.text = text;
        else Debug.Log(text);
    }
}
