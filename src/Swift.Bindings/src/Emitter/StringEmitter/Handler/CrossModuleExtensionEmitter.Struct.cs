// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using static BindingsGeneration.ExtensionMarshallingHelper;

namespace BindingsGeneration;

/// <summary>
/// Struct-receiver path for <see cref="CrossModuleExtensionEmitter"/>: emits
/// C# extension methods on a foreign frozen struct when the current module
/// declares <c>extension SomeForeignStruct { ... }</c>.
///
/// The class-receiver path can dispatch the original Swift CallConvSwift symbol
/// directly because a class instance pointer fits cleanly into the swiftcc
/// self register. A frozen struct receiver passes by value across registers
/// (e.g. <c>Point(x: Double, y: Double)</c> goes in d0+d1) and the .NET
/// runtime cannot synthesize that register split through <c>SwiftSelf&lt;T&gt;</c>.
/// So the struct path instead emits a Swift <c>@_cdecl</c> trampoline in the
/// current module's wrapper library, which reads <c>self</c> via
/// <c>self_.assumingMemoryBound(to: T.self).pointee</c>, and a matching C#
/// extension method that passes <c>(IntPtr)(&amp;self)</c> straight through.
/// (The receiver is a by-value extension parameter, so it is already on the
/// stack and a <c>fixed</c> block on it would trigger C# CS0213. The setter
/// path uses <c>ref self</c> and DOES require <c>fixed</c>.) This matches the
/// ABI shape <see cref="FrozenStructHandler"/> uses for the foreign struct's
/// own members.
///
/// Supported return shapes: void, primitive, ObjC class, Swift class, frozen
/// struct (rendered via <c>resultPtr</c> sret), simple enum (lowered to its
/// raw integer across the cdecl boundary, reconstructed via <c>rawValue</c>
/// in Swift / cast in C#).
/// Supported param shapes: primitive, ObjC class, Swift class, simple enum
/// (lowered to underlying int). Frozen-struct params would require a second
/// <c>fixed</c> per arg and are deferred — the gate rejects them so the method
/// is skipped cleanly rather than emitting broken code.
/// </summary>
public static partial class CrossModuleExtensionEmitter
{
    private static void EmitStruct(
        CSharpWriter csWriter,
        SwiftWriter swiftWriter,
        StructDecl structDecl,
        ModuleDecl moduleDecl,
        Conductor conductor,
        IEnvironment env,
        ILogger logger)
    {
        var typeDatabase = env.TypeDatabase;
        var origModule = structDecl.SwiftTypeName.Module;
        var currentModule = moduleDecl.Name;

        // Same fallback-receiver guard as the class path: if the foreign struct
        // is unknown to the type database, we cannot emit a safe extension class
        // (no C# type to extend, no Swift type to assumingMemoryBound to).
        if (!typeDatabase.TryGetTypeRecord(structDecl.SwiftTypeName, out var origRecord))
        {
            logger.LogInformation(
                "Cross-module extension on struct {Type} from {Module}: foreign type not in TypeDatabase, skipping.",
                structDecl.Name, currentModule);
            return;
        }

        // Cross-module struct extension only routes frozen value structs at this
        // phase. Non-frozen structs (RequiresMemoryManagement) project to a
        // SafeHandle-backed C# class, which has different self-passing rules and
        // is handled by a different ABI shape that this path does not cover.
        if (origRecord.Kind != TypeRecordKind.Struct ||
            !origRecord.Flags.HasFlag(TypeRecordFlags.Frozen) ||
            origRecord.Flags.HasFlag(TypeRecordFlags.RequiresMemoryManagement))
        {
            logger.LogInformation(
                "Cross-module extension on struct {Type} from {Module}: receiver is not a frozen value struct, skipping.",
                structDecl.Name, currentModule);
            return;
        }

        var origCSharpType = origRecord.CSharpTypeName.FullyQualifiedName;
        var origSwiftTypeQualified = $"{origModule}.{structDecl.Name}";

        // Filter to only members from the current module (the extension members).
        var methods = new List<MethodDecl>();
        var properties = new List<PropertyDecl>();

        foreach (var method in structDecl.Methods)
        {
            if (method.ModuleDecl?.Name != currentModule)
                continue;
            if (method.IsConstructor)
                continue;
            methods.Add(method);
        }

        foreach (var property in structDecl.Properties)
        {
            if (property.ModuleDecl?.Name != currentModule)
                continue;
            properties.Add(property);
        }

        if (methods.Count == 0 && properties.Count == 0)
        {
            logger.LogDebug("Cross-module struct extension {Type} from {Module}: no members from current module, skipping.",
                structDecl.Name, currentModule);
            return;
        }

        var className = $"{structDecl.Name}{currentModule}Extensions";
        var wrapperLibPath = typeDatabase.AsyncLibraryName ?? $"{currentModule}SwiftBindings";

        csWriter.WriteLine();
        csWriter.WriteLine($"/// <summary>");
        csWriter.WriteLine($"/// Extension methods for {structDecl.Name} defined in {currentModule}.");
        csWriter.WriteLine($"/// </summary>");
        csWriter.WriteLine($"public static partial class {className}");
        csWriter.WriteLine("{");
        csWriter.Indent++;

        int emittedCount = 0;
        var emittedSignatures = new HashSet<string>();
        var pinvokeDecls = new List<StructPInvokeInfo>();

        foreach (var property in properties)
        {
            if (TryEmitStructPropertyExtension(csWriter, swiftWriter, property, structDecl,
                origCSharpType, origSwiftTypeQualified, wrapperLibPath, currentModule,
                typeDatabase, emittedSignatures, pinvokeDecls, logger))
            {
                emittedCount++;
            }
        }

        foreach (var method in methods)
        {
            if (method.IsAccessor)
                continue;

            if (TryEmitStructMethodExtension(csWriter, swiftWriter, method, structDecl,
                origCSharpType, origSwiftTypeQualified, wrapperLibPath, currentModule,
                typeDatabase, emittedSignatures, pinvokeDecls, logger))
            {
                emittedCount++;
            }
        }

        if (emittedCount > 0)
        {
            csWriter.WriteLine();
            csWriter.WriteLine("private static partial class NativeMethods");
            csWriter.WriteLine("{");
            csWriter.Indent++;
            foreach (var info in pinvokeDecls)
            {
                PInvokeEmitHelper.EmitDeclaration(csWriter, new PInvokeEmissionInfo
                {
                    LibraryPath = wrapperLibPath,
                    EntryPoint = info.EntryPoint,
                    MethodName = info.MethodName,
                    ReturnType = info.ReturnType,
                    ParametersString = string.Join(", ", info.Parameters),
                    CallingConvention = PInvokeCallingConvention.Cdecl,
                    Visibility = PInvokeVisibility.Internal,
                });
                csWriter.WriteLine();
            }
            csWriter.Indent--;
            csWriter.WriteLine("}");
        }

        csWriter.Indent--;
        csWriter.WriteLine("}");
        csWriter.WriteLine();

        if (emittedCount > 0)
        {
            logger.LogInformation(
                "Emitted {Count} cross-module struct extension members for {Type} from {Module}",
                emittedCount, structDecl.Name, currentModule);
        }
    }

