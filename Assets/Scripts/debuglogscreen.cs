using System.Collections.Concurrent;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

[DisallowMultipleComponent]
public class OnScreenLogger : MonoBehaviour
{
    public static OnScreenLogger Instance { get; private set; }

    [SerializeField] private Text logText;
    [SerializeField, Range(1, 40)] private int maxLines = 15;
    [SerializeField] private bool showInfoMessages = true;
    [SerializeField] private bool showTimestamps = true;
    [SerializeField] private bool captureUnityDebugLog = true;

    private readonly Queue<string> lines = new Queue<string>();
    private static readonly ConcurrentQueue<PendingLine> pendingLines = new ConcurrentQueue<PendingLine>();

    private struct PendingLine
    {
        public readonly string Channel;
        public readonly string Message;
        public readonly LogType Type;

        public PendingLine(string channel, string message, LogType type)
        {
            Channel = channel;
            Message = message;
            Type = type;
        }
    }

    private void Awake()
    {
        if (Instance != null && Instance != this)
            Debug.LogWarning("[OnScreenLogger] Multiple logger instances exist. Latest instance will receive static writes.");

        Instance = this;

        // Attach this component directly to the UI Text object, or assign Log Text manually.
        if (logText == null)
            logText = GetComponent<Text>();

        if (logText == null)
            logText = GetComponentInChildren<Text>(true);

        if (logText == null)
        {
            Debug.LogError("[OnScreenLogger] No UI Text assigned. Add this script to the Text object or assign Log Text.");
            return;
        }

        logText.supportRichText = true;
        AppendFormattedLine(new PendingLine("LOGGER", "Ready. Waiting for Unity logs and server/client comms...", LogType.Log));
    }

    private void OnEnable()
    {
        Application.logMessageReceivedThreaded += HandleUnityLogThreaded;
    }

    private void OnDisable()
    {
        Application.logMessageReceivedThreaded -= HandleUnityLogThreaded;
    }

    private void OnDestroy()
    {
        if (Instance == this)
            Instance = null;
    }

    private void Update()
    {
        FlushPendingLines();
    }

    public static void WriteLine(string message)
    {
        Enqueue("APP", message, LogType.Log);
    }

    public static void WriteClientLine(string message)
    {
        Enqueue("CLIENT", message, LogType.Log);
    }

    public static void WriteServerLine(string message)
    {
        Enqueue("SERVER", message, LogType.Log);
    }

    public static void WriteTriggerLine(string message)
    {
        Enqueue("TRIGGER", message, LogType.Log);
    }

    public static void WriteWarningLine(string message)
    {
        Enqueue("WARN", message, LogType.Warning);
    }

    public static void WriteErrorLine(string message)
    {
        Enqueue("ERROR", message, LogType.Error);
    }

    private static void Enqueue(string channel, string message, LogType type)
    {
        pendingLines.Enqueue(new PendingLine(channel, message, type));
    }

    private void HandleUnityLogThreaded(string message, string stackTrace, LogType type)
    {
        if (!captureUnityDebugLog)
            return;

        if (!showInfoMessages && type == LogType.Log)
            return;

        pendingLines.Enqueue(new PendingLine("UNITY", message, type));
    }

    private void FlushPendingLines()
    {
        if (logText == null)
            return;

        bool changed = false;

        while (pendingLines.TryDequeue(out PendingLine line))
        {
            AppendFormattedLine(line, false);
            changed = true;
        }

        if (changed)
            logText.text = string.Join("\n", lines);
    }

    private void AppendFormattedLine(PendingLine line, bool updateText = true)
    {
        if (logText == null)
            return;

        string timestamp = showTimestamps ? $"[{Time.unscaledTime:F1}] " : "";
        string prefix = BuildPrefix(line.Channel, line.Type);
        string formatted = timestamp + prefix + EscapeRichText(line.Message);

        lines.Enqueue(formatted);
        while (lines.Count > maxLines)
            lines.Dequeue();

        if (updateText)
            logText.text = string.Join("\n", lines);
    }

    private static string BuildPrefix(string channel, LogType type)
    {
        if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert)
            return "<color=red>[E]</color> ";

        if (type == LogType.Warning)
            return "<color=yellow>[W]</color> ";

        switch (channel)
        {
            case "SERVER":
                return "<color=orange>[S]</color> ";
            case "CLIENT":
                return "<color=cyan>[C]</color> ";
            case "TRIGGER":
                return "<color=lime>[T]</color> ";
            case "LOGGER":
                return "<color=cyan>[Logger]</color> ";
            case "UNITY":
                return "<color=cyan>[U]</color> ";
            default:
                return "<color=white>[I]</color> ";
        }
    }

    private static string EscapeRichText(string value)
    {
        if (string.IsNullOrEmpty(value))
            return "";

        return value
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;");
    }
}
