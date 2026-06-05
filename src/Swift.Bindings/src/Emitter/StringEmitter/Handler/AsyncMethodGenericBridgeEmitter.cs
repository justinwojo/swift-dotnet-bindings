// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Async/throws counterpart to <see cref="MethodGenericBridgeEmitter"/>.
///
/// Emits @_cdecl Swift wrappers for async (and throws) methods with method-level
/// generic parameters constrained to a class-bound protocol without Self or
/// associated-type requirements.
///
/// The bridge uses Swift 5.7+ implicit existential opening exactly like the sync
/// emitter. The Swift wrapper takes the generic argument as <c>UnsafeRawPointer</c>,
/// recovers the heap object via <c>Unmanaged&lt;AnyObject&gt;.fromOpaque(...)</c>,
/// casts it to <c>any &lt;ConstraintProtocol&gt;</c>, and Swift opens the existential
/// implicitly when calling the original generic method. The @_cdecl wrapper itself
/// is non-generic, so it lives at module scope and survives Swift's symbol-mangling
/// rules for generic specialization.
///
/// Pipeline placement: this adapter must run BEFORE
/// <see cref="MethodGenericBridgeAdapter"/> in the bridge dispatch table — both
/// would match the same eligibility shape, but the sync emitter is gated by
/// <c>!IsAsync &amp;&amp; !Throws</c> while this one requires the opposite.
///
/// V1 return-shape support:
/// <list type="bullet">
///   <item>void</item>
///   <item>primitive blittable (Int, Int32, Bool, Float, Double, ...)</item>
///   <item>Swift class — Unmanaged.passRetained on Swift, ARC-owning ctor on C#</item>
///   <item>complex value type — heap-allocated indirect via Swift
///         <c>UnsafeMutableRawPointer.allocate</c> + <c>initializeMemory(as:)</c>.
///         The C# callback dispatches by projection shape (mirroring
///         <see cref="AsyncHarnessEmitter"/>'s <c>cbTakesOwnership</c>/
///         <c>carrierNeedsDestroy</c> selector): frozen-blittable structs and
///         simple enums value-copy via <c>MarshalFromSwift&lt;T&gt;</c>; frozen-with-refs
///         (<c>ClassWithBufferStruct</c>) <c>MarshalFromSwift</c> + VWT-Destroy
///         the carrier; non-frozen / complex-enum (<c>ClassWithOpaquePayload</c>)
///         <c>NativeMemory.Alloc</c> a fresh <c>__resultBuf</c>, <c>InitializeWithCopy</c>
///         the carrier into it, VWT-Destroy the carrier (the SafeHandle owns
///         <c>__resultBuf</c> and frees it via <c>NativeMemory.Free</c> in
///         <c>ReleaseHandle</c>). The Swift carrier is freed in <c>finally</c>
///         via the per-module <c>SBW_Free</c> helper (allocator-paired with
///         <c>.allocate</c> / <c>.deallocate()</c>).</item>
/// </list>
/// V1 explicitly excludes: tuple, string, array-of-string, generic collection,
/// ObjC-bridgeable, optional-class. These all have specialised harness shapes that
/// the bridge would have to recreate; punt to a future V2 if real-world demand
/// surfaces.
///
/// Default-parameter handling: when the original method has trailing default-valued
/// parameters (the StoreKit2 <c>Product.purchase&lt;S: UIScene&gt;(confirmIn:, options: Set&lt;…&gt; = [])</c>
/// shape), the bridge emits both:
/// <list type="bullet">
///   <item>The FULL primary @_cdecl wrapper (all params, including the defaulted Set/Array/Dictionary
///         marshalled through the existing projection plan via the
///         <c>AsyncDeferredDisposeList</c> hand-off).</item>
///   <item>One trim @_cdecl wrapper per trailing-default level, each calling the
///         original Swift method with fewer args so Swift fills the rest from the
///         defaults declared on the source signature.</item>
/// </list>
/// All variants share the same C# success/error callback delegate fields (the
/// callback shape depends only on the return type, which is identical across
/// overloads).
/// </summary>
public static class AsyncMethodGenericBridgeEmitter
{
    /// <summary>
    /// Maximum number of trim variants emitted (parity with
    /// <see cref="DefaultParameterOverloadEmitter.MaxOverloads"/>).
    /// </summary>
    private const int MaxOverloads = 4;

    /// <summary>
    /// Information about an eligible method-level generic parameter.
    /// Mirrors <see cref="MethodGenericBridgeEmitter"/>'s record so the eligibility
    /// helpers can be kept structurally identical.
    /// </summary>
    internal record GenericParamInfo(
        GenericArgumentDecl Param,
        string SwiftParamName,
        SwiftTypeName ConstraintProtocol,
        string ConstraintProtocolSwiftName);

    /// <summary>
    /// V1-supported return shape classification.
    /// </summary>
    private enum AsyncReturnKind
    {
        Void,
        Primitive,
        SwiftClass,
        ComplexValue,
    }

    /// <summary>
    /// Attempts to emit an async method-generic bridge for the given method.
    /// Returns true if the method was handled (caller should skip normal emission).
    /// </summary>
    public static bool TryEmit(
        CSharpWriter csWriter,
        SwiftWriter swiftWriter,
        MethodEnvironment env,
        TypeDecl? parentDecl,
        ModuleEmissionContext? ctx = null)
    {
        ctx ??= ModuleEmissionContext.Default;
        var methodDecl = env.MethodDecl;

        if (methodDecl.IsConstructor) return false;
        if (methodDecl.IsAccessor) return false;
        if (!methodDecl.IsAsync) return false; // sync goes through MethodGenericBridgeEmitter
        if (methodDecl.UsesWrapperLibrary) return false;
        if (methodDecl.IsProtocolExtensionMethod) return false;

        if (!WrapperValidation.HasMethodOwnGenericParameters(methodDecl))
            return false;

        // Must be on a type (parent generics excluded for parity with sync version)
        if (parentDecl == null) return false;
        if (parentDecl.IsGeneric) return false;

        var genericInfo = FindEligibleGenericParam(methodDecl, env.TypeDatabase);
        if (genericInfo == null) return false;

        if (!AreNonGenericParamsCompatible(methodDecl, genericInfo, env.TypeDatabase))
            return false;

        // Generic param must not appear in the return type (V1 keeps return-shape
        // classification simple: opening only occurs at param positions).
        if (ReturnContainsGenericParam(methodDecl, genericInfo.Param.TypeName))
            return false;

        if (WrapperValidation.IsMetatypeTypeIncludingOptional(methodDecl.CSSignature[0].SwiftTypeSpec))
            return false;

        if (!WrapperValidation.IsXCFrameworkMode(env.TypeDatabase))
            return false;

        // Classify the return shape; bail if outside V1 scope.
        var returnTypeSpec = methodDecl.CSSignature[0].SwiftTypeSpec;
        var returnKind = ClassifyReturnKind(returnTypeSpec, env.TypeDatabase, out var returnTypeRecord);
        if (returnKind == null) return false;

        var moduleName = parentDecl.SwiftTypeName.Module;
        var typeName = parentDecl.Name;
        var mangledHash = EmitterUtility.DeterministicHash8(methodDecl.MangledName);
        var primaryCdeclSymbol = $"SBW_{moduleName}_{typeName.Replace(".", "_")}_{methodDecl.Name}_{mangledHash}_XMA";

        // Compute trim variants. Defaulted args that the user did not pass are dropped
        // from the trim @_cdecl wrapper; Swift fills them from the source defaults.
        int trailingDefaultCount = DefaultParameterOverloadEmitter.CountTrailingDefaults(methodDecl);
        bool allMappable = DefaultParameterOverloadEmitter.AllTrailingDefaultsAreCSharpMappable(
            methodDecl, env.TypeDatabase);
        int maxTrim = allMappable ? 0 : Math.Min(trailingDefaultCount, MaxOverloads);

        // Route the C# emitter through the wrapper library so
        // WrapperValidation.IsSkippedWrapperDirectPInvoke (line 1624 kill-gate) lets
        // this method survive — without UsesWrapperLibrary the async-generic killer
        // would silently drop our emission.
        methodDecl.UsesWrapperLibrary = true;
        methodDecl.UsesFreeFunctionWrapper = true;
        methodDecl.HasGenericClosureBridge = true; // suppresses normal async path
        methodDecl.MangledName = primaryCdeclSymbol;

        // Emit shared C# infrastructure (cancel P/Invoke + success/error callbacks)
        // before the per-variant emission loop. Callback fields are tied to method
        // name + hash so all overloads share them.
        EmitSharedCSharpInfra(csWriter, env, parentDecl, returnKind.Value, returnTypeSpec, returnTypeRecord, ctx);

        // Emit FULL primary (trim 0) + each trim variant (trim 1..maxTrim).
        for (int trim = 0; trim <= maxTrim; trim++)
        {
            var keptArgs = ComputeKeptNonGenericArgs(methodDecl, trim);

            string variantSymbol = trim == 0
                ? primaryCdeclSymbol
                : $"{primaryCdeclSymbol}_T{trim}";
            string variantPInvokeName = trim == 0
                ? $"{NameProvider.GetPInvokeName(methodDecl)}_XMA"
                : $"{NameProvider.GetPInvokeName(methodDecl)}_T{trim}_XMA";

            EmitSwiftWrapper(swiftWriter, env, parentDecl, genericInfo, variantSymbol, keptArgs,
                returnKind.Value, returnTypeSpec, returnTypeRecord, ctx);
            EmitPInvoke(csWriter, env, methodDecl, genericInfo, variantSymbol, variantPInvokeName,
                env.TypeDatabase.AsyncLibraryName ?? env.TypeDatabase.GetLibraryPath(methodDecl.ModuleDecl!.Name),
                methodDecl.Throws, keptArgs);
            EmitPublicTaskMethod(csWriter, env, parentDecl, genericInfo, variantPInvokeName,
                returnKind.Value, returnTypeSpec, returnTypeRecord, methodDecl.Throws, keptArgs);
        }

        methodDecl.WasEmitted = true;
        return true;
    }

