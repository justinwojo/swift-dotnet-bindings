# SwiftBindings 0.19.2

Patch release: fixes macOS and Mac Catalyst builds failing with
`ditto: …/<Name>.framework/Modules: Not a directory` when frameworks are copied
again over an existing build — after a package update, a cleaned `obj/`, or a
republish. Reported in
[swift-dotnet-packages#3](https://github.com/justinwojo/swift-dotnet-packages/issues/3).

0.19.1 rewrites each embedded framework into Apple's versioned bundle layout
before signing, but a later copy from the package could land on the rewritten
bundle and fail on its symbolic links. `SwiftBindings.Runtime` now removes an
already-rewritten framework before that copy runs, so every copy starts from an
empty destination and the rewrite runs again on the fresh files — which also
ensures a re-copied framework ships the new package's binary rather than the
previous one. iOS and tvOS apps are unaffected.

To pick the fix up in an existing app, add
`<PackageReference Include="SwiftBindings.Runtime" Version="0.19.2" />`.

## Packages

| Package                  | Version |
|--------------------------|---------|
| SwiftBindings.Runtime    | 0.19.2  |
| SwiftBindings.Sdk        | 0.19.2  |
| SwiftBindings.Templates  | 0.19.2  |

`SwiftBindings.Apple` is unchanged at `26.2.8`.

See the [GitHub wiki](https://github.com/justinwojo/swift-dotnet-bindings/wiki) for
installation and usage.
