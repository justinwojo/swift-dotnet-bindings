// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Emits a tombstoned-but-reachable C# surface for methods/constructors/free
/// functions whose only blocker is an unsupported closure parameter shape.
///
/// Without this, the surrounding member is dropped wholesale and the API is
/// invisible to consumers. With this, the member exists at the API surface
/// (so consumers see "this is the right API, the binding can't bridge it
/// today") but is unreachable: the unsupported closure parameter projects to
/// <c>object?</c>, the member carries <c>[Obsolete(... DiagnosticId = "SB0005")]</c>
/// + <c>[UnsupportedSwiftType("Unsupported closure fallback", ...)]</c>, and the
/// body throws <see cref="System.NotSupportedException"/>.
///
/// Scope (v1):
/// - Free functions on a module (parent = ModuleDecl)
/// - Class instance/static methods, including class constructors
/// - Struct/enum instance/static methods (NOT struct/enum constructors —
///   definite-assignment rules require all fields be assigned)
/// - Excludes: accessors, async methods, mutating methods, generic methods,
///   methods with unsupported return-position types or unsupported
///   non-closure parameter types
/// </summary>
internal static class ClosureParamTombstoneEmitter
{
    private const string DiagnosticId = "SB0005";
    private const string ObsoleteMessage = "Closure parameter shape not yet bridgeable from C#.";
    private const string FallbackReason = "Unsupported closure fallback";
    private const string ThrowMessage =
        "Closure parameter shape not yet bridgeable from C#. This API is exposed for visibility only and cannot be invoked.";
    private const string UrlFormat = "https://github.com/justinwojo/swift-dotnet-bindings/wiki/Troubleshooting";

    /// <summary>
    /// Determines whether <paramref name="method"/> is in v1 tombstone scope.
    /// </summary>
    public static bool IsEligible(MethodDecl method, ITypeDatabase typeDatabase)
    {
        if (method.IsAccessor) return false;
        if (method.IsAsync) return false;
        if (method.IsMutating) return false;

        // Method-own generics aren't projected here (would require resolving
        // T0/T1 aliases in the tombstone signature). Type-level generics are OK.
        if (method.GenericParameters != null && method.GenericParameters.Count > 0)
        {
            // Allow only if every generic param is also declared on the parent type
            // (the parser sometimes copies parent params onto the method).
            var parentTypeDecl = method.ParentDecl as TypeDecl;
            var parentNames = new HashSet<string>(StringComparer.Ordinal);
            if (parentTypeDecl != null)
            {
                foreach (var g in parentTypeDecl.GenericParameters)
                    parentNames.Add(g.TypeName);
            }
            foreach (var g in method.GenericParameters)
            {
                if (!parentNames.Contains(g.TypeName))
                    return false;
            }
        }

        var parent = method.ParentDecl;
        bool parentOk =
            parent is ModuleDecl ||
            parent is ClassDecl ||
            (parent is StructDecl && !method.IsConstructor) ||
            (parent is EnumDecl && !method.IsConstructor);
        if (!parentOk) return false;

        // Module-level (free function) constructors aren't a thing — already excluded.

        var closureHandler = new ClosureHandler(typeDatabase);

        // Require at least one parameter-position closure to be unsupported.
        bool hasUnsupportedParamClosure = false;
        for (int i = 1; i < method.CSSignature.Count; i++)
        {
            var arg = method.CSSignature[i];
            if (!closureHandler.IsClosure(arg)) continue;
            var spec = closureHandler.GetClosureTypeSpec(arg);
            if (spec == null || !closureHandler.IsSupportedClosure(spec))
            {
                hasUnsupportedParamClosure = true;
                break;
            }
        }
        if (!hasUnsupportedParamClosure) return false;

        // Reject if the return type is itself an unsupported closure
        // (return-position is a different problem class).
        if (method.CSSignature.Count > 0)
        {
            var ret = method.CSSignature[0];
            if (closureHandler.IsClosure(ret))
            {
                var rspec = closureHandler.GetClosureTypeSpec(ret);
                if (rspec == null || !closureHandler.IsSupportedClosure(rspec))
                    return false;
            }
        }

        // Verify all non-closure parameters and the return type project cleanly.
        // Anything that fails projection here would also crash inside Emit, so
        // disqualify and let the wholesale-skip path proceed.
        var genericContext = parent is TypeDecl parentType
            ? GenericContext.FromMethodInType(method, parentType)
            : GenericContext.FromMethod(method);

        // Same module context the emission below uses, so eligibility probes the exact
        // projections that will be written.
        var emittingModule = ResolveEmittingModule(method);

        try
        {
            var factory = new TypeProjectionFactory();

            // Return type (skip for constructors — handled by the type itself).
            if (!method.IsConstructor && method.CSSignature.Count > 0)
            {
                var ret = method.CSSignature[0];
                if (!ret.SwiftTypeSpec.IsEmptyTuple && !closureHandler.IsClosure(ret))
                {
                    var proj = factory.Project(ret.SwiftTypeSpec, new ProjectionContext
                    {
                        TypeDatabase = typeDatabase,
                        IsParameter = false,
                        GenericContext = genericContext,
                        ParentTypeDecl = parent as TypeDecl,
                        CurrentModuleName = emittingModule,
                    });
                    if (proj == null) return false;
                }
            }

            for (int i = 1; i < method.CSSignature.Count; i++)
            {
                var arg = method.CSSignature[i];
                if (closureHandler.IsClosure(arg)) continue;
                if (arg.SwiftTypeSpec.IsEmptyTuple) continue;
                if (DefaultParameterOverloadEmitter.IsDebugParameter(arg)) continue;

                var proj = factory.Project(arg.SwiftTypeSpec, new ProjectionContext
                {
                    TypeDatabase = typeDatabase,
                    IsParameter = true,
                    GenericContext = genericContext,
                    ParentTypeDecl = parent as TypeDecl,
                    CurrentModuleName = emittingModule,
                });
                if (proj == null) return false;
            }
        }
        catch
        {
            return false;
        }

        // A tombstone renders projected type names verbatim into this module's compile
        // unit. Naming a type that lives in a SIBLING Swift binding module is only safe
        // if that module's managed assembly is referenced here — and the generator cannot
        // know that: a declared native dependency contributes headers and a module
        // database but injects no managed reference, so the emitted name resolves to
        // nothing and the whole file fails to compile. That is a strictly worse outcome
        // than the member being absent, and the tombstone is a visibility-only,
        // never-invocable surface, so there is nothing to preserve by degrading the
        // offending parameter. Disqualify the member and let the ordinary wholesale-skip
        // comment stand. Types from this module, the Swift standard library, and the
        // Apple planes are always reachable — the generated project references those
        // unconditionally.
        if (ReferencesUnreachableModule(method, closureHandler, emittingModule))
            return false;

        return true;
    }

