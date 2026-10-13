using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Yaspire;

sealed class YaspireException(string message) : Exception(message);

/// <summary>Translates an apphost.yaml document into a single-file C# AppHost.</summary>
static partial class Generator
{
    // Keys starting with one of these words are called as written; other camelCase keys get a `With` prefix.
    static readonly string[] Verbs = ["With", "Wait", "Publish", "Run", "As", "On", "Exclude", "Disable", "Configure", "Subscribe", "Clear"];

    static readonly string[] TopLevelKeys = ["aspire", "packages", "usings", "options", "resources", "code"];

    static readonly JsonSerializerOptions LiteralOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>A resource and its entries: the type first, then the calls applied to it.</summary>
    sealed record Resource(string Name, string Parent, YamlNode Node, List<(YamlNode Key, YamlNode? Value)> Entries);

    public static YamlMappingNode Load(string yaml, string yamlPath)
    {
        var stream = new YamlStream();
        try
        {
            stream.Load(new MergingParser(new Parser(new StringReader(yaml))));
        }
        catch (YamlException e)
        {
            throw Error(yamlPath, e.Start, e.InnerException?.Message ?? e.Message);
        }

        return stream.Documents is [{ RootNode: YamlMappingNode root }]
            ? root
            : throw new YaspireException($"{yamlPath}: error: expected a single YAML document with a mapping at its root.");
    }

    public static string Generate(string yaml, string yamlPath, Func<string> defaultSdkVersion)
    {
        var root = Load(yaml, yamlPath);

        // Like Compose, top-level `x-` keys are free-form extension fields, handy for YAML anchors.
        if (root.Children.Keys.FirstOrDefault(k => !TopLevelKeys.Contains(Text(k)) && !Text(k).StartsWith("x-", StringComparison.Ordinal)) is { } unknown)
        {
            throw Error(yamlPath, unknown.Start, $"unknown top-level key '{Text(unknown)}'; expected {string.Join(", ", TopLevelKeys)} or an x- extension.");
        }

        var resources = new List<Resource>();
        Collect(Child(root, "resources"), "builder", resources, yamlPath);
        var emitter = new Emitter(resources, yamlPath);

        var sdkVersion = Child(root, "aspire") is { } aspire ? Text(aspire) : defaultSdkVersion();
        var code = new StringBuilder();
        code.AppendLine($"#:sdk Aspire.AppHost.Sdk@{sdkVersion}");
        code.AppendLine("#:property AspireUseCliBundle=true");
        foreach (var package in Items(Child(root, "packages")))
        {
            code.AppendLine($"#:package {PackageReference(Text(package), sdkVersion, package.Start, yamlPath)}");
        }

        code.AppendLine();
        foreach (var import in Items(Child(root, "usings")))
        {
            code.AppendLine($"using {Text(import)};");
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(yamlPath))!;
        var options = new Dictionary<string, string>
        {
            ["Args"] = "args",
            ["ProjectDirectory"] = Literal(directory),
            ["DashboardApplicationName"] = Literal(Path.GetFileName(directory)),
        };
        foreach (var (key, value) in Child(root, "options") as YamlMappingNode ?? [])
        {
            options[Pascal(Text(key))] = emitter.Value(value);
        }

        code.AppendLine($"var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions {{ {string.Join(", ", options.Select(o => $"{o.Key} = {o.Value}"))} }});");

        foreach (var resource in emitter.InDependencyOrder())
        {
            code.AppendLine(emitter.Declaration(resource));
        }

        if (Child(root, "code") is YamlScalarNode raw)
        {
            code.AppendLine(Line(raw.Start.Line + (raw.Style is ScalarStyle.Literal or ScalarStyle.Folded ? 1 : 0), yamlPath));
            code.AppendLine(raw.Value);
        }

        foreach (var resource in resources)
        {
            code.Append(emitter.Configuration(resource));
        }

        code.AppendLine("#line default");
        code.AppendLine("builder.Build().Run();");
        return code.ToString();
    }