    /// <summary>
    /// Eligibility check for the placeholder gate / validator.
    /// Returns true when the method is a candidate for this bridge — the actual
    /// emission still verifies XCFramework mode and return-shape classification.
    /// </summary>
    public static bool IsEligible(MethodDecl method, ITypeDatabase typeDatabase)
    {
        if (method.IsConstructor || method.IsAccessor) return false;
        if (!method.IsAsync) return false;
        if (method.UsesWrapperLibrary || method.IsProtocolExtensionMethod) return false;
        if (!WrapperValidation.HasMethodOwnGenericParameters(method)) return false;
        if (method.ParentDecl is not TypeDecl parentDecl || parentDecl.IsGeneric) return false;
        if (!WrapperValidation.IsXCFrameworkMode(typeDatabase)) return false;

        var genericInfo = FindEligibleGenericParam(method, typeDatabase);
        if (genericInfo == null) return false;
        if (!AreNonGenericParamsCompatible(method, genericInfo, typeDatabase)) return false;
        if (ReturnContainsGenericParam(method, genericInfo.Param.TypeName)) return false;
        if (WrapperValidation.IsMetatypeTypeIncludingOptional(method.CSSignature[0].SwiftTypeSpec))
            return false;

        return ClassifyReturnKind(method.CSSignature[0].SwiftTypeSpec, typeDatabase, out _) != null;
    }

    // ─── Eligibility Helpers ─────────────────────────────────────────

    internal static GenericParamInfo? FindEligibleGenericParam(MethodDecl methodDecl, ITypeDatabase typeDatabase)
    {
        var parentTypeParamNames = methodDecl.ParentDecl is TypeDecl td && td.IsGeneric
            ? new HashSet<string>(td.GenericParameters.Select(p => p.TypeName))
            : new HashSet<string>();

        var ownParams = methodDecl.GenericParameters
            .Where(p => !parentTypeParamNames.Contains(p.TypeName))
            .ToList();

        if (ownParams.Count != 1) return null;

        var param = ownParams[0];

        // Must be class-bound — opening via Unmanaged<AnyObject> only works for heap
        // allocations. Struct/enum conformers would crash at fromOpaque time.
        // Class-boundedness comes from either:
        //   (a) explicit AnyObject conformance on the generic param, or
        //   (b) a protocol conformance whose target protocol is itself class-bound
        //       (e.g. `protocol P: AnyObject` — the ABI JSON only lists P on the
        //       generic param, not AnyObject, so we must look up P's record).
        var hasExplicitAnyObject = param.GenericConformances
            .Any(c => c.Kind == ConformanceKind.Protocol &&
                       (c.ConformanceTarget.Name == "AnyObject" ||
                        c.ConformanceTarget.ModuleQualifiedName == "Swift.AnyObject"));

        var protocolConformances = param.GenericConformances
            .Where(c => c.Kind == ConformanceKind.Protocol &&
                        c.ConformanceTarget.Name != "AnyObject" &&
                        c.ConformanceTarget.ModuleQualifiedName != "Swift.AnyObject")
            .ToList();
        if (protocolConformances.Count != 1) return null;

        var protocolConformance = protocolConformances[0];
        var protocolName = protocolConformance.ConformanceTarget;

        var hasTransitiveClassBound = !hasExplicitAnyObject &&
            typeDatabase.TryGetTypeRecord(protocolName, out var protocolRecord) &&
            protocolRecord.Kind == TypeRecordKind.Protocol &&
            (protocolRecord.Flags & TypeRecordFlags.ClassBound) != 0;

        if (!hasExplicitAnyObject && !hasTransitiveClassBound) return null;

        if (HasSelfOrAssociatedTypeRequirements(param, protocolName, typeDatabase))
            return null;

        if (!GenericParamOnlyInDirectPositions(methodDecl, param.TypeName))
            return null;

        return new GenericParamInfo(
            param,
            param.TypeName,
            protocolName,
            protocolName.ModuleQualifiedName);
    }

    private static bool HasSelfOrAssociatedTypeRequirements(
        GenericArgumentDecl param, SwiftTypeName protocolName, ITypeDatabase typeDatabase)
    {
        if (param.AssosiatedTypeConformances.Count > 0) return true;

        var name = protocolName.ModuleQualifiedName;
        if (name is "Swift.Equatable" or "Swift.Hashable" or "Swift.Comparable"
            or "Swift.Identifiable" or "Swift.RawRepresentable"
            or "Swift.Strideable" or "Swift.Numeric" or "Swift.SignedNumeric"
            or "Swift.BinaryInteger" or "Swift.FloatingPoint"
            or "Swift.FixedWidthInteger" or "Swift.UnsignedInteger" or "Swift.SignedInteger"
            or "Swift.StringProtocol" or "Swift.CodingKey"
            or "Swift.Encodable" or "Swift.Decodable" or "Swift.Codable"
            or "Swift.Sequence" or "Swift.Collection" or "Swift.MutableCollection"
            or "Swift.BidirectionalCollection" or "Swift.RandomAccessCollection"
            or "Swift.RangeReplaceableCollection" or "Swift.LazySequenceProtocol"
            or "Swift.IteratorProtocol" or "Swift.SetAlgebra"
            or "Swift.AdditiveArithmetic" or "Swift.ExpressibleByIntegerLiteral"
            or "Swift.ExpressibleByFloatLiteral" or "Swift.ExpressibleByStringLiteral"
            or "Swift.ExpressibleByBooleanLiteral" or "Swift.ExpressibleByNilLiteral"
            or "Swift.ExpressibleByArrayLiteral" or "Swift.ExpressibleByDictionaryLiteral"
            or "Foundation.NSObjectProtocol")
        {
            return true;
        }

        if (MethodValidationGates.IsUnsupportedProtocolConstraint(protocolName, typeDatabase))
            return true;

        return false;
    }

    private static bool GenericParamOnlyInDirectPositions(MethodDecl methodDecl, string genericParamName)
    {
        foreach (var arg in methodDecl.CSSignature.Skip(1))
        {
            if (!ContainsGenericParam(arg.SwiftTypeSpec, genericParamName)) continue;
            if (arg.SwiftTypeSpec is not NamedTypeSpec named || named.Name != genericParamName)
                return false;
            if (arg.IsInOut) return false;
        }
        return true;
    }

    private static bool ReturnContainsGenericParam(MethodDecl methodDecl, string genericParamName)
    {
        if (methodDecl.CSSignature.Count == 0) return false;
        return ContainsGenericParam(methodDecl.CSSignature[0].SwiftTypeSpec, genericParamName);
    }

    private static bool ContainsGenericParam(TypeSpec typeSpec, string paramName)
    {
        switch (typeSpec)
        {
            case AssociatedTypeReferenceSpec assocRef:
                return assocRef.BaseType == paramName;
            case NamedTypeSpec named:
                if (named.Name == paramName) return true;
                if (named.Name.StartsWith(paramName + ".")) return true;
                return named.GenericParameters.Any(gp => ContainsGenericParam(gp, paramName));
            case TupleTypeSpec tuple:
                return tuple.Elements.Any(e => ContainsGenericParam(e, paramName));
            case ClosureTypeSpec closure:
                return ContainsGenericParam(closure.Arguments, paramName) ||
                       ContainsGenericParam(closure.ReturnType, paramName);
            default:
                return false;
        }
    }

