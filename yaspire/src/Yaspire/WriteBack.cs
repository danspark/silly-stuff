using YamlDotNet.Core.Events;
using YamlDotNet.RepresentationModel;

namespace Yaspire;

/// <summary>
/// Copies the SDK and package directives that Aspire CLI commands such as <c>aspire add</c> and
/// <c>aspire update</c> write into the generated AppHost back into the YAML, editing it in place.
/// </summary>
static class WriteBack
{
    /// <returns>The entries written to the YAML, and the entries the user must write because their spot can't be edited safely.</returns>
    public static (List<string> Updated, List<string> Manual) Apply(string yamlPath, string generated, string current)
    {
        var (oldSdk, oldPackages) = Directives(generated);
        var (sdk, packages) = Directives(current);
        var yaml = File.ReadAllText(yamlPath);
        var root = Generator.Load(yaml, yamlPath);
        var top = root is { Style: not MappingStyle.Flow, Children.Count: > 0 } ? root.Children.Keys.First().Start.Index : (long?)null;
        var edits = new List<(long Start, long End, string Text)>();
        var updated = new List<string>();
        var manual = new List<string>();

        if (sdk is not null && sdk != oldSdk)
        {
            var aspire = Generator.Child(root, "aspire");
            if (aspire is YamlScalarNode scalar && IsPlain(yaml, scalar))
            {
                edits.Add((scalar.Start.Index, scalar.End.Index, sdk));
            }
            else if (aspire is null && top is { } index)
            {
                edits.Add((index, index, $"aspire: {sdk}\n"));
            }

            (edits.Count > 0 ? updated : manual).Add($"aspire: {sdk}");
        }

        var node = Generator.Child(root, "packages");
        var items = Generator.Items(node).OfType<YamlScalarNode>().ToList();
        var added = new List<string>();
        foreach (var (name, version) in packages.Where(p => oldPackages.GetValueOrDefault(p.Key) != p.Value))
        {
            var item = items.FirstOrDefault(i => i.Value!.Split('@')[0].Equals(name, StringComparison.OrdinalIgnoreCase));
            var followsSdk = item?.Value!.Contains('@') != true && name.StartsWith("Aspire.", StringComparison.OrdinalIgnoreCase) && version == sdk;
            var text = followsSdk ? name : $"{name}@{version}";
            if (item is null)
            {
                added.Add(text);
            }
            else if (item.Value != text)
            {
                var editable = IsPlain(yaml, item);
                if (editable)
                {
                    edits.Add((item.Start.Index, item.End.Index, text));
                }

                (editable ? updated : manual).Add(text);
            }
        }

        if (added.Count > 0)
        {
            var insertion = node switch
            {
                YamlSequenceNode { Style: SequenceStyle.Block, Children: [.., YamlScalarNode last] } when IsPlain(yaml, last) => AfterLastItem(yaml, last, added),
                YamlSequenceNode { Style: SequenceStyle.Flow, Children: [.., YamlScalarNode last] } when IsPlain(yaml, last) => (last.End.Index, string.Concat(added.Select(a => $", {a}"))),
                YamlSequenceNode { Style: SequenceStyle.Flow, Children: [] } empty when yaml[(int)empty.Start.Index] == '[' => (empty.Start.Index + 1, string.Join(", ", added)),
                null when top is { } index => (index, $"packages:\n{string.Concat(added.Select(a => $"  - {a}\n"))}"),
                _ => ((long, string)?)null,
            };
            if (insertion is var (at, text))
            {
                edits.Add((at, at, text));
            }

            (insertion is null ? manual : updated).AddRange(added);
        }

        foreach (var (start, end, text) in edits.OrderByDescending(e => e.Start))
        {
            yaml = string.Concat(yaml.AsSpan(0, (int)start), text, yaml.AsSpan((int)end));
        }

        if (edits.Count > 0)
        {
            File.WriteAllText(yamlPath, yaml);
        }

        return (updated, manual);
    }

    // Only scalars written as their plain or quoted value can be replaced; anchors and aliases point elsewhere.
    static bool IsPlain(string yaml, YamlScalarNode node) =>
        yaml[(int)node.Start.Index..(int)node.End.Index].Trim() is var text
        && (text == node.Value || text == $"\"{node.Value}\"" || text == $"'{node.Value}'");

    // Repeats the last item's "  - " prefix on a new line.
    static (long, string) AfterLastItem(string yaml, YamlScalarNode last, List<string> added)
    {
        var lineStart = yaml.LastIndexOf('\n', (int)last.Start.Index) + 1;
        var prefix = yaml[lineStart..(int)last.Start.Index];
        return (last.End.Index, string.Concat(added.Select(a => $"\n{prefix}{a}")));
    }

    static (string? Sdk, Dictionary<string, string> Packages) Directives(string code)
    {
        string? sdk = null;
        var packages = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in code.Split('\n').Select(l => l.Trim()))
        {
            switch (line.Split(' ', 2))
            {
                case ["#:sdk", var reference] when reference.Split('@') is ["Aspire.AppHost.Sdk", var version]:
                    sdk = version;
                    break;
                case ["#:package", var reference] when reference.Split('@') is [var name, var version]:
                    packages[name] = version;
                    break;
            }
        }

        return (sdk, packages);
    }
}
