// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Collections.Generic;

namespace BindingsGeneration;

/// <summary>
/// Emits a <c>string</c> convenience overload for methods that take a scalar Swift
/// <c>Foundation.URL</c> parameter (projected to <c>Foundation.NSUrl</c>). C# callers of an API
/// like <c>Download(url: URL)</c> otherwise have to hand-construct <c>new Foundation.NSUrl("…")</c>
/// at every call site; the additive overload lets them pass the URL string directly and forwards
/// through <c>new Foundation.NSUrl(s)</c> — the C# analogue of Swift's <c>URL(string:)</c>.
///
/// Purely additive C# delegation, no Swift wrapper — same shape as <see cref="NativeIntOverloadEmitter"/>.
/// Deliberately narrow: only NON-optional, NON-inout, scalar URL parameters that actually project to
/// the <c>Foundation.NSUrl</c> bridge get the overload. Optional URL params are left alone so an
/// explicit <c>null</c> call stays unambiguous; collection (<c>[URL]</c>) and protocol-conformance
/// (<c>URL: SomeProtocol</c>) shapes are out of scope for this scalar sugar.
/// </summary>
internal static class UrlStringConvenienceOverloadEmitter
{
    /// <summary>
    /// Swift type name for <c>Foundation.URL</c>, the module-qualified form real ABI-JSON-parsed URL
    /// parameters carry (verified against generated output — a scalar URL param emits
    /// <c>Foundation.NSUrl</c>). No bare unqualified form is handled: it is unevidenced for URL, and
    /// the projection guard below would reject any name that does not actually project to the NSUrl
    /// bridge anyway.
    /// </summary>
    private const string UrlSwiftTypeName = "Foundation.URL";

    /// <summary>The C# public type a scalar Swift <c>URL</c> projects to via the ObjC bridge.</summary>
    private const string NSUrlPublicType = "Foundation.NSUrl";

    public static void TryEmitOverload(CSharpWriter csWriter, MethodEnvironment methodEnv)
    {
        var methodDecl = methodEnv.MethodDecl;

        // Same gate as NativeIntOverloadEmitter: skip constructors, accessors, async, missing symbols.
        if (methodDecl.IsConstructor || methodDecl.IsAccessor || methodDecl.IsAsync)
            return;
        if (methodDecl.IsMissingExportedSymbol)
            return;

        // Skip methods with their own generic parameters beyond the parent type's — the convenience
        // overload can't reconstruct the missing generic context (mirrors NativeIntOverloadEmitter).
        var parentGenericCount = (methodDecl.ParentDecl as TypeDecl)?.GenericParameters?.Count ?? 0;
        var methodGenericCount = methodDecl.GenericParameters?.Count ?? 0;
        if (methodGenericCount > parentGenericCount)
            return;

        var csSignature = methodDecl.CSSignature;
        if (csSignature.Count < 2)
            return;

        // Detect scalar, non-optional Foundation.URL params (skip return type at index 0).
        var urlParamIndices = new List<int>();
        for (int i = 1; i < csSignature.Count; i++)
        {
            var arg = csSignature[i];
            if (DefaultParameterOverloadEmitter.IsDebugParameter(arg))
                continue;
            // An `inout URL` can't be forwarded from a fresh `new NSUrl(...)` rvalue by `ref`.
            // Bail on the whole overload (rare shape) rather than emit a broken forwarder.
            if (arg.IsInOut)
                return;
            if (IsScalarUrl(arg.SwiftTypeSpec, methodEnv))
                urlParamIndices.Add(i);
        }

        if (urlParamIndices.Count == 0)
            return;

        // Borrow the primary's exact return-type spelling rather than re-deriving it (same reasoning
        // as NativeIntOverloadEmitter — this overload's body just calls the primary, so any divergence
        // is a compile error, not a difference of opinion). Drop the sugar if even the primary could
        // not project the return (AnyType) — forwarding to a never-emitted signature won't compile.
        var returnTypeSpec = csSignature[0].SwiftTypeSpec;
        bool hasReturn = !returnTypeSpec.IsEmptyTuple;
        string returnType = hasReturn
            ? new SignatureHandler(methodEnv).GetWrapperSignature().ReturnType
            : "void";
        if (hasReturn && returnType.Contains(TypeDatabaseExtensions.AnyType.CSharpTypeName.FullyQualifiedName))
            return;

        // Dedup: if a sibling method already occupies this exact projected signature (e.g. a real
        // Swift overload that already takes a `string` at the URL position), skip — the primary
        // registered its key at the main dedup loop, so a match here means emitting would be CS0111.
        if (methodEnv.EmittedProjectedSignatures != null)
        {
            var overloadKey = BuildOverloadKey(methodEnv, urlParamIndices);
            if (!methodEnv.EmittedProjectedSignatures.Add(overloadKey))
                return;
        }

        var methodName = methodEnv.CSharpMethodName;

        // Build the parameter list and forwarding call arguments.
        var paramParts = new List<string>();
        var callArgs = new List<string>();
        for (int i = 1; i < csSignature.Count; i++)
        {
            var arg = csSignature[i];
            if (DefaultParameterOverloadEmitter.IsDebugParameter(arg))
                continue;

            var paramName = NameProvider.GetCSharpParameterName(arg);
            if (urlParamIndices.Contains(i))
            {
                paramParts.Add($"string {paramName}");
                callArgs.Add($"new global::Foundation.NSUrl({paramName})");
            }
            else
            {
                var typeName = NativeIntOverloadEmitter.ResolveType(arg.SwiftTypeSpec, methodEnv, isParameter: true);
                paramParts.Add($"{typeName} {paramName}");
                callArgs.Add(paramName);
            }
        }

        var paramStr = string.Join(", ", paramParts);
        var argsStr = string.Join(", ", callArgs);

        var isStatic = methodDecl.MethodType == MethodType.Static;
        var staticModifier = isStatic ? "static " : "";

        // Carry the primary's @MainActor isolation onto the forwarder, keyed on the identical decision
        // the primary uses, so both overloads share the main-thread contract (mirrors NativeIntOverloadEmitter).
        if (WrapperValidation.NeedsMainActorAnnotation(
                methodEnv.ParentDecl, methodDecl.IsMainActorIsolated, methodDecl.IsNonisolated))
        {
            TypeAnnotationHelper.EmitSwiftMainActorMemberAnnotation(csWriter);
        }

        // Inherit [SupportedOSPlatform]/[ObsoletedOSPlatform] from the primary so CA1416 doesn't flag
        // the forwarder as reachable on lower OS versions than the platform-gated target it delegates to.
        AvailabilityAttributeEmitter.EmitAvailabilityAttributes(
            csWriter, methodDecl, methodDecl.ParentDecl, emitObsolete: false);

        if (hasReturn)
        {
            csWriter.WriteLine($"public {staticModifier}{returnType} {methodName}({paramStr}) => {methodName}({argsStr});");
        }
        else
        {
            csWriter.WriteLine($"public {staticModifier}void {methodName}({paramStr}) => {methodName}({argsStr});");
        }
    }