    /// <summary>
    /// Returns true when a defaulted Set&lt;T&gt; argument can be marshalled by the
    /// bridge through <see cref="SetProjection"/> + the AsyncDeferredDisposeList
    /// hand-off. V1: only Set&lt;T&gt;. Array/Dictionary could follow the same
    /// pattern but are not yet exercised by real-world fixtures.
    /// </summary>
    private static bool IsBridgeableDefaultedContainer(ArgumentDecl arg, ITypeDatabase typeDatabase)
    {
        if (!arg.HasDefaultArg) return false;
        if (!MarshallingHelpers.IsSwiftSet(arg.SwiftTypeSpec)) return false;

        var factory = new TypeProjectionFactory();
        var projection = factory.Project(arg.SwiftTypeSpec, new ProjectionContext
        {
            TypeDatabase = typeDatabase,
            IsParameter = true,
        });
        return projection is SetProjection;
    }

    private static bool AreNonGenericParamsCompatible(
        MethodDecl method, GenericParamInfo genericInfo, ITypeDatabase typeDatabase)
    {
        foreach (var arg in method.CSSignature.Skip(1))
        {
            if (arg.SwiftTypeSpec is NamedTypeSpec named && named.Name == genericInfo.Param.TypeName)
                continue;
            if (DefaultParameterOverloadEmitter.IsDebugParameter(arg)) continue;

            // Defaulted Set<T> args travel through SetProjection in the FULL primary
            // and are dropped (Swift fills the default) in trim variants — both shapes
            // are produced by the bridge.
            if (IsBridgeableDefaultedContainer(arg, typeDatabase))
                continue;

            // ABI passability allowlist is canonical on MethodClosureBridge.IsAbiCategoryPassable.
            // Non-defaulted args of unsupported shapes still fail the bridge. Defaulted args
            // of unsupported shapes are tolerated only when the bridge can drop them entirely
            // via a trim — but the FULL primary would have to materialise them, so fail
            // eligibility here.
            if (!MethodClosureBridge.IsAbiCategoryPassable(
                    MethodClosureBridge.ClassifyParam(arg, typeDatabase)))
            {
                return false;
            }
        }
        return true;
    }

    private static AsyncReturnKind? ClassifyReturnKind(
        TypeSpec returnTypeSpec, ITypeDatabase typeDatabase, out TypeRecord? typeRecord)
    {
        typeRecord = null;
        if (returnTypeSpec.IsEmptyTuple)
            return AsyncReturnKind.Void;

        // Reject tuples, closures, ProtocolList composition, etc. outright — V1 scope.
        if (returnTypeSpec is not NamedTypeSpec namedReturn)
            return null;

        // String, Array<String>, generic collections, optional types — bail. Their
        // harness shapes need specialised marshalling we don't recreate in V1.
        if (namedReturn.ContainsGenericParameters)
            return null;

        if (!IsSwiftPrimitive(namedReturn.Name))
        {
            // Look up the type record so we can distinguish class-vs-value.
            if (!typeDatabase.TryGetTypeRecord(namedReturn, out var rec))
                return null;
            typeRecord = rec;

            // ObjC-bridged / ObjC-rooted / ObjC-bridgeable value types — punt for V1.
            if (MarshallingHelpers.IsObjCBridged(rec)
                || MarshallingHelpers.IsObjCRooted(rec)
                || MarshallingHelpers.IsObjCBridgeable(rec))
                return null;

            if (rec.Kind == TypeRecordKind.Class)
                return AsyncReturnKind.SwiftClass;

            // Frozen-blittable / non-frozen struct / enum — heap-indirect.
            if (rec.Kind == TypeRecordKind.Struct || rec.Kind == TypeRecordKind.Enum)
                return AsyncReturnKind.ComplexValue;

            return null;
        }

        return AsyncReturnKind.Primitive;
    }

    private static bool IsSwiftPrimitive(string swiftTypeName)
        => ClosureEmitter.IsSwiftPrimitive(swiftTypeName);

    /// <summary>
    /// Returns the non-empty, non-debug arguments to keep for a given trim level.
    /// Trim N drops the last N trailing defaulted args.
    /// </summary>
    private static List<ArgumentDecl> ComputeKeptNonGenericArgs(MethodDecl methodDecl, int trim)
    {
        var allArgs = methodDecl.CSSignature.Skip(1)
            .Where(a => !a.SwiftTypeSpec.IsEmptyTuple)
            .Where(a => !DefaultParameterOverloadEmitter.IsDebugParameter(a))
            .ToList();
        if (trim == 0) return allArgs;

        // Drop last `trim` consecutive trailing defaulted args (matches CountTrailingDefaults).
        int remaining = trim;
        for (int i = allArgs.Count - 1; i >= 0 && remaining > 0; i--, remaining--)
        {
            if (!allArgs[i].HasDefaultArg) break;
            allArgs.RemoveAt(i);
        }
        return allArgs;
    }

    // ─── Swift Wrapper Generation ────────────────────────────────────

    private static void EmitSwiftWrapper(
        SwiftWriter swiftWriter,
        MethodEnvironment env,
        TypeDecl parentDecl,
        GenericParamInfo genericInfo,
        string cdeclSymbol,
        List<ArgumentDecl> keptArgs,
        AsyncReturnKind returnKind,
        TypeSpec returnTypeSpec,
        TypeRecord? returnTypeRecord,
        ModuleEmissionContext ctx)
    {
        // S5 audited (Tier B): the `_XMA` suffix distinguishes async-generic-bridge symbols
        // from both `_XM` (sync generic bridge) and unsuffixed plain method wrappers.
        // Inter-emitter collision is impossible by the suffix convention.
        if (!ctx.TryAddMethodWrapperSymbol(cdeclSymbol))
            return;

        var methodDecl = env.MethodDecl;
        bool throws = methodDecl.Throws;
        bool isClass = parentDecl is ClassDecl;
        bool isInstance = methodDecl.MethodType != MethodType.Static;
        var moduleQualifiedName = parentDecl.SwiftTypeName.ModuleQualifiedName;
        var moduleName = parentDecl.SwiftTypeName.Module;

        // Build callback param list — what Swift passes back to C# on success.
        var callbackTypeParams = new List<string>();
        switch (returnKind)
        {
            case AsyncReturnKind.Void:
                break;
            case AsyncReturnKind.Primitive:
                callbackTypeParams.Add(ExistentialBypassEmitter.RenderSwiftTypeSpec(returnTypeSpec));
                break;
            case AsyncReturnKind.SwiftClass:
                callbackTypeParams.Add("UnsafeMutableRawPointer");
                break;
            case AsyncReturnKind.ComplexValue:
                callbackTypeParams.Add("UnsafeMutableRawPointer");
                break;
        }
        callbackTypeParams.Add("Int64");
        var callbackSignature = $"@convention(c) ({string.Join(", ", callbackTypeParams)}) -> Void";

        // Build wrapper function parameters.
        var swiftParams = new List<string>
        {
            $"_ callback: {callbackSignature}",
        };

        if (throws)
        {
            // Cascade error callback ABI: errorPtr, errorSize, errorMsgPtr, isCancelled, taskId, errorTypeId
            swiftParams.Add("_ errorCallback: @convention(c) (UnsafeRawPointer?, Int, UnsafePointer<CChar>?, Int32, Int64, Int32) -> Void");
        }

        swiftParams.Add("_ _sbwTask: Int64");
        // Monotonic cancellation-registry key, distinct from the GCHandle context (_sbwTask).
        // See SwiftAsyncCancellation / P1-17.
        swiftParams.Add("_ _sbwCancelKey: Int64");

        // Regular parameters (with existential opening for the generic param).
        var callArgs = new List<string>();
        var reconstructions = new List<string>();
        // Sibling bindings so the hand-emitted generic-pointer binding and the Map'd non-generic
        // params each dodge their siblings (user-vs-sibling half of the P1-22 class).
        var siblings = CdeclParamMapper.CollectSiblingBindingNames(keptArgs);
        foreach (var arg in keptArgs)
        {
            var label = !string.IsNullOrEmpty(arg.PrivateName) ? arg.PrivateName : arg.Name;

            if (arg.SwiftTypeSpec is NamedTypeSpec named && named.Name == genericInfo.Param.TypeName)
            {
                // Hand-emitted generic-pointer binding — escape it (NOT routed through Map). A
                // generic param internally named `_self` yields `__self`, duplicating the receiver
                // body local `let __self`; `self` yields `_self`, duplicating the injected self param.
                // `__self`/`_self` are reserved, so the escape resolves both; siblings cover a
                // generic binding clashing with another user param (P1-22).
                var genericBinding = NameProvider.EscapeReservedSwiftWrapperLabel($"_{label}", siblings);
                swiftParams.Add($"_ {genericBinding}: UnsafeRawPointer");
                var argLabel = GetSwiftArgLabel(arg);
                callArgs.Add($"{argLabel}(Unmanaged<AnyObject>.fromOpaque({genericBinding}).takeUnretainedValue() as! any {genericInfo.ConstraintProtocolSwiftName})");
            }
            else
            {
                // `useUtf8Strings: true` — Swift.String marshals as (UInt8 ptr, Int len)
                // pair; reconstruction `let {label}Val = String(...)` happens BEFORE the
                // Task is scheduled, so the byte[] pin on the C# side only has to wrap
                // the synchronous P/Invoke call (which creates the Task) — not the await.
                // Matches the MGBE/CPSE Utf8Slice marshalling shape.
                var (cdeclParam, reconstruction, callArg) = CdeclParamMapper.Map(arg, label, env, omitLabels: false, useUtf8Strings: true, reservedSiblings: siblings);
                swiftParams.Add(cdeclParam);
                callArgs.Add(callArg);
                if (!string.IsNullOrEmpty(reconstruction))
                    reconstructions.Add(reconstruction);
            }
        }

        // Self parameter (last for instance methods).
        if (isInstance)
        {
            if (isClass)
                swiftParams.Add("_ _self: UnsafeMutableRawPointer");
            else
                swiftParams.Add("_ _self: UnsafeRawPointer");
        }

        // Self conversion.
        string selfConversion = "";
        if (isInstance)
        {
            selfConversion = isClass
                ? $"let __self = unsafeBitCast(OpaquePointer(_self), to: {moduleQualifiedName}.self)"
                : $"let __self = _self.assumingMemoryBound(to: {moduleQualifiedName}.self).pointee";
        }

        var callTarget = isInstance ? "__self" : moduleQualifiedName;
        var swiftMethodName = NameProvider.ParserNameToSwift(methodDecl);
        var callExpr = $"{callTarget}.{swiftMethodName}({string.Join(", ", callArgs)})";
        var awaitKeyword = throws ? "try await" : "await";

        bool needsMainActor = WrapperValidation.NeedsMainActorAnnotation(
            parentDecl, methodDecl.IsMainActorIsolated, methodDecl.IsNonisolated);

        var availability = WrapperEmitterHelpers.MergeAvailability(
            methodDecl.AvailabilityAnnotations, parentDecl);
        WrapperEmitterHelpers.EmitSwiftAvailability(swiftWriter, availability);

        if (needsMainActor)
            swiftWriter.WriteLine("@MainActor");
        swiftWriter.WriteLine($"@_cdecl(\"{cdeclSymbol}\")");
        swiftWriter.WriteLine($"public func {cdeclSymbol}(");
        swiftWriter.WriteLine($"    {string.Join(",\n    ", swiftParams)}");
        swiftWriter.WriteLine(") {");

        if (!string.IsNullOrEmpty(selfConversion))
            swiftWriter.WriteLine($"    {selfConversion}");
        foreach (var reconstruction in reconstructions)
            swiftWriter.WriteLine($"    {reconstruction}");

        swiftWriter.WriteLine($"    let _entry = _SBWTaskEntry()");
        swiftWriter.WriteLine($"    _sbwRegisterTask(_sbwCancelKey, _entry)");
        var taskOpen = needsMainActor ? "Task { @MainActor in" : "Task {";
        swiftWriter.WriteLine($"    _entry.task = {taskOpen}");
        swiftWriter.WriteLine($"        defer {{");
        swiftWriter.WriteLine($"            _sbwUnregisterTask(_sbwCancelKey)");
        swiftWriter.WriteLine($"        }}");

        if (throws)
        {
            swiftWriter.WriteLine($"        do {{");
            EmitTaskBody(swiftWriter, "            ", returnKind, returnTypeSpec, callExpr, awaitKeyword, methodDecl.Name, returnTypeRecord);
            swiftWriter.WriteLine($"        }} catch {{");
            EmitCatchBody(swiftWriter, "            ", moduleName, ctx);
            swiftWriter.WriteLine($"        }}");
        }
        else
        {
            EmitTaskBody(swiftWriter, "        ", returnKind, returnTypeSpec, callExpr, awaitKeyword, methodDecl.Name, returnTypeRecord);
        }

        swiftWriter.WriteLine($"    }}");
        swiftWriter.WriteLine("}");
        swiftWriter.WriteLine();
    }

