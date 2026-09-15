using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace carton.GUI.Tests.Localization;

/// <summary>
/// Guards the localization dictionaries. Without this, deleting a key from ONE dictionary
/// while a view still references it fails silently: DynamicResource resolves to null at
/// runtime (exactly how "Groups.Label.Current" disappeared from the English UI in 8dd8cbc),
/// and nothing in the build or the test suite notices.
/// </summary>
public class LocalizationKeyTests
{
    private static readonly Regex KeyRegex = new("x:Key=\"([^\"]+)\"", RegexOptions.Compiled);
    // Localization keys are dotted PascalCase (Groups.Label.Current, Navigation.Groups, ...);
    // the filter keeps theme/brush keys (CartonControlBackgroundChromeMediumLowBrush) out.
    private static readonly Regex DynamicResourceRegex = new(
        "\\{DynamicResource\\s+([A-Za-z][A-Za-z0-9_]*(?:\\.[A-Za-z0-9_]+)+)\\s*\\}",
        RegexOptions.Compiled);

    [Fact]
    public void BothDictionariesDefineTheSameKeys()
    {
        var english = Keys(Path.Combine("src", "carton.GUI", "Resources", "Localization", "Strings.en.axaml"));
        var chinese = Keys(Path.Combine("src", "carton.GUI", "Resources", "Localization", "Strings.zh-Hans.axaml"));

        var missingInEnglish = chinese.Except(english).OrderBy(k => k).ToList();
        var missingInChinese = english.Except(chinese).OrderBy(k => k).ToList();

        Assert.True(
            missingInEnglish.Count == 0 && missingInChinese.Count == 0,
            $"Localization keys differ between Strings.en.axaml and Strings.zh-Hans.axaml.\n" +
            $"Only in zh-Hans (add to en): {string.Join(", ", missingInEnglish)}\n" +
            $"Only in en (add to zh-Hans): {string.Join(", ", missingInChinese)}");
    }

    [Fact]
    public void EveryReferencedLocalizationKeyExists()
    {
        var english = Keys(Path.Combine("src", "carton.GUI", "Resources", "Localization", "Strings.en.axaml"));
        var chinese = Keys(Path.Combine("src", "carton.GUI", "Resources", "Localization", "Strings.zh-Hans.axaml"));

        var referenced = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var file in AxamlFiles())
        {
            foreach (Match match in DynamicResourceRegex.Matches(File.ReadAllText(file)))
            {
                referenced.Add(match.Groups[1].Value);
            }
        }

        // The dotted pattern above is what separates localization keys (Groups.Label.Current)
        // from theme/brush resources (CartonControlBackgroundChromeMediumLowBrush), so every
        // match collected here is expected to be localized.
        var unknown = referenced
            .Where(key => !english.Contains(key) || !chinese.Contains(key))
            .ToList();

        Assert.True(
            unknown.Count == 0,
            "Referenced localization keys missing from a dictionary:\n" +
            string.Join("\n", unknown.Select(k =>
                $"  {k}: en={(english.Contains(k) ? "yes" : "NO")} zh-Hans={(chinese.Contains(k) ? "yes" : "NO")}")));
    }

    /// <summary>
    /// Keys that are intentionally kept although nothing references them yet (a localized string
    /// added ahead of the UI that will use it). Keep this list short and explain each entry.
    /// </summary>
    private static readonly string[] AllowedUnusedKeys = [];

    [Fact]
    public void NoUnusedKeys()
    {
        var defined = Keys(Path.Combine("src", "carton.GUI", "Resources", "Localization", "Strings.en.axaml"));
        var source = string.Join('\n', SourceFiles().Select(File.ReadAllText));

        var unused = defined
            .Where(key => !AllowedUnusedKeys.Contains(key) && !source.Contains(key, StringComparison.Ordinal))
            .OrderBy(key => key)
            .ToList();

        Assert.True(
            unused.Count == 0,
            "Localization keys defined but never referenced (delete them, or list them in " +
            nameof(AllowedUnusedKeys) + "); note keys are looked up as literals, including via " +
            "GetString(\"key\", fallback):\n  " + string.Join("\n  ", unused));
    }

    private static HashSet<string> Keys(string relativePath)
    {
        var path = Path.Combine(RepositoryRoot(), relativePath);
        Assert.True(File.Exists(path), $"Not found: {path}");
        return KeyRegex.Matches(File.ReadAllText(path))
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static IEnumerable<string> AxamlFiles()
    {
        return Directory.EnumerateFiles(Path.Combine(RepositoryRoot(), "src", "carton.GUI"), "*.axaml", SearchOption.AllDirectories)
            .Where(IsSourceFile);
    }

    /// <summary>Every file that can reference a localization key (C# literals and axaml).</summary>
    private static IEnumerable<string> SourceFiles()
    {
        var root = Path.Combine(RepositoryRoot(), "src");
        return Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                        || path.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase))
            .Where(IsSourceFile);
    }

    private static bool IsSourceFile(string path)
    {
        var sep = Path.DirectorySeparatorChar;
        return !path.Contains($"{sep}obj{sep}")
            && !path.Contains($"{sep}bin{sep}")
            && !path.Contains($"{sep}artifacts{sep}")
            && !path.EndsWith("Strings.en.axaml", StringComparison.OrdinalIgnoreCase)
            && !path.EndsWith("Strings.zh-Hans.axaml", StringComparison.OrdinalIgnoreCase);
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "src", "carton.GUI", "carton.GUI.csproj")))
            {
                return directory.FullName;
            }

            directory = directory.Parent!;
        }

        throw new InvalidOperationException($"Could not locate the repository root above {AppContext.BaseDirectory}");
    }
}
