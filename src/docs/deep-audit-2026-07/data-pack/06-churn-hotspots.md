# Data Pack — Git Churn Hotspots (src/Swift.Bindings/src since 2025-01-01)

Commits touching path (name-only log counts) — **edit frequency ≠ bug count**, but predicts AI footgun / dual-oracle drift risk.

| Touches | Path | Audit track |
|--------:|------|-------------|
| 195 | MethodHandler.cs | A1 / admission |
| 146 | Program.cs | pipeline / G1 |
| 143 | SwiftABIParser.cs | A8 |
| 133 | ModuleHandler.cs | emission |
| 121 | PropertyHandler.cs | properties |
| 105 | WrapperEmitter.Async.cs | A7 |
| 105 | ClassHandler.cs | classes |
| 102 | WrapperEmitter.Marshalling.cs | A1 |
| 96 | WrapperEmitter.Return.cs | A1 |
| 90 | ProtocolProxyEmitter.Receivers.cs | A5b |
| 89 | EveryProtocolEmitter.cs | A5a |
| 86 | ProtocolHandler.cs | A5 |
| 86 | EnumHandler.cs | enums |
| 83 | PInvokeEmitter.cs | A1 |
| 80 | WrapperEmitter.cs | wrappers |
| 79 | MethodWrapperEmitter.cs | A1 |
| 79 | FrozenStructHandler.cs | A2 |
| 78 | NonFrozenStructHandler.cs | A2 |
| 75 | ClosureHandler.cs | A4 |
| 72 | NameProvider.cs | keys / C2 |
| 72 | WrapperValidation.cs | admission |
| 72 | ModuleEmissionContext.cs | state |
| 72 | ConstructorWrapperEmitter.cs | ctors |
| 69 | MarshallingHelpers.cs | dual oracles |
| 69 | BoundGenericsHandler.cs | A6 |

**Worker note:** Top churn cluster is **MethodHandler + WrapperEmitter\* + Protocol\* + Parser** — matches where dual oracles and admission live. Simplification sessions should prefer these over low-churn leaf files.