    private static void EmitTaskBody(
        SwiftWriter swiftWriter,
        string indent,
        AsyncReturnKind returnKind,
        TypeSpec returnTypeSpec,
        string callExpr,
        string awaitKeyword,
        string methodName,
        TypeRecord? returnTypeRecord)
    {
        switch (returnKind)
        {
            case AsyncReturnKind.Void:
                swiftWriter.WriteLine($"{indent}{awaitKeyword} {callExpr}");
                swiftWriter.WriteLine($"{indent}callback(_sbwTask)");
                break;

            case AsyncReturnKind.Primitive:
                swiftWriter.WriteLine($"{indent}let _result = {awaitKeyword} {callExpr}");
                swiftWriter.WriteLine($"{indent}callback(_result, _sbwTask)");
                break;

            case AsyncReturnKind.SwiftClass:
                swiftWriter.WriteLine($"{indent}let _result = {awaitKeyword} {callExpr}");
                // Pass +1 retained class pointer — C# ctor takes ownership of this retain.
                swiftWriter.WriteLine($"{indent}callback(Unmanaged.passRetained(_result as AnyObject).toOpaque(), _sbwTask)");
                break;

            case AsyncReturnKind.ComplexValue:
                {
                    var renderedReturn = ExistentialBypassEmitter.RenderModuleQualifiedSwiftTypeSpec(returnTypeSpec);
                    swiftWriter.WriteLine($"{indent}let _result = {awaitKeyword} {callExpr}");
                    // Heap-allocate so the buffer outlives this Task's frame.
                    swiftWriter.WriteLine($"{indent}let _resultBuf = UnsafeMutableRawPointer.allocate(");
                    swiftWriter.WriteLine($"{indent}    byteCount: MemoryLayout<{renderedReturn}>.size,");
                    swiftWriter.WriteLine($"{indent}    alignment: MemoryLayout<{renderedReturn}>.alignment)");
                    swiftWriter.WriteLine($"{indent}_resultBuf.initializeMemory(as: {renderedReturn}.self, repeating: _result, count: 1)");
                    // C# reads via MarshalFromSwift<T>, then VWT.Destroy + NativeMemory.Free.
                    swiftWriter.WriteLine($"{indent}callback(_resultBuf, _sbwTask)");
                    break;
                }
        }
    }

    private static void EmitCatchBody(
        SwiftWriter swiftWriter, string indent, string moduleName, ModuleEmissionContext ctx)
    {
        // Use cascade dispatch when the module has registered Error-conforming types;
        // otherwise emit untyped fallback to keep wire shape consistent with the C#
        // error callback delegate type.
        if (ctx.ErrorTypeOrder.Count > 0)
        {
            var dispatchSymbol = ErrorRegistryHelperEmitter.GetSwiftDispatchSymbolName(moduleName);
            swiftWriter.WriteLine($"{indent}{dispatchSymbol}(error, _sbwTask, errorCallback)");
        }
        else
        {
            swiftWriter.WriteLine($"{indent}let _isCancelled: Int32 = (error is CancellationError) ? 1 : 0");
            swiftWriter.WriteLine($"{indent}let errorMessage = String(describing: error)");
            swiftWriter.WriteLine($"{indent}errorMessage.withCString {{ _msgPtr in");
            swiftWriter.WriteLine($"{indent}    errorCallback(nil, 0, _msgPtr, _isCancelled, _sbwTask, 0)");
            swiftWriter.WriteLine($"{indent}}}");
        }
    }

    // ─── C# Code Generation ──────────────────────────────────────────

