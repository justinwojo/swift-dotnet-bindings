// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Register-schema handling for arguments of a direct (<c>CallConvSwift</c>) closure callback.
/// </summary>
/// <remarks>
/// Swift passes a loadable closure argument BY VALUE: the value is exploded into scalar leaves that
/// arrive in registers, and for the shapes modelled here those registers, concatenated at 8-byte
/// offsets, reproduce the value's memory image exactly. The reverse trampoline therefore declares the
/// extra words as ordinary parameters, rebuilds the image in a stack buffer, and passes that buffer's
/// address to the existing address-based marshalling — which keeps one marshalling path for both the
/// exploded and the genuinely-indirect cases. The alternative (declaring the C# carrier by value and
/// letting the runtime lower it) has no precedent in a reverse <c>CallConvSwift</c> trampoline here and
/// would behave differently under Mono and NativeAOT.
///
/// Every site that renders a direct-lane trampoline signature — the callback definition, its function
/// pointer type, and the throwing / indirect-return variants — must expand the argument list through
/// these helpers, or the declared function pointer and the callback disagree on arity.
/// </remarks>
public static partial class ClosureEmitter
{
    /// <summary>
    /// Native parameter types for the words of <paramref name="arg"/> AFTER the first. Empty for
    /// every shape that occupies a single register and for the whole <c>@_cdecl</c> lane, whose
    /// Swift-side adapter hands over a pointer by construction.
    /// </summary>
    internal static IReadOnlyList<string> DirectLaneExtraWordTypes(
        TypeSpec arg, ClosureHandler closureHandler, bool useCdecl)
    {
        if (useCdecl)
            return Array.Empty<string>();

        var lowering = closureHandler.ClassifyDirectClosureArg(arg);
        return lowering.Abi == DirectClosureArgAbi.ExplodedWords
            ? lowering.ExtraWordTypes
            : Array.Empty<string>();
    }

    /// <summary>
    /// Appends the extra-word parameter declarations for <paramref name="arg"/> to a callback
    /// parameter list.
    /// </summary>
    internal static void AppendDirectLaneExtraWordParameters(
        List<string> parameters, TypeSpec arg, int argIndex, ClosureHandler closureHandler, bool useCdecl)
    {
        var extra = DirectLaneExtraWordTypes(arg, closureHandler, useCdecl);
        for (int w = 0; w < extra.Count; w++)
            parameters.Add($"{extra[w]} arg{argIndex}_w{w + 1}");
    }

    /// <summary>
    /// Appends the extra-word parameter TYPES for <paramref name="arg"/> to a
    /// <c>delegate* unmanaged[Swift]</c> type argument list.
    /// </summary>
    internal static void AppendDirectLaneExtraWordTypes(
        List<string> types, TypeSpec arg, ClosureHandler closureHandler, bool useCdecl)
    {
        foreach (var wordType in DirectLaneExtraWordTypes(arg, closureHandler, useCdecl))
            types.Add(wordType);
    }

    /// <summary>
    /// Statements that rebuild <paramref name="arg"/>'s memory image from its registers, or an empty
    /// string when the argument needs no buffer. Multi-word shapes get a stack buffer; a single-word
    /// shape reuses the parameter's own storage, so its address expression is simply <c>&amp;argN</c>.
    /// </summary>
    internal static string BuildDirectLaneWordBufferPrologue(
        TypeSpec arg, int argIndex, ClosureHandler closureHandler, bool useCdecl)
    {
        if (useCdecl)
            return string.Empty;

        var lowering = closureHandler.ClassifyDirectClosureArg(arg);
        if (lowering.Abi != DirectClosureArgAbi.ExplodedWords || lowering.WordCount == 1)
            return string.Empty;

        var lines = new List<string>
        {
            $"byte* __arg{argIndex} = stackalloc byte[{lowering.BufferBytes}];",
            $"*(void**)__arg{argIndex} = arg{argIndex};",
        };

        for (int w = 0; w < lowering.ExtraWordTypes.Count; w++)
        {
            var offset = 8 * (w + 1);
            var source = $"arg{argIndex}_w{w + 1}";
            lines.Add(lowering.ExtraWordTypes[w] == "byte"
                ? $"*(__arg{argIndex} + {offset}) = {source};"
                : $"*(void**)(__arg{argIndex} + {offset}) = {source};");
        }

        return string.Join("\n" + PrologueContinuationIndent, lines) + "\n" + PrologueContinuationIndent;
    }

