# SwiftBindings 0.20.1

This release fixes two Objective-C binding defects: a protocol that adopts two protocols with similarly named methods no longer breaks the C# compile, and class methods whose names start with `init` are now bound as static methods instead of constructors. To pick it up, update the package and regenerate; a few Objective-C method names may change (see below), and code that called one of those mis-bound constructors with `new` should call the static method instead (the constructor never worked at runtime).

## Highlights

- **Protocols that combine two protocols with same-named methods compile again** — when two adopted protocols each had a method that came out with the same C# name (such as `didLoad:withAdInfo:` and `didRecordImpression:withAdInfo:`, both `WithAdInfo`), the combined protocol and its conforming classes declared the member twice and failed with `CS0111`. One method keeps its existing name and the other is named from its full selector (`DidRecordImpressionWithAdInfo`), so both callbacks reach your overrides.
- **Class methods named `init…` are callable** — a class method such as `+initWithRequest:completion:` was emitted as a constructor, so `new T(...)` compiled but sent the message to a fresh instance that didn't respond to it, and there was no way to call the real class method. It is now a `[Static]` method (`T.InitWithRequest(...)`), and instance initializers stay constructors as before.

## Other fixes

- **Objective-C method names now follow C# overload rules** — a method that was given its longer full-selector name to avoid a duplicate that C# would have accepted now keeps its short name as an ordinary overload (for example `ReadContext(out double, nint)` becomes `Read(out double, nint)`), so a few call sites may need updating after you regenerate. Methods whose parameters really are the same type in C#, such as `nint` and `IntPtr`, are now renamed instead of failing with `CS0111`.

## Reported issues fixed

- **[#47](https://github.com/justinwojo/swift-dotnet-bindings/issues/47) — Protocol adopting two protocols with same-named methods generates `CS0111`** — the combined delegate protocol now binds and compiles without renaming anything by hand.
- **[#48](https://github.com/justinwojo/swift-dotnet-bindings/issues/48) — Class methods starting with `init` bound as constructors** — `+initWithRequest:completion:` and similar class methods are now static methods you can call directly.

## Packages

| Package                  | Version |
|--------------------------|---------|
| SwiftBindings.Runtime    | 0.20.1  |
| SwiftBindings.Sdk        | 0.20.1  |
| SwiftBindings.Templates  | 0.20.1  |

`SwiftBindings.Apple` is unchanged at `26.2.9`. It declares a floor-only Runtime range, so the published supplement rides forward without a republish.

See the [GitHub wiki](https://github.com/justinwojo/swift-dotnet-bindings/wiki) for installation and usage.