    /// <summary>
    /// Emits shared C# infrastructure that all variants reuse:
    /// per-type SBW_CancelTask P/Invoke (deduped via <see cref="ModuleEmissionContext"/>),
    /// success-callback delegate field+body, and error-callback delegate field+body.
    /// </summary>
    private static void EmitSharedCSharpInfra(
        CSharpWriter csWriter,
        MethodEnvironment env,
        TypeDecl parentDecl,
        AsyncReturnKind returnKind,
        TypeSpec returnTypeSpec,
        TypeRecord? returnTypeRecord,
        ModuleEmissionContext ctx)
    {
        var methodDecl = env.MethodDecl;
        var moduleDecl = methodDecl.ModuleDecl
            ?? throw new InvalidOperationException("MethodDecl.ModuleDecl required for async wrapper");
        var moduleLibPath = env.TypeDatabase.GetLibraryPath(moduleDecl.Name);
        var wrapperLibPath = env.TypeDatabase.AsyncLibraryName ?? moduleLibPath;
        var callbackFieldName = NameProvider.GetAsyncCallbackFieldName(methodDecl);
        var callbackMethodName = NameProvider.GetAsyncCallbackMethodName(methodDecl);
        var errorCallbackFieldName = NameProvider.GetAsyncErrorCallbackFieldName(methodDecl);
        var errorCallbackMethodName = NameProvider.GetAsyncErrorCallbackMethodName(methodDecl);

        bool throws = methodDecl.Throws;

        // SBW_CancelTask P/Invoke (per-type dedup).
        var cancelSymbolName = CancellationTaskEmitter.GetCancelSymbolName(moduleDecl.Name);
        var typeKey = parentDecl.SwiftTypeName.ModuleQualifiedName;
        if (!CancellationTaskEmitter.HasCancelPInvokeForType(typeKey, ctx))
        {
            CancellationTaskEmitter.MarkCancelPInvokeEmittedForType(typeKey, ctx);
            csWriter.WriteLine("[global::System.Runtime.InteropServices.UnmanagedCallConv(CallConvs = new global::System.Type[] { typeof(global::System.Runtime.CompilerServices.CallConvCdecl) })]");
            csWriter.WriteLines($"""
                [global::System.Runtime.InteropServices.LibraryImport("{wrapperLibPath}", EntryPoint = "{cancelSymbolName}")]
                private static partial void SBW_CancelTask(long taskId);

                """);
        }

        // SBW_Free P/Invoke (per-type dedup). The Swift wrapper allocates the result
        // carrier via UnsafeMutableRawPointer.allocate; the matching deallocator is the
        // module-scoped SBW_Free helper (ptr?.deallocate()). Using NativeMemory.Free on
        // a Swift allocation is an allocator mismatch — see Issue #32 / AsyncComplexTypeTests
        // regression note. Module-scoped Swift symbol is emitted unconditionally by ModuleHandler.
        var freeSymbolName = Utf8SliceEmitter.GetFreeSymbolName(moduleDecl.Name);
        if (!Utf8SliceEmitter.HasFreePInvokeForType(typeKey, ctx))
        {
            Utf8SliceEmitter.MarkFreePInvokeEmittedForType(typeKey, ctx);
            csWriter.WriteLine("[global::System.Runtime.InteropServices.UnmanagedCallConv(CallConvs = new global::System.Type[] { typeof(global::System.Runtime.CompilerServices.CallConvCdecl) })]");
            csWriter.WriteLines($"""
                [global::System.Runtime.InteropServices.LibraryImport("{wrapperLibPath}", EntryPoint = "{freeSymbolName}")]
                private static partial void SBW_Free(IntPtr ptr);

                """);
        }

        // Determine result delegate signature + C# return type.
        var (csReturnType, callbackParamSig, callbackParamList) = ResolveCallbackTypes(
            returnKind, returnTypeSpec, returnTypeRecord, env);

        // ── Success callback delegate field + body ──
        var callbackPtrType = $"delegate* unmanaged[Cdecl]<{callbackParamSig}, void>";
        csWriter.WriteLine($"private static unsafe {callbackPtrType} {callbackFieldName} = &{callbackMethodName};");
        csWriter.WriteLine("[UnmanagedCallersOnly(CallConvs = new[] { typeof(global::System.Runtime.CompilerServices.CallConvCdecl) })]");
        csWriter.WriteLine($"private static unsafe void {callbackMethodName}({callbackParamList})");
        csWriter.WriteLine("{");
        csWriter.Indent++;
        EmitCallbackBody(csWriter, returnKind, csReturnType, returnTypeRecord);
        csWriter.Indent--;
        csWriter.WriteLine("}");
        csWriter.WriteLine();

        // ── Error callback (throws only) ──
        if (throws)
        {
            csWriter.WriteLine("private static unsafe delegate* unmanaged[Cdecl]<IntPtr, nint, IntPtr, int, long, int, void> "
                + $"{errorCallbackFieldName} = &{errorCallbackMethodName};");
            csWriter.WriteLine("[UnmanagedCallersOnly(CallConvs = new[] { typeof(global::System.Runtime.CompilerServices.CallConvCdecl) })]");
            csWriter.WriteLine($"private static unsafe void {errorCallbackMethodName}(IntPtr errorPtr, nint errorSize, IntPtr errorMessagePtr, int isCancellation, long task, int errorTypeId)");
            csWriter.WriteLine("{");
            csWriter.Indent++;
            EmitErrorCallbackBody(csWriter, csReturnType, moduleDecl.Name, ctx);
            csWriter.Indent--;
            csWriter.WriteLine("}");
            csWriter.WriteLine();
        }
    }

    private static (string csReturnType, string callbackParamSig, string callbackParamList) ResolveCallbackTypes(
        AsyncReturnKind returnKind, TypeSpec returnTypeSpec, TypeRecord? returnTypeRecord, MethodEnvironment env)
    {
        switch (returnKind)
        {
            case AsyncReturnKind.Void:
                return ("void", "long", "long task");

            case AsyncReturnKind.Primitive:
                {
                    var rawType = MethodClosureBridge.GetPInvokePrimitiveType(returnTypeSpec);
                    var publicType = ResolveCSharpPublicType(returnTypeSpec, env) ?? rawType;
                    var pInvokeRaw = MarshallingHelpers.IsBoolType(returnTypeSpec)
                        ? "[MarshalAs(UnmanagedType.U1)] bool rawResult, "
                        : $"{rawType} rawResult, ";
                    return (publicType, $"{rawType}, long", pInvokeRaw + "long task");
                }

            case AsyncReturnKind.SwiftClass:
                {
                    var publicType = ResolveCSharpPublicType(returnTypeSpec, env) ?? "global::Swift.Runtime.SwiftHandle";
                    return (publicType, "IntPtr, long", "IntPtr rawResult, long task");
                }

            case AsyncReturnKind.ComplexValue:
                {
                    var publicType = ResolveCSharpPublicType(returnTypeSpec, env) ?? "IntPtr";
                    return (publicType, "IntPtr, long", "IntPtr rawResult, long task");
                }

            default:
                throw new InvalidOperationException($"Unhandled async return kind {returnKind}");
        }
    }

    private static string? ResolveCSharpPublicType(TypeSpec returnTypeSpec, MethodEnvironment env)
    {
        var factory = new TypeProjectionFactory();
        var projection = factory.Project(returnTypeSpec, new ProjectionContext
        {
            TypeDatabase = env.TypeDatabase,
            IsParameter = false,
            ParentTypeDecl = env.ParentDecl as TypeDecl,
            CurrentModuleName = env.MethodDecl.ModuleDecl?.Name,
        });
        return projection?.PublicType;
    }

    /// <summary>
    /// Emits a catch block guarding a generic-bridge async [UnmanagedCallersOnly] callback.
    /// A managed exception escaping into native Swift aborts the process (SIGABRT); worse, if
    /// it escapes before the TaskCompletionSource is resolved the awaiting Task never completes
    /// and the caller hangs. Re-resolve the TCS from the still-live GCHandle target and fault it
    /// — <c>TrySetException</c> is a no-op if the result was already set, so this is safe even
    /// when the throw happens after the success path partially ran. The holder's native
    /// resources are freed here too: the fault is reachable from result marshalling (before the
    /// success path's cleanup), and the loop is not idempotent, so this catch is the only place
    /// the slots get freed on the throw path. <c>handle.Free()</c> stays in the callback's own
    /// <c>finally</c> and runs after this catch.
    /// </summary>
    /// <param name="tcsTypeParam">Generic suffix for the TCS type (e.g. <c>&lt;int&gt;</c>, or empty for void).</param>
    private static void EmitAsyncCallbackFaultCatch(CSharpWriter csWriter, string tcsTypeParam)
    {
        csWriter.WriteLine("catch (global::System.Exception __ex)");
        csWriter.WriteLine("{");
        csWriter.Indent++;
        csWriter.WriteLine("// Never let a managed exception unwind into native Swift (SIGABRT); fault the");
        csWriter.WriteLine("// awaiting Task instead so the failure is observable and the awaiter cannot hang.");
        csWriter.WriteLine($"if (handle.Target is object[] __holder && __holder[0] is TaskCompletionSource{tcsTypeParam} __faultTcs)");
        csWriter.WriteLine("{");
        csWriter.Indent++;
        // The fault is reachable from result marshalling, which runs BEFORE the success path's
        // holder cleanup (frees retained self, copy buffers, existential heap, deferred
        // containers, cancellation registration), so those native resources are still live and
        // must be freed here too — the callback's finally only frees the carrier and GCHandle.
        csWriter.WriteLines(AsyncHarnessEmitter.BuildHolderCleanupCode("__holder", "    "));
        csWriter.WriteLine("__faultTcs.TrySetException(__ex);");
        csWriter.Indent--;
        csWriter.WriteLine("}");
        csWriter.Indent--;
        csWriter.WriteLine("}");
    }