    /// <summary>
    /// Leading whitespace for prologue lines after the first. Every callback template interpolates
    /// the prologue at the same depth — the first statement inside the callback's <c>try</c> — and
    /// the writer indents a rendered line by whatever it already carries, so the first line inherits
    /// the template's indentation and the rest have to state it.
    /// </summary>
    private const string PrologueContinuationIndent = "        ";

    /// <summary>
    /// The address to marshal <paramref name="arg"/> from: the rebuilt stack buffer for a multi-word
    /// exploded value, the parameter's own address for a single-word exploded value, and the
    /// historical <c>new IntPtr(argN)</c> for everything else (where the register already holds the
    /// value's address, or is itself the value).
    /// </summary>
    internal static string DirectLaneArgAddress(
        TypeSpec arg, int argIndex, ClosureHandler closureHandler, bool useCdecl)
    {
        if (!useCdecl)
        {
            var lowering = closureHandler.ClassifyDirectClosureArg(arg);
            if (lowering.Abi == DirectClosureArgAbi.ExplodedWords)
                return lowering.WordCount == 1
                    ? $"new IntPtr(&arg{argIndex})"
                    : $"new IntPtr(__arg{argIndex})";
        }

        return $"new IntPtr(arg{argIndex})";
    }

    /// <summary>
    /// Builds the buffer prologue for every argument of a direct-lane callback, in argument order.
    /// </summary>
    internal static string BuildDirectLaneWordBufferPrologue(
        ClosureTypeSpec closureTypeSpec, ClosureHandler closureHandler, bool useCdecl)
    {
        var prologue = string.Empty;
        int argIndex = 0;
        foreach (var arg in closureTypeSpec.EachArgument())
        {
            prologue += BuildDirectLaneWordBufferPrologue(arg, argIndex, closureHandler, useCdecl);
            argIndex++;
        }

        return prologue;
    }

    /// <summary>
    /// True when any argument of <paramref name="closureTypeSpec"/> reaches the direct lane in a
    /// loadable shape this generator does not model. Such a member is failed closed rather than
    /// emitted: the address model compiles and then reads the wrong memory, which no compiler and
    /// no verify-recover loop can see.
    /// </summary>
    internal static bool HasUnmodelledDirectLaneArg(
        ClosureTypeSpec closureTypeSpec, ClosureHandler closureHandler, out string shape)
    {
        foreach (var arg in closureTypeSpec.EachArgument())
        {
            var lowering = closureHandler.ClassifyDirectClosureArg(arg);
            if (lowering.Abi == DirectClosureArgAbi.Unmodelled)
            {
                shape = lowering.Shape;
                return true;
            }
        }

        shape = string.Empty;
        return false;
    }

