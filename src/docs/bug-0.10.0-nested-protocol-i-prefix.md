# Bug: Nested-protocol type references emit `IParent.Nested` instead of `Parent.INested`

> SDK 0.10.0 generator regression. Discovered 2026-05-05 attempting to bump
> [SwiftBindings.Nuke](https://github.com/justinwojo/swift-dotnet-packages) from
> Nuke 12.8.0 → 13.0.5.

## Summary

When a Swift protocol is declared as a nested type (e.g. `Nuke.ImagePipeline.Delegate`),
the generator emits the *interface declaration* with the correct nested layout
(`ImagePipeline.IDelegate`) but emits *type references* to that protocol with the
`I` prefix on the **parent type** instead of the nested interface — producing
`IImagePipeline.Delegate`, which doesn't exist.

The generated file fails to compile with `CS0246: The type or namespace name
'IImagePipeline' could not be found`.

## Environment

- SwiftBindings.Sdk 0.10.0 / Runtime 0.10.0 / Apple 26.2.2
- .NET SDK 10.0.107, workload 10.0.107
- Xcode 26.2 / Swift 6.2.x
- macOS 26.x, arm64

## Repro

```bash
# In swift-dotnet-packages
$ jq '.version = "13.0.5"' libraries/Nuke/library.json | sponge libraries/Nuke/library.json
# csproj already on SwiftBindings.Sdk/0.10.0
$ dotnet nuke BuildLibrary --library Nuke
```

Fails at `dotnet build`:

```
obj/Debug/net10.0-ios/swift-binding/Nuke.cs(14687,153): error CS0246:
  The type or namespace name 'IImagePipeline' could not be found
obj/Debug/net10.0-ios/swift-binding/Nuke.cs(14728,100): error CS0246:
  The type or namespace name 'IImagePipeline' could not be found
```

Reverting `library.json` to Nuke `12.8.0` against the same SDK 0.10.0 builds
clean — the difference is that Nuke 12.8.0 declares the protocol as top-level
`Nuke.ImagePipelineDelegate`, while Nuke 13.0 nested it as
`Nuke.ImagePipeline.Delegate`.

## Generated code

The interface declaration is correct (nested inside the `ImagePipeline` class):

```csharp
// Nuke.cs:13918
public interface IDelegate
{
    [global::Swift.UnsupportedSwiftType("Existential type fallback", "any Nuke.ImageDecoding")]
    IImageDecoding? ImageDecoder(Nuke.ImageDecodingContext context, Nuke.ImagePipeline pipeline);
    ...
}
```

But every *reference* to that protocol in parameter types and existential factories
mis-prefixes the parent class:

```csharp
// Nuke.cs:14687 — wrong: IImagePipeline.Delegate (no such type)
public ImagePipeline(
    Nuke.ImagePipeline.ConfigurationType configuration,
    [global::Swift.OriginalSwiftType("any Nuke.ImagePipeline.Delegate")]
    IImagePipeline.Delegate? @delegate = null)
{
    ...
    @delegateSwiftInner = SwiftOptional<...>.NewSome(
        ExistentialContainerFactory.GetOrCreate<IImagePipeline.Delegate>(
            @delegateValue,
            static __v => new ImagePipeline.DelegateProxy(__v)));
    ...
}

// Nuke.cs:14728 — same bug in second overload
public ImagePipeline(
    [global::Swift.OriginalSwiftType("any Nuke.ImagePipeline.Delegate")]
    IImagePipeline.Delegate? @delegate,
    global::System.Action<Nuke.ImagePipeline.ConfigurationType> configure)
{
    ...
}
```

What it *should* emit:

```csharp
ImagePipeline.IDelegate? @delegate = null
ExistentialContainerFactory.GetOrCreate<ImagePipeline.IDelegate>(...)
```

i.e. the `I` prefix attaches to the leaf identifier (the protocol/interface name),
not to a path component.

## Comparison with Nuke 12.8.0 (SDK 0.10.0, builds clean)

Top-level protocol → no nesting, single identifier, `I` prefix lands on the leaf
because the leaf *is* the only identifier:

```csharp
// Nuke 12.8.0 generated — correct
public ImagePipeline(
    Nuke.ImagePipeline.ConfigurationType configuration,
    [global::Swift.OriginalSwiftType("any Nuke.ImagePipelineDelegate")]
    IImagePipelineDelegate? @delegate = null) { ... }
```

The bug only manifests when a protocol is nested in a class/struct/enum.

## Hypothesis

Likely a regression from one of 0.10.0's protocol-related changes (release notes
call out "label-distinct overloads", "protocol-hierarchy covariance", and the
modern-generator unification). The interface-declaration emission already places
the `I` prefix on the leaf (correct); the type-reference emission appears to
prefix the *first* segment of the qualified name instead of the *last*.

Plausible fix site: wherever the generator formats an interface type reference
from a Swift type identifier, the `I` prefix should be applied to the final
component (`Last()`) of the dotted path, not the first.

## Impact

- Blocks Nuke 13.x in [swift-dotnet-packages](https://github.com/justinwojo/swift-dotnet-packages).
- Likely affects any other Swift library that nests a delegate protocol inside
  its primary class — a common Swift idiom (`Foo.Delegate`, `Bar.DataSource`,
  etc.). Worth a quick audit of the other 14 libraries before promising they're
  unaffected by the 0.10.0 generator change, even though all 15 currently
  publish on 0.9.0 and pass tests.

## Workaround

None on the consumer side — the generator regenerates the file on every build,
so manual patches don't survive. Fix has to land in
[swift-dotnet-bindings](https://github.com/justinwojo/swift-dotnet-bindings) and
ship in 0.10.1 (or 0.11.0).

Until then, `libraries/Nuke/library.json` stays pinned at 13.0.5 with a build
failure, or reverts to 12.8.0 to keep CI green.