    private static void EmitCallbackBody(
        CSharpWriter csWriter, AsyncReturnKind returnKind, string csReturnType, TypeRecord? returnTypeRecord)
    {
        csWriter.WriteLine("GCHandle handle = GCHandle.FromIntPtr((IntPtr)task);");
        csWriter.WriteLine("try");
        csWriter.WriteLine("{");
        csWriter.Indent++;

        // ComplexValue dispatch by projection shape — mirrors AsyncHarnessEmitter's
        // EmitAsyncWrapperForComplexType cbTakesOwnership / carrierNeedsDestroy split.
        // The Swift wrapper allocates the result carrier via UnsafeMutableRawPointer.allocate
        // and writes the value via initializeMemory(as:repeating:count:1), which performs
        // a +1 retain on internal references. The C# callback must release that +1
        // unless ownership is transferred wholesale to a SafeHandle.
        bool returnIsNonFrozenStruct = false;
        bool returnIsComplexEnum = false;
        bool returnIsFrozenAsClass = false;
        if (returnKind == AsyncReturnKind.ComplexValue && returnTypeRecord is not null)
        {
            returnIsNonFrozenStruct = returnTypeRecord.Kind == TypeRecordKind.Struct
                && !MarshallingHelpers.IsTypeFrozen(returnTypeRecord);
            returnIsComplexEnum = returnTypeRecord.Kind == TypeRecordKind.Enum
                && !returnTypeRecord.Flags.HasFlag(TypeRecordFlags.SimpleEnum);
            returnIsFrozenAsClass = MarshallingHelpers.IsFrozenStructProjectedAsClass(returnTypeRecord);
        }
        bool cbTakesOwnership = returnIsNonFrozenStruct || returnIsComplexEnum;
        bool carrierNeedsDestroy = cbTakesOwnership || returnIsFrozenAsClass;

        // Materialise result expression.
        switch (returnKind)
        {
            case AsyncReturnKind.Void:
                break;

            case AsyncReturnKind.Primitive:
                csWriter.WriteLine($"var result = ({csReturnType})rawResult;");
                break;

            case AsyncReturnKind.SwiftClass:
                csWriter.WriteLine($"var result = new {csReturnType}(new global::Swift.Runtime.SwiftHandle(rawResult));");
                break;

            case AsyncReturnKind.ComplexValue:
                if (cbTakesOwnership)
                {
                    // Non-frozen struct / complex enum → ClassWithOpaquePayload. Copy the
                    // carrier into a fresh NativeMemory.Alloc buffer that the SafeHandle
                    // owns, then VWT-Destroy the original carrier to release its +1; the
                    // raw allocation is freed below in finally. Mirrors AsyncHarnessEmitter's
                    // newFromPayloadTakesOwnership branch.
                    csWriter.WriteLines($$"""
                        var __resultMetadata = SwiftObjectHelper<{{csReturnType}}>.GetTypeMetadata();
                        IntPtr __resultBuf = (IntPtr)NativeMemory.Alloc(__resultMetadata.Size);
                        __resultMetadata.ValueWitnessTable->InitializeWithCopy((void*)__resultBuf, (void*)rawResult, __resultMetadata);
                        var result = SwiftMarshal.MarshalFromSwift<{{csReturnType}}>(__resultBuf);
                        __resultMetadata.ValueWitnessTable->Destroy((void*)rawResult, __resultMetadata);
                        """);
                }
                else if (carrierNeedsDestroy)
                {
                    // Frozen struct with ref fields (ClassWithBufferStruct). NewFromPayload
                    // runs its own InitializeWithCopy into a managed buffer; we still need
                    // to release the carrier's +1 before freeing it.
                    csWriter.WriteLines($$"""
                        var result = SwiftMarshal.MarshalFromSwift<{{csReturnType}}>(rawResult);
                        var __resultMetadata = SwiftObjectHelper<{{csReturnType}}>.GetTypeMetadata();
                        __resultMetadata.ValueWitnessTable->Destroy((void*)rawResult, __resultMetadata);
                        """);
                }
                else
                {
                    // Frozen blittable struct / simple enum — value-copied by MarshalFromSwift.
                    // The carrier holds no internal refs, so a raw free below is enough.
                    csWriter.WriteLine($"var result = SwiftMarshal.MarshalFromSwift<{csReturnType}>(rawResult);");
                }
                break;
        }

        csWriter.WriteLine("var holder = (object[])handle.Target!;");
        csWriter.WriteLines(AsyncHarnessEmitter.BuildHolderCleanupCode("holder", "    "));
        csWriter.WriteLine($"var _tcs = (TaskCompletionSource{(csReturnType == "void" ? "" : $"<{csReturnType}>")})holder[0];");

        if (returnKind == AsyncReturnKind.Void)
            csWriter.WriteLine("_tcs.TrySetResult();");
        else
            csWriter.WriteLine("_tcs.TrySetResult(result);");

        csWriter.Indent--;
        csWriter.WriteLine("}");
        EmitAsyncCallbackFaultCatch(csWriter, csReturnType == "void" ? "" : $"<{csReturnType}>");
        csWriter.WriteLine("finally");
        csWriter.WriteLine("{");
        csWriter.Indent++;
        // Free the Swift-allocated carrier for every ComplexValue path via the module-scoped
        // SBW_Free helper (matches UnsafeMutableRawPointer.allocate / .deallocate). SwiftClass
        // returns pass a +1 retained class pointer (no carrier to free); Primitive / Void paths
        // have nothing to free either. The C#-allocated __resultBuf (cbTakesOwnership branch)
        // is owned by the SafeHandle and freed via NativeMemory.Free in ReleaseHandle.
        if (returnKind == AsyncReturnKind.ComplexValue)
            csWriter.WriteLine("SBW_Free(rawResult);");
        csWriter.WriteLine("handle.Free();");
        csWriter.Indent--;
        csWriter.WriteLine("}");
    }

    private static void EmitErrorCallbackBody(
        CSharpWriter csWriter, string csReturnType, string moduleName, ModuleEmissionContext ctx)
    {
        var helperClass = ErrorRegistryHelperEmitter.GetCSharpHelperClassName(moduleName);
        bool hasRegistry = ctx.ErrorTypeOrder.Count > 0;
        var tcsTypeParam = csReturnType == "void" ? "" : $"<{csReturnType}>";

        csWriter.WriteLine("GCHandle handle = GCHandle.FromIntPtr((IntPtr)task);");
        csWriter.WriteLine("try");
        csWriter.WriteLine("{");
        csWriter.Indent++;
        csWriter.WriteLine("var holder = (object[])handle.Target!;");
        csWriter.WriteLine($"var _tcs = (TaskCompletionSource{tcsTypeParam})holder[0];");
        csWriter.WriteLine("var errorMessage = Marshal.PtrToStringUTF8(errorMessagePtr) ?? \"Unknown Swift error\";");
        csWriter.WriteLine("if (isCancellation != 0)");
        csWriter.WriteLine("{");
        csWriter.Indent++;
        // Capture the cancellation token (read-only) for TrySetCanceled propagation BEFORE
        // running cleanup, which disposes the registration. Both steps delegate to the
        // exception-safe, idempotent runtime helper so the cancel/success/fault paths share
        // one slot-walk and cannot drift.
        csWriter.WriteLine("global::System.Threading.CancellationToken cancelToken = global::Swift.Runtime.SwiftAsyncCallHolder.CaptureCancellationToken(holder);");
        csWriter.WriteLines(AsyncHarnessEmitter.BuildHolderCleanupCode("holder", "    "));
        // Cascade dispatcher and untyped fallback both pass errorPtr=nil on cancellation,
        // so no carrier free is needed here. For the success-with-throw path, the dispatcher
        // helper frees the carrier in its own finally — see ErrorRegistryHelperEmitter.
        csWriter.WriteLine("_tcs.TrySetCanceled(cancelToken);");
        csWriter.Indent--;
        csWriter.WriteLine("}");
        csWriter.WriteLine("else");
        csWriter.WriteLine("{");
        csWriter.Indent++;
        if (hasRegistry)
        {
            csWriter.WriteLine($"var exception = {helperClass}.CreateException(errorTypeId, errorPtr, errorSize, errorMessage);");
        }
        else
        {
            csWriter.WriteLine("var exception = new global::Swift.Runtime.SwiftException(errorMessage);");
        }
        csWriter.WriteLines(AsyncHarnessEmitter.BuildHolderCleanupCode("holder", "    "));
        csWriter.WriteLine("_tcs.TrySetException(exception);");
        csWriter.Indent--;
        csWriter.WriteLine("}");
        csWriter.Indent--;
        csWriter.WriteLine("}");
        EmitAsyncCallbackFaultCatch(csWriter, tcsTypeParam);
        csWriter.WriteLine("finally");
        csWriter.WriteLine("{");
        csWriter.Indent++;
        csWriter.WriteLine("handle.Free();");
        csWriter.Indent--;
        csWriter.WriteLine("}");
    }