    private static bool TryEmitStructMethodExtension(
        CSharpWriter csWriter,
        SwiftWriter swiftWriter,
        MethodDecl method,
        StructDecl structDecl,
        string origCSharpType,
        string origSwiftTypeQualified,
        string wrapperLibPath,
        string currentModule,
        ITypeDatabase typeDatabase,
        HashSet<string> emittedSignatures,
        List<StructPInvokeInfo> pinvokeDecls,
        ILogger logger)
    {
        if (method.IsGeneric || method.IsAsync || method.Throws || method.IsMutating)
            return false;

        // Static methods on a struct don't take a self pointer — the only thing
        // that distinguishes the class-receiver static case is the receiver-class
        // type owning the wrapper. For struct extensions, the wrapper would still
        // need a path through the cdecl trampoline. Defer this until we see a
        // motivating fixture.
        if (method.MethodType == MethodType.Static)
            return false;

        var returnTypeSpec = method.CSSignature.Count > 0 ? method.CSSignature[0].SwiftTypeSpec : null;
        var returnCategory = ClassifyReturnType(returnTypeSpec, typeDatabase);
        if (returnCategory == null)
            return false;
        if (returnCategory == ReturnKind.NonFrozenStruct)
            return false;

        // ClassifyReturnType folds SimpleEnum into ReturnKind.Primitive (the shared
        // helper is also used by ForeignTypeExtensionEmitter where direct CallConvSwift
        // dispatch makes that fold correct). For the struct trampoline path the
        // distinction matters: the @_cdecl boundary cannot return a Swift enum directly,
        // so we must lower to the underlying integer and cast back in C#.
        var returnEnumLowering = TryGetReturnSimpleEnumLowering(returnTypeSpec, typeDatabase);
        if (returnCategory == ReturnKind.Primitive && returnTypeSpec is NamedTypeSpec retNamed &&
            returnEnumLowering == null &&
            ProbeIsUnsupportedSimpleEnumReturn(retNamed, typeDatabase))
        {
            // Simple enum that we cannot lower (e.g. String-raw) — skip cleanly.
            return false;
        }

        var parameters = new List<StructParamInfo>();
        for (int i = 1; i < method.CSSignature.Count; i++)
        {
            var arg = method.CSSignature[i];
            var paramCategory = ClassifyParameterType(arg.SwiftTypeSpec, typeDatabase);
            if (paramCategory == null || paramCategory == ParamKind.FrozenStruct)
                return false;

            var paramName = NameProvider.GetCSharpParameterName(arg);
            var csharpType = ResolveCSharpTypeName(arg.SwiftTypeSpec, typeDatabase);

            string? simpleEnumUnderlyingCS = null;
            string? simpleEnumUnderlyingSwift = null;
            string? simpleEnumQualifiedSwift = null;
            if (paramCategory == ParamKind.SimpleEnum &&
                TryGetSimpleEnumLowering(arg.SwiftTypeSpec, typeDatabase,
                    out simpleEnumUnderlyingCS, out simpleEnumUnderlyingSwift, out simpleEnumQualifiedSwift) == false)
            {
                // Classified as SimpleEnum but we cannot derive a raw integer lowering
                // (no TypeRecord, or an unsupported String raw value). Skip the method
                // rather than emit a trampoline whose Swift side would fail to compile.
                return false;
            }

            parameters.Add(new StructParamInfo(
                Name: paramName,
                CSharpType: csharpType,
                SwiftType: ExistentialBypassEmitter.RenderSwiftTypeSpec(arg.SwiftTypeSpec),
                Kind: paramCategory.Value,
                TypeSpec: arg.SwiftTypeSpec,
                SimpleEnumUnderlyingCSType: simpleEnumUnderlyingCS,
                SimpleEnumUnderlyingSwiftType: simpleEnumUnderlyingSwift,
                SimpleEnumQualifiedSwiftType: simpleEnumQualifiedSwift));
        }

        var methodName = NameProvider.ToPascalCase(method.Name);
        var signatureKey = $"instance:{methodName}({string.Join(",", parameters.Select(p => p.CSharpType))})";
        if (!emittedSignatures.Add(signatureKey))
            return false;

        // Symbol and pinvoke names — deterministic and overload-safe via a hash
        // of the Swift mangled name (falls back to the structural signature when
        // no mangled name is available so synthetic methods still hash distinctly).
        var hashSeed = !string.IsNullOrEmpty(method.MangledName)
            ? method.MangledName
            : $"{currentModule}|{origSwiftTypeQualified}|{method.Name}|{string.Join(",", parameters.Select(p => p.SwiftType))}";
        var symbolHash = EmitterUtility.DeterministicHash8(hashSeed);
        var symbolName = $"SBW_{currentModule}_Ext_{SafeTypeName(structDecl.Name)}_{method.Name}_{symbolHash}";
        var pinvokeName = $"PInvoke_{methodName}_{symbolHash}";

        var csharpReturnType = returnCategory.Value == ReturnKind.Void || returnTypeSpec == null
            ? "void"
            : ResolveCSharpTypeName(returnTypeSpec, typeDatabase);
        var publicReturnType = MapBoolType(csharpReturnType);

        // Build public extension parameter list
        var publicParamParts = new List<string> { $"this {origCSharpType} self" };
        foreach (var p in parameters)
        {
            publicParamParts.Add($"{MapBoolType(p.CSharpType)} {p.Name}");
        }

        csWriter.WriteLine();
        csWriter.WriteLine($"public static unsafe {publicReturnType} {methodName}({string.Join(", ", publicParamParts)})");
        csWriter.WriteLine("{");
        csWriter.Indent++;

        // The receiver is a by-value frozen struct parameter; it already lives on the
        // call stack so `&self` is a stable pointer for the duration of the method.
        // Wrapping in `fixed` triggers CS0213 ("already fixed expression").
        var pinvokeCallArgs = new List<string>();
        bool returnsViaResultPtr = returnCategory.Value == ReturnKind.FrozenStruct;
        if (returnsViaResultPtr)
        {
            csWriter.WriteLine($"{publicReturnType} __result = default;");
            pinvokeCallArgs.Add("(IntPtr)(&__result)");
        }
        foreach (var p in parameters)
        {
            pinvokeCallArgs.Add(GetCdeclArgExpression(p));
        }
        pinvokeCallArgs.Add($"(IntPtr)(&self)");

        var nativeCall = $"NativeMethods.{pinvokeName}({string.Join(", ", pinvokeCallArgs)})";
        EmitStructReturnMarshalling(csWriter, returnCategory.Value, nativeCall, publicReturnType, returnEnumLowering);

        csWriter.Indent--;
        csWriter.WriteLine("}");

        // P/Invoke param list
        var pinvokeParams = new List<string>();
        if (returnsViaResultPtr)
            pinvokeParams.Add("IntPtr __resultPtr");
        foreach (var p in parameters)
        {
            pinvokeParams.Add(BuildCdeclPInvokeParam(p, typeDatabase));
        }
        pinvokeParams.Add("IntPtr __self");

        pinvokeDecls.Add(new StructPInvokeInfo(
            EntryPoint: symbolName,
            MethodName: pinvokeName,
            ReturnType: GetCdeclPInvokeReturnType(returnTypeSpec, returnCategory.Value, typeDatabase, returnsViaResultPtr, returnEnumLowering),
            Parameters: pinvokeParams));

        // Swift @_cdecl trampoline
        EmitSwiftMethodTrampoline(swiftWriter, method, structDecl, origSwiftTypeQualified,
            symbolName, parameters, returnTypeSpec, returnCategory.Value, returnEnumLowering);

        return true;
    }