    /// <summary>Returns <c>Name@version</c>; unversioned Aspire packages follow the SDK version.</summary>
    static string PackageReference(string package, string sdkVersion, Mark mark, string yamlPath) =>
        package.Contains('@') ? package
        : package.StartsWith("Aspire.", StringComparison.OrdinalIgnoreCase) ? $"{package}@{sdkVersion}"
        : throw Error(yamlPath, mark, $"package '{package}' needs a version, e.g. '{package}@1.0.0'.");

    static void Collect(YamlNode? node, string parent, List<Resource> resources, string yamlPath)
    {
        if (node is null)
        {
            return;
        }

        if (node is not YamlMappingNode map)
        {
            throw Error(yamlPath, node.Start, "'resources' must be a mapping of resource names to definitions.");
        }

        foreach (var (key, value) in map)
        {
            var name = Text(key);
            if (name is "builder" or "args" || resources.Any(r => Identifier(r.Name) == Identifier(name)))
            {
                throw Error(yamlPath, key.Start, $"resource name '{name}' is reserved or already used.");
            }

            var entries = Entries(value, yamlPath);
            if (entries.Count > 0 && Text(entries[0].Key) == "resources")
            {
                throw Error(yamlPath, value.Start, $"resource '{name}' must start with its type, e.g. 'redis:' or 'project: ../Api/Api.csproj'.");
            }

            resources.Add(new Resource(name, parent, value, entries));
            foreach (var (_, children) in entries.Where(e => Text(e.Key) == "resources"))
            {
                Collect(children, name, resources, yamlPath);
            }
        }
    }

    // A definition is a type name, raw C#, a mapping (type first, then calls), or a sequence of
    // single-key mappings for when calls must repeat or keep a strict order.
    static List<(YamlNode Key, YamlNode? Value)> Entries(YamlNode node, string yamlPath) => node switch
    {
        YamlScalarNode raw when IsCode(raw) => [],
        YamlScalarNode type => [(type, null)],
        YamlMappingNode { Children.Count: > 0 } map => map.Select(kv => (kv.Key, (YamlNode?)kv.Value)).ToList(),
        YamlSequenceNode { Children.Count: > 0 } steps => steps.Select(step => step switch
        {
            YamlScalarNode call => (call, null),
            YamlMappingNode { Children.Count: 1 } call => (call.Children.First().Key, (YamlNode?)call.Children.First().Value),
            _ => throw Error(yamlPath, step.Start, "each step must be a method name or a single-key mapping."),
        }).ToList(),
        _ => throw Error(yamlPath, node.Start, "a resource must be a type name, a mapping or a sequence of calls."),
    };

    /// <summary>Renders YAML nodes as C#, resolving plain scalars that name a resource to its variable.</summary>
    sealed class Emitter(List<Resource> resources, string yamlPath)
    {
        readonly Dictionary<string, Resource> byName = resources.ToDictionary(r => r.Name);

        public List<Resource> InDependencyOrder()
        {
            var visited = new Dictionary<Resource, bool>();
            var sorted = new List<Resource>();
            foreach (var resource in resources)
            {
                Visit(resource, []);
            }

            return sorted;

            void Visit(Resource resource, List<string> path)
            {
                if (visited.TryGetValue(resource, out var done))
                {
                    if (!done)
                    {
                        throw Error(yamlPath, resource.Node.Start, $"circular reference: {string.Join(" -> ", path.SkipWhile(p => p != resource.Name))} -> {resource.Name}.");
                    }

                    return;
                }

                visited[resource] = false;
                var dependencies = resource.Entries is [] ? References(resource.Node) : References(resource.Entries[0].Value);
                foreach (var dependency in dependencies.Append(resource.Parent).Where(byName.ContainsKey).Select(n => byName[n]).Where(d => d != resource))
                {
                    Visit(dependency, [.. path, resource.Name]);
                }

                visited[resource] = true;
                sorted.Add(resource);
            }
        }

