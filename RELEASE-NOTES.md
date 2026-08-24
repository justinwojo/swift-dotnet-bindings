# SwiftBindings 0.19.1

Patch release: fixes Mac App Store submissions being rejected with
`ITMS-90291`/`ITMS-90292` "Malformed Framework". Reported in
[swift-dotnet-packages#2](https://github.com/justinwojo/swift-dotnet-packages/issues/2).

macOS and Mac Catalyst apps must embed frameworks in Apple's versioned bundle layout,
while ours shipped in the shallow shape iOS requires. A build step in
`SwiftBindings.Runtime` now rewrites each framework embedded in the built app into the
versioned layout before the bundle is signed. iOS and tvOS apps are unaffected.

To pick the fix up in an existing app, add
`<PackageReference Include="SwiftBindings.Runtime" Version="0.19.1" />`. To opt out,
set `<SwiftBindingsDeepenMacFrameworks>false</SwiftBindingsDeepenMacFrameworks>`.

## Packages

| Package                  | Version |
|--------------------------|---------|
| SwiftBindings.Runtime    | 0.19.1  |
| SwiftBindings.Sdk        | 0.19.1  |
| SwiftBindings.Templates  | 0.19.1  |

`SwiftBindings.Apple` is unchanged at `26.2.8`.

See the [GitHub wiki](https://github.com/justinwojo/swift-dotnet-bindings/wiki) for
installation and usage.