    private static bool TryEmitStructPropertyExtension(
        CSharpWriter csWriter,
        SwiftWriter swiftWriter,
        PropertyDecl property,
        StructDecl structDecl,
        string origCSharpType,
        string origSwiftTypeQualified,
        string wrapperLibPath,
        string currentModule,
        ITypeDatabase typeDatabase,
        HashSet<string> emittedSignatures,
        List<StructPInvokeInfo> pinvokeDecls,
        ILogger logger)
    {
        if (property.IsStatic)
            return false;

        var returnCategory = ClassifyReturnType(property.SwiftTypeSpec, typeDatabase);
        if (returnCategory == null || returnCategory.Value == ReturnKind.Void)
            return false;
        if (returnCategory.Value == ReturnKind.NonFrozenStruct)
            return false;

        var returnEnumLowering = TryGetReturnSimpleEnumLowering(property.SwiftTypeSpec, typeDatabase);
        if (returnCategory.Value == ReturnKind.Primitive && property.SwiftTypeSpec is NamedTypeSpec propNamed &&
            returnEnumLowering == null && ProbeIsUnsupportedSimpleEnumReturn(propNamed, typeDatabase))
        {
            return false;
        }

        var propertyName = NameProvider.ToPascalCase(property.Name);
        if (!emittedSignatures.Add($"Get{propertyName}"))
            return false;

        var csharpType = ResolveCSharpTypeName(property.SwiftTypeSpec, typeDatabase);
        var publicType = MapBoolType(csharpType);

        var getterAccessor = property.Accessors.OfType<GetAccessorDecl>().FirstOrDefault();
        if (getterAccessor == null)
            return false;

        var hashSeed = !string.IsNullOrEmpty(getterAccessor.Method.MangledName)
            ? getterAccessor.Method.MangledName
            : $"{currentModule}|{origSwiftTypeQualified}|get_{property.Name}";
        var symbolHash = EmitterUtility.DeterministicHash8(hashSeed);
        var symbolName = $"SBW_{currentModule}_Ext_{SafeTypeName(structDecl.Name)}_get_{property.Name}_{symbolHash}";
        var pinvokeName = $"PInvoke_Get{propertyName}_{symbolHash}";

        csWriter.WriteLine();
        csWriter.WriteLine($"public static unsafe {publicType} Get{propertyName}(this {origCSharpType} self)");
        csWriter.WriteLine("{");
        csWriter.Indent++;

        bool returnsViaResultPtr = returnCategory.Value == ReturnKind.FrozenStruct;
        var pinvokeCallArgs = new List<string>();
        if (returnsViaResultPtr)
        {
            csWriter.WriteLine($"{publicType} __result = default;");
            pinvokeCallArgs.Add("(IntPtr)(&__result)");
        }
        pinvokeCallArgs.Add($"(IntPtr)(&self)");

        var nativeCall = $"NativeMethods.{pinvokeName}({string.Join(", ", pinvokeCallArgs)})";
        EmitStructReturnMarshalling(csWriter, returnCategory.Value, nativeCall, publicType, returnEnumLowering);

        csWriter.Indent--;
        csWriter.WriteLine("}");

        var getterParams = new List<string>();
        if (returnsViaResultPtr)
            getterParams.Add("IntPtr __resultPtr");
        getterParams.Add("IntPtr __self");

        pinvokeDecls.Add(new StructPInvokeInfo(
            EntryPoint: symbolName,
            MethodName: pinvokeName,
            ReturnType: GetCdeclPInvokeReturnType(property.SwiftTypeSpec, returnCategory.Value, typeDatabase, returnsViaResultPtr, returnEnumLowering),
            Parameters: getterParams));

        EmitSwiftPropertyGetterTrampoline(swiftWriter, property, structDecl, origSwiftTypeQualified,
            symbolName, returnCategory.Value, returnEnumLowering);

        // Setter — primitives only, mirrors the class path's conservatism.
        var setterAccessor = property.Accessors.OfType<SetAccessorDecl>().FirstOrDefault();
        if (setterAccessor != null && returnCategory.Value == ReturnKind.Primitive)
        {
            var setterHashSeed = !string.IsNullOrEmpty(setterAccessor.Method.MangledName)
                ? setterAccessor.Method.MangledName
                : $"{currentModule}|{origSwiftTypeQualified}|set_{property.Name}";
            var setterHash = EmitterUtility.DeterministicHash8(setterHashSeed);
            var setterSymbol = $"SBW_{currentModule}_Ext_{SafeTypeName(structDecl.Name)}_set_{property.Name}_{setterHash}";
            var setterPInvoke = $"PInvoke_Set{propertyName}_{setterHash}";

            // SimpleEnum-typed properties must lower the value across the C ABI
            // for the same reason as getters: Swift @_cdecl cannot accept a Swift
            // enum directly. Cast (int)value on the C# side, declare the P/Invoke
            // parameter as the underlying scalar, and reconstruct T(rawValue:)! in
            // the Swift trampoline body.
            var setterEnumLowering = returnEnumLowering;

            csWriter.WriteLine();
            csWriter.WriteLine($"public static unsafe void Set{propertyName}(this ref {origCSharpType} self, {publicType} value)");
            csWriter.WriteLine("{");
            csWriter.Indent++;
            csWriter.WriteLine($"fixed ({origCSharpType}* __self = &self)");
            csWriter.WriteLine("{");
            csWriter.Indent++;
            var setterValueExpr = setterEnumLowering is { } se
                ? $"({se.UnderlyingCSType})value"
                : "value";
            csWriter.WriteLine($"NativeMethods.{setterPInvoke}({setterValueExpr}, (IntPtr)__self);");
            csWriter.Indent--;
            csWriter.WriteLine("}");
            csWriter.Indent--;
            csWriter.WriteLine("}");

            string setterParam;
            if (setterEnumLowering is { } sel)
                setterParam = $"{sel.UnderlyingCSType} value";
            else if (property.SwiftTypeSpec is NamedTypeSpec n && n.Name == "Swift.Bool")
                setterParam = "[MarshalAs(UnmanagedType.U1)] bool value";
            else
                setterParam = $"{ResolveCSharpTypeName(property.SwiftTypeSpec!, typeDatabase)} value";

            pinvokeDecls.Add(new StructPInvokeInfo(
                EntryPoint: setterSymbol,
                MethodName: setterPInvoke,
                ReturnType: "void",
                Parameters: new List<string> { setterParam, "IntPtr __self" }));

            EmitSwiftPropertySetterTrampoline(swiftWriter, property, structDecl, origSwiftTypeQualified,
                setterSymbol, setterEnumLowering);
        }

        return true;
    }