    /// <summary>
    /// True when <paramref name="typeSpec"/> is a scalar (non-optional, non-generic) Swift
    /// <c>Foundation.URL</c> that actually projects to <c>Foundation.NSUrl</c>. The projection check
    /// guards against an incomplete TypeDatabase degrading URL to <c>AnyType</c> — a <c>string</c>
    /// forwarder into an <c>AnyType</c> parameter would be CS1503.
    /// </summary>
    private static bool IsScalarUrl(TypeSpec typeSpec, MethodEnvironment methodEnv)
    {
        // Optional<URL> has Name "Swift.Optional", so the name check already excludes optional URLs.
        if (typeSpec is not NamedTypeSpec ns || ns.Name != UrlSwiftTypeName)
            return false;
        return NativeIntOverloadEmitter.ResolveType(typeSpec, methodEnv, isParameter: true) == NSUrlPublicType;
    }

    /// <summary>
    /// Builds the projected-signature dedup key for the emitted overload — the URL positions become
    /// <c>string</c>, every other position projects exactly as the primary's key builder does, so the
    /// key collides with a real sibling <c>Foo(string, …)</c> that the main dedup loop already reserved.
    /// Mirrors <see cref="NativeIntOverloadEmitter"/>'s BuildOverloadKey.
    /// </summary>
    private static string BuildOverloadKey(MethodEnvironment methodEnv, List<int> urlParamIndices)
    {
        var methodDecl = methodEnv.MethodDecl;
        var methodName = methodEnv.CSharpMethodName;
        var visibleGenericNames = BaseHandler.CollectVisibleGenericParamNames(methodDecl);

        var paramTypes = new List<string>();
        for (int i = 1; i < methodDecl.CSSignature.Count; i++)
        {
            var arg = methodDecl.CSSignature[i];
            if (DefaultParameterOverloadEmitter.IsDebugParameter(arg))
                continue;

            if (urlParamIndices.Contains(i))
            {
                paramTypes.Add("string");
            }
            else
            {
                var typeSpecForKey = ProtocolSignatureHelper.StripOptionalClassLikeForOverloadIdentity(
                    arg.SwiftTypeSpec, methodEnv.TypeDatabase, visibleGenericNames);
                var paramType = NativeIntOverloadEmitter.ResolveType(typeSpecForKey, methodEnv, isParameter: true);
                paramType = ProtocolSignatureHelper.NormalizeParamTypeForOverloadIdentity(
                    paramType, arg.SwiftTypeSpec, methodEnv.TypeDatabase);
                paramTypes.Add(paramType);
            }
        }

        return $"{methodName}({string.Join(",", paramTypes)})";
    }
}
