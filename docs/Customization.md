# Customization

## CLI Options Reference

The generator accepts these options:

| Option | Description |
|--------|-------------|
| `--xcframework <path>` | Path to an xcframework. Auto-resolves all inputs. Mutually exclusive with `-a/-d/-t`. |
| `-a, --swiftabi <path>` | Path to Swift ABI JSON file |
| `-d, --dylib <path>` | Path to the dynamic library |
| `-t, --tbd <path>` | Path to the TBD file |
| `-o, --output <path>` | **(Required)** Output directory |
| `--platform-target <target>` | `simulator` (default) or `device`. Selects xcframework slice. |
| `-l, --library-name <name>` | Runtime library name for `DllImport`. Defaults to dylib path. |
| `--async-library <name>` | Library name for async wrapper functions. Usually `SwiftBindings`. |
| `-s, --swiftinterface <path>` | Path to `.swiftinterface` file. Detects `@inlinable internal` members. |
| `--symbolgraph <path>` | Path to symbol graph JSON. Generates C# XML doc comments from Swift docs. In xcframework mode, extracted automatically if not provided. |
| `--no-docs` | Disable automatic symbol graph extraction. Does not affect explicit `--symbolgraph` paths. |
| `--bridge-hints <path>` | Path to [bridge hints JSON](SwiftUI-Interop#bridge-hints) for SwiftUI views. |
| `--namespace-pattern <pattern>` | C# namespace pattern. Supports `{Module}` and `{Framework}`. Default: `Swift.{Module}` |
| `--sdk-mode` | Skip `.csproj` emission (used when the MSBuild SDK is the project system). |
| `--package-id <id>` | NuGet package ID override. Default: `{Module}.Swift.iOS` |
| `--wrapper-architectures <scope>` | `simulator` (default), `device`, or `all` (both slices). |
| `--framework-dependency <path>` | Dependency xcframework path. Repeatable. Adds `-F` search paths for wrapper compilation and `PackageReference` in emitted `.csproj`. Requires `--xcframework`. |
| `--module-database <path>` | Load a dependency module database XML for cross-module type resolution. Repeatable. Generated alongside bindings as `{Module}Database.xml`. |
| `-v, --verbose <level>` | `0` = silent, `1` = normal (default), `2` = debug |

## MSBuild SDK Properties

When using the `Swift.Bindings.Sdk`, these MSBuild properties are available in your `.csproj`:

| Property | Default | Description |
|----------|---------|-------------|
| `SwiftPlatformTarget` | `simulator` | Platform slice for generation |
| `SwiftWrapperArchitectures` | `all` | Wrapper compilation scope: `simulator`, `device`, or `all` |
| `SwiftRuntimeVersion` | `0.1.0-preview.1` | Version of `Swift.Runtime` package |
| `SwiftGenerateDocComments` | `true` | Auto-extract symbol graph for C# XML doc comments |

### SwiftFramework Item Metadata

Each `<SwiftFramework>` item supports optional metadata:

```xml
<SwiftFramework Include="MyLib.xcframework">
  <!-- Custom C# namespace (default: Swift.{Module}) -->
  <NamespacePattern>MyCompany.MyLib</NamespacePattern>

  <!-- Symbol graph for C# XML doc comments -->
  <SymbolGraph>MyLib.symbols.json</SymbolGraph>

  <!-- Bridge hints for SwiftUI views -->
  <BridgeHints>bridge-hints.json</BridgeHints>

  <!-- Swiftinterface for @inlinable internal detection -->
  <SwiftInterface>MyLib.swiftinterface</SwiftInterface>
</SwiftFramework>
```

## Namespace Control

By default, all generated types go into `Swift.{Module}` (e.g., `Swift.Nuke`).

Override with `--namespace-pattern`:

```bash
# Use library name directly
--namespace-pattern "Nuke"

# Company prefix
--namespace-pattern "MyCompany.{Module}"
```

Or in the MSBuild SDK:

```xml
<SwiftFramework Include="Nuke.xcframework">
  <NamespacePattern>Nuke</NamespacePattern>
</SwiftFramework>
```

## XML Doc Comments

In xcframework mode, the generator **automatically** extracts symbol graph data from the framework's `.swiftmodule` and converts Swift documentation into C# XML doc comments (for IntelliSense). No extra steps needed — `dotnet build` produces bindings with full documentation.

To disable automatic extraction:

```bash
# CLI
--no-docs

# MSBuild SDK
<SwiftGenerateDocComments>false</SwiftGenerateDocComments>
```

To provide a pre-extracted symbol graph instead (overrides auto-extraction even with `--no-docs`):

```bash
# CLI
--symbolgraph path/to/symbolgraph/

# MSBuild SDK
<SwiftFramework Include="MyLib.xcframework">
  <SymbolGraph>path/to/symbolgraph/</SymbolGraph>
</SwiftFramework>
```

## Incremental Builds

The MSBuild SDK uses fingerprint-based incremental builds. Generation is skipped if none of these change:
- Framework binaries (dylib content)
- `Info.plist`
- `.swiftinterface` files
- Supplementary inputs (SymbolGraph, BridgeHints, SwiftInterface)
- Generation-affecting properties (namespace, platform target, etc.)

The fingerprint check takes ~50ms. To force regeneration, delete the stamp file:

```bash
rm obj/swift-binding/swift-binding.stamp
dotnet build
```

---

## Next Steps

- **[SwiftUI Interop](SwiftUI-Interop)** — Bridge hints and SwiftUI view customization
- **[Troubleshooting](Troubleshooting)** — Common errors and how to fix them