    // =================== Swift trampoline emission ===================

    private static void EmitSwiftMethodTrampoline(
        SwiftWriter swiftWriter,
        MethodDecl method,
        StructDecl structDecl,
        string origSwiftTypeQualified,
        string symbolName,
        List<StructParamInfo> parameters,
        TypeSpec? returnTypeSpec,
        ReturnKind returnCategory,
        SimpleEnumLowering? returnEnumLowering)
    {
        var swiftParams = new List<string>();
        bool returnsViaResultPtr = returnCategory == ReturnKind.FrozenStruct;
        if (returnsViaResultPtr)
            swiftParams.Add("_ __resultPtr: UnsafeMutableRawPointer");
        foreach (var p in parameters)
        {
            swiftParams.Add($"_ {p.Name}: {RenderSwiftParamType(p)}");
        }
        swiftParams.Add("_ self_: UnsafeRawPointer");

        string swiftReturn = returnCategory switch
        {
            ReturnKind.Void or ReturnKind.FrozenStruct => "",
            ReturnKind.Primitive when returnEnumLowering is { } e => " -> " + e.UnderlyingSwiftType,
            ReturnKind.Primitive => " -> " + ExistentialBypassEmitter.RenderSwiftTypeSpec(returnTypeSpec!),
            ReturnKind.ObjCClass or ReturnKind.SwiftClass => " -> UnsafeMutableRawPointer",
            _ => "",
        };

        swiftWriter.WriteLine();
        swiftWriter.WriteLine($"// Cross-module struct-extension @_cdecl trampoline for {origSwiftTypeQualified}.{method.Name}");
        swiftWriter.WriteLine($"@_cdecl(\"{symbolName}\")");
        swiftWriter.WriteLine($"public func _sbw_ext_{symbolName}({string.Join(", ", swiftParams)}){swiftReturn} {{");
        swiftWriter.Indent++;

        // SimpleEnum params arrive as raw scalars and must be reconstructed via
        // T(rawValue:). Use the guard-let / preconditionFailure shape (matching
        // CdeclParamMapper) so an invalid raw value traps with a descriptive
        // message instead of an opaque force-unwrap crash.
        foreach (var p in parameters)
        {
            if (p.Kind == ParamKind.SimpleEnum)
            {
                swiftWriter.WriteLine($"guard let {p.Name}Val = {p.SimpleEnumQualifiedSwiftType}(rawValue: {p.Name}) else {{ preconditionFailure(\"Invalid raw value \\({p.Name}) for {p.SimpleEnumQualifiedSwiftType}\") }}");
            }
        }

        swiftWriter.WriteLine($"let __self = self_.assumingMemoryBound(to: {origSwiftTypeQualified}.self).pointee");

        // Reconstruct call args using the Swift external label. ArgumentDecl.Name
        // is the external (call-site) label; PrivateName is the function-body name
        // when it differs. An external label of "_" means the parameter is unlabeled
        // at the call site, so it must NOT be prefixed in the trampoline body.
        var callArgs = new List<string>();
        for (int i = 1; i < method.CSSignature.Count; i++)
        {
            var argDecl = method.CSSignature[i];
            var p = parameters[i - 1];
            var label = argDecl.Name;
            var bound = ConvertCdeclSwiftArg(p);
            callArgs.Add(string.IsNullOrEmpty(label) || label == "_"
                ? bound
                : $"{label}: {bound}");
        }

        var callExpr = $"__self.{NameProvider.EscapeSwiftKeyword(method.Name)}({string.Join(", ", callArgs)})";

        switch (returnCategory)
        {
            case ReturnKind.Void:
                swiftWriter.WriteLine(callExpr);
                break;
            case ReturnKind.Primitive when returnEnumLowering is { }:
                // Simple-enum return: surface the raw value, not the enum itself.
                swiftWriter.WriteLine($"return {callExpr}.rawValue");
                break;
            case ReturnKind.Primitive:
                swiftWriter.WriteLine($"return {callExpr}");
                break;
            case ReturnKind.ObjCClass:
            case ReturnKind.SwiftClass:
                swiftWriter.WriteLine($"let __r = {callExpr}");
                swiftWriter.WriteLine("return Unmanaged.passRetained(__r).toOpaque()");
                break;
            case ReturnKind.FrozenStruct:
                // Module-qualified — unqualified Swift names can collide when the
                // current module imports several frameworks that each declare a type
                // by the same name, and `.initializeMemory(as: T.self)` would then
                // initialize the buffer as the wrong type.
                var retSwift = ExistentialBypassEmitter.RenderModuleQualifiedSwiftTypeSpec(returnTypeSpec!);
                swiftWriter.WriteLine($"let __r = {callExpr}");
                swiftWriter.WriteLine($"__resultPtr.initializeMemory(as: {retSwift}.self, repeating: __r, count: 1)");
                break;
        }

        swiftWriter.Indent--;
        swiftWriter.WriteLine("}");
    }