    /// <summary>
    /// True when <paramref name="method"/> carries a closure parameter that will be marshalled by the
    /// direct <c>CallConvSwift</c> trampoline and whose argument list contains a loadable shape this
    /// generator does not model. The member is skipped at validation time rather than emitted.
    /// </summary>
    /// <remarks>
    /// The predicate mirrors the lane selection that later runs in the handlers, from the declaration
    /// alone: a closure reaches the direct lane only when it needs a reverse trampoline at all and the
    /// method takes neither the <c>@_cdecl</c> closure wrapper nor one of the bridge emitters, each of
    /// which hoists its own callback and marshals arguments under a different convention.
    /// <c>NeedsClosureCdeclWrapper</c> is the gate every <c>@_cdecl</c> lane consults for a closure —
    /// the standalone method wrapper and the constructor wrapper both reject a closure on exactly that
    /// predicate — so a member passing it is that lane's concern regardless of member kind. Async
    /// methods and async closures are excluded because neither routes a user closure through this
    /// trampoline.
    ///
    /// Every one of those exempting lanes emits a Swift-side adapter into the companion wrapper
    /// library, which exists only in xcframework mode. Outside it the member falls back to the direct
    /// trampoline no matter what the lane predicates say, so the exemption is conditioned on the mode
    /// rather than taken on the predicate alone — otherwise a shape the wrapper WOULD have carried
    /// passes validation and is then emitted on the very lane that cannot carry it.
    /// Over-restricting the predicate loses members that bind correctly today, so anything outside
    /// the verified direct lane is left alone.
    /// </remarks>
    internal static bool HasUnmodelledDirectLaneClosureParam(
        MethodDecl method, ClosureHandler closureHandler, ITypeDatabase typeDatabase, out string shape)
    {
        shape = string.Empty;

        if (method.IsAsync)
            return false;

        var closureArgs = method.CSSignature.Skip(1).Where(closureHandler.IsClosure).ToList();
        if (closureArgs.Count == 0)
            return false;

        // A method routed to a bridge emitter or to the @_cdecl closure wrapper never reaches the
        // direct trampoline, so its argument lowering is that lane's concern, not this one.
        if (WrapperValidation.IsXCFrameworkMode(typeDatabase) &&
            !WrapperLaneRefusesRegardlessOfClosure(method, typeDatabase) &&
            (NeedsClosureCdeclWrapper(method, closureHandler) ||
             MethodClosureBridge.IsEligible(method, closureHandler, typeDatabase) ||
             NestedClosureBridge.IsEligible(method, closureHandler, typeDatabase) ||
             ClosedConstrainedClosureEmitter.IsEligible(method, typeDatabase)))
            return false;

        // The protocol-extension closure bridge hoists its own callback on either mode.
        if (method.IsProtocolExtensionMethod)
            return false;

        foreach (var arg in closureArgs)
        {
            var spec = closureHandler.GetClosureTypeSpec(arg);
            if (spec == null)
                continue;
            if (DirectLaneClosureIsUnmodelled(spec, method.MangledName, closureArgs.Count, closureHandler, out shape))
                return true;
        }

        shape = string.Empty;
        return false;
    }

