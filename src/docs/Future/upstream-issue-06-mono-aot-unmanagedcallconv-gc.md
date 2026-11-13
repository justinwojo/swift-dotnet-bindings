# [Mono AOT] `mono-aot-cross` aborts with "Sgen STW requires a working mono-context" when an app has tens of thousands of P/Invokes carrying `[UnmanagedCallConv]`

> Standalone bug report for filing against [dotnet/runtime](https://github.com/dotnet/runtime/issues). Project: [swift-dotnet-bindings](https://github.com/justinwojo/swift-dotnet-bindings). Repro: inline below (plain C#, no interop library). Contact: Justin Wojciechowski.

## Title

`[Mono AOT] mono-aot-cross SIGABRT ("Sgen STW requires a working mono-context") decoding [UnmanagedCallConv] on ~40k P/Invokes`

## Labels

`area-Codegen-AOT-mono`, `os-ios`, `bug`, `runtime-mono`

## Description

**Environment:**
- .NET SDK 10.0.107, Microsoft.iOS.Sdk.net10.0_26.2 26.2.10233, Xcode 26.3, macOS 26 (Apple Silicon)
- Mono full AOT, the .NET-for-iOS default device runtime: `dotnet build -c Debug -r ios-arm64` (no `PublishAot`); the
  arm64 simulator (`-r iossimulator-arm64`) AOT-compiles the same way and fails identically.

**Summary:**

The AOT step fails with `Failed to AOT compile aot-instances.dll, the AOT compiler exited with code 134` once
the app's assemblies together contain roughly 30–40 thousand P/Invokes that carry
`[UnmanagedCallConv(CallConvs = ...)]` (a single assembly at about 50 thousand fails in its own step too).
The same P/Invokes without the attribute compile. Re-running the recorded `mono-aot-cross` command prints one
line and aborts:

```
Sgen STW requires a working mono-context
```

**Stack (lldb on `mono-aot-cross`):**

```
sgen_client_stop_world <- sgen_stop_world <- sgen_perform_collection <- sgen_ensure_free_space
<- sgen_alloc_obj_nolock <- mono_gc_alloc_vector <- mono_array_new_specific_internal
<- mono_reflection_create_custom_attr_data_args <- mono_marshal_get_native_wrapper
<- collect_methods <- aot_assembly <- mono_aot_assemblies <- main_thread_handler <- mono_main
```

**Cause, from the release/10.0 sources:**
- Under `MONO_CROSS_COMPILE` a stop-the-world is a fatal error (`mono/sgen/sgen-stw.c`, the
  `g_error("Sgen STW requires a working mono-context")`), so the first nursery collection kills the compiler.
- For each P/Invoke with the default calling convention, `mono_marshal_get_native_wrapper` calls
  `mono_marshal_set_callconv_from_unmanaged_callconv_attribute`, which decodes the attribute through
  `mono_reflection_create_custom_attr_data_args` and allocates managed arrays per P/Invoke.
- The `[UnmanagedCallersOnly]` decode in the same file uses `mono_reflection_create_custom_attr_data_args_noalloc`.
  Using the non-allocating decode for `[UnmanagedCallConv]` too would remove the allocation.

The allocation volume, not the IL, is the trigger: the failing command exits 0 with
`MONO_GC_PARAMS=nursery-size=512m` (compiled 27,059/27,071 methods, about 860 MB resident).

## Minimal reproduction

A bare `net10.0-ios` Exe (`SupportedOSPlatformVersion` 15.0, `RunAnalyzers` false) plus one generated file of
N never-called P/Invokes, 500 per static class:

```python
import sys
n, attr, out = int(sys.argv[1]), sys.argv[2] == "1", sys.argv[3]
with open(out, "w") as f:
    f.write("using System.Runtime.CompilerServices;\nusing System.Runtime.InteropServices;\n")
    for c in range((n + 499) // 500):
        f.write(f"\nstatic class Natives{c}\n{{\n")
        for i in range(c * 500, min(n, (c + 1) * 500)):
            if attr:
                f.write("    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]\n")
            f.write(f'    [DllImport("libnative", EntryPoint = "f{i}")] internal static extern int F{i}(int x);\n')
        f.write("}\n")
```

`dotnet build -c Debug -r iossimulator-arm64`:

| N | Attribute | RID | Result |
|---|---|---|---|
| 20,000 | yes | iossimulator-arm64 | builds |
| 30,000 | yes | iossimulator-arm64 | builds |
| 40,000 | yes | iossimulator-arm64 | `aot-instances.dll` exits 134 |
| 50,000 | yes | iossimulator-arm64 | `aot-instances.dll` and the app assembly exit 134 |
| 50,000 | yes | ios-arm64 | same |
| 50,000 | no | iossimulator-arm64 | builds |
| 60,000 | yes | iossimulator-arm64 | exits 134; builds with `MONO_GC_PARAMS=nursery-size=256m` passed to the AOT compiler |

The threshold depends on everything else the compiler allocates, so it is approximate.

## How it reaches SwiftBindings consumers

A generated binding declares a P/Invoke per bound member, and the generator writes `[UnmanagedCallConv]` on
every one, including the C-convention majority (`CallConvCdecl`, already the default on every Apple target, so
the attribute changes nothing there). The BindingTests fixture alone carries about 15 thousand attributed
P/Invokes, so an app combining it with a large set of bindings crossed the threshold. Omitting the redundant
`CallConvCdecl` attribute (about 88% of the attributed P/Invokes in that app) let the same app's AOT step pass.

App-side workaround, verified on that app: forward a larger nursery to the AOT compiler.

```xml
<ItemGroup>
  <_AotEnvironment Include="MONO_GC_PARAMS" Value="nursery-size=512m" />
</ItemGroup>
```

`_AotEnvironment` is a private (underscore) item of the iOS SDK, not a supported contract.