    private static void EmitSwiftPropertyGetterTrampoline(
        SwiftWriter swiftWriter,
        PropertyDecl property,
        StructDecl structDecl,
        string origSwiftTypeQualified,
        string symbolName,
        ReturnKind returnCategory,
        SimpleEnumLowering? returnEnumLowering)
    {
        bool returnsViaResultPtr = returnCategory == ReturnKind.FrozenStruct;
        var swiftParams = new List<string>();
        if (returnsViaResultPtr)
            swiftParams.Add("_ __resultPtr: UnsafeMutableRawPointer");
        swiftParams.Add("_ self_: UnsafeRawPointer");

        string swiftReturn = returnCategory switch
        {
            ReturnKind.Primitive when returnEnumLowering is { } e => " -> " + e.UnderlyingSwiftType,
            ReturnKind.Primitive => " -> " + ExistentialBypassEmitter.RenderSwiftTypeSpec(property.SwiftTypeSpec!),
            ReturnKind.ObjCClass or ReturnKind.SwiftClass => " -> UnsafeMutableRawPointer",
            ReturnKind.FrozenStruct => "",
            _ => "",
        };

        swiftWriter.WriteLine();
        swiftWriter.WriteLine($"// Cross-module struct-extension @_cdecl getter trampoline for {origSwiftTypeQualified}.{property.Name}");
        swiftWriter.WriteLine($"@_cdecl(\"{symbolName}\")");
        swiftWriter.WriteLine($"public func _sbw_ext_{symbolName}({string.Join(", ", swiftParams)}){swiftReturn} {{");
        swiftWriter.Indent++;
        swiftWriter.WriteLine($"let __self = self_.assumingMemoryBound(to: {origSwiftTypeQualified}.self).pointee");
        var accessExpr = $"__self.{NameProvider.EscapeSwiftKeyword(property.Name)}";

        switch (returnCategory)
        {
            case ReturnKind.Primitive when returnEnumLowering is { }:
                swiftWriter.WriteLine($"return {accessExpr}.rawValue");
                break;
            case ReturnKind.Primitive:
                swiftWriter.WriteLine($"return {accessExpr}");
                break;
            case ReturnKind.ObjCClass:
            case ReturnKind.SwiftClass:
                swiftWriter.WriteLine($"let __r = {accessExpr}");
                swiftWriter.WriteLine("return Unmanaged.passRetained(__r).toOpaque()");
                break;
            case ReturnKind.FrozenStruct:
                var retSwift = ExistentialBypassEmitter.RenderModuleQualifiedSwiftTypeSpec(property.SwiftTypeSpec!);
                swiftWriter.WriteLine($"let __r = {accessExpr}");
                swiftWriter.WriteLine($"__resultPtr.initializeMemory(as: {retSwift}.self, repeating: __r, count: 1)");
                break;
        }

        swiftWriter.Indent--;
        swiftWriter.WriteLine("}");
    }