        IEnumerable<string> References(YamlNode? node) => node switch
        {
            YamlScalarNode raw when IsCode(raw) => VariablePattern().Matches(LiteralsAndComments().Replace(raw.Value!, m => m.Value[0] is '$' or '@' ? m.Value : " ")).Select(m => byName.Keys.FirstOrDefault(n => Identifier(n) == "@" + m.Value)).OfType<string>(),
            YamlScalarNode { Style: ScalarStyle.Plain, Value: { } value } when byName.ContainsKey(value) => [value],
            YamlSequenceNode items => items.SelectMany(References),
            YamlMappingNode map => map.Children.Values.SelectMany(References),
            _ => [],
        };

        public string Declaration(Resource resource)
        {
            var variable = Identifier(resource.Name);
            if (resource.Entries is [])
            {
                return $"{Line(resource.Node.Start.Line, yamlPath)}\nvar {variable} = {Raw((YamlScalarNode)resource.Node)};";
            }

            var (type, value) = resource.Entries[0];
            var arguments = value is null ? "" : Arguments(value, sequenceIsArguments: true);
            var separator = arguments.Length > 0 ? ", " : "";
            var parent = byName.ContainsKey(resource.Parent) ? Identifier(resource.Parent) : resource.Parent;
            return $"{Line(type.Start.Line, yamlPath)}\nvar {variable} = {parent}.Add{Pascal(Text(type))}({Literal(resource.Name)}{separator}{arguments});";
        }

        public string Configuration(Resource resource)
        {
            var calls = resource.Entries.Skip(1)
                .Where(e => Text(e.Key) != "resources")
                .SelectMany(e => Calls(Method(Text(e.Key)), e.Key, e.Value))
                .Select(call => $"{Line(call.Line, yamlPath)}\n    .{call.Code}")
                .ToList();
            return calls is [] ? "" : $"{Identifier(resource.Name)}\n{string.Join('\n', calls)};\n";
        }

        IEnumerable<(long Line, string Code)> Calls(string method, YamlNode key, YamlNode? value) => (value, Keyword(value)) switch
        {
            (null, _) or (_, "null" or "true") => [(key.Start.Line, $"{method}()")],
            (_, "false") => [],
            (YamlSequenceNode items, _) => items.Select(item => (item.Start.Line, $"{method}({Arguments(item, sequenceIsArguments: true)})")),
            (YamlMappingNode pairs, _) when !IsNamedArguments(pairs) => pairs.Select(kv => (kv.Key.Start.Line, $"{method}({Key(kv.Key)}, {Value(kv.Value)})")),
            _ => [(key.Start.Line, $"{method}({Arguments(value, sequenceIsArguments: false)})")],
        };

        string Arguments(YamlNode node, bool sequenceIsArguments) => node switch
        {
            _ when Keyword(node) is "null" => "",
            YamlSequenceNode items when sequenceIsArguments => string.Join(", ", items.Select(Value)),
            YamlMappingNode named when IsNamedArguments(named) => string.Join(", ", named.Select(kv => $"{Text(kv.Key)}: {Value(kv.Value)}")),
            _ => Value(node),
        };

        public string Value(YamlNode node) => node switch
        {
            YamlScalarNode raw when IsCode(raw) => Raw(raw),
            YamlScalarNode { Tag.IsEmpty: true, Style: ScalarStyle.Plain } plain => Keyword(plain) ?? Plain(plain.Value!),
            YamlScalarNode quoted => Literal(quoted.Value!),
            YamlSequenceNode items => $"[{string.Join(", ", items.Select(Value))}]",
            YamlMappingNode properties => $"new() {{ {string.Join(", ", properties.Select(kv => $"{Pascal(Text(kv.Key))} = {Value(kv.Value)}"))} }}",
            _ => throw Error(yamlPath, node.Start, "unsupported YAML node."),
        };

