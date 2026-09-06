// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Emits int/uint convenience overloads for methods with nint/nuint parameters.
/// C# developers expect Skip(3) and Take(5) with int, not nint.
/// Overloads are pure C# delegation — no Swift wrapper needed.
/// </summary>
internal static class NativeIntOverloadEmitter
{
    private static readonly Dictionary<string, (string NativeType, string ConvenienceType)> NativeIntMap = new()
    {
        ["Swift.Int"] = ("nint", "int"),
        ["Swift.UInt"] = ("nuint", "uint"),
        // Unqualified forms from swiftinterface-parsed protocol extension methods
        ["Int"] = ("nint", "int"),
        ["UInt"] = ("nuint", "uint"),
    };

    /// <summary>
    /// Tries to emit an int/uint convenience overload for a method with nint/nuint params.
    /// </summary>
    public static void TryEmitOverload(CSharpWriter csWriter, MethodEnvironment methodEnv)
    {
        var methodDecl = methodEnv.MethodDecl;

        // Constructors take the same convenience overload methods take. Without it an
        // `init(sizeLimit: UInt)` demands a `nuint` while the `sizeLimit` property that reads it
        // back is narrowed to `uint`, so the two halves of the same value disagree about which
        // range a consumer may use. The forwarding shape differs — a constructor chains with
        // `: this(...)` rather than delegating in an expression body — but the parameter analysis,
        // the dedup and the attribute inheritance are shared with the method path below.
        //
        // An initializer does not always emit as a constructor. A failable `init?`/`init!` emits as a
        // static `TryCreate…` factory with a trailing `out` result, and an init whose projected
        // signature collided with a sibling's is recovered under a label-named `CreateWith{Labels}`
        // static factory. Each gets the same convenience treatment under its own shape, so all three
        // initializer flavours agree about which range a consumer may pass. An async init (a static
        // `CreateAsync` factory) is out on the async gate below, and a parent that is not a type (a
        // module-level function) has no initializer at all.
        bool isConstructorOverload = methodDecl.IsConstructor;
        if (methodDecl.IsAccessor || methodDecl.IsAsync)
            return;
        var ctorShape = default(ConstructorEmissionShape);
        if (isConstructorOverload)
        {
            if (methodDecl.ParentDecl is not TypeDecl)
                return;
            ctorShape = methodDecl.IsFailable
                ? new ConstructorEmissionShape(ConstructorEmissionKind.FailableFactory,
                    methodEnv.FailableFactoryName ?? DefaultFailableFactoryName)
                : methodEnv.InitFactoryName is { } initFactory
                    ? new ConstructorEmissionShape(ConstructorEmissionKind.InitFactory, initFactory)
                    : new ConstructorEmissionShape(ConstructorEmissionKind.Constructor, null);
        }
        if (methodDecl.IsMissingExportedSymbol)
            return;

        // Skip methods with their own generic parameters (beyond the parent type's).
        // E.g., randomInteger<TRNG>(width: Int, generator: inout TRNG) — the overload
        // can't express the TRNG constraint. But skip(count: Int) on Observable<Element>
        // is safe because the int overload inherits the class-level Element naturally.
        var parentGenericCount = (methodDecl.ParentDecl as TypeDecl)?.GenericParameters?.Count ?? 0;
        var methodGenericCount = methodDecl.GenericParameters?.Count ?? 0;
        if (methodGenericCount > parentGenericCount)
            return;

        var csSignature = methodDecl.CSSignature;
        if (csSignature.Count < 2)
            return;

        // Detect nint/nuint params (skip return type at index 0), including Optional<Swift.Int> → int?
        var conversions = new List<(int index, string nativeType, string convType, bool isOptional)>();
        for (int i = 1; i < csSignature.Count; i++)
        {
            var arg = csSignature[i];
            if (DefaultParameterOverloadEmitter.IsDebugParameter(arg))
                continue;
            // Never narrow an inout native-int param. inout is both input AND output, so narrowing
            // it carries the same silent-truncation hazard the return type does (see the return-type
            // note above) — AND the forwarder can't pass an `(nint)i` rvalue cast by `ref`. The param
            // stays native `ref nint`/`ref nuint` below; other (non-inout) params still narrow, and a
            // method whose only narrowable param is the inout one emits no overload (conversions empty).
            if (arg.IsInOut)
                continue;
            if (arg.SwiftTypeSpec is NamedTypeSpec ns && NativeIntMap.TryGetValue(ns.Name, out var mapping))
            {
                conversions.Add((i, mapping.NativeType, mapping.ConvenienceType, isOptional: false));
            }
            else if (arg.SwiftTypeSpec is NamedTypeSpec optNs &&
                     optNs.Name == "Swift.Optional" &&
                     optNs.GenericParameters.Count == 1 &&
                     optNs.GenericParameters[0] is NamedTypeSpec innerNs &&
                     NativeIntMap.TryGetValue(innerNs.Name, out var optMapping))
            {
                conversions.Add((i, optMapping.NativeType, optMapping.ConvenienceType, isOptional: true));
            }
        }

        if (conversions.Count == 0)
            return;

        // A plain constructor is emitted under the type's own name, not under CSharpMethodName (which
        // for an init holds only the internal dedup identity); a recovered init emits under its
        // factory's name instead.
        var constructorName = isConstructorOverload
            ? ctorShape.FactoryName ?? NameProvider.GetEmittedParentTypeName(methodDecl.ParentDecl!, methodEnv.TypeDatabase)
            : null;

        // Dedup: check if this overload signature already exists.
        //
        // A failable factory keys into the "failable-factory:" namespace the rest of the emitter
        // reserves it under, not the plain method namespace: its declaration carries a trailing
        // `out` result the key's parameter list does not spell, so `TryCreate(int)` in the shared
        // namespace would read as a collision with an unrelated `TryCreate(int)` method — and one
        // of the two, both legal C#, would be silently dropped.
        var keyName = isConstructorOverload && ctorShape.Kind == ConstructorEmissionKind.FailableFactory
            ? $"failable-factory:{constructorName}"
            : constructorName;
        if (methodEnv.EmittedProjectedSignatures != null)
        {
            var overloadKey = BuildOverloadKey(methodEnv, conversions, keyName);
            if (!methodEnv.EmittedProjectedSignatures.Add(overloadKey))
                return;
        }

        // A sibling init already declaring the narrowed shape (e.g. `init(n: Int32)` next to
        // `init(n: UInt)`) would be duplicated by this overload — CS0111, which the projected-key
        // set above cannot see because the sibling never passed through this emitter. Initializers
        // are the exposed case: every one that lands on the same C# member name (the type's own, or
        // one factory name) contests the same signature.
        if (isConstructorOverload &&
            ConstructorOverloadCollidesWithSibling(methodEnv, conversions, ctorShape))
        {
            return;
        }

        // Determine return type — keep nint/nuint return type as-is for method overloads.
        // Narrowing the return would change overload resolution: int literals (e.g., Skip(3)) would
        // pick the int overload → silent truncation for values exceeding Int32 range.
        // Property types ARE narrowed (no overload ambiguity there).
        //
        // Take the return type from the SAME oracle the primary method emits from rather than
        // re-deriving it. This overload is pure C# delegation — its body is a call to the primary —
        // so any divergence between the two spellings is by definition a compile error in the
        // emitted binding, not a difference of opinion worth having. The local ResolveType below is
        // a weaker projection (no parent decl, no full generic context, no module), so for a
        // Self-typed or placeholder return it degrades to AnyType and composes nonsense like
        // AnyType<T> for a bound generic, while the primary projects the real type.
        var returnTypeSpec = csSignature[0].SwiftTypeSpec;
        // A constructor has no C# return type; its CSSignature[0] carries Self, which must not be
        // projected as one.
        bool hasReturn = !isConstructorOverload && !returnTypeSpec.IsEmptyTuple;
        var primarySignature = new SignatureHandler(methodEnv).GetWrapperSignature();
        string returnType = hasReturn ? primarySignature.ReturnType : "void";

        // The primary's own parameter declarations, keyed by the name both sides derive from the
        // same argument. Every parameter this overload does not narrow is copied from here for the
        // same reason the return type is: the local projection below carries no parent decl and no
        // generic context, so a container over the parent's generic parameter (an `[Element]`
        // alongside an `Int`) degrades to the AnyType placeholder and composes a name no compiler
        // resolves, while the primary spells the real type. The builder may split one Swift argument
        // into two C# ones (a UTF-8 pointer and its length, a decomposed optional), which simply
        // leaves that name unmatched and falls back to the local projection.
        var primaryParameters = new Dictionary<string, Parameter>(StringComparer.Ordinal);
        foreach (var primaryParam in primarySignature.Parameters)
            primaryParameters.TryAdd(primaryParam.Name, primaryParam);

        // If even the primary could not project the return, there is no real type to borrow.
        // Emitting the sugar overload anyway would either fabricate an unresolvable placeholder
        // spelling or forward to a primary that was never emitted under that signature. Neither
        // compiles, and this overload is a convenience — dropping it costs only sugar.
        if (hasReturn && returnType.Contains(TypeDatabaseExtensions.AnyType.CSharpTypeName.FullyQualifiedName))
            return;

        // Build the method name
        var methodName = methodEnv.CSharpMethodName;

        // Build parameter list and call arguments
        var paramParts = new List<string>();
        var callArgs = new List<string>();
        for (int i = 1; i < csSignature.Count; i++)
        {
            var arg = csSignature[i];
            if (DefaultParameterOverloadEmitter.IsDebugParameter(arg))
                continue;
            // Swift's zero-sized Void carries no value and the primary declares no parameter for it,
            // so neither may this forwarder — declaring one would change the arity it forwards with.
            if (arg.SwiftTypeSpec.IsEmptyTuple)
                continue;

            var paramName = NameProvider.GetCSharpParameterName(arg);
            var conv = conversions.Find(c => c.index == i);
            if (conv != default)
            {
                // inout params are excluded from `conversions` above, so a narrowed param is never
                // inout — no ref modifier here.
                if (conv.isOptional)
                {
                    paramParts.Add($"{conv.convType}? {paramName}");
                    callArgs.Add($"({conv.nativeType}?){paramName}");
                }
                else
                {
                    paramParts.Add($"{conv.convType} {paramName}");
                    callArgs.Add($"({conv.nativeType}){paramName}");
                }
            }
            else if (primaryParameters.TryGetValue(paramName, out var primaryParam))
            {
                // Non-narrowed param — declared exactly as the primary declares it, modifier and all,
                // so the forwarder cannot disagree with the member it forwards to.
                // The forwarder calls the public primary member, so the argument is the parameter
                // itself under whatever modifier its declaration carries — no marshalling here.
                var primaryModifier = string.IsNullOrWhiteSpace(primaryParam.modifier)
                    ? string.Empty
                    : primaryParam.modifier.Trim() + " ";
                paramParts.Add(primaryParam.SignatureString());
                callArgs.Add($"{primaryModifier}{paramName}");
            }
            else
            {
                // No matching parameter on the primary (the builder expanded this argument into
                // several). Fall back to the local projection, preserving inout as `ref`. For an
                // inout native-int param, force the native type (nint/nuint) so the forwarder
                // matches the primary's `ref nint` signature; ResolveType could otherwise
                // idiomatically narrow it to int and produce a CS1503 ref-type mismatch.
                var refModifier = arg.IsInOut ? "ref " : "";
                string typeName;
                if (arg.IsInOut && arg.SwiftTypeSpec is NamedTypeSpec inoutNs &&
                    NativeIntMap.TryGetValue(inoutNs.Name, out var inoutMapping))
                    typeName = inoutMapping.NativeType;
                else
                    typeName = ResolveType(arg.SwiftTypeSpec, methodEnv, isParameter: true);
                paramParts.Add($"{refModifier}{typeName} {paramName}");
                callArgs.Add($"{refModifier}{paramName}");
            }
        }

        var paramStr = string.Join(", ", paramParts);
        var argsStr = string.Join(", ", callArgs);

        // Same reasoning as the return-type guard above, one axis over: a parameter that could not
        // be projected leaves the placeholder's name in the signature, which does not compile —
        // and with a generic argument attached it does not even name a generic type. Dropping the
        // convenience overload costs sugar; emitting it costs the whole binding.
        if (paramStr.Contains(TypeDatabaseExtensions.AnyType.CSharpTypeName.FullyQualifiedName))
            return;

        // The primary member's own static decision, so a module-level free function (MethodType.Instance,
        // parent is the module) gets a static overload a consumer calling through the type name can reach.
        var isStatic = methodEnv.EmitsStatic;
        var staticModifier = isStatic ? "static " : "";

        // Surface @MainActor isolation on the convenience int/uint forwarder too, keyed on the SAME
        // decision the primary method's marker uses (WrapperEmitter.NeedsMainActorSurfacing — accessors
        // are already excluded by the gate above), so both method overloads carry the identical
        // main-thread contract instead of the marker landing only on the nint form.
        if (WrapperValidation.NeedsMainActorAnnotation(
                methodEnv.ParentDecl, methodDecl.IsMainActorIsolated, methodDecl.IsNonisolated))
        {
            TypeAnnotationHelper.EmitSwiftMainActorMemberAnnotation(csWriter);
        }

        // Inherit [SupportedOSPlatform] / [ObsoletedOSPlatform] from the primary method.
        // Without these, CA1416 flags the forwarder as reachable on lower OS versions than
        // the platform-gated target it delegates to (e.g. MakeMockBook(int) reachable on
        // iOS 15.0 but forwarding to MakeMockBook(nint) restricted to iOS 16.0+).
        AvailabilityAttributeEmitter.EmitAvailabilityAttributes(
            csWriter, methodDecl, methodDecl.ParentDecl, emitObsolete: false);

        // Emit the overload
        if (isConstructorOverload && ctorShape.Kind != ConstructorEmissionKind.Constructor)
        {
            // The recovered lanes emit as static factories, so the forwarder delegates in an
            // expression body the way a method's does rather than chaining with `: this(...)`.
            var factoryTypeName = NameProvider.GetEmittedParentTypeName(methodDecl.ParentDecl!, methodEnv.TypeDatabase);
            if (methodDecl.ParentDecl is TypeDecl factoryParent && factoryParent.IsGeneric)
                factoryTypeName += GenericTypeEmitter.GetGenericParameterList(factoryParent);

            if (ctorShape.Kind == ConstructorEmissionKind.InitFactory)
            {
                csWriter.WriteLine($"public static {factoryTypeName} {constructorName}({paramStr}) => {constructorName}({argsStr});");
            }
            else
            {
                // The failable factory reports success through the return and hands the instance back
                // in a trailing `out`. The forwarder declares its own name for that parameter, chosen
                // clear of the names its own parameters took.
                var resultName = ChooseFailableFactoryResultName(csSignature);
                var separator = paramParts.Count > 0 ? ", " : string.Empty;
                csWriter.WriteLine(
                    $"public static bool {constructorName}({paramStr}{separator}out {factoryTypeName} {resultName}) " +
                    $"=> {constructorName}({argsStr}{separator}out {resultName});");
            }
        }
        else if (isConstructorOverload)
        {
            csWriter.WriteLine($"public {constructorName}({paramStr}) : this({argsStr}) {{ }}");
        }
        else if (hasReturn)
        {
            csWriter.WriteLine($"public {staticModifier}{returnType} {methodName}({paramStr}) => {methodName}({argsStr});");
        }
        else
        {
            csWriter.WriteLine($"public {staticModifier}void {methodName}({paramStr}) => {methodName}({argsStr});");
        }
    }