    private static void EmitSwiftPropertySetterTrampoline(
        SwiftWriter swiftWriter,
        PropertyDecl property,
        StructDecl structDecl,
        string origSwiftTypeQualified,
        string symbolName,
        SimpleEnumLowering? enumLowering)
    {
        // SimpleEnum setters lower newValue to the raw scalar across the C ABI
        // and reconstruct the enum on assignment — see TryGetSimpleEnumLowering.
        // Use the guard-let / preconditionFailure shape (matching CdeclParamMapper)
        // so an invalid raw value traps with a descriptive message.
        var valueType = enumLowering is { } e
            ? e.UnderlyingSwiftType
            : ExistentialBypassEmitter.RenderSwiftTypeSpec(property.SwiftTypeSpec!);

        swiftWriter.WriteLine();
        swiftWriter.WriteLine($"// Cross-module struct-extension @_cdecl setter trampoline for {origSwiftTypeQualified}.{property.Name}");
        swiftWriter.WriteLine($"@_cdecl(\"{symbolName}\")");
        swiftWriter.WriteLine($"public func _sbw_ext_{symbolName}(_ newValue: {valueType}, _ self_: UnsafeMutableRawPointer) {{");
        swiftWriter.Indent++;
        if (enumLowering is { } el)
        {
            swiftWriter.WriteLine($"guard let newValueVal = {el.QualifiedSwiftType}(rawValue: newValue) else {{ preconditionFailure(\"Invalid raw value \\(newValue) for {el.QualifiedSwiftType}\") }}");
            swiftWriter.WriteLine($"self_.assumingMemoryBound(to: {origSwiftTypeQualified}.self).pointee.{NameProvider.EscapeSwiftKeyword(property.Name)} = newValueVal");
        }
        else
        {
            swiftWriter.WriteLine($"self_.assumingMemoryBound(to: {origSwiftTypeQualified}.self).pointee.{NameProvider.EscapeSwiftKeyword(property.Name)} = newValue");
        }
        swiftWriter.Indent--;
        swiftWriter.WriteLine("}");
    }

