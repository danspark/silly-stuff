using System.Diagnostics;

namespace Yaspire;

/// <summary>Finds apphost.yaml files the way the Aspire CLI finds AppHosts.</summary>
static class Discovery
{
    static readonly string[] FileNames = ["apphost.yaml", "apphost.yml"];

    // The Aspire CLI skips these directories (and these subpaths) when scanning for AppHosts.
    static readonly string[] Skipped =
    [
        "bin", "obj", "dist", "out", "target", "artifacts", "coverage",
        ".git", ".vs", ".idea",
        ".agents", ".claude", ".github/skills", ".opencode/skill",
        "node_modules", ".next", ".nuxt", ".cache", ".turbo", ".svelte-kit", ".parcel-cache", ".yarn", ".pnpm-store", "bower_components",
        "__pycache__", ".venv", "venv", ".tox", ".pytest_cache", ".mypy_cache", ".ruff_cache",
        ".gradle",
    ];

    /// <summary>
    /// Lists candidates below <paramref name="directory"/>. Ambient discovery honours .gitignore through git when
    /// available; an explicitly requested directory is walked with only the skip list applied.
    /// </summary>
    public static List<string> Find(string directory, bool ambient)
    {
        var files = (ambient ? GitFiles(directory) : null) ?? Walk(directory);
        return files
            .Where(file => FileNames.Contains(Path.GetFileName(file), StringComparer.OrdinalIgnoreCase) && File.Exists(file))
            .Where(file => !IsSkipped(Path.GetRelativePath(directory, Path.GetDirectoryName(file)!)))
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    static bool IsSkipped(string relativeDirectory)
    {
        var segments = $"/{relativeDirectory.Replace('\\', '/')}/";
        return Skipped.Any(skipped => segments.Contains($"/{skipped}/", StringComparison.OrdinalIgnoreCase));
    }

    static IEnumerable<string> Walk(string directory)
    {
        var options = new EnumerationOptions { IgnoreInaccessible = true };
        return Directory.EnumerateFiles(directory, "*", options).Concat(
            Directory.EnumerateDirectories(directory, "*", options)
                .Where(child => !IsSkipped(Path.GetFileName(child)))
                .SelectMany(Walk));
    }

    static IEnumerable<string>? GitFiles(string directory)
    {
        try
        {
            var git = new ProcessStartInfo("git", ["ls-files", "--cached", "--others", "--exclude-standard", "-z"])
            {
                WorkingDirectory = directory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var process = Process.Start(git)!;
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode == 0
                ? output.Split('\0', StringSplitOptions.RemoveEmptyEntries).Select(file => Path.GetFullPath(Path.Combine(directory, file)))
                : null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }
}