    /// <summary>
    /// The Swift module whose compile unit this member is being emitted into.
    /// </summary>
    private static string ResolveEmittingModule(MethodDecl method)
    {
        if (!string.IsNullOrEmpty(method.ModuleDecl?.Name))
            return method.ModuleDecl!.Name;
        return (method.ParentDecl as TypeDecl)?.SwiftTypeName.Module ?? string.Empty;
    }

    /// <summary>
    /// True when the return type or any parameter that the tombstone renders with its real
    /// projected name reaches a type owned by a module other than the emitting module, the
    /// Swift standard library, or a known Apple framework. Unsupported closure parameters
    /// are exempt — they render as <c>object?</c> and name nothing.
    /// </summary>
    private static bool ReferencesUnreachableModule(
        MethodDecl method, ClosureHandler closureHandler, string emittingModule)
    {
        for (int i = 0; i < method.CSSignature.Count; i++)
        {
            var arg = method.CSSignature[i];
            if (i == 0 && method.IsConstructor) continue;
            if (arg.SwiftTypeSpec == null || arg.SwiftTypeSpec.IsEmptyTuple) continue;

            if (closureHandler.IsClosure(arg))
            {
                var spec = closureHandler.GetClosureTypeSpec(arg);
                if (spec == null || !closureHandler.IsSupportedClosure(spec)) continue;
            }

            if (ReachesUnreachableModule(arg.SwiftTypeSpec, emittingModule))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Walks a type-spec tree and reports whether any named node resolves to a module the
    /// emitting compile unit has no guaranteed managed reference to. Nested generic
    /// arguments, tuple elements, closure parameters/returns, and protocol-composition
    /// members are all visited: a foreign type buried inside <c>[Foo]</c> or
    /// <c>(Foo) -> Void</c> is rendered into the signature just as plainly as a bare one.
    /// </summary>
    private static bool ReachesUnreachableModule(TypeSpec? spec, string emittingModule)
    {
        if (spec == null) return false;

        switch (spec)
        {
            case NamedTypeSpec named:
                if (IsUnreachableModuleName(named, emittingModule)) return true;
                break;
            case TupleTypeSpec tuple:
                foreach (var element in tuple.Elements)
                    if (ReachesUnreachableModule(element, emittingModule)) return true;
                break;
            case ClosureTypeSpec closure:
                if (ReachesUnreachableModule(closure.Arguments, emittingModule)) return true;
                if (ReachesUnreachableModule(closure.ReturnType, emittingModule)) return true;
                break;
            case ProtocolListTypeSpec protocols:
                foreach (var protocol in protocols.Protocols.Keys)
                    if (ReachesUnreachableModule(protocol, emittingModule)) return true;
                break;
        }

        foreach (var generic in spec.GenericParameters)
            if (ReachesUnreachableModule(generic, emittingModule)) return true;

        return false;
    }

    private static bool IsUnreachableModuleName(NamedTypeSpec named, string emittingModule)
    {
        // Only a well-formed module-qualified name carries module identity. A bare or
        // generic-parameter name is not a cross-module reference.
        if (!SwiftTypeName.TryFromModuleQualifiedName(named.Name, out var typeName))
            return false;

        var module = typeName.Module;
        if (string.IsNullOrEmpty(module)) return false;
        if (string.Equals(module, emittingModule, StringComparison.Ordinal)) return false;
        // The standard library projects onto the always-referenced runtime assembly.
        if (module is "Swift" or "Builtin") return false;
        // Apple frameworks project onto the platform assembly or the Apple supplement,
        // both of which the generated project references unconditionally.
        if (AppleFrameworkRegistry.IsKnownModule(module)) return false;

        return true;
    }

    /// <summary>
    /// Emits the tombstoned member declaration. Caller must set
    /// <c>WasEmitted = true</c> and record the wrap.
    /// </summary>
    public static void Emit(CSharpWriter csWriter, MethodEnvironment env)
    {
        var method = env.MethodDecl;
        var typeDatabase = env.TypeDatabase;
        var closureHandler = env.ClosureHandler;

        // Pre-deduplicate parameter names so collisions across overloaded
        // closure-bearing inits don't produce CS0100.
        NameProvider.DeduplicateParameterNames(method.CSSignature);

        var parent = method.ParentDecl;
        var genericContext = parent is TypeDecl parentType
            ? GenericContext.FromMethodInType(method, parentType)
            : GenericContext.FromMethod(method);

        // A tombstone renders real projected type names into this compile unit, so the module
        // being emitted has to travel with every projection below. The eligibility gate already
        // rejects a signature naming a plainly foreign module, but a protocol whose spec carries
        // an umbrella module name while its record lives in a sibling passes that gate — and its
        // interface still has to name the module that actually owns it.
        var emittingModule = ResolveEmittingModule(method);

        // Build the parameter list. Closures → object?; other params → projected public type.
        var paramStrings = new List<string>();
        // The same parameters without their names — what an API-surface record names them by.
        var paramTypes = new List<string>();
        var unsupportedClosureSpecs = new List<string>();
        var factory = new TypeProjectionFactory();
        for (int i = 1; i < method.CSSignature.Count; i++)
        {
            var arg = method.CSSignature[i];
            if (arg.SwiftTypeSpec.IsEmptyTuple) continue;
            if (DefaultParameterOverloadEmitter.IsDebugParameter(arg)) continue;

            var paramName = NameProvider.GetCSharpParameterName(arg);
            string paramType;

            if (closureHandler.IsClosure(arg))
            {
                var spec = closureHandler.GetClosureTypeSpec(arg);
                bool isUnsupported = spec == null || !closureHandler.IsSupportedClosure(spec);
                if (isUnsupported)
                {
                    paramType = "object?";
                    unsupportedClosureSpecs.Add(arg.SwiftTypeSpec?.ToString() ?? "<unknown closure>");
                }
                else
                {
                    // Supported closures still project normally — keep the natural
                    // delegate signature so the surface is honest.
                    var proj = factory.Project(arg.SwiftTypeSpec, new ProjectionContext
                    {
                        TypeDatabase = typeDatabase,
                        IsParameter = true,
                        GenericContext = genericContext,
                        ParentTypeDecl = parent as TypeDecl,
                        CurrentModuleName = emittingModule,
                    });
                    paramType = proj?.PublicType ?? "object?";
                }
            }
            else
            {
                var proj = factory.Project(arg.SwiftTypeSpec, new ProjectionContext
                {
                    TypeDatabase = typeDatabase,
                    IsParameter = true,
                    GenericContext = genericContext,
                    ParentTypeDecl = parent as TypeDecl,
                    CurrentModuleName = emittingModule,
                });
                // IsEligible already verified non-null projection; defensive fallback.
                paramType = proj?.PublicType ?? "object?";
            }

            paramStrings.Add($"{paramType} {paramName}");
            paramTypes.Add(paramType);
        }
        var paramList = string.Join(", ", paramStrings);
        var emittedParameterPortion = ModuleEmissionContext.FormatParameterPortion(paramTypes);

        // Pick the swift-side closure spec for the [UnsupportedSwiftType] attribute.
        // If multiple unsupported closures, list them comma-separated.
        var swiftFallbackType = unsupportedClosureSpecs.Count > 0
            ? string.Join("; ", unsupportedClosureSpecs)
            : "<closure>";

        // Emit attributes.
        csWriter.WriteLine(
            $"[global::Swift.UnsupportedSwiftType(\"{UnsupportedSwiftTypeSupport.EscapeStringLiteral(FallbackReason)}\", " +
            $"\"{UnsupportedSwiftTypeSupport.EscapeStringLiteral(swiftFallbackType)}\")]");
        csWriter.WriteLine(
            $"[global::System.Obsolete(\"{UnsupportedSwiftTypeSupport.EscapeStringLiteral(ObsoleteMessage)}\", " +
            $"DiagnosticId = \"{DiagnosticId}\", " +
            $"UrlFormat = \"{UrlFormat}\")]");

        var accessModifier = NameProvider.GetAccessModifier(method.IsSynthesizedAccessor);
        var throwBody =
            $"throw new global::System.NotSupportedException(\"{UnsupportedSwiftTypeSupport.EscapeStringLiteral(ThrowMessage)}\");";

        if (method.IsConstructor)
        {
            // Constructors only ever land here for ClassDecl parents (IsEligible filter).
            var classParent = (ClassDecl)parent!;
            var ctorName = classParent.Name;

            var baseChain =
                (classParent.HasResolvedSuperclass || classParent.HasCrossModuleSwiftSuperclass)
                    ? " : base(default(SwiftInheritanceChain))"
                    : "";

            env.EmissionContext?.RecordEmittedApiShape(method, ctorName, emittedParameterPortion);

            csWriter.WriteLine($"{accessModifier} {ctorName}({paramList}){baseChain}");
            csWriter.WriteLine("{");
            csWriter.Indent++;
            csWriter.WriteLine(throwBody);
            csWriter.Indent--;
            csWriter.WriteLine("}");
        }
        else
        {
            // Methods (instance/static, free function, struct/enum non-mutating).
            bool isStatic = method.MethodType == MethodType.Static || parent is ModuleDecl;
            string staticKw = isStatic ? "static " : "";

            // C# return type
            string returnType = "void";
            if (method.CSSignature.Count > 0)
            {
                var ret = method.CSSignature[0];
                if (!ret.SwiftTypeSpec.IsEmptyTuple)
                {
                    if (closureHandler.IsClosure(ret))
                    {
                        // IsEligible only allows supported return-position closures here.
                        var proj = factory.Project(ret.SwiftTypeSpec, new ProjectionContext
                        {
                            TypeDatabase = typeDatabase,
                            IsParameter = false,
                            GenericContext = genericContext,
                            ParentTypeDecl = parent as TypeDecl,
                            CurrentModuleName = emittingModule,
                        });
                        returnType = proj?.PublicType ?? "object?";
                    }
                    else
                    {
                        var proj = factory.Project(ret.SwiftTypeSpec, new ProjectionContext
                        {
                            TypeDatabase = typeDatabase,
                            IsParameter = false,
                            GenericContext = genericContext,
                            ParentTypeDecl = parent as TypeDecl,
                            CurrentModuleName = emittingModule,
                        });
                        returnType = proj?.PublicType ?? "object?";
                    }
                }
            }

            // Use the env's resolved C# method name (handles renames + collisions).
            var methodName = env.CSharpMethodName;

            env.EmissionContext?.RecordEmittedApiShape(method, methodName, emittedParameterPortion);

            csWriter.WriteLine($"{accessModifier} {staticKw}{returnType} {methodName}({paramList})");
            csWriter.WriteLine("{");
            csWriter.Indent++;
            csWriter.WriteLine(throwBody);
            csWriter.Indent--;
            csWriter.WriteLine("}");
        }
    }
}