    // =================== Marshalling helpers ===================

    private static void EmitStructReturnMarshalling(
        CSharpWriter csWriter,
        ReturnKind category,
        string nativeCall,
        string csharpType,
        SimpleEnumLowering? returnEnumLowering)
    {
        switch (category)
        {
            case ReturnKind.Void:
                csWriter.WriteLine($"{nativeCall};");
                break;
            case ReturnKind.Primitive when returnEnumLowering is { }:
                // The P/Invoke returns the underlying integer; cast back to the public enum.
                csWriter.WriteLine($"return ({csharpType}){nativeCall};");
                break;
            case ReturnKind.Primitive:
                csWriter.WriteLine($"return {nativeCall};");
                break;
            case ReturnKind.ObjCClass:
                csWriter.WriteLine($"var result = {nativeCall};");
                csWriter.WriteLine($"return {MarshallingHelpers.FormatObjCBridgeCall(csharpType, "result", nonNull: true)};");
                break;
            case ReturnKind.SwiftClass:
                csWriter.WriteLine($"var result = {nativeCall};");
                csWriter.WriteLine($"return ({csharpType})SwiftMarshal.MarshalFromSwift<{csharpType}>(result);");
                break;
            case ReturnKind.FrozenStruct:
                csWriter.WriteLine($"{nativeCall};");
                csWriter.WriteLine("return __result;");
                break;
        }
    }

    private static string GetCdeclArgExpression(StructParamInfo p) => p.Kind switch
    {
        ParamKind.Primitive => p.Name,
        ParamKind.ObjCClass => $"{p.Name}.Handle",
        ParamKind.SwiftClass => $"{p.Name}.Payload.DangerousGetHandle()",
        // Simple enums lower to their raw integer across the cdecl boundary.
        // The C# enum's underlying type matches the Swift RawValue (e.g. Int32 -> int),
        // so the cast is a no-op at runtime but enforces the correct P/Invoke shape.
        ParamKind.SimpleEnum => $"({p.SimpleEnumUnderlyingCSType}){p.Name}",
        _ => p.Name,
    };

    private static string BuildCdeclPInvokeParam(StructParamInfo p, ITypeDatabase typeDatabase) => p.Kind switch
    {
        ParamKind.Primitive => p.TypeSpec is NamedTypeSpec n && n.Name == "Swift.Bool"
            ? $"[MarshalAs(UnmanagedType.U1)] bool {p.Name}"
            : $"{ResolveCSharpTypeName(p.TypeSpec, typeDatabase)} {p.Name}",
        ParamKind.ObjCClass => $"IntPtr {p.Name}",
        ParamKind.SwiftClass => $"IntPtr {p.Name}",
        // The C# P/Invoke takes the underlying integer, NOT the enum type — Swift's
        // @_cdecl trampoline cannot accept a Swift enum directly across the C ABI.
        ParamKind.SimpleEnum => $"{p.SimpleEnumUnderlyingCSType} {p.Name}",
        _ => $"IntPtr {p.Name}",
    };

    private static string GetCdeclPInvokeReturnType(
        TypeSpec? returnTypeSpec,
        ReturnKind category,
        ITypeDatabase typeDatabase,
        bool returnsViaResultPtr,
        SimpleEnumLowering? returnEnumLowering)
    {
        if (returnsViaResultPtr)
            return "void";
        return category switch
        {
            ReturnKind.Void => "void",
            ReturnKind.Primitive when returnEnumLowering is { } e => e.UnderlyingCSType,
            ReturnKind.Primitive => returnTypeSpec is NamedTypeSpec n && n.Name == "Swift.Bool"
                ? "bool"
                : ResolveCSharpTypeName(returnTypeSpec!, typeDatabase),
            ReturnKind.ObjCClass => "IntPtr",
            ReturnKind.SwiftClass => "IntPtr",
            _ => "void",
        };
    }

    private static string RenderSwiftParamType(StructParamInfo p) => p.Kind switch
    {
        ParamKind.Primitive => p.SwiftType,
        ParamKind.ObjCClass => "UnsafeMutableRawPointer",
        ParamKind.SwiftClass => "UnsafeMutableRawPointer",
        // Swift @_cdecl signature uses the raw scalar; the body reconstructs the
        // enum via T(rawValue:). See ConvertCdeclSwiftArg.
        ParamKind.SimpleEnum => p.SimpleEnumUnderlyingSwiftType!,
        _ => "UnsafeMutableRawPointer",
    };

    private static string ConvertCdeclSwiftArg(StructParamInfo p) => p.Kind switch
    {
        ParamKind.Primitive => p.Name,
        ParamKind.ObjCClass => $"(Unmanaged<AnyObject>.fromOpaque({p.Name}).takeUnretainedValue() as! {p.SwiftType})",
        ParamKind.SwiftClass => $"Unmanaged<{p.SwiftType}>.fromOpaque({p.Name}).takeUnretainedValue()",
        // The enum was reconstructed via guard-let above the call site (see
        // EmitSwiftMethodTrampoline). Reference the bound local here.
        ParamKind.SimpleEnum => $"{p.Name}Val",
        _ => p.Name,
    };