        string Plain(string value) => value switch
        {
            _ when byName.ContainsKey(value) => Identifier(value),
            _ when NumberPattern().IsMatch(value) => value,
            _ => Literal(value),
        };

        static string Key(YamlNode node) => node is YamlScalarNode raw && IsCode(raw) ? Raw(raw) : Literal(Text(node));

        // Plain camelCase keys are parameter names; anything else (ENV_VARS, quoted keys) is a name/value pair.
        static bool IsNamedArguments(YamlMappingNode map) =>
            map.Children.Keys.All(k => k is YamlScalarNode { Tag.IsEmpty: true, Style: ScalarStyle.Plain, Value: { } v } && ParameterPattern().IsMatch(v));
    }

    static bool IsCode(YamlScalarNode node) => node.Tag is { IsLocal: true, Value: "!cs" };

    // A line break keeps a trailing `//` comment in the C# from swallowing the code that follows it.
    static string Raw(YamlScalarNode node) => node.Value!.Contains("//") ? $"{node.Value}\n" : node.Value!;

    /// <summary>Returns <c>null</c>, <c>true</c> or <c>false</c> for the YAML core schema spellings of those plain scalars.</summary>
    static string? Keyword(YamlNode? node) => node is YamlScalarNode { Tag.IsEmpty: true, Style: ScalarStyle.Plain, Value: var value } ? value switch
    {
        "" or "~" or "null" or "Null" or "NULL" => "null",
        "true" or "True" or "TRUE" => "true",
        "false" or "False" or "FALSE" => "false",
        _ => null,
    } : null;

    static string Method(string key)
    {
        var method = Pascal(key);
        return char.IsUpper(key[0]) || Verbs.Contains(FirstWordPattern().Match(method).Value) ? method : $"With{method}";
    }

    public static YamlNode? Child(YamlMappingNode map, string key) => map.Children.TryGetValue(new YamlScalarNode(key), out var value) ? value : null;

    public static IEnumerable<YamlNode> Items(YamlNode? node) => node switch
    {
        null or YamlScalarNode { Value: "" } => [],
        YamlSequenceNode items => items,
        _ => [node],
    };

    static string Text(YamlNode node) => node is YamlScalarNode { Value: { } value } ? value : throw new YaspireException($"expected a scalar at line {node.Start.Line}.");

    static string Pascal(string name) => string.Concat(char.ToUpperInvariant(name[0]).ToString(), name[1..]);

    static string Identifier(string name) => "@" + IdentifierPattern().Replace(name, "_");

    static string Literal(string value) => JsonSerializer.Serialize(value, LiteralOptions);

    static string Line(long line, string yamlPath) => $"#line {line} {Literal(Path.GetFullPath(yamlPath))}";

    static YaspireException Error(string yamlPath, Mark mark, string message) => new($"{yamlPath}({mark.Line},{mark.Column}): error: {message}");

    [GeneratedRegex(@"^[-+]?(\d+(\.\d+)?|\.\d+)([eE][-+]?\d+)?$")]
    private static partial Regex NumberPattern();

    [GeneratedRegex("^[a-z][A-Za-z0-9]*$")]
    private static partial Regex ParameterPattern();

    [GeneratedRegex("^[A-Z][a-z0-9]*")]
    private static partial Regex FirstWordPattern();

    [GeneratedRegex(@"\W")]
    private static partial Regex IdentifierPattern();

    // Identifiers that are not member names, after string literals and comments are blanked out.
    [GeneratedRegex(@"(?<![.\w])[A-Za-z_]\w*")]
    private static partial Regex VariablePattern();

    // Interpolated strings come first so they match whole; callers keep them because their holes can reference resources.
    [GeneratedRegex(@"(?:\$@?|@\$)""(?:[^""\\]|\\.)*""|""(?:[^""\\]|\\.)*""|'(?:[^'\\]|\\.)*'|//.*|/\*[\s\S]*?\*/")]
    private static partial Regex LiteralsAndComments();
}