    /// <summary>
    /// Property twin of <see cref="HasUnmodelledDirectLaneClosureParam"/>. A closure-typed property
    /// hands the closure TO Swift only through its setter, so only a settable property builds a
    /// reverse trampoline whose arguments this schema governs; a getter returns the Swift closure and
    /// is invoked through the Swift-side invoke thunk, a different convention entirely.
    /// </summary>
    /// <remarks>
    /// Property emission never routes through <c>ShouldSkipMethodEmission</c> — it validates through
    /// the pipeline and then emits its accessors directly — so the gate has to be stated for the
    /// property as well or the accessor reaches the trampoline unchecked. The lane exemption mirrors
    /// the property <c>@_cdecl</c> wrapper's own eligibility: it refuses a bare closure setter
    /// outright, and accepts an <c>Optional</c>-wrapped one only when the closure is cdecl-compatible
    /// — and, like every wrapper lane, only in xcframework mode.
    /// </remarks>
    internal static bool HasUnmodelledDirectLaneClosureAccessorArg(
        PropertyDecl property, ClosureHandler closureHandler, ITypeDatabase typeDatabase, out string shape)
    {
        shape = string.Empty;

        if (!property.Accessors.OfType<SetAccessorDecl>().Any())
            return false;

        var spec = closureHandler.GetClosureTypeSpec(property);
        if (spec == null)
            return false;

        var setter = property.Accessors.OfType<SetAccessorDecl>().First();

        // Four refusals are settled by the declaration alone and none of them mention the closure.
        // Three are naming refusals: the wrapper body has to name the property and its parent by
        // their module-qualified names, which the separate wrapper-compilation module cannot do for
        // a member or a parent type that is internal to the Swift module or @_spi. The fourth is
        // isolation: a member isolated to a custom actor can only be entered through an async hop,
        // which a synchronous @_cdecl adapter cannot perform, so the wrapper declines it as well
        // (@MainActor is the deliberate exception and stays wrappable). A property refused on any of
        // these keeps its accessors and binds them as direct P/Invokes, so the trampoline is this
        // gate's concern after all. Methods need no counterpart: a closure-bearing method that is
        // itself internal, or sits on an internal parent, is dropped outright before validation
        // reaches here, because no lane can carry it.
        //
        // The isolation arm is read through the wrapper's own predicate, and the `nonisolated`
        // opt-out is narrowed exactly as the wrapper narrows it: an otherwise-nonisolated member
        // whose signature needs parameterized-protocol metadata falls back to the isolated gate,
        // because the adapter cannot spell that type at the deployment target. The setter accessor
        // is the signature the wrapper judges here, since it is the accessor that carries the
        // closure into Swift.
        var effectiveNonisolated = property.IsNonisolated &&
            !WrapperValidation.SignatureContainsParameterizedProtocol(setter.Method, typeDatabase);

        var declVisibleRefusal =
            property.IsModuleInternal ||
            property.IsSpiProtected ||
            property.ParentDecl is TypeDecl { IsModuleInternal: true } ||
            WrapperValidation.IsActorIsolatedMember(
                property.ParentDecl,
                property.IsActorIsolated,
                property.IsMainActorIsolated,
                effectiveNonisolated);

        // A generic parent adds two more refusals the closure shape cannot express — the wrapper takes
        // a concrete-typed property on a generic CLASS but defers one on a generic struct that is not
        // a Collection conformer, and it declines any parent that inherits its generic context from an
        // enclosing type, which cannot be extended to carry the adapter — after which the accessor
        // falls back to the direct P/Invoke. Both decisions are read from the wrapper's own predicates
        // rather than restated here, so the two cannot drift into over-skipping a member the wrapper
        // would have carried.
        if (WrapperValidation.IsXCFrameworkMode(typeDatabase) &&
            !declVisibleRefusal &&
            (property.ParentDecl is not TypeDecl { IsGeneric: true } genericParent ||
             (PropertyWrapperEmitter.CanEmitGenericClassPropertyWrapper(property, genericParent) &&
              !WrapperValidation.IsInheritedGenericContext(genericParent))) &&
            property.SwiftTypeSpec is not ClosureTypeSpec &&
            IsClosureCdeclCompatible(spec, closureHandler))
            return false;

        var setterSymbol = setter.Method.MangledName;
        return DirectLaneClosureIsUnmodelled(spec, setterSymbol, 1, closureHandler, out shape);
    }

    /// <summary>
    /// Subscript twin of <see cref="HasUnmodelledDirectLaneClosureParam"/>. Index parameters travel
    /// INTO Swift on both accessors, and the element type does so on the setter, so each is a closure
    /// argument list the direct trampoline would have to lower. The subscript path P/Invokes the raw
    /// dispatch thunk with no <c>@_cdecl</c> transport of its own, so there is no lane to exempt.
    /// </summary>
    internal static bool HasUnmodelledDirectLaneClosureAccessorArg(
        SubscriptDecl subscript, ClosureHandler closureHandler, out string shape)
    {
        shape = string.Empty;

        // The @convention(c) fallback reads the containing symbol's demangled tree, which can only be
        // attributed to a specific closure when the member carries exactly one — so the count has to
        // be the accessor's real closure count, element type included. Understating it lets one
        // @convention(c) marker vouch for a sibling Swift closure that does need a reverse thunk.
        var closureCount = subscript.IndexParameters.Count(closureHandler.IsClosure);
        if (closureHandler.GetClosureTypeSpec(subscript.ReturnTypeSpec) != null)
            closureCount++;

        foreach (var indexParam in subscript.IndexParameters)
        {
            var indexSpec = closureHandler.GetClosureTypeSpec(indexParam);
            if (indexSpec == null)
                continue;
            if (DirectLaneClosureIsUnmodelled(indexSpec, subscript.MangledName, closureCount, closureHandler, out shape))
                return true;
        }

        if (subscript.HasSetter)
        {
            var elementSpec = closureHandler.GetClosureTypeSpec(subscript.ReturnTypeSpec);
            if (elementSpec != null &&
                DirectLaneClosureIsUnmodelled(elementSpec, subscript.MangledName, closureCount, closureHandler, out shape))
                return true;
        }

        shape = string.Empty;
        return false;
    }