    /// <summary>
    /// Resolves a SimpleEnum TypeSpec to the raw-integer lowering used at the
    /// cdecl boundary: the C# underlying integer name (e.g. "int"), the matching
    /// Swift scalar (e.g. "Int32"), and the fully module-qualified Swift enum
    /// name for <c>T(rawValue:)</c> reconstruction.
    ///
    /// Returns false for any enum we cannot lower to a single integer:
    /// String-raw enums (not blittable across the C ABI) and no-raw simple
    /// enums (no <c>init(rawValue:)</c> / <c>.rawValue</c>; reconstructed via
    /// tag-switch lowering elsewhere). The existing free-function wrappers
    /// cover those surfaces; this trampoline path only handles integer-raw.
    /// </summary>
    private static bool TryGetSimpleEnumLowering(
        TypeSpec typeSpec,
        ITypeDatabase typeDatabase,
        out string? underlyingCSType,
        out string? underlyingSwiftType,
        out string? qualifiedSwiftType)
    {
        underlyingCSType = null;
        underlyingSwiftType = null;
        qualifiedSwiftType = null;

        if (typeSpec is not NamedTypeSpec named)
            return false;

        SwiftTypeName swiftTypeName;
        try
        {
            swiftTypeName = SwiftTypeName.FromModuleQualifiedName(named.Name);
        }
        catch (ArgumentException)
        {
            return false;
        }

        if (!typeDatabase.TryGetTypeRecord(swiftTypeName, out var record))
            return false;
        if (record.Kind != TypeRecordKind.Enum || !record.Flags.HasFlag(TypeRecordFlags.SimpleEnum))
            return false;

        // String-raw enums are not blittable across the C ABI.
        // No-raw simple enums lack init(rawValue:) / .rawValue — routing them through
        // the integer-raw lowering would emit Swift that fails to compile.
        // Lower only integer-raw simple enums on this trampoline surface.
        if (string.IsNullOrEmpty(record.RawValueTypeName) || record.RawValueTypeName == "String")
            return false;

        underlyingCSType = EnumHandler.GetCSharpEnumUnderlyingType(record.RawValueTypeName);
        underlyingSwiftType = EnumHandler.GetSwiftScalarType(underlyingCSType);
        qualifiedSwiftType = record.SwiftTypeName.ModuleQualifiedName;
        return true;
    }

    private static SimpleEnumLowering? TryGetReturnSimpleEnumLowering(TypeSpec? typeSpec, ITypeDatabase typeDatabase)
    {
        if (typeSpec == null)
            return null;
        if (!TryGetSimpleEnumLowering(typeSpec, typeDatabase, out var cs, out var sw, out var qs))
            return null;
        return new SimpleEnumLowering(cs!, sw!, qs!);
    }

    /// <summary>
    /// Distinguishes "Primitive-classified SimpleEnum that we cannot lower" from a
    /// real primitive. Returns true ONLY when the TypeSpec resolves to a simple
    /// enum type record AND <see cref="TryGetSimpleEnumLowering"/> would have failed.
    /// Caller already classified this as Primitive, so the type is either a true
    /// scalar or an unsupported SimpleEnum (String-raw or no-raw). The probe lets
    /// us skip the latter cleanly instead of emitting a Swift trampoline whose
    /// <c>@_cdecl</c> would fail to compile.
    /// </summary>
    private static bool ProbeIsUnsupportedSimpleEnumReturn(NamedTypeSpec named, ITypeDatabase typeDatabase)
    {
        SwiftTypeName swiftTypeName;
        try
        {
            swiftTypeName = SwiftTypeName.FromModuleQualifiedName(named.Name);
        }
        catch (ArgumentException)
        {
            return false;
        }
        if (!typeDatabase.TryGetTypeRecord(swiftTypeName, out var record))
            return false;
        if (record.Kind != TypeRecordKind.Enum || !record.Flags.HasFlag(TypeRecordFlags.SimpleEnum))
            return false;
        // String-raw and no-raw fail the lowering. Integer-raw passes; for those
        // TryGetReturnSimpleEnumLowering already produced a non-null lowering, so
        // this probe never gets asked about them.
        return string.IsNullOrEmpty(record.RawValueTypeName) || record.RawValueTypeName == "String";
    }

    private readonly record struct SimpleEnumLowering(
        string UnderlyingCSType,
        string UnderlyingSwiftType,
        string QualifiedSwiftType);

    private static string SafeTypeName(string name) => name.Replace(".", "_");

    private static string MapBoolType(string csharpType) =>
        MarshallingHelpers.IsBoolType(csharpType) ? "bool" : csharpType;

    private readonly record struct StructParamInfo(
        string Name,
        string CSharpType,
        string SwiftType,
        ParamKind Kind,
        TypeSpec TypeSpec,
        // Populated when Kind == SimpleEnum. The cdecl boundary lowers the enum
        // to its raw integer (RawValueTypeName-derived C# type) and rebuilds via
        // T(rawValue:) on the Swift side. Both are non-null for SimpleEnum kinds.
        string? SimpleEnumUnderlyingCSType = null,
        string? SimpleEnumUnderlyingSwiftType = null,
        string? SimpleEnumQualifiedSwiftType = null);

    private readonly record struct StructPInvokeInfo(
        string EntryPoint,
        string MethodName,
        string ReturnType,
        List<string> Parameters);
}