    private static void EmitPInvoke(
        CSharpWriter csWriter, MethodEnvironment env, MethodDecl methodDecl, GenericParamInfo genericInfo,
        string cdeclSymbol, string variantPInvokeMethodName, string wrapperLibPath, bool throws,
        List<ArgumentDecl> keptArgs)
    {
        var pinvokeParams = new List<string>
        {
            "void* callback",
        };
        if (throws) pinvokeParams.Add("void* errorCallback");
        pinvokeParams.Add("long taskId");
        // Monotonic cancellation-registry key (P1-17), threaded right after the GCHandle
        // context so the C# declaration stays positionally aligned with the Swift @_cdecl
        // wrapper's `_ _sbwCancelKey: Int64`.
        pinvokeParams.Add("long cancelKey");

        foreach (var arg in keptArgs)
        {
            var csName = NameProvider.GetCSharpParameterName(arg);
            if (arg.SwiftTypeSpec is NamedTypeSpec named && named.Name == genericInfo.Param.TypeName)
            {
                pinvokeParams.Add($"IntPtr {csName}");
                continue;
            }

            // Set<T> defaulted args travel as opaque IntPtr (a pointer to the SwiftSet's
            // payload buffer). Swift side reassembles via assumingMemoryBound + .pointee.
            if (IsBridgeableDefaultedContainer(arg, env.TypeDatabase))
            {
                pinvokeParams.Add($"IntPtr {csName}");
                continue;
            }

            var category = MethodClosureBridge.ClassifyParam(arg, env.TypeDatabase);
            switch (category)
            {
                case MethodClosureBridge.ParamAbiCategory.Primitive:
                    if (MarshallingHelpers.IsBoolType(arg.SwiftTypeSpec))
                        pinvokeParams.Add($"[MarshalAs(UnmanagedType.U1)] bool {csName}");
                    else
                        pinvokeParams.Add($"{MethodClosureBridge.GetPInvokePrimitiveType(arg.SwiftTypeSpec)} {csName}");
                    break;
                case MethodClosureBridge.ParamAbiCategory.Utf8Slice:
                    // Swift.String ABI matches the @_silgen_name wrapper's
                    // `_ {label}Utf8Ptr: UnsafePointer<UInt8>, _ {label}Utf8Len: Int`.
                    pinvokeParams.Add($"IntPtr {csName}Utf8Ptr");
                    pinvokeParams.Add($"nint {csName}Utf8Len");
                    break;
                default:
                    pinvokeParams.Add($"IntPtr {csName}");
                    break;
            }
        }

        if (methodDecl.MethodType == MethodType.Instance)
            pinvokeParams.Add("IntPtr self_");

        PInvokeEmitHelper.EmitDeclaration(csWriter, new PInvokeEmissionInfo
        {
            LibraryPath = wrapperLibPath,
            EntryPoint = cdeclSymbol,
            MethodName = variantPInvokeMethodName,
            ReturnType = "void",
            ParametersString = string.Join(", ", pinvokeParams),
            CallingConvention = PInvokeCallingConvention.Cdecl,
            Visibility = PInvokeVisibility.Internal,
            IsUnsafe = true,
        });
        csWriter.WriteLine();
    }

