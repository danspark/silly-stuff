using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using Yaspire;

try
{
    return Run(args);
}
catch (YaspireException e)
{
    Console.Error.WriteLine(e.Message);
    return 1;
}

static int Run(string[] args)
{
    var (file, forwarded) = Split(args);
    if (forwarded is [] or ["-h" or "--help" or "-?"])
    {
        Console.WriteLine("""
            .NET Yaspire: Aspire AppHosts in YAML.

            Usage: yaspire [-f|--file <apphost.yaml or directory>] <aspire command> [options]

            Without -f, yaspire finds apphost.yaml or apphost.yml the way the Aspire CLI finds AppHosts,
            generates a C# AppHost from it and passes everything else to the Aspire CLI.

            """);
        return Aspire([.. forwarded, "--help"]);
    }

    // Only arguments before `--` belong to the Aspire CLI; the rest go to the AppHost.
    var separator = forwarded.IndexOf("--");
    var options = forwarded[..(separator < 0 ? forwarded.Count : separator)];
    if (options.FindIndex(o => o == "--apphost" || o.StartsWith("--apphost=", StringComparison.Ordinal)) is var apphost and >= 0)
    {
        var inline = options[apphost] != "--apphost";
        var target = inline ? options[apphost]["--apphost=".Length..] : options.ElementAtOrDefault(apphost + 1);
        if (target is null || !IsYaml(target))
        {
            return Aspire(forwarded);
        }

        file = target;
        forwarded.RemoveRange(apphost, inline ? 1 : 2);
        options.RemoveRange(apphost, inline ? 1 : 2);
    }

    if (options.Intersect(["-h", "--help", "-?"]).Any() || !AspireOutput([.. options, "--help"]).Contains("--apphost"))
    {
        return Aspire(forwarded);
    }

    if (Resolve(file, options.Contains("--non-interactive")) is not { } yamlPath)
    {
        return Aspire(forwarded);
    }

    var code = Generator.Generate(File.ReadAllText(yamlPath), Path.GetRelativePath(Environment.CurrentDirectory, yamlPath), DefaultSdkVersion);
    var generated = Write(yamlPath, code);
    forwarded.InsertRange(options.Count, ["--apphost", generated]);
    var exitCode = Aspire(forwarded);

    // Commands like `aspire add` and `aspire update` edit the AppHost's directives; keep those edits.
    if (File.Exists(generated) && File.ReadAllText(generated) is var current && current != code)
    {
        var relativePath = Path.GetRelativePath(Environment.CurrentDirectory, yamlPath);
        var (updated, manual) = WriteBack.Apply(yamlPath, code, current);
        if (updated.Count > 0)
        {
            Console.Error.WriteLine($"Updated {relativePath}: {string.Join(", ", updated)}");
        }

        if (manual.Count > 0)
        {
            Console.Error.WriteLine($"Add these to {relativePath} by hand: {string.Join(", ", manual)}");
        }
    }

    return exitCode;
}

static (string? File, List<string> Forwarded) Split(string[] args)
{
    string? file = null;
    var index = 0;
    for (; index < args.Length; index++)
    {
        if (args[index] is "-f" or "--file" && index + 1 < args.Length)
        {
            file = args[++index];
        }
        else if (args[index].StartsWith("--file=", StringComparison.Ordinal))
        {
            file = args[index]["--file=".Length..];
        }
        else
        {
            break;
        }
    }

    return (file, args[index..].ToList());
}

static bool IsYaml(string path) => Path.GetExtension(path).ToLowerInvariant() is ".yaml" or ".yml";

// Mirrors the Aspire CLI: an explicit file or directory, else the nearest aspire.config.json AppHost,
// else a scan of the working directory. Returns null when the Aspire CLI should resolve the AppHost itself.
static string? Resolve(string? file, bool nonInteractive)
{
    if (file is not null && File.Exists(file))
    {
        return Path.GetFullPath(file);
    }

    if (file is not null && !Directory.Exists(file))
    {
        throw new YaspireException($"'{file}' does not exist.");
    }

    if (file is null && ConfiguredAppHost() is { } configured)
    {
        return IsYaml(configured) ? configured : null;
    }

    var directory = Path.GetFullPath(file ?? ".");
    return Discovery.Find(directory, ambient: file is null) switch
    {
        [] when file is null => null,
        [] => throw new YaspireException($"No apphost.yaml found below '{directory}'."),
        [var only] => only,
        var candidates => Select(candidates, nonInteractive),
    };
}

