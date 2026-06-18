using System;
using System.IO;
using UnityEngine;

/// <summary>
/// Central runtime path resolver for files intentionally stored outside Unity assets.
/// In builds this is the folder containing the executable on Windows/Linux, and the
/// folder containing the .app bundle on macOS. In the Editor it is the project root.
/// </summary>
public static class RuntimePaths
{
    public static string GameRoot()
    {
#if UNITY_STANDALONE_OSX && !UNITY_EDITOR
        string appParent = TryGetMacAppParent();
        if (!string.IsNullOrEmpty(appParent))
            return appParent;
#endif
        return Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
    }

    public static string CardsRoot() => Path.Combine(GameRoot(), "Cards");

    public static string DecksRoot() => Path.Combine(GameRoot(), "Decks");

    public static string LogsRoot() => Path.Combine(GameRoot(), "Logs Export");

    public static string MlLogsRoot() => Path.Combine(LogsRoot(), "ML");

    public static string BenchmarkLogsRoot() => Path.Combine(LogsRoot(), "Benchmark");

    public static string ClientIdPath() => Path.Combine(GameRoot(), "client_id.txt");

    public static string DebugLogPath() => Path.Combine(GameRoot(), "debug_log.txt");

    public static string ConfigPath(string fileName)
    {
        string external = Path.Combine(GameRoot(), fileName);
        if (!Application.isEditor || File.Exists(external))
            return external;

        // In the Editor, keep using StreamingAssets so source-controlled defaults remain easy to edit.
        return Path.Combine(Application.streamingAssetsPath, fileName);
    }

#if UNITY_STANDALONE_OSX && !UNITY_EDITOR
    private static string TryGetMacAppParent()
    {
        DirectoryInfo current = new DirectoryInfo(Path.GetFullPath(Application.dataPath));
        while (current != null)
        {
            if (current.Name.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
                return current.Parent?.FullName;
            current = current.Parent;
        }
        return null;
    }
#endif
}