    private static void EmitPublicTaskMethod(
        CSharpWriter csWriter, MethodEnvironment env, TypeDecl parentDecl,
        GenericParamInfo genericInfo, string variantPInvokeMethodName,
        AsyncReturnKind returnKind, TypeSpec returnTypeSpec, TypeRecord? returnTypeRecord,
        bool throws,
        List<ArgumentDecl> keptArgs)
    {
        var methodDecl = env.MethodDecl;
        var methodName = NameProvider.ToPascalCase(methodDecl.Name);

        var (csReturnType, _, _) = ResolveCallbackTypes(returnKind, returnTypeSpec, returnTypeRecord, env);
        var tcsTypeParam = csReturnType == "void" ? "" : $"<{csReturnType}>";
        var taskReturnType = csReturnType == "void"
            ? "global::System.Threading.Tasks.Task"
            : $"global::System.Threading.Tasks.Task<{csReturnType}>";

        var callbackFieldName = NameProvider.GetAsyncCallbackFieldName(methodDecl);
        var errorCallbackFieldName = NameProvider.GetAsyncErrorCallbackFieldName(methodDecl);

        bool isInstance = methodDecl.MethodType != MethodType.Static;
        bool isClass = parentDecl is ClassDecl;
        bool isObjCRooted = parentDecl is ClassDecl objc && objc.IsObjCRooted;

        // Collect the Set<T> defaulted args we need to marshal into the holder.
        var setArgs = keptArgs
            .Where(a => IsBridgeableDefaultedContainer(a, env.TypeDatabase))
            .ToList();
        bool needsDeferredList = setArgs.Count > 0;

        // Build public parameter list.
        var publicParams = new List<string>();
        foreach (var arg in keptArgs)
        {
            var csName = NameProvider.GetCSharpParameterName(arg);
            if (arg.SwiftTypeSpec is NamedTypeSpec named && named.Name == genericInfo.Param.TypeName)
            {
                publicParams.Add($"global::Swift.Runtime.ISwiftObject {csName}");
                continue;
            }

            if (IsBridgeableDefaultedContainer(arg, env.TypeDatabase))
            {
                var publicType = ResolveCSharpPublicType(arg.SwiftTypeSpec, env) ?? "IntPtr";
                publicParams.Add($"{publicType} {csName}");
                continue;
            }

            var category = MethodClosureBridge.ClassifyParam(arg, env.TypeDatabase);
            switch (category)
            {
                case MethodClosureBridge.ParamAbiCategory.Primitive:
                    if (MarshallingHelpers.IsBoolType(arg.SwiftTypeSpec))
                        publicParams.Add($"bool {csName}");
                    else
                        publicParams.Add($"{MethodClosureBridge.GetPInvokePrimitiveType(arg.SwiftTypeSpec)} {csName}");
                    break;
                case MethodClosureBridge.ParamAbiCategory.ObjCHandle:
                case MethodClosureBridge.ParamAbiCategory.PayloadHandle:
                    publicParams.Add($"global::Swift.Runtime.ISwiftObject {csName}");
                    break;
                case MethodClosureBridge.ParamAbiCategory.Utf8Slice:
                    publicParams.Add($"string {csName}");
                    break;
                default:
                    publicParams.Add($"IntPtr {csName}");
                    break;
            }
        }
        publicParams.Add("global::System.Threading.CancellationToken cancellationToken = default");

        // Holder cleanup is delegated to the runtime helper (a single method call, no inlined
        // loop), so no loop-index reservation is needed in this public method body. The only
        // emitted loops here are over `count` / synthetic `__{name}` locals, none of which can
        // collide with a user parameter. See SwiftAsyncCallHolder.Cleanup.
        XmlDocCommentEmitter.EmitMethodDocComment(csWriter, methodDecl);
        var staticStr = methodDecl.MethodType == MethodType.Static ? "static " : "";
        csWriter.WriteLine($"public {staticStr}unsafe {taskReturnType} {methodName}Async({string.Join(", ", publicParams)})");
        csWriter.WriteLine("{");
        csWriter.Indent++;

        // TCS.
        csWriter.WriteLine($"TaskCompletionSource{tcsTypeParam} _tcs = new TaskCompletionSource{tcsTypeParam}(global::System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);");

        // AsyncDeferredDisposeList — owns SwiftSet/Array/Dictionary container payloads
        // until the Swift continuation finishes reading them. Disposed by the holder
        // cleanup loop on success / exception / cancellation.
        if (needsDeferredList)
        {
            csWriter.WriteLine("AsyncDeferredDisposeList _asyncDeferredList = new AsyncDeferredDisposeList();");
        }

        // Self retain (instance methods only).
        if (isInstance && isClass && isObjCRooted)
        {
            csWriter.WriteLines("""
                IntPtr _selfPtr = Handle;
                Arc.UnknownObjectRetain(_selfPtr);
                """);
        }
        else if (isInstance && isClass)
        {
            csWriter.WriteLines("""
                bool _selfSuccess = false;
                _handle.DangerousAddRef(ref _selfSuccess);
                IntPtr _selfPtr = _handle.DangerousGetHandle();
                Arc.UnknownObjectRetain(_selfPtr);
                _handle.DangerousRelease();
                """);
        }

        // Marshal Set<T> args into SwiftSet containers (hoisted into _asyncDeferredList).
        // Must happen AFTER `_asyncDeferredList` is allocated and BEFORE holder construction
        // so the holder slot reflects the live container.
        var bufferVarNames = new Dictionary<string, string>();
        foreach (var arg in setArgs)
        {
            var csName = NameProvider.GetCSharpParameterName(arg);
            var factory = new TypeProjectionFactory();
            var projection = factory.Project(arg.SwiftTypeSpec, new ProjectionContext
            {
                TypeDatabase = env.TypeDatabase,
                IsParameter = true,
                ParentTypeDecl = parentDecl,
                CurrentModuleName = methodDecl.ModuleDecl?.Name,
            }) ?? throw new InvalidOperationException(
                $"Set projection unavailable for {arg.SwiftTypeSpec} on async-generic bridge");
            var plan = projection.GetParameterPlan(csName);
            CdeclMarshallingHelper.RenderWithHandleOverride(csWriter, plan, csName, "_asyncDeferredList");
            bufferVarNames[csName] = NameProvider.GetBoundGenericBufferName(csName);
        }

        // Holder construction. Non-frozen param copies are skipped — V1 eligibility
        // already restricts non-generic params to primitive / ObjCHandle / PayloadHandle.
        // ISwiftObject-typed args (the class-bound generic param + any other
        // ObjCHandle/PayloadHandle wrappers) are stored as plain object refs so the
        // GCHandle on the holder keeps them rooted across the async boundary —
        // otherwise a finalizer racing the Swift Task could release the underlying
        // Swift object (or dispose the wrapper's SafeHandle) while Swift still
        // holds the raw pointer. The holder cleanup loop ignores entries it
        // doesn't recognize, so plain object refs are root-only.
        var heldObjectArgs = new List<string>();
        foreach (var arg in keptArgs)
        {
            var csNameHold = NameProvider.GetCSharpParameterName(arg);
            // Set<T> default args are converted to a SwiftSet wrapper that's already
            // owned by _asyncDeferredList; no need to double-root via the holder.
            if (IsBridgeableDefaultedContainer(arg, env.TypeDatabase))
                continue;
            // The class-bound generic param itself is an ISwiftObject reference.
            if (arg.SwiftTypeSpec is NamedTypeSpec nts && nts.Name == genericInfo.Param.TypeName)
            {
                heldObjectArgs.Add(csNameHold);
                continue;
            }
            var category = MethodClosureBridge.ClassifyParam(arg, env.TypeDatabase);
            if (category == MethodClosureBridge.ParamAbiCategory.ObjCHandle
                || category == MethodClosureBridge.ParamAbiCategory.PayloadHandle)
            {
                heldObjectArgs.Add(csNameHold);
            }
        }
        string heldArgsSlot = heldObjectArgs.Count == 0
            ? ""
            : ", " + string.Join(", ", heldObjectArgs.Select(n => $"(object){n}"));
        string deferredListSlot = needsDeferredList ? ", _asyncDeferredList" : "";
        if (isInstance && isClass)
        {
            csWriter.WriteLine($"object[] _asyncCallHolder = new object[] {{ _tcs, new RetainedSelfPtr(_selfPtr), (object)this{heldArgsSlot}{deferredListSlot}, null! }};");
        }
        else if (isInstance)
        {
            csWriter.WriteLine($"object[] _asyncCallHolder = new object[] {{ _tcs, new DeferredSafeHandleRelease(_payload), (object)this{heldArgsSlot}{deferredListSlot}, null! }};");
        }
        else
        {
            csWriter.WriteLine($"object[] _asyncCallHolder = new object[] {{ _tcs{heldArgsSlot}{deferredListSlot}, null! }};");
        }
        csWriter.WriteLine("GCHandle handle = GCHandle.Alloc(_asyncCallHolder, GCHandleType.Normal);");

        // Pre-cancel check.
        csWriter.WriteLines("""
            if (cancellationToken.IsCancellationRequested)
            {
            """);
        csWriter.WriteLines(AsyncHarnessEmitter.BuildHolderCleanupCode("_asyncCallHolder", "    "));
        csWriter.WriteLines($$"""
                handle.Free();
                return {{(csReturnType == "void"
                    ? $"global::System.Threading.Tasks.Task.FromCanceled(cancellationToken)"
                    : $"global::System.Threading.Tasks.Task.FromCanceled<{csReturnType}>(cancellationToken)")}};
            }
            """);

        csWriter.WriteLines($$"""
            long _sbwCancelKey = SwiftAsyncCancellation.NextCancelKey();
            if (cancellationToken.CanBeCanceled)
            {
                var _cancelRegistration = cancellationToken.Register(
                    static state =>
                    {
                        var (tcs, token, id) = ((TaskCompletionSource{{tcsTypeParam}}, global::System.Threading.CancellationToken, long))state!;
                        SBW_CancelTask(id);
                        tcs.TrySetCanceled(token);
                    },
                    (_tcs, cancellationToken, _sbwCancelKey));
                _asyncCallHolder[_asyncCallHolder.Length - 1] = new CancellationRegistrationHolder(_cancelRegistration, cancellationToken);
            }
            """);

        // Try wrapping the P/Invoke launch.
        csWriter.WriteLine("try");
        csWriter.WriteLine("{");
        csWriter.Indent++;

        // Build P/Invoke call arguments.
        var callArgs = new List<string>
        {
            $"(void*){callbackFieldName}",
        };
        if (throws) callArgs.Add($"(void*){errorCallbackFieldName}");
        callArgs.Add("(long)(IntPtr)handle");
        // Monotonic cancellation key (P1-17) — registry key, distinct from the GCHandle
        // context above. Defined as a local before the CanBeCanceled block.
        callArgs.Add("_sbwCancelKey");

        // Track Utf8Slice (Swift.String) params so we can emit the byte[] prelude +
        // `fixed (...)` pin around ONLY the synchronous P/Invoke call below. The Swift
        // @_silgen_name wrapper reconstructs `let {label}Val = String(bytes:…)` BEFORE
        // scheduling the Task, so the pin lifetime does not need to extend across the
        // C# await — it just has to cover the P/Invoke that fires the Task.
        var utf8SliceLocals = new List<(string csName, string bareName)>();

        foreach (var arg in keptArgs)
        {
            var csName = NameProvider.GetCSharpParameterName(arg);
            if (arg.SwiftTypeSpec is NamedTypeSpec named && named.Name == genericInfo.Param.TypeName)
            {
                callArgs.Add($"{csName}.SwiftHandle");
                continue;
            }

            if (IsBridgeableDefaultedContainer(arg, env.TypeDatabase))
            {
                callArgs.Add(bufferVarNames[csName]);
                continue;
            }

            var category = MethodClosureBridge.ClassifyParam(arg, env.TypeDatabase);
            switch (category)
            {
                case MethodClosureBridge.ParamAbiCategory.Primitive:
                    callArgs.Add(csName);
                    break;
                case MethodClosureBridge.ParamAbiCategory.ObjCHandle:
                case MethodClosureBridge.ParamAbiCategory.PayloadHandle:
                    callArgs.Add($"{csName}.SwiftHandle");
                    break;
                case MethodClosureBridge.ParamAbiCategory.Utf8Slice:
                {
                    var bareName = NameProvider.StripVerbatimPrefix(csName);
                    utf8SliceLocals.Add((csName, bareName));
                    callArgs.Add($"(IntPtr)__{bareName}Ptr");
                    callArgs.Add($"(nint)__{bareName}Utf8.Length");
                    break;
                }
                default:
                    callArgs.Add(csName);
                    break;
            }
        }

        if (isInstance)
        {
            var selfExpr = isClass
                ? (isObjCRooted ? "Handle" : "_handle.DangerousGetHandle()")
                : "_payload.DangerousGetHandle()";
            callArgs.Add(selfExpr);
        }

        // Prelude byte[] allocations precede the fixed-block stack (mirrors MGBE/CPSE).
        foreach (var (csName, bareName) in utf8SliceLocals)
            csWriter.WriteLine($"var __{bareName}Utf8 = System.Text.Encoding.UTF8.GetBytes({csName});");

        foreach (var (_, bareName) in utf8SliceLocals)
        {
            csWriter.WriteLine($"fixed (byte* __{bareName}Ptr = __{bareName}Utf8)");
            csWriter.WriteLine("{");
            csWriter.Indent++;
        }

        csWriter.WriteLine($"{variantPInvokeMethodName}({string.Join(", ", callArgs)});");

        for (int i = 0; i < utf8SliceLocals.Count; i++)
        {
            csWriter.Indent--;
            csWriter.WriteLine("}");
        }

        csWriter.Indent--;
        csWriter.WriteLine("}");
        csWriter.WriteLine("catch");
        csWriter.WriteLine("{");
        csWriter.Indent++;
        csWriter.WriteLines(AsyncHarnessEmitter.BuildHolderCleanupCode("_asyncCallHolder", "    "));
        csWriter.WriteLine("handle.Free();");
        csWriter.WriteLine("throw;");
        csWriter.Indent--;
        csWriter.WriteLine("}");

        csWriter.WriteLine("return _tcs.Task;");
        csWriter.Indent--;
        csWriter.WriteLine("}");
        csWriter.WriteLine();
    }

    // ─── Helpers ─────────────────────────────────────────────────────

    private static string GetSwiftArgLabel(ArgumentDecl arg)
        => ClosureEmitter.GetSwiftArgLabelForCdecl(arg);
}
