using System;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

/// <summary>
/// Single on-disk config: StreamingAssets/GameConfig.json, split into top-level sections.
/// Each owner reads/writes only its own section:
///   - "gameRules" → GameRulesConfig
///   - "benchmark" → BenchmarkRunner
///
/// Writers use read-merge-write so saving one section never clobbers the sibling section.
/// Comments are not preserved on write (Newtonsoft re-serializes), but they are ignored on
/// read so a hand-annotated file still parses.
/// </summary>
public static class ConfigFile
{
    public const string FileName = "GameConfig.json";

    public const string SectionGameRules = "gameRules";
    public const string SectionBenchmark = "benchmark";

    public static string FullPath => Path.Combine(Application.streamingAssetsPath, FileName);

    /// <summary>
    /// Returns the named section as a JObject, or null if the file or section is absent.
    /// Backward compat: a legacy flat file (no known sections at the root) is treated as
    /// the section itself, so old GameRulesConfig.json / BenchmarkConfig.json contents still load.
    /// </summary>
    public static JObject ReadSection(string section)
    {
        JObject root = ReadRoot();
        if (root == null) return null;
        if (root[section] is JObject s) return s;

        // Legacy flat file (no sectioned structure): treat the whole root as this section.
        bool hasSections = root[SectionGameRules] != null || root[SectionBenchmark] != null;
        return hasSections ? null : root;
    }

    public static JObject ReadRoot()
    {
        try
        {
            if (!File.Exists(FullPath)) return null;
            return JObject.Parse(File.ReadAllText(FullPath), new JsonLoadSettings
            {
                CommentHandling = CommentHandling.Ignore
            });
        }
        catch (Exception e)
        {
            Debug.LogError($"[ConfigFile] Failed to read {FileName}: {e.Message}");
            return null;
        }
    }

    /// <summary>Writes the given section, preserving every sibling section already on disk.</summary>
    public static void WriteSection(string section, JObject content)
    {
        JObject root = ReadRoot() ?? new JObject();
        root[section] = content;
        try
        {
            File.WriteAllText(FullPath, root.ToString(Formatting.Indented));
        }
        catch (Exception e)
        {
            Debug.LogError($"[ConfigFile] Failed to write {FileName}: {e.Message}");
        }
    }
}
