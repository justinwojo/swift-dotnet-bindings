// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;

namespace BindingsGeneration;

/// <summary>
/// Emits Foundation KVO observe(_:options:changeHandler:) bridges
/// for <c>@objc dynamic</c> stored properties on NSObject-rooted Swift classes.
///
/// <para>
/// Per (NSObject-rooted class C, supported <c>@objc dynamic</c> property P of
/// type V):
/// </para>
/// <list type="bullet">
///   <item><description>One Swift <c>@_cdecl("SBW_KVO_{Module}_{C}_observe{P}")</c>
///   trampoline that calls <c>obj.observe(\.P, options: ...)</c>, unpacks
///   <c>change.newValue ?? obj.P</c>, and forwards through a
///   <c>@convention(c)</c> callback the C# side supplies. The +1 retained
///   <c>NSKeyValueObservation</c> token is returned as an opaque pointer.</description></item>
///   <item><description>One C# <c>UnmanagedCallersOnly</c> dispatch trampoline that
///   reconstructs the managed handler via <see cref="System.Runtime.InteropServices.GCHandle"/>
///   and forwards.</description></item>
///   <item><description>One C# extension method
///   <c>Observe{P}(this C, SbwKvoOptions, Action&lt;C, V_csharp&gt;) → KvoToken</c>.</description></item>
/// </list>
///
/// <para>
/// One per-class <c>@_cdecl("SBW_KVO_{Module}_{C}_invalidate")</c> shim is
/// emitted alongside the first observe shim. It is referenced by every
/// <see cref="Swift.Runtime.KvoToken"/> the generated extension produces.
/// </para>
///
/// <para>
/// v1 scope: primitive value-type properties only (Int / Int32 / Int64 / Bool /
/// Double / Float). String, Optional, struct, and class-typed properties are
/// recognised but skipped — observe shims for those need separate ABI design.
/// </para>
/// </summary>
internal static class KvoExtensionEmitter
{
    /// <summary>
    /// Value-type whitelist for v1. The string is the Swift name to match
    /// against <see cref="NamedTypeSpec"/>; the tuple carries the Swift literal
    /// name (for use inside the <c>@_cdecl</c> trampoline's
    /// <c>@convention(c)</c> typealias) and the C# type rendered in the
    /// <see cref="Action{T1,T2}"/> closure and dispatch trampoline.
    /// </summary>
    private static readonly Dictionary<string, (string SwiftAbi, string CSharpAbi)> s_supportedTypes =
        new(StringComparer.Ordinal)
        {
            { "Swift.Int",    ("Int",     "nint")   },
            { "Swift.Int32",  ("Int32",   "int")    },
            { "Swift.Int64",  ("Int64",   "long")   },
            { "Swift.UInt",   ("UInt",    "nuint")  },
            { "Swift.UInt32", ("UInt32",  "uint")   },
            { "Swift.UInt64", ("UInt64",  "ulong")  },
            { "Swift.Bool",   ("Bool",    "bool")   },
            { "Swift.Double", ("Double",  "double") },
            { "Swift.Float",  ("Float",   "float")  },
        };

    public static void EmitKvoExtensionsForClass(
        CSharpWriter csWriter,
        SwiftWriter swiftWriter,
        ClassDecl classDecl,
        ITypeDatabase typeDatabase,
        ModuleEmissionContext emissionContext,
        ILogger logger)
    {
        // ObjC-rooted gate: only NSObject-rooted classes can be KVO targets.
        // KVO is Foundation's contract on top of NSObject; non-NSObject ObjC
        // roots in the wild are vanishingly rare and not worth the v1
        // surface-area gamble.
        if (!classDecl.IsObjCRooted) return;

        // The wrapper xcframework is the host for @_cdecl shims — without it,
        // we have nowhere to put the Swift side. Mirrors the gate used by
        // every other shim-emitting pass.
        if (!WrapperValidation.IsXCFrameworkMode(typeDatabase)) return;
        if (classDecl.ModuleDecl is null) return;

        // Generic class parents are out of scope for v1: an
        // @_cdecl with a generic Self type can't be expressed.
        if (classDecl.IsGeneric) return;

        // Nested classes inside another type are skipped for the same reason
        // KeyPathSingletonEmitter skips them — `\.{prop}` literal + symbol
        // naming want an unambiguous top-level receiver.
        if (classDecl.ParentDecl is TypeDecl) return;

        var observable = new List<PropertyDecl>();
        foreach (var prop in classDecl.Properties)
        {
            if (!prop.IsObjCDynamic) continue;
            if (!prop.HasStorage) continue;
            if (prop.IsStatic) continue;
            if (prop.IsModuleInternal) continue;
            if (prop.IsSpiProtected) continue;
            if (prop.SwiftTypeSpec is not NamedTypeSpec named) continue;
            if (!s_supportedTypes.ContainsKey(named.Name)) continue;
            observable.Add(prop);
        }
        if (observable.Count == 0) return;

        // Module-level dedup. Some types are reachable through multiple
        // emission paths (extensions, nested type emission); we only want
        // one extension class + one invalidate shim per class.
        var dedupKey = $"KVO|{classDecl.SwiftTypeName?.ModuleQualifiedName ?? classDecl.Name}";
        if (!emissionContext.TryAddKeyPathSingletonContainer(dedupKey)) return;

        EmitCSharpExtensionClass(csWriter, classDecl, observable, typeDatabase);
        EmitSwiftShims(swiftWriter, classDecl, observable);
    }

