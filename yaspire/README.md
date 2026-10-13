# .NET Yaspire

Write your [Aspire](https://aspire.dev) AppHost in YAML.

```yaml
# apphost.yaml
packages:
  - Aspire.Hosting.Redis

resources:
  cache: redis

  api:
    project: Api/Api.csproj
    reference: cache
    waitFor: cache
    externalHttpEndpoints: true
```

```sh
yaspire run
```

## Install

.NET Yaspire is a .NET tool. It needs the [Aspire CLI](https://aspire.dev/get-started/install-cli/) on your `PATH` and the .NET 10 SDK or later.

```sh
dotnet tool install --global yaspire   # then: yaspire run
```

As a local tool, it runs through `dotnet`:

```sh
dotnet new tool-manifest
dotnet tool install yaspire            # then: dotnet yaspire run
```

## Usage

```text
yaspire [-f|--file <apphost.yaml or directory>] <aspire command> [options]
```

Everything after the yaspire options is an Aspire CLI command line: `yaspire run`, `yaspire start`, `yaspire publish -o out`, `yaspire deploy`, `yaspire add redis`, `yaspire logs api` and so on. For commands that work on an AppHost, yaspire:

1. finds the YAML file;
2. generates a single-file C# AppHost from it in `.yaspire/<file name>/apphost.cs`, next to the YAML;
3. runs `aspire <your command> --apphost .yaspire/<file name>/apphost.cs`.

Other commands, such as `yaspire new` or `yaspire ps`, go to the Aspire CLI unchanged. When yaspire finds no YAML file, the command also goes to the Aspire CLI unchanged.

Without `-f`, yaspire looks for the YAML file the same way the Aspire CLI looks for AppHosts. It reads `appHost.path` from the nearest `aspire.config.json`, then searches the current directory for `apphost.yaml` or `apphost.yml`. Inside a Git repository the search honours `.gitignore`, and it always skips build output and dependency folders. If it finds more than one file, yaspire asks which one to use. `-f` takes a file, or a directory to search with only the folder skip list applied.

Paths in the YAML are relative to the YAML file. Compiler errors point at YAML lines:

```text
apphost.yaml(14,5): error CS1061: 'IResourceBuilder<RedisResource>' does not contain a definition for 'WithBogus' ...
```

`yaspire add` and `yaspire update` change the generated AppHost's package directives, and yaspire copies those changes into the YAML's `packages` and `aspire` keys. When a change can't be made in place, such as one that would edit an anchored value or add an entry to a single-string `packages` value, yaspire prints what to add instead.

`.yaspire/` contains its own `.gitignore`, so the generated files stay out of source control.

## The YAML

### Top-level keys

| Key | Meaning |
| --- | --- |
| `resources` | The resources, keyed by resource name. |
| `packages` | Hosting integrations as `Name` or `Name@version`. Unversioned `Aspire.*` packages use the Aspire SDK version; other packages need a version. |
| `aspire` | Aspire SDK version. Defaults to the installed Aspire CLI's version. |
| `usings` | Extra namespaces to import. |
| `options` | `DistributedApplicationOptions` properties, such as `disableDashboard: true`. |
| `code` | C# statements that run after every resource is declared and before any resource is configured. |
| `x-*` | Ignored. Use these keys for YAML anchors. |

### Resources

A resource is a mapping. Its first key is the resource type, which becomes an `Add` call; every other key is a method called on the resource:

```yaml
resources:
  postgres:
    postgres:                 # builder.AddPostgres("postgres")
    dataVolume:               #   .WithDataVolume()
    pgAdmin:                  #   .WithPgAdmin()
    lifetime: !cs ContainerLifetime.Persistent
    resources:
      visits: database        # postgres.AddDatabase("visits")
```

- A resource can be just its type: `cache: redis` means `builder.AddRedis("cache")`.
- `resources` inside a resource declares child resources, which are added to the parent.
- A resource can be a C# expression: `db: !cs builder.AddConnectionString("db")`.
- When calls must repeat or keep a strict order, write the resource as a list of steps. Each step is a method name or a one-key mapping:

  ```yaml
  web:
    - container: { image: nginx, tag: alpine }
    - httpEndpoint: { targetPort: 80, name: public }
    - httpEndpoint: { targetPort: 8080, name: admin }
    - externalHttpEndpoints
  ```

Resources can appear in any order. yaspire declares each resource after the resources its type arguments refer to.

### Method names

Keys are method names in camelCase without the `With` prefix: `reference` calls `WithReference`, `dataVolume` calls `WithDataVolume`. Keys that start with a verb are called as written: `waitFor`, `publishAsDockerFile`, `runAsEmulator`, `asHttp2Service`, `excludeFromManifest`. The verbs are `with`, `wait`, `publish`, `run`, `as`, `on`, `exclude`, `disable`, `configure`, `subscribe` and `clear`. A PascalCase key calls the method with exactly that name, e.g. `UseSomething: true`.

### Arguments

The resource name is always the first argument of the type's `Add` call. A key's value supplies the rest:

| Value | Type key | Method key |
| --- | --- | --- |
| empty or `~` | `AddX("name")` | `.WithX()` |
| `true` | `AddX("name", true)` | `.WithX()` |
| `false` | `AddX("name", false)` | no call |
| scalar | `AddX("name", value)` | `.WithX(value)` |
| list | positional arguments: `AddX("name", a, b)` | one call per item; an item is a scalar, a list of positional arguments, or a mapping of named arguments |
| mapping with camelCase keys | named arguments: `AddX("name", image: "nginx")` | named arguments: `.WithX(port: 8080)` |
| mapping with other keys | | one call per entry: `environment: { API_KEY: key }` calls `.WithEnvironment("API_KEY", key)` |

To pass names that look like parameter names as pairs, quote them: `environment: { "debug": "1" }`.

### Values

Plain (unquoted) scalars are typed:

- `null`, `~` and an empty value are `null`; `true` and `false` are booleans; numbers are numbers.
- A scalar that equals a resource name refers to that resource.
- Everything else is a string.

Quoted scalars are always strings. Quote values that only look like numbers or resource names, such as `tag: "8.2"`.

`!cs` marks raw C#. Use it for enums, lambdas, endpoints and reference expressions:

```yaml
environment:
  API_URL: !cs api.GetEndpoint("http")
urlForEndpoint: { endpointName: http, callback: !cs 'url => url.DisplayText = "Home"' }
```

Inside C#, a resource is a variable named after the resource, with `-` replaced by `_`: `my-api` is `my_api`.

Nested lists become collection expressions, and nested mappings become `new() { ... }` object initializers.

## Samples

- [containers](samples/containers/apphost.yaml): nginx serving a local folder, Redis with Redis Insight, and Docker Compose publishing.
- [dotnet](samples/dotnet/apphost.yaml): an ASP.NET Core API using Redis and PostgreSQL, a parameter, and a `code` block.
- [node](samples/node/apphost.yaml): a Node.js server written as a list of steps.

Run one with `yaspire run` from its folder, or `yaspire -f samples/node run` from here.