static string? ConfiguredAppHost()
{
    for (var directory = new DirectoryInfo(Environment.CurrentDirectory); directory is not null; directory = directory.Parent)
    {
        var config = Path.Combine(directory.FullName, "aspire.config.json");
        try
        {
            using var json = JsonDocument.Parse(File.Exists(config) ? File.ReadAllText(config) : "{}", new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (json.RootElement is { ValueKind: JsonValueKind.Object } root
                && root.TryGetProperty("appHost", out var appHost) && appHost.ValueKind is JsonValueKind.Object
                && appHost.TryGetProperty("path", out var path) && path.ValueKind is JsonValueKind.String)
            {
                var appHostPath = Path.GetFullPath(path.GetString()!, directory.FullName);
                return File.Exists(appHostPath) || Directory.Exists(appHostPath) ? appHostPath : null;
            }
        }
        catch (JsonException)
        {
            return null;
        }
    }

    return null;
}

static string Select(List<string> candidates, bool nonInteractive)
{
    var list = string.Join(Environment.NewLine, candidates.Select((path, i) => $"  {i + 1}. {Path.GetRelativePath(Environment.CurrentDirectory, path)}"));
    if (nonInteractive || Console.IsInputRedirected)
    {
        throw new YaspireException($"Multiple AppHosts found; pick one with -f:{Environment.NewLine}{list}");
    }

    Console.Error.WriteLine($"Select an AppHost to use:{Environment.NewLine}{list}");
    while (true)
    {
        Console.Error.Write("> ");
        var answer = Console.ReadLine() ?? throw new YaspireException("No AppHost selected.");
        if (int.TryParse(answer, out var choice) && choice >= 1 && choice <= candidates.Count)
        {
            return candidates[choice - 1];
        }
    }
}

// Generated files live in .yaspire/<yaml file name>/ beside the YAML: apphost.cs plus an aspire.config.json
// that stops the Aspire CLI from recording the generated AppHost in the user's own configuration.
static string Write(string yamlPath, string code)
{
    var root = Path.Combine(Path.GetDirectoryName(yamlPath)!, ".yaspire");
    var directory = Path.Combine(root, Path.GetFileName(yamlPath));
    Directory.CreateDirectory(directory);
    WriteIfChanged(Path.Combine(root, ".gitignore"), "*\n");
    WriteIfChanged(Path.Combine(directory, "aspire.config.json"), """{ "appHost": { "path": "apphost.cs" } }""" + "\n");
    var apphost = Path.Combine(directory, "apphost.cs");
    WriteIfChanged(apphost, code);
    return apphost;
}

static void WriteIfChanged(string path, string content)
{
    if (!File.Exists(path) || File.ReadAllText(path) != content)
    {
        var temporary = $"{path}.{Environment.ProcessId}.tmp";
        File.WriteAllText(temporary, content);
        File.Move(temporary, path, overwrite: true);
    }
}

static string DefaultSdkVersion() => AspireOutput(["--version"]).Trim().Split('+')[0];

static int Aspire(IEnumerable<string> args)
{
    Console.CancelKeyPress += (_, e) => e.Cancel = true;
    using var process = Start(args, redirect: false);
    process.WaitForExit();
    return process.ExitCode;
}

static string AspireOutput(IEnumerable<string> args)
{
    using var process = Start(args, redirect: true);
    var output = process.StandardOutput.ReadToEnd();
    process.WaitForExit();
    return output;
}

static Process Start(IEnumerable<string> args, bool redirect)
{
    try
    {
        return Process.Start(new ProcessStartInfo("aspire", args) { RedirectStandardOutput = redirect, RedirectStandardError = redirect })!;
    }
    catch (Win32Exception)
    {
        throw new YaspireException("The Aspire CLI ('aspire') was not found on PATH. Install it from https://aspire.dev.");
    }
}
