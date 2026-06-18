using System;
using System.IO;
using UnityEngine;

/// <summary>
/// Captures all Debug.Log / Warning / Error and writes them to debug_log.txt
/// next to the Assets folder. File is opened in AutoInitialize (before any scene load)
/// so logs are captured even if the game crashes before Awake runs.
/// </summary>
public class DebugLogger : MonoBehaviour
{
    private static StreamWriter writer;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void AutoInitialize()
    {
        // Close any writer left over from a previous Play session (Enter Play Mode without Domain Reload)
        if (writer != null)
        {
            Application.logMessageReceived -= OnLog;
            Application.quitting           -= OnQuit;
            writer.Close();
            writer = null;
        }

        string logPath = RuntimePaths.DebugLogPath();

        var fs = new FileStream(logPath, FileMode.Create, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
        writer = new StreamWriter(fs) { AutoFlush = true };
        writer.WriteLine($"=== Session started: {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
        writer.WriteLine();

        Application.logMessageReceived += OnLog;
        Application.quitting += OnQuit;

        var go = new GameObject("[DebugLogger]");
        DontDestroyOnLoad(go);
        go.AddComponent<DebugLogger>();
    }

    private static bool IsCardLoadNoise(string message) =>
        message.StartsWith("Loaded: ", StringComparison.Ordinal) &&
        (message.EndsWith("[Pokemon]", StringComparison.Ordinal) ||
         message.EndsWith("[Trainer]", StringComparison.Ordinal));

    private static void OnLog(string message, string stackTrace, LogType type)
    {
        if (writer == null) return;
        if (type == LogType.Log && IsCardLoadNoise(message)) return;
        string prefix = type switch
        {
            LogType.Warning   => "[WARN]",
            LogType.Error     => "[ERROR]",
            LogType.Exception => "[EXCEPTION]",
            _                 => "[LOG]",
        };

        writer.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {prefix} {message}");

        if (type is LogType.Error or LogType.Exception)
            writer.WriteLine(stackTrace);
    }

    private static void OnQuit()
    {
        Application.logMessageReceived -= OnLog;
        Application.quitting           -= OnQuit;
        writer?.Close();
        writer = null;
    }

    private void OnDestroy()
    {
        // Guard: only close if this is the last instance (scene unload, not app quit)
        if (this != null && Application.isPlaying) return;
        OnQuit();
    }
}
