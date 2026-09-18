// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

// Compile-check only: never part of a binding, a package or the runtime tests.
//
// Generated code sits inside the types it binds, so a bound member or nested type named like a
// namespace root (`System`, `Swift`, `Foundation`, the module itself) captures every
// `Root.X` reference written in that scope. The generator therefore spells each such chain from
// `global::`. The declarations below put a type of each root's name into the generated
// namespaces, where C# finds it before the namespace for any reference that is not spelled from
// `global::`, so a single unqualified chain in the generated output fails this compile.
//
// Bare names cannot hide here either: CompileCheck.csproj declares no implicit usings and the
// generated files carry no external using directive, so a bare `IntPtr` or `Task` that an emitter
// forgets to qualify is already a compile error.

namespace SwiftBindingsTestLib
{
    internal static class System { }
    internal static class Swift { }
    internal static class Microsoft { }
    internal static class ObjCRuntime { }
    internal static class Foundation { }
    internal static class UIKit { }
    internal static class CoreGraphics { }
    internal static class CoreLocation { }
    internal static class CoreText { }
    internal static class ImageIO { }
    internal static class PassKit { }
    internal static class SwiftUI { }
    internal static class Vision { }
    internal static class AuthenticationServices { }
    internal static class SwiftBindingsTestLib { }
    internal static class SwiftBindingsTestLibDependency { }
}

namespace SwiftBindingsTestLibDependency
{
    internal static class System { }
    internal static class Swift { }
    internal static class Microsoft { }
    internal static class ObjCRuntime { }
    internal static class Foundation { }
    internal static class SwiftBindingsTestLibDependency { }
}