    private static void EmitCSharpExtensionClass(
        CSharpWriter csWriter,
        ClassDecl classDecl,
        List<PropertyDecl> observable,
        ITypeDatabase typeDatabase)
    {
        var className = NameProvider.ToPascalCaseForTypeName(classDecl.Name);
        var moduleName = classDecl.ModuleDecl!.Name;
        var invalidateEntryPoint = $"SBW_KVO_{moduleName}_{className}_invalidate";
        var libName = typeDatabase.AsyncLibraryName ?? "libSwiftBindings";

        // Class-level [SupportedOSPlatform] from the observed type's @available.
        // Members of this extension class call into the gated class; without the
        // class-level attribute, every Observe* call site would trip CA1416.
        AvailabilityAttributeEmitter.EmitSupportedOSPlatformsFromAnnotations(
            csWriter, classDecl.AvailabilityAnnotations);
        csWriter.WriteLine($"public static partial class {className}KvoExtensions");
        csWriter.WriteLine("{");
        csWriter.Indent++;

        // Per-class invalidate P/Invoke. UnmanagedCallConv with CallConvCdecl is
        // mandatory: SBW_ wrappers are cdecl trampolines, and EntryPointCallConvPairingTests
        // enforces the pairing on every emitted [LibraryImport] in the binding output.
        csWriter.WriteLine("[global::System.Runtime.InteropServices.UnmanagedCallConv(CallConvs = new[] { typeof(global::System.Runtime.CompilerServices.CallConvCdecl) })]");
        csWriter.WriteLine($"[global::System.Runtime.InteropServices.LibraryImport(\"{libName}\", EntryPoint = \"{invalidateEntryPoint}\")]");
        csWriter.WriteLine("private static partial void InvalidateNative(global::System.IntPtr token);");
        csWriter.WriteLine();
        csWriter.WriteLine("private static readonly global::System.Action<global::System.IntPtr> s_invalidate = InvalidateNative;");
        csWriter.WriteLine();

        foreach (var prop in observable)
        {
            var propPascal = NameProvider.ToPascalCaseForTypeName(prop.Name);
            var observeEntry = $"SBW_KVO_{moduleName}_{className}_observe{propPascal}";
            var swiftTypeName = ((NamedTypeSpec)prop.SwiftTypeSpec).Name;
            var (_, csType) = s_supportedTypes[swiftTypeName];

            // P/Invoke — observe entry point. Same SBW_ + cdecl pairing rule as
            // the invalidate shim above; missing UnmanagedCallConv would trip
            // EntryPointCallConvPairingTests at runtime.
            csWriter.WriteLine("[global::System.Runtime.InteropServices.UnmanagedCallConv(CallConvs = new[] { typeof(global::System.Runtime.CompilerServices.CallConvCdecl) })]");
            csWriter.WriteLine($"[global::System.Runtime.InteropServices.LibraryImport(\"{libName}\", EntryPoint = \"{observeEntry}\")]");
            csWriter.WriteLine($"private static partial global::System.IntPtr Observe{propPascal}Native(global::System.IntPtr self, nuint options, global::System.IntPtr fn, global::System.IntPtr ctx);");
            csWriter.WriteLine();

            // UnmanagedCallersOnly dispatch trampoline. Any managed exception thrown
            // from `handler` would unwind across the Swift→C# unmanaged boundary and
            // terminate the process, so the body is wrapped in a fail-soft try/catch
            // that prints the exception and continues. This matches the standard
            // UnmanagedCallersOnly pattern across the runtime.
            csWriter.WriteLine("[global::System.Runtime.InteropServices.UnmanagedCallersOnly(CallConvs = new[] { typeof(global::System.Runtime.CompilerServices.CallConvCdecl) })]");
            csWriter.WriteLine($"private static void Dispatch{propPascal}(global::System.IntPtr selfPtr, {csType} newValue, global::System.IntPtr ctx)");
            csWriter.WriteLine("{");
            csWriter.Indent++;
            csWriter.WriteLine("try");
            csWriter.WriteLine("{");
            csWriter.Indent++;
            csWriter.WriteLine("var gch = global::System.Runtime.InteropServices.GCHandle.FromIntPtr(ctx);");
            csWriter.WriteLine($"var handler = (global::System.Action<{className}, {csType}>)gch.Target!;");
            // The observed object is surfaced to the user's handler and is always a class reference.
            // MarshalBorrowedClassFromSwift takes an owning +1 (isa-aware, so ObjC-rooted KVO classes
            // retain correctly) so the wrapper balances on Dispose/finalize instead of over-releasing
            // a borrowed handle on NativeAOT. See ClosureHandler.BorrowedCallbackArgMarshal.
            csWriter.WriteLine($"var obj = global::Swift.Runtime.InteropServices.SwiftMarshal.MarshalBorrowedClassFromSwift<{className}>(selfPtr);");
            csWriter.WriteLine("handler(obj, newValue);");
            csWriter.Indent--;
            csWriter.WriteLine("}");
            csWriter.WriteLine("catch (global::System.Exception ex)");
            csWriter.WriteLine("{");
            csWriter.Indent++;
            csWriter.WriteLine($"global::System.Console.Error.WriteLine($\"[KVO] Unhandled exception in {className}.{propPascal} change handler: {{ex}}\");");
            csWriter.Indent--;
            csWriter.WriteLine("}");
            csWriter.Indent--;
            csWriter.WriteLine("}");
            csWriter.WriteLine();

            // Per-property [SupportedOSPlatform] for properties stricter than the
            // class (the helper dedups against the class-level set, so unchanged
            // properties emit nothing extra).
            AvailabilityAttributeEmitter.EmitSupportedOSPlatformsFromAnnotations(
                csWriter, prop.AvailabilityAnnotations, classDecl.AvailabilityAnnotations);
            // The extension method itself.
            csWriter.WriteLine($"public static global::Swift.Runtime.KvoToken Observe{propPascal}(");
            csWriter.Indent++;
            csWriter.WriteLine($"this {className} obj,");
            csWriter.WriteLine("global::Swift.Runtime.SbwKvoOptions options,");
            csWriter.WriteLine($"global::System.Action<{className}, {csType}> changed)");
            csWriter.Indent--;
            csWriter.WriteLine("{");
            csWriter.Indent++;
            csWriter.WriteLine("global::System.ArgumentNullException.ThrowIfNull(obj);");
            csWriter.WriteLine("global::System.ArgumentNullException.ThrowIfNull(changed);");
            csWriter.WriteLine("var gch = global::System.Runtime.InteropServices.GCHandle.Alloc(changed);");
            csWriter.WriteLine("global::System.IntPtr token;");
            csWriter.WriteLine("unsafe");
            csWriter.WriteLine("{");
            csWriter.Indent++;
            csWriter.WriteLine($"delegate* unmanaged[Cdecl]<global::System.IntPtr, {csType}, global::System.IntPtr, void> fn = &Dispatch{propPascal};");
            csWriter.WriteLine("try");
            csWriter.WriteLine("{");
            csWriter.Indent++;
            csWriter.WriteLine($"token = Observe{propPascal}Native(");
            csWriter.Indent++;
            csWriter.WriteLine("((global::Swift.Runtime.ISwiftObject)obj).SwiftHandle,");
            csWriter.WriteLine("(nuint)options,");
            csWriter.WriteLine("(global::System.IntPtr)fn,");
            csWriter.WriteLine("global::System.Runtime.InteropServices.GCHandle.ToIntPtr(gch));");
            csWriter.Indent--;
            csWriter.Indent--;
            csWriter.WriteLine("}");
            csWriter.WriteLine("catch");
            csWriter.WriteLine("{");
            csWriter.Indent++;
            csWriter.WriteLine("gch.Free();");
            csWriter.WriteLine("throw;");
            csWriter.Indent--;
            csWriter.WriteLine("}");
            csWriter.Indent--;
            csWriter.WriteLine("}");
            csWriter.WriteLine("return new global::Swift.Runtime.KvoToken(token, gch, s_invalidate);");
            csWriter.Indent--;
            csWriter.WriteLine("}");
            csWriter.WriteLine();
        }

        csWriter.Indent--;
        csWriter.WriteLine("}");
        csWriter.WriteLine();
    }

