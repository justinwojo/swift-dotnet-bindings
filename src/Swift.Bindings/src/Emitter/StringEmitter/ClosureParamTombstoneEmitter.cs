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
                });
                if (proj == null) return false;
            }
        }
        catch
        {
            return false;
        }

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

        // Build the parameter list. Closures → object?; other params → projected public type.
        var paramStrings = new List<string>();
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
                });
                // IsEligible already verified non-null projection; defensive fallback.
                paramType = proj?.PublicType ?? "object?";
            }

            paramStrings.Add($"{paramType} {paramName}");
        }
        var paramList = string.Join(", ", paramStrings);

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

        var accessModifier = NameProvider.GetAccessModifier(method.Visibility);
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
                        });
                        returnType = proj?.PublicType ?? "object?";
                    }
                }
            }

            // Use the env's resolved C# method name (handles renames + collisions).
            var methodName = env.CSharpMethodName;

            csWriter.WriteLine($"{accessModifier} {staticKw}{returnType} {methodName}({paramList})");
            csWriter.WriteLine("{");
            csWriter.Indent++;
            csWriter.WriteLine(throwBody);
            csWriter.Indent--;
            csWriter.WriteLine("}");
        }
    }
}
