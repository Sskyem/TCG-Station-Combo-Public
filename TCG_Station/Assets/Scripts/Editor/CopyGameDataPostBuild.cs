using System.IO;
using UnityEditor;
using UnityEditor.Callbacks;
using UnityEngine;

/// <summary>
/// Ships the project-root <c>Cards/</c> and <c>Decks/</c> folders next to a build automatically.
///
/// Why this exists: the card/deck JSON lives at the PROJECT ROOT (outside <c>Assets/</c>), so Unity
/// does not include it in a build. At runtime <see cref="JsonLoader"/> reads them from
/// <see cref="RuntimePaths.GameRoot"/>. Without this step every build would start with zero cards
/// and zero decks and had to be patched by hand.
///
/// This post-build hook copies both folders into the exact directory the runtime treats as game
/// root, per platform:
///   - Windows / Linux: the folder containing the executable           (GetParent(&lt;Product&gt;_Data))
///   - macOS:           the folder containing the &lt;App&gt;.app bundle
///
/// Only data is copied (JSON + any art the colleague drops in). <c>.meta</c> and <c>.DS_Store</c>
/// files are skipped. The destination is rebuilt cleanly on every build so stale cards never linger.
///
/// Keeping data outside the macOS .app also keeps logs and ML inputs visible to external tooling.
/// </summary>
public static class CopyGameDataPostBuild
{
    private static readonly string[] DataFolders = { "Cards", "Decks" };
    private static readonly string[] ConfigFiles = { "GameRulesConfig.json", "BenchmarkConfig.json" };
    private static readonly string[] LocalConfigExamples =
    {
        "GameRulesConfig.local.example.json",
        "LogUploader.local.example.json"
    };

    [PostProcessBuild(1)]
    public static void OnPostProcessBuild(BuildTarget target, string pathToBuiltProject)
    {
        // In the Editor, Application.dataPath == <project>/Assets, so the project root is its parent.
        string projectRoot = Directory.GetParent(Application.dataPath).FullName;

        string gameRoot = ResolveRuntimeGameRoot(target, pathToBuiltProject);
        if (gameRoot == null)
        {
            Debug.LogWarning($"[CopyGameDataPostBuild] Unsupported build target '{target}'. " +
                             "Cards/ and Decks/ were NOT copied — copy them next to the build manually.");
            return;
        }

        foreach (string folder in DataFolders)
        {
            string src = Path.Combine(projectRoot, folder);
            string dst = Path.Combine(gameRoot, folder);
            CopyDirectory(src, dst);
        }

        foreach (string fileName in ConfigFiles)
        {
            string src = Path.Combine(Application.streamingAssetsPath, fileName);
            string dst = Path.Combine(gameRoot, fileName);
            CopyFile(src, dst);
        }

        foreach (string fileName in LocalConfigExamples)
        {
            string src = Path.Combine(projectRoot, fileName);
            string dst = Path.Combine(gameRoot, fileName);
            CopyFile(src, dst);
        }

        Debug.Log(
            $"[CopyGameDataPostBuild] Copied {string.Join(", ", DataFolders)}, " +
            $"{string.Join(", ", ConfigFiles)}, and local config examples into: {gameRoot}");
    }

    /// <summary>
    /// The folder that the runtime resolves as <see cref="RuntimePaths.GameRoot"/> for the given
    /// target — i.e. where <see cref="JsonLoader"/> will look for Cards/ and Decks/.
    /// </summary>
    private static string ResolveRuntimeGameRoot(BuildTarget target, string pathToBuiltProject)
    {
        switch (target)
        {
            case BuildTarget.StandaloneWindows:
            case BuildTarget.StandaloneWindows64:
            case BuildTarget.StandaloneLinux64:
                // pathToBuiltProject is the executable; runtime dataPath is <dir>/<Product>_Data,
                // whose parent is the executable's directory.
                return Path.GetDirectoryName(pathToBuiltProject);

            case BuildTarget.StandaloneOSX:
                // pathToBuiltProject is the .app bundle; RuntimePaths.GameRoot() resolves to the
                // directory containing that bundle, not to any folder inside Contents/.
                return Path.GetDirectoryName(pathToBuiltProject);

            default:
                return null;
        }
    }

    private static void CopyDirectory(string src, string dst)
    {
        if (!Directory.Exists(src))
        {
            Debug.LogWarning($"[CopyGameDataPostBuild] Source folder missing, skipped: {src}");
            return;
        }

        // Clean copy: drop any previous version so removed/renamed files don't linger in the build.
        if (Directory.Exists(dst))
            Directory.Delete(dst, recursive: true);
        Directory.CreateDirectory(dst);

        foreach (string dir in Directory.GetDirectories(src, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(dst, GetRelativePath(src, dir)));

        foreach (string file in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
        {
            string name = Path.GetFileName(file);
            if (name.EndsWith(".meta") || name == ".DS_Store")
                continue; // Unity meta / macOS junk has no place next to a build.

            string destFile = Path.Combine(dst, GetRelativePath(src, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destFile));
            File.Copy(file, destFile, overwrite: true);
        }
    }

    private static void CopyFile(string src, string dst)
    {
        if (!File.Exists(src))
        {
            Debug.LogWarning($"[CopyGameDataPostBuild] Source config missing, skipped: {src}");
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(dst));
        File.Copy(src, dst, overwrite: true);
    }

    private static string GetRelativePath(string root, string fullPath)
    {
        // Trailing separator so the substring removal yields a clean relative path on all platforms.
        string prefix = root.EndsWith(Path.DirectorySeparatorChar.ToString())
            ? root
            : root + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(prefix) ? fullPath.Substring(prefix.Length) : Path.GetFileName(fullPath);
    }
}