    private static void EmitSwiftShims(
        SwiftWriter swiftWriter,
        ClassDecl classDecl,
        List<PropertyDecl> observable)
    {
        var className = NameProvider.ToPascalCaseForTypeName(classDecl.Name);
        var moduleName = classDecl.ModuleDecl!.Name;
        var swiftQualified = $"{moduleName}.{classDecl.Name}";
        var invalidateSym = $"SBW_KVO_{moduleName}_{className}_invalidate";

        foreach (var prop in observable)
        {
            var propPascal = NameProvider.ToPascalCaseForTypeName(prop.Name);
            var observeSym = $"SBW_KVO_{moduleName}_{className}_observe{propPascal}";
            var (swiftAbi, _) = s_supportedTypes[((NamedTypeSpec)prop.SwiftTypeSpec).Name];

            // Top-level @_cdecl wrapper functions do NOT inherit enclosing-type
            // availability — merge class + property annotations so the shim
            // compiles for SDK deployment targets below the gated API's introduced OS.
            WrapperEmitterHelpers.EmitSwiftAvailability(
                swiftWriter,
                WrapperEmitterHelpers.MergeAvailability(prop.AvailabilityAnnotations, classDecl));
            swiftWriter.WriteLine($"@_cdecl(\"{observeSym}\")");
            swiftWriter.WriteLine($"public func {observeSym}(_ selfPtr: UnsafeRawPointer, _ options: UInt, _ fnPtr: UnsafeRawPointer, _ ctx: UnsafeRawPointer) -> UnsafeMutableRawPointer {{");
            swiftWriter.Indent++;
            swiftWriter.WriteLine($"let obj = Unmanaged<{swiftQualified}>.fromOpaque(selfPtr).takeUnretainedValue()");
            swiftWriter.WriteLine("let opts = Foundation.NSKeyValueObservingOptions(rawValue: options)");
            swiftWriter.WriteLine($"typealias Callback = @convention(c) (UnsafeRawPointer, {swiftAbi}, UnsafeRawPointer) -> Void");
            swiftWriter.WriteLine("let cb = unsafeBitCast(fnPtr, to: Callback.self)");
            swiftWriter.WriteLine($"let token = obj.observe(\\.{prop.Name}, options: opts) {{ observed, change in");
            swiftWriter.Indent++;
            swiftWriter.WriteLine($"let value = change.newValue ?? observed.{prop.Name}");
            swiftWriter.WriteLine("let observedPtr = Unmanaged.passUnretained(observed).toOpaque()");
            swiftWriter.WriteLine("cb(observedPtr, value, ctx)");
            swiftWriter.Indent--;
            swiftWriter.WriteLine("}");
            swiftWriter.WriteLine("return Unmanaged.passRetained(token).toOpaque()");
            swiftWriter.Indent--;
            swiftWriter.WriteLine("}");
            swiftWriter.WriteLine();
        }

        WrapperEmitterHelpers.EmitSwiftAvailability(swiftWriter, classDecl.AvailabilityAnnotations);
        swiftWriter.WriteLine($"@_cdecl(\"{invalidateSym}\")");
        swiftWriter.WriteLine($"public func {invalidateSym}(_ tokenPtr: UnsafeRawPointer) {{");
        swiftWriter.Indent++;
        swiftWriter.WriteLine("let token = Unmanaged<Foundation.NSKeyValueObservation>.fromOpaque(tokenPtr).takeRetainedValue()");
        swiftWriter.WriteLine("token.invalidate()");
        swiftWriter.Indent--;
        swiftWriter.WriteLine("}");
        swiftWriter.WriteLine();
    }
}
