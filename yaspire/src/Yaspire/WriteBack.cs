using YamlDotNet.Core.Events;
using YamlDotNet.RepresentationModel;

namespace Yaspire;

/// <summary>
/// Copies the SDK and package directives that Aspire CLI commands such as <c>aspire add</c> and
/// <c>aspire update</c> write into the generated AppHost back into the YAML, editing it in place.
/// </summary>
static class WriteBack
{
    public static IReadOnlyList<string> Apply(string yamlPath, string generated, string current)
    {
        var (oldSdk, oldPackages) = Directives(generated);
        var (sdk, packages) = Directives(current);
        var yaml = File.ReadAllText(yamlPath);
        var root = Generator.Load(yaml, yamlPath);
        var top = root.Children.Count > 0 ? root.Children.Keys.First().Start.Index : 0;
        var edits = new List<(long Start, long End, string Text)>();
        var changes = new List<string>();

        if (sdk is not null && sdk != oldSdk)
        {
            edits.Add(Generator.Child(root, "aspire") is { } aspire ? (aspire.Start.Index, aspire.End.Index, sdk) : (top, top, $"aspire: {sdk}\n"));
            changes.Add($"aspire {sdk}");
        }

        var node = Generator.Child(root, "packages");
        var items = (node as YamlSequenceNode)?.Children.OfType<YamlScalarNode>().ToList() ?? [];
        var added = new List<string>();
        foreach (var (name, version) in packages.Where(p => oldPackages.GetValueOrDefault(p.Key) != p.Value))
        {
            var item = items.FirstOrDefault(i => i.Value!.Split('@')[0].Equals(name, StringComparison.OrdinalIgnoreCase));
            var followsSdk = item?.Value!.Contains('@') != true && name.StartsWith("Aspire.", StringComparison.OrdinalIgnoreCase) && version == sdk;
            var text = followsSdk ? name : $"{name}@{version}";
            if (item is null)
            {
                added.Add(text);
                changes.Add(text);
            }
            else if (item.Value != text)
            {
                edits.Add((item.Start.Index, item.End.Index, text));
                changes.Add(text);
            }
        }

        if (added.Count > 0)
        {
            edits.Add(node switch
            {
                YamlSequenceNode { Style: SequenceStyle.Block, Children.Count: > 0 } block => AfterLastItem(yaml, block, added),
                YamlSequenceNode { Children: [.., var last] } => (last.End.Index, last.End.Index, string.Concat(added.Select(a => $", {a}"))),
                null => (top, top, $"packages:\n{string.Concat(added.Select(a => $"  - {a}\n"))}"),
                YamlSequenceNode empty => (empty.Start.Index, empty.End.Index, $"[{string.Join(", ", added)}]"),
                _ => (node.Start.Index, node.End.Index, $" [{string.Join(", ", added)}]"),
            });
        }

        foreach (var (start, end, text) in edits.OrderByDescending(e => e.Start))
        {
            yaml = string.Concat(yaml.AsSpan(0, (int)start), text, yaml.AsSpan((int)end));
        }

        if (edits.Count > 0)
        {
            File.WriteAllText(yamlPath, yaml);
        }

        return changes;
    }

    // Repeats the last item's "  - " prefix on a new line.
    static (long, long, string) AfterLastItem(string yaml, YamlSequenceNode block, List<string> added)
    {
        var last = block.Children[^1];
        var lineStart = yaml.LastIndexOf('\n', (int)last.Start.Index) + 1;
        var prefix = yaml[lineStart..(int)last.Start.Index];
        return (last.End.Index, last.End.Index, string.Concat(added.Select(a => $"\n{prefix}{a}")));
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