    /// <summary>The name a failable initializer's static factory takes when nothing disambiguated it.</summary>
    private const string DefaultFailableFactoryName = "TryCreate";

    /// <summary>Which C# member shape an initializer is emitted under.</summary>
    private enum ConstructorEmissionKind
    {
        /// <summary>A plain constructor, named after the type.</summary>
        Constructor,

        /// <summary>A static factory recovering a non-failable init whose signature collided.</summary>
        InitFactory,

        /// <summary>A static <c>TryCreate…</c> factory with a trailing <c>out</c> result.</summary>
        FailableFactory,
    }

    /// <summary>An initializer's emitted member shape: its lane, and the factory name where it has one.</summary>
    private readonly record struct ConstructorEmissionShape(ConstructorEmissionKind Kind, string? FactoryName);

    /// <summary>
    /// Classifies which C# member an initializer on this type emits as, from the declaration alone.
    /// The factory-recovery answer comes from the resolver that decided it, memoized per type body,
    /// so it does not depend on whether the sibling has been emitted yet.
    /// </summary>
    private static ConstructorEmissionShape ClassifyConstructorEmission(MethodDecl init, ITypeDatabase typeDatabase)
    {
        if (init.IsFailable)
            return new ConstructorEmissionShape(ConstructorEmissionKind.FailableFactory, null);
        return OverloadNameDisambiguator.ForConstructor(init, typeDatabase) is { } recovery
            ? new ConstructorEmissionShape(ConstructorEmissionKind.InitFactory, recovery.FactoryName)
            : new ConstructorEmissionShape(ConstructorEmissionKind.Constructor, null);
    }