    /// <summary>
    /// True when a <c>@_cdecl</c> wrapper lane declines <paramref name="method"/> on a ground that
    /// has nothing to do with its closure, so the member lands on the direct trampoline after all
    /// even though the closure itself is cdecl-compatible.
    /// </summary>
    /// <remarks>
    /// This asks only about INITIALIZERS. An ordinary method that needs a closure wrapper always gets
    /// one: the standalone closure wrapper claims it on the closure predicate alone, whatever the
    /// method <c>@_cdecl</c> wrapper decided about the rest of the signature — so its callback is
    /// never the direct trampoline's and there is nothing here to answer. The same fallback exists
    /// for initializers but is limited to non-failable ones on a frozen struct, which is why the
    /// question has an answer for the rest of them.
    ///
    /// Wrapper eligibility is otherwise decided from a fully-built method environment, which
    /// validation does not have, so only the rejections a declaration plus the type database can
    /// settle are read here — each through the very predicate the wrapper consults, never a
    /// restatement of it. That makes this an UNDER-estimate of refusal by construction, which is the
    /// safe direction: a refusal it cannot see leaves the member exactly as it binds today, while a
    /// refusal it invented would skip a member the wrapper carries.
    /// </remarks>
    private static bool WrapperLaneRefusesRegardlessOfClosure(
        MethodDecl method, ITypeDatabase typeDatabase)
    {
        if (!method.IsConstructor)
            return false;

        // The standalone closure wrapper's own constructor arm. When it claims the initializer the
        // callback is a @_cdecl one no matter what the constructor wrapper decided.
        if (!method.IsFailable && method.ParentDecl is StructDecl { IsFrozen: true })
            return false;

        // A failable initializer on a resilient struct is declined outright, and the fallback above
        // does not take it either, so nothing claims it.
        if (method.IsFailable && method.ParentDecl is StructDecl { IsFrozen: false })
            return true;

        if (method.HasVariadicParameter || ConstructorAdmissibility.HasConstLiteralParameter(method))
            return true;

        if (WrapperValidation.HasRawGenericTypeParams(method) &&
            method.ParentDecl is not TypeDecl { IsGeneric: true })
            return true;

        if (method.ParentDecl is TypeDecl { IsGeneric: true } genericParent &&
            WrapperValidation.IsInheritedGenericContext(genericParent))
            return true;

        // A nested frozen struct and a metatype have no C representation, a `_const` parameter
        // demands a literal the wrapper cannot forward, and the remaining two shapes the wrapper
        // names outright.
        return method.CSSignature.Skip(1).Any(a =>
            WrapperValidation.IsNestedFrozenStructParam(a, typeDatabase) ||
            WrapperValidation.IsMetatypeTypeIncludingOptional(a.SwiftTypeSpec) ||
            a.IsConstLiteral ||
            a.SwiftTypeSpec is NamedTypeSpec
            {
                Name: "Swift.UnsafeBufferPointer" or "Swift.UnsafeMutableBufferPointer"
            } ||
            WrapperValidation.IsNonCopyableType(a.SwiftTypeSpec, typeDatabase, method.ModuleDecl));
    }

    /// <summary>
    /// Shared tail of the three entry points: a closure only reaches the direct trampoline when it is
    /// a supported non-async closure that needs a reverse thunk at all, and only then does its
    /// argument lowering matter here.
    /// </summary>
    private static bool DirectLaneClosureIsUnmodelled(
        ClosureTypeSpec spec, string mangledName, int closureParamCount,
        ClosureHandler closureHandler, out string shape)
    {
        shape = string.Empty;

        if (!closureHandler.IsSupportedClosure(spec) || closureHandler.IsAsyncClosure(spec))
            return false;
        if (!closureHandler.RequiresThunk(spec, mangledName, closureParamCount))
            return false;

        return HasUnmodelledDirectLaneArg(spec, closureHandler, out shape);
    }
}