    /// <summary>
    /// True when the narrowed initializer signature this emitter is about to write is already
    /// declared by another init emitting under the same C# member name on the same type.
    /// </summary>
    /// <remarks>
    /// Only siblings in the same lane can contest the signature: a recovered <c>CreateWith…</c>
    /// factory and a <c>TryCreate…</c> factory are separate members from the type's constructor and
    /// from each other, so counting them as constructor occupants would suppress a legitimate
    /// convenience overload. Within the init-factory lane the resolver's own factory names separate
    /// them further. The failable lane is compared name-blind — the name a failable init's factory
    /// settles on is decided in the type's dedup pass, which this emitter cannot re-run — so it can
    /// decline an overload two differently-named factories would have had room for. Declining costs
    /// only the convenience overload; the primary factory still binds and an <c>int</c> argument
    /// still widens implicitly at the call site.
    /// </remarks>
    private static bool ConstructorOverloadCollidesWithSibling(
        MethodEnvironment methodEnv,
        List<(int index, string nativeType, string convType, bool isOptional)> conversions,
        ConstructorEmissionShape shape)
    {
        var methodDecl = methodEnv.MethodDecl;
        if (methodDecl.ParentDecl is not TypeDecl parentType)
            return false;

        var narrowedShape = BuildNarrowedParameterShape(methodEnv, methodDecl, conversions);

        foreach (var sibling in parentType.Methods)
        {
            if (!sibling.IsConstructor || sibling.IsAsync)
                continue;
            if (ReferenceEquals(sibling, methodDecl) || sibling.MangledName == methodDecl.MangledName)
                continue;

            var siblingEmission = ClassifyConstructorEmission(sibling, methodEnv.TypeDatabase);
            if (siblingEmission.Kind != shape.Kind)
                continue;
            if (shape.Kind == ConstructorEmissionKind.InitFactory &&
                !string.Equals(siblingEmission.FactoryName, shape.FactoryName, StringComparison.Ordinal))
                continue;

            // The sibling's own emitted parameter list, read through the same projection. Its
            // native-int params are NOT narrowed here (the primary keeps nint/nuint) — but its
            // convenience overload would be, and that one is caught by the projected-key set.
            // The failable lane's trailing `out` result is on both sides, so it is left out.
            var siblingShape = BuildNarrowedParameterShape(methodEnv, sibling, conversions: null);
            if (siblingShape.SequenceEqual(narrowedShape, StringComparer.Ordinal))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Picks the failable-factory forwarder's <c>out</c> parameter name, stepping around any
    /// parameter of its own that already took it. Mirrors the primary factory's own rule.
    /// </summary>
    private static string ChooseFailableFactoryResultName(IReadOnlyList<ArgumentDecl> csSignature)
    {
        var taken = new HashSet<string>(
            csSignature.Skip(1).Select(NameProvider.GetCSharpParameterName), StringComparer.Ordinal);
        var resultName = "result";
        if (taken.Contains(resultName))
            resultName = "__resultOut";
        for (var i = 1; taken.Contains(resultName); i++)
            resultName = $"__resultOut{i}";
        return resultName;
    }

    /// <summary>
    /// Projects a declaration's parameter list to the C# type names it is emitted with, applying
    /// the int/uint narrowing described by <paramref name="conversions"/> when one is supplied.
    /// </summary>
    private static List<string> BuildNarrowedParameterShape(
        MethodEnvironment methodEnv,
        MethodDecl decl,
        List<(int index, string nativeType, string convType, bool isOptional)>? conversions)
    {
        var shape = new List<string>();
        for (int i = 1; i < decl.CSSignature.Count; i++)
        {
            var arg = decl.CSSignature[i];
            if (DefaultParameterOverloadEmitter.IsDebugParameter(arg))
                continue;
            // Neither sibling declares a parameter for Swift's zero-sized Void, so counting one
            // here would let two initializers that really do collide in C# look distinct.
            if (arg.SwiftTypeSpec.IsEmptyTuple)
                continue;

            var conv = conversions?.Find(c => c.index == i) ?? default;
            if (conv != default)
            {
                shape.Add(conv.isOptional ? $"{conv.convType}?" : conv.convType);
                continue;
            }

            var prefix = arg.IsInOut ? "ref " : "";
            shape.Add(prefix + ResolveType(arg.SwiftTypeSpec, methodEnv, isParameter: true));
        }
        return shape;
    }

    /// <summary>
    /// Tries to emit an int/uint convenience indexer overload for a subscript with nint/nuint params.
    /// </summary>
    public static void TryEmitIndexerOverload(
        CSharpWriter csWriter,
        SubscriptDecl subscriptDecl,
        string returnTypeName,
        List<(string typeName, string paramName, ITypeProjection? projection)> paramInfos,
        HashSet<string>? emittedIndexerKeys = null,
        ModuleEmissionContext? emissionContext = null,
        ITypeDatabase? typeDatabase = null)
    {
        // Detect nint/nuint params
        var conversions = new List<(int index, string nativeType, string convType)>();
        for (int i = 0; i < paramInfos.Count; i++)
        {
            if (paramInfos[i].typeName == "nint")
                conversions.Add((i, "nint", "int"));
            else if (paramInfos[i].typeName == "nuint")
                conversions.Add((i, "nuint", "uint"));
        }

        if (conversions.Count == 0)
            return;

        // Dedup: build converted signature key and check against already-emitted indexers
        if (emittedIndexerKeys != null)
        {
            var convertedTypes = paramInfos.Select((p, i) =>
            {
                var conv = conversions.Find(c => c.index == i);
                return conv != default ? conv.convType : p.typeName;
            });
            var overloadKey = string.Join(",", convertedTypes);
            if (!emittedIndexerKeys.Add(overloadKey))
                return;
        }

        // Build param list with converted types
        var paramParts = new List<string>();
        var castArgs = new List<string>();
        for (int i = 0; i < paramInfos.Count; i++)
        {
            var (typeName, paramName, _) = paramInfos[i];
            var conv = conversions.Find(c => c.index == i);
            if (conv != default)
            {
                paramParts.Add($"{conv.convType} {paramName}");
                castArgs.Add($"({conv.nativeType}){paramName}");
            }
            else
            {
                paramParts.Add($"{typeName} {paramName}");
                castArgs.Add(paramName);
            }
        }

        var paramStr = string.Join(", ", paramParts);
        var castArgStr = string.Join(", ", castArgs);

        var hasGetter = subscriptDecl.Accessors.OfType<GetAccessorDecl>().Any();
        var hasSetter = subscriptDecl.Accessors.OfType<SetAccessorDecl>().Any();

        // Surface @MainActor isolation on the convenience int/uint overload too, keyed on the same
        // oracle inputs as the primary indexer (SubscriptHandler.EmitIndexer), so both indexer members
        // carry the identical main-thread contract rather than the marker landing only on the nint form.
        var isolationAccessor =
            subscriptDecl.Accessors.OfType<GetAccessorDecl>().FirstOrDefault()?.Method
            ?? subscriptDecl.Accessors.OfType<SetAccessorDecl>().FirstOrDefault()?.Method;
        if (WrapperValidation.NeedsMainActorAnnotation(
                subscriptDecl.ParentDecl,
                isolationAccessor?.IsMainActorIsolated ?? false,
                isolationAccessor?.IsNonisolated ?? false))
        {
            TypeAnnotationHelper.EmitSwiftMainActorMemberAnnotation(csWriter);
        }

        // Inherit availability attributes from the primary subscript — same CA1416
        // concern as the method overload path above.
        AvailabilityAttributeEmitter.EmitAvailabilityAttributes(
            csWriter, subscriptDecl, subscriptDecl.ParentDecl, emitObsolete: false);

        // The primary this[nint] getter was SB0006-poisoned (its suppressed-proxy read can only throw), so
        // its getter is [Obsolete(error:true)] with a throwing body. Forwarding `this[(nint)i]` from a
        // convenience overload would read that poisoned getter (CS0619). Mirror the poison instead — a
        // throwing, poisoned getter — while keeping the setter forward where the primary has a usable setter.
        bool getterPoisoned = hasGetter && emissionContext?.WasSubscriptGetterProduceThrow(subscriptDecl) == true;

        // The convenience overload is a public indexer in its own right — record it under its own
        // narrowed index types so the API manifest carries both indexer forms, not just the nint one.
        emissionContext?.RecordSubscriptApiManifestEntry(
            subscriptDecl,
            paramInfos.Select((p, i) =>
            {
                var conv = conversions.Find(c => c.index == i);
                return conv != default ? conv.convType : p.typeName;
            }),
            typeDatabase);

        if (getterPoisoned)
        {
            csWriter.WriteLine($"public {returnTypeName} this[{paramStr}]");
            csWriter.WriteLine("{");
            csWriter.Indent++;
            WrapperEmitter.EmitSuppressedProxyReadPoison(csWriter);
            csWriter.WriteLine($"get => throw new NotSupportedException(\"{WrapperEmitter.ProxySuppressedMessage}\");");
            if (hasSetter)
                csWriter.WriteLine($"set => this[{castArgStr}] = value;");
            csWriter.Indent--;
            csWriter.WriteLine("}");
        }
        else if (hasGetter && hasSetter)
        {
            csWriter.WriteLine($"public {returnTypeName} this[{paramStr}]");
            csWriter.WriteLine("{");
            csWriter.Indent++;
            csWriter.WriteLine($"get => this[{castArgStr}];");
            csWriter.WriteLine($"set => this[{castArgStr}] = value;");
            csWriter.Indent--;
            csWriter.WriteLine("}");
        }
        else if (hasGetter)
        {
            csWriter.WriteLine($"public {returnTypeName} this[{paramStr}] => this[{castArgStr}];");
        }
        csWriter.WriteLine();
    }

    /// <summary>
    /// Resolves a Swift TypeSpec to a C# type name using the same projection pipeline
    /// as MethodHandler (TypeProjectionFactory → TypeDatabase → ToString fallback),
    /// with generic type parameter substitution.
    /// </summary>
    internal static string ResolveType(TypeSpec typeSpec, MethodEnvironment methodEnv, bool isParameter)
    {
        // Handle generic type parameters via GenericTypeMapping
        if (typeSpec is NamedTypeSpec ns && methodEnv.GenericTypeMapping.TryGetValue(ns.ToString(), out var mapping))
            return mapping.TypeParameter;

        var factory = new TypeProjectionFactory();
        var projection = factory.Project(typeSpec, new ProjectionContext
        {
            TypeDatabase = methodEnv.TypeDatabase,
            IsParameter = isParameter,
            // The resolved name is written straight into the emitted overload signature, so an
            // existential owned by a sibling module must name that module — the generated file
            // emits no using for the sibling's namespace.
            CurrentModuleName = methodEnv.ExistentialHandler.CurrentModuleName
        });
        if (projection != null)
            return projection.PublicType;

        // For named types with generic arguments, resolve base name + each generic arg recursively.
        // Must come BEFORE TryGetTypeRecord which strips generic args.
        if (typeSpec is NamedTypeSpec namedSpec && namedSpec.GenericParameters?.Count > 0)
        {
            // SIMD bound-generic alias: Swift.SIMD3<Swift.Float> resolves to a non-generic
            // typealias (simd.simd_float3 → System.Numerics.Vector3). Probe this BEFORE
            // the bare-name fallback so the int-overload mirrors the primary method's
            // projection — otherwise the overload emits Swift.SIMD3<float> which doesn't
            // exist as a C# type (CS0234).
            if (TypeDatabaseExtensions.TryResolveBoundGenericAlias(methodEnv.TypeDatabase, namedSpec, out var aliasRecord))
                return aliasRecord.CSharpTypeName.FullyQualifiedName;

            // Swift.Optional<T> must emit C# nullable form (T?), never the raw generic shape
            // "Swift.Optional<...>" — that doesn't exist as a C# type. The projection path at
            // line 230 normally produces this through OptionalProjection, but if it returns
            // null (incomplete TypeDatabase, edge generic context) the fallback would compose
            // an invalid identifier. Recurse on the inner element and append "?".
            if (namedSpec.Name == "Swift.Optional" && namedSpec.GenericParameters.Count == 1)
            {
                var innerResolved = ResolveType(namedSpec.GenericParameters[0], methodEnv, isParameter);
                return innerResolved.EndsWith('?') ? innerResolved : $"{innerResolved}?";
            }

            // Swift.ClosedRange<Bound> is a registered stdlib generic with no
            // TypeProjectionFactory branch (the wrapper is consumed directly through
            // the stdlib-generic cdecl bridge, not via an idiomatic .NET projection).
            // The fallback below would compose "Swift.AnyType<Bound>" because the bare
            // "Swift.ClosedRange" identity short-circuits to AnyType via
            // BareGenericGuardStrategy. Map the bound generic directly to the
            // SwiftClosedRange wrapper so the int-overload mirrors the primary method.
            if (namedSpec.Name == "Swift.ClosedRange" && namedSpec.GenericParameters.Count == 1)
            {
                var boundResolved = ResolveType(namedSpec.GenericParameters[0], methodEnv, isParameter);
                return $"Swift.SwiftClosedRange<{boundResolved}>";
            }

            var bareSpec = new NamedTypeSpec(namedSpec.Name);
            string baseName;
            if (methodEnv.TypeDatabase.TryGetTypeRecord(bareSpec, out var rec))
                baseName = rec.CSharpTypeName.FullyQualifiedName;
            else
                baseName = namedSpec.Name;
            var genericArgs = namedSpec.GenericParameters.Select(gp => ResolveType(gp, methodEnv, isParameter));
            return $"{baseName}<{string.Join(", ", genericArgs)}>";
        }

        if (methodEnv.TypeDatabase.TryGetTypeRecord(typeSpec, out var record))
            return record.CSharpTypeName.FullyQualifiedName;

        return typeSpec.ToString();
    }

    /// <summary>
    /// Narrows nint/nuint C# type names to int/uint for idiomatic APIs.
    /// Returns the input unchanged if not a native int type.
    /// </summary>
    public static string NarrowNativeIntType(string typeName) => typeName switch
    {
        "nint" => "int",
        "nuint" => "uint",
        "nint?" => "int?",
        "nuint?" => "uint?",
        _ => typeName
    };

    /// <summary>
    /// The suffix appended to a narrowed property's name to form its lossless companion accessor
    /// (<c>SizeLimit</c> → <c>SizeLimitNative</c>). One constant so the emitter that places the
    /// companion and the sibling-name sets that reserve it can never drift.
    /// </summary>
    public const string NativeWidthSuffix = "Native";

    /// <summary>
    /// Picks the C# name for the lossless companion accessor placed next to a narrowed property,
    /// or null when every candidate is already occupied on the type.
    /// </summary>
    /// <remarks>
    /// The candidate is the property's own emitted name plus <see cref="NativeWidthSuffix"/>. It is
    /// checked against the same sibling set the method dedup loop reserves (properties after their
    /// renames, plus emitted nested-type leaves), the case constructors an enum contributes, the
    /// enclosing type's own emitted name (a member may not repeat it — CS0542), and the companions
    /// already placed on this type — so two narrowed properties named <c>foo</c> and <c>fooNative</c>
    /// cannot both land on <c>FooNative</c>. Methods are not consulted here and do not need to be:
    /// they are named after the properties pass, from a sibling set this companion joins, so a method
    /// projecting onto the companion's name takes the existing rename instead. A numeric bump follows
    /// the enum <c>Value</c>-suffix recovery's shape and keeps stepping until it lands clear; the
    /// occupied set is finite, so it always does.
    /// </remarks>
    public static string? ChooseNativeWidthCompanionName(
        TypeDecl typeDecl, string propertyName, ITypeDatabase typeDatabase)
    {
        var taken = new HashSet<string>(
            NameProvider.BuildSiblingMemberNames(typeDecl, typeDatabase), StringComparer.Ordinal);

        foreach (var property in typeDecl.Properties)
        {
            if (property.EmittedNativeWidthCSharpName is { Length: > 0 } companion)
                taken.Add(companion);
        }

        if (typeDecl is EnumDecl enumDecl)
        {
            var caseNameMap = NameProvider.ComputeCaseNameMap(enumDecl.Cases);
            foreach (var enumCase in enumDecl.Cases)
                taken.Add(NameProvider.GetCaseName(enumCase.Name, caseNameMap));
        }

        // A member may not carry its enclosing type's name (CS0542), which a type named for the
        // companion suffix would otherwise walk straight into: `CountNative.count` narrows to
        // `Count` and would ask for a `CountNative` member on `CountNative`.
        taken.Add(NameProvider.GetEmittedParentTypeName(typeDecl, typeDatabase));

        // A property name is PascalCase, so it never needs the verbatim '@' escape; drop one anyway
        // rather than emit it in the middle of a compound identifier if a future naming rule adds it.
        var baseName = $"{propertyName.TrimStart('@')}{NativeWidthSuffix}";
        if (!taken.Contains(baseName))
            return baseName;

        // One more step than there are occupants guarantees a free candidate, so the companion is
        // never dropped for want of a name on a type that simply has many members.
        for (int suffix = 2; suffix <= taken.Count + 2; suffix++)
        {
            var candidate = $"{baseName}{suffix}";
            if (!taken.Contains(candidate))
                return candidate;
        }

        return null;
    }

    /// <summary>
    /// Builds the read expression for a narrowed native-int accessor: the runtime conversion that
    /// reports an out-of-range value instead of dropping its high bits. Returns null when
    /// <paramref name="narrowedTypeName"/> is not one of the four narrowed native-int type names,
    /// leaving the caller on its own (non-narrowing) path.
    /// </summary>
    /// <param name="narrowedTypeName">The narrowed C# type name ("int", "uint", "int?", "uint?").</param>
    /// <param name="expression">The native-width expression to convert.</param>
    /// <param name="memberName">The C# member the value is read through, for the message.</param>
    /// <param name="nativeMemberName">The lossless companion accessor's name, when one was emitted.</param>
    public static string? BuildCheckedNarrowingExpression(
        string narrowedTypeName, string expression, string memberName, string? nativeMemberName)
    {
        var method = narrowedTypeName switch
        {
            "int" or "int?" => "ToInt32",
            "uint" or "uint?" => "ToUInt32",
            _ => null,
        };
        if (method is null)
            return null;

        // Both names are C# identifiers by construction (they are emitted member names), so a plain
        // quoted literal needs no escaping. The '@' a keyword-escaped identifier carries is dropped
        // for the message, which is prose rather than code.
        static string Literal(string identifier) => $"\"{identifier.TrimStart('@')}\"";

        var companionArg = nativeMemberName is { Length: > 0 } ? $", {Literal(nativeMemberName)}" : string.Empty;
        return $"global::Swift.Runtime.NativeIntegerNarrowing.{method}({expression}, {Literal(memberName)}{companionArg})";
    }

    /// <summary>
    /// For plain Swift.Int/Swift.UInt, returns the ABI widening type ("nint"/"nuint").
    /// Used in receiver getters to widen narrowed int/uint back to nint/nuint for MarshalToSwiftBuffer.
    /// Returns false for Optional variants (their implicit widening in SwiftOptional.NewSome handles it).
    /// </summary>
    public static bool TryGetAbiWideningType(TypeSpec typeSpec, out string abiType)
    {
        if (typeSpec is NamedTypeSpec ns && NativeIntMap.TryGetValue(ns.Name, out var mapping))
        {
            abiType = mapping.NativeType; // "nint" for Swift.Int, "nuint" for Swift.UInt
            return true;
        }
        abiType = "";
        return false;
    }

    /// <summary>
    /// For Swift.Int/Swift.UInt (plain or Optional), returns the narrowed C# type.
    /// Used in receiver setters to narrow ABI nint/nuint to int/uint for property assignment.
    /// Returns "int" for Swift.Int, "uint" for Swift.UInt,
    /// "int?" for Optional&lt;Swift.Int&gt;, "uint?" for Optional&lt;Swift.UInt&gt;.
    /// </summary>
    public static bool TryGetNarrowedType(TypeSpec typeSpec, out string narrowedType)
    {
        if (typeSpec is NamedTypeSpec ns && NativeIntMap.TryGetValue(ns.Name, out var mapping))
        {
            narrowedType = mapping.ConvenienceType; // "int" or "uint"
            return true;
        }
        if (typeSpec is NamedTypeSpec optNs && optNs.Name == "Swift.Optional"
            && optNs.GenericParameters.Count == 1
            && optNs.GenericParameters[0] is NamedTypeSpec inner
            && NativeIntMap.TryGetValue(inner.Name, out var optMapping))
        {
            narrowedType = $"{optMapping.ConvenienceType}?"; // "int?" or "uint?"
            return true;
        }
        narrowedType = "";
        return false;
    }

    private static string BuildOverloadKey(
        MethodEnvironment methodEnv,
        List<(int index, string nativeType, string convType, bool isOptional)> conversions,
        string? nameOverride = null)
    {
        var methodDecl = methodEnv.MethodDecl;
        var methodName = nameOverride ?? methodEnv.CSharpMethodName;
        var visibleGenericNames = BaseHandler.CollectVisibleGenericParamNames(methodDecl);

        var paramTypes = new List<string>();
        for (int i = 1; i < methodDecl.CSSignature.Count; i++)
        {
            var arg = methodDecl.CSSignature[i];
            if (DefaultParameterOverloadEmitter.IsDebugParameter(arg))
                continue;
            // Skipped in the emitted signature and by the shared projected-key builder alike, so
            // the reserved key names the same parameter list the declaration does.
            if (arg.SwiftTypeSpec.IsEmptyTuple)
                continue;

            var conv = conversions.Find(c => c.index == i);
            if (conv != default)
            {
                paramTypes.Add(conv.isOptional ? $"{conv.convType}?" : conv.convType);
            }
            else
            {
                var typeSpecForKey = ProtocolSignatureHelper.StripOptionalClassLikeForOverloadIdentity(
                    arg.SwiftTypeSpec, methodEnv.TypeDatabase, visibleGenericNames);
                var paramType = ResolveType(typeSpecForKey, methodEnv, isParameter: true);
                paramType = ProtocolSignatureHelper.NormalizeParamTypeForOverloadIdentity(paramType, arg.SwiftTypeSpec, methodEnv.TypeDatabase);
                paramTypes.Add(paramType);
            }
        }

        return $"{methodName}({string.Join(",", paramTypes)})";
    }
}
