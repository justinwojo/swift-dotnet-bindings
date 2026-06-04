// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

public partial class ProtocolProxyEmitter
{
    private void EmitInterfaceImplementation(CSharpWriter writer, ProtocolDecl protocolDecl, string interfaceName, WitnessDispatchEmitter dispatchEmitter)
    {
        writer.WriteLine("#region Interface Implementation");
        writer.WriteLine();

        // Track emitted members to avoid duplicates
        var emittedMembers = new HashSet<string>();

        // Properties (skip static properties - they're not part of the interface)
        foreach (var property in protocolDecl.Properties)
        {
            if (property.IsStatic)
                continue;
            if (emittedMembers.Add($"property:{property.Name}"))
            {
                if (_skippedPropertyNames.Contains(property.Name))
                {
                    // Closure-skipped properties are now in the interface — emit NotSupported stub
                    if (_closureSkippedPropertyNames.Contains(property.Name))
                        EmitNotSupportedPropertyStub(writer, property);
                    continue;
                }
                EmitPropertyImplementation(writer, property, protocolDecl, dispatchEmitter);
            }
        }

        // Subscripts (as indexers) - skip static subscripts
        int subscriptIndex = 0;
        foreach (var subscript in protocolDecl.Subscripts)
        {
            if (subscript.IsStatic)
                continue;
            var key = $"subscript:{subscriptIndex}";
            if (emittedMembers.Add(key))
            {
                // Skip subscripts that the interface skipped due to AnyType generic args
                if (_skippedSubscriptIndices.Contains(subscriptIndex))
                {
                    subscriptIndex++;
                    continue;
                }
                EmitSubscriptImplementation(writer, subscript, protocolDecl, subscriptIndex);
            }
            subscriptIndex++;
        }

        // Collect emitted C# property names for method/property collision detection.
        // Use the canonical cached set populated by ProtocolHandler / InterfacePropertyNamePrecomputer
        // so the proxy's view matches what the interface actually emits. The canonical set
        // includes BOTH instance properties (closure-stub or otherwise) AND static abstract
        // property names — both produce real C# members on the proxy class (instance via the
        // interface contract, static via EmitStaticAbstractStubs below) and either can collide
        // with an instance method's projected name (e.g., a `static var Foo` plus instance
        // `func foo(...)` forces the method to emit as `FooMethod`).
        var protoQualifiedName = protocolDecl.SwiftTypeName?.ModuleQualifiedName
                               ?? $"{protocolDecl.ModuleDecl?.Name ?? "Unknown"}.{protocolDecl.Name}";
        var canonicalPropertyNames = _emissionContext.GetInterfacePropertyNames(protoQualifiedName);
        HashSet<string> emittedCSharpPropertyNames;
        if (canonicalPropertyNames != null)
        {
            emittedCSharpPropertyNames = new HashSet<string>(canonicalPropertyNames);
        }
        else
        {
            // Defensive fallback: the prepass populates the cache for every protocol in the
            // module, so this branch should not trigger in practice. Mirror the canonical
            // construction (instance + static), not the previous instance-only approximation.
            emittedCSharpPropertyNames = new HashSet<string>();
            foreach (var property in protocolDecl.Properties)
            {
                if (property.IsStatic)
                {
                    if (_staticAbstractPropertyNames.Contains(property.Name))
                        emittedCSharpPropertyNames.Add(NameProvider.GetPropertyName(property.Name));
                }
                else if (!_skippedPropertyNames.Contains(property.Name) || _closureSkippedPropertyNames.Contains(property.Name))
                {
                    emittedCSharpPropertyNames.Add(NameProvider.GetPropertyName(property.Name));
                }
            }
        }

        // Methods - track by signature to handle overloads.
        // Value carries the originally-emitted MethodDecl so the inherited-walk pass can
        // compare projected return types and emit a covariant-return forwarder when a child
        // protocol refines a base protocol's return type (CS0738 fix).
        int methodIndex = 0;
        var methodIndices = new Dictionary<string, int>();
        var emittedCSharpKeys = new Dictionary<string, MethodDecl>();
        foreach (var method in protocolDecl.Methods)
        {
            if (method.IsConstructor || method.MethodType == MethodType.Static)
                continue;

            var methodKey = ProtocolSignatureHelper.GetMethodSignatureKey(method, _typeDatabase, protocolDecl);
            if (!methodIndices.ContainsKey(methodKey))
            {
                var idx = methodIndex++;
                methodIndices[methodKey] = idx;
                if (_skippedMethodKeys.Contains(methodKey))
                {
                    // Closure-skipped methods are now in the interface — emit NotSupported stub
                    if (_closureSkippedMethodKeys.Contains(methodKey))
                    {
                        // Pass the proxy's own propertyNames so the dedup key reflects the
                        // collision-aware C# member name (Foo -> FooMethod when this protocol
                        // has a property Foo). Without it, two methods that emit under
                        // different C# names can falsely share a dedup key, dropping one
                        // emission entirely.
                        var projectedKeySkipped = ProtocolSignatureHelper.GetProjectedCSharpMethodKey(method, _typeDatabase, protocolDecl, emittedCSharpPropertyNames);
                        if (emittedCSharpKeys.ContainsKey(projectedKeySkipped))
                            continue;
                        emittedCSharpKeys[projectedKeySkipped] = method;
                        EmitNotSupportedMethodStub(writer, method, "closure parameters cannot be marshalled", emittedCSharpPropertyNames);
                    }
                    continue;
                }
                var projectedKey = ProtocolSignatureHelper.GetProjectedCSharpMethodKey(method, _typeDatabase, protocolDecl, emittedCSharpPropertyNames);
                if (emittedCSharpKeys.ContainsKey(projectedKey))
                    continue;
                emittedCSharpKeys[projectedKey] = method;
                EmitMethodImplementation(writer, method, protocolDecl, dispatchEmitter, idx, emittedCSharpPropertyNames);
            }
        }

        // Static virtual members: emit NotSupportedException stubs to satisfy the interface contract.
        // Proxy types can't dispatch static protocol requirements to Swift (no existential container
        // for statics). NOTE: When EveryProtocol conformance is skipped for static method protocols,
        // the proxy's SetVtable/WitnessTable P/Invoke symbols don't exist — this is a pre-existing
        // latent issue (see ProtocolHandler.cs TODO). The static stubs here don't make it worse;
        // the proxy was already emitted and runtime-broken for these protocols. Full fix requires
        // co-gating proxy emission with EveryProtocol conformance.
        EmitStaticAbstractStubs(writer, protocolDecl);

        // Emit implementations for inherited protocol interface members.
        // When the C# interface uses inheritance (IDrawable : IDescribable), the proxy must
        // also implement the parent interface members to avoid CS0535.
        EmitInheritedInterfaceImplementations(writer, protocolDecl, dispatchEmitter, emittedMembers, emittedCSharpKeys, emittedCSharpPropertyNames);

        writer.WriteLine("#endregion");
        writer.WriteLine();
    }

    private void EmitStaticAbstractStubs(CSharpWriter writer, ProtocolDecl protocolDecl)
    {
        if (_staticAbstractPropertyNames.Count == 0 && _staticAbstractMethodKeys.Count == 0)
            return;

        writer.WriteLine();
        writer.WriteLine("// Static abstract member stubs (protocol proxy cannot dispatch static requirements)");

        // Static property stubs (dedup by name — protocols can have duplicate entries in ABI JSON)
        var emittedStaticPropertyNames = new HashSet<string>();
        foreach (var property in protocolDecl.Properties)
        {
            if (!property.IsStatic || !_staticAbstractPropertyNames.Contains(property.Name))
                continue;
            if (!emittedStaticPropertyNames.Add(property.Name))
                continue;

            var csharpTypeName = GetInterfaceCompatiblePropertyTypeName(property);
            var propertyName = NameProvider.GetPropertyName(property.Name);
            var hasGetter = property.Accessors.OfType<GetAccessorDecl>().Any();
            var hasSetter = property.Accessors.OfType<SetAccessorDecl>().Any();

            if (hasGetter && hasSetter)
            {
                writer.WriteLine($"public static {csharpTypeName} {propertyName}");
                writer.WriteLine("{");
                writer.Indent++;
                writer.WriteLine("get => throw new NotSupportedException(\"Static protocol members cannot be dispatched through protocol proxy types.\");");
                writer.WriteLine("set => throw new NotSupportedException(\"Static protocol members cannot be dispatched through protocol proxy types.\");");
                writer.Indent--;
                writer.WriteLine("}");
            }
            else if (hasSetter)
            {
                writer.WriteLine($"public static {csharpTypeName} {propertyName}");
                writer.WriteLine("{");
                writer.Indent++;
                writer.WriteLine("set => throw new NotSupportedException(\"Static protocol members cannot be dispatched through protocol proxy types.\");");
                writer.Indent--;
                writer.WriteLine("}");
            }
            else
            {
                writer.WriteLine($"public static {csharpTypeName} {propertyName} => throw new NotSupportedException(\"Static protocol members cannot be dispatched through protocol proxy types.\");");
            }
        }

        // Collect property names for method/property collision detection on the static
        // method stub path. Source from the canonical cached set (ProtocolHandler /
        // InterfacePropertyNamePrecomputer) so the dedup key at the projection-key site below
        // and the methodName resolution use the same set the interface itself used. The
        // canonical set includes static-property names that pass duplicate detection regardless
        // of gate result (mirroring ProtocolHandler.Emit's emittedCSharpPropertyNames), so a
        // gate-skipped static `Foo` plus static `foo(...)` correctly forces the interface to
        // emit `FooMethod(...)` AND the proxy stub to emit `FooMethod(...)`.
        var staticStubsProtoQualifiedName = protocolDecl.SwiftTypeName?.ModuleQualifiedName
                                          ?? $"{protocolDecl.ModuleDecl?.Name ?? "Unknown"}.{protocolDecl.Name}";
        var staticStubsCanonicalNames = _emissionContext.GetInterfacePropertyNames(staticStubsProtoQualifiedName);
        HashSet<string> staticPropertyNames;
        if (staticStubsCanonicalNames != null)
        {
            staticPropertyNames = new HashSet<string>(staticStubsCanonicalNames);
        }
        else
        {
            // Defensive fallback: prepass populates the cache for every protocol in the
            // module, so this branch should not trigger in practice.
            staticPropertyNames = new HashSet<string>();
            foreach (var property in protocolDecl.Properties)
            {
                if (property.IsStatic && _staticAbstractPropertyNames.Contains(property.Name))
                    staticPropertyNames.Add(NameProvider.GetPropertyName(property.Name));
            }
            foreach (var property in protocolDecl.Properties)
            {
                if (!property.IsStatic &&
                    (!_skippedPropertyNames.Contains(property.Name) || _closureSkippedPropertyNames.Contains(property.Name)))
                    staticPropertyNames.Add(NameProvider.GetPropertyName(property.Name));
            }
        }

        // Static method stubs
        var emittedStaticMethodCSharpKeys = new HashSet<string>();
        foreach (var method in protocolDecl.Methods)
        {
            if (method.MethodType != MethodType.Static)
                continue;
            var methodKey = ProtocolSignatureHelper.GetMethodSignatureKey(method, _typeDatabase, protocolDecl);
            if (!_staticAbstractMethodKeys.Contains(methodKey))
                continue;

            // Pass staticPropertyNames so the dedup key reflects the same collision-aware
            // C# member name resolution used at the methodName site below (line 226). Without
            // it, two static methods that emit under different C# names (e.g., `Foo` -> `FooMethod`
            // because of a static `var Foo` collision) can falsely share a dedup key, dropping
            // one stub entirely.
            var projectedKey = ProtocolSignatureHelper.GetProjectedCSharpMethodKey(method, _typeDatabase, protocolDecl, staticPropertyNames);
            if (!emittedStaticMethodCSharpKeys.Add(projectedKey))
                continue;

            var returnType = method.CSSignature.FirstOrDefault()?.SwiftTypeSpec;
            var hasReturn = returnType != null && !returnType.IsEmptyTuple;
            var returnTypeName = hasReturn ? GetCSharpTypeName(returnType!, isParameter: false) : "void";

            if (method.IsAsync)
                returnTypeName = returnTypeName == "void" ? "Task" : $"Task<{returnTypeName}>";

            var parameters = new List<string>();
            foreach (var param in method.CSSignature.Skip(1))
            {
                if (DefaultParameterOverloadEmitter.IsDebugParameter(param))
                    continue;
                if (param.SwiftTypeSpec.IsEmptyTuple)
                    continue;
                var paramTypeName = GetCSharpTypeName(param.SwiftTypeSpec, isParameter: true);
                var paramName = NameProvider.GetCSharpParameterName(param);
                parameters.Add($"{paramTypeName} {paramName}");
            }
            if (method.IsAsync)
                parameters.Add("global::System.Threading.CancellationToken cancellationToken = default");

            var isSelfReturning = MethodEnvironment.IsSelfReturningMethod(method);
            var methodName = NameProvider.GetPublicMethodName(method.Name, method.IsAsync, hasReturn,
                propertyNames: staticPropertyNames,
                isSelfReturning: isSelfReturning,
                parameterCount: method.CSSignature.Skip(1).Count(a => !DefaultParameterOverloadEmitter.IsDebugParameter(a) && !a.SwiftTypeSpec.IsEmptyTuple));

            writer.WriteLine($"public static {returnTypeName} {methodName}({string.Join(", ", parameters)}) => throw new NotSupportedException(\"Static protocol members cannot be dispatched through protocol proxy types.\");");
        }
    }

    /// <summary>
    /// Emits NotSupportedException stub implementations for inherited protocol interface members.
    /// When C# interface uses inheritance (IDrawable : IDescribable), the proxy must implement
    /// parent interface members to satisfy the C# compiler (CS0535).
    /// Uses stubs because the parent protocol's witness dispatch P/Invoke symbols are declared
    /// in the parent proxy's NativeMethods, not the child proxy's. Full dispatch support would
    /// require duplicating P/Invoke declarations across proxy classes.
    /// </summary>
    private void EmitInheritedInterfaceImplementations(
        CSharpWriter writer, ProtocolDecl protocolDecl, WitnessDispatchEmitter dispatchEmitter,
        HashSet<string> emittedMembers, Dictionary<string, MethodDecl> emittedCSharpKeys,
        HashSet<string> emittedCSharpPropertyNames)
    {
        if (protocolDecl.InheritedProtocols.Count == 0)
            return;

        var moduleDecl = protocolDecl.ModuleDecl;
        if (moduleDecl == null)
            return;

        // Collect all inherited protocols that would be emitted as C# interface parents.
        // Must match the filtering logic in ProtocolHandler.GetInheritedInterfaceList.
        var inheritedProtocolDecls = new List<ProtocolDecl>();
        foreach (var inherited in protocolDecl.InheritedProtocols)
        {
            if (inherited.Name is "Swift.AnyObject" or "AnyObject")
                continue;
            if (inherited.NameWithoutModule is "Sendable" or "Escapable" or "Copyable" or "SendableMetatype")
                continue;

            var swiftTypeName = SwiftTypeName.FromTypeSpec(inherited);
            if (!_typeDatabase.TryGetTypeRecord(swiftTypeName, out var inheritedRecord))
                continue;
            if (inheritedRecord.Kind != TypeRecordKind.Protocol)
                continue;
            if (inheritedRecord.Flags.HasFlag(TypeRecordFlags.HasAssociatedTypes) ||
                inheritedRecord.Flags.HasFlag(TypeRecordFlags.HasSelfRequirement))
                continue;

            // Skip underscore-suppressed protocols — their interfaces aren't emitted
            if (swiftTypeName != null && _emissionContext.IsUnderscoreSuppressed(swiftTypeName.ToString()))
                continue;

            // Resolve the ProtocolDecl: same-module from moduleDecl.Protocols, cross-module
            // from moduleDecl.DependencyProtocols (populated when --framework-dependency
            // ABI was parsed). Cross-module parents are emitted as interface parents by
            // ProtocolHandler.GetInheritedInterfaceList — without their stubs the proxy
            // fails CS0535 on the inherited interface contract. Required for
            // justinwojo/swift-dotnet-bindings#40 cross-module variant.
            ProtocolDecl? parentProtoDecl = ResolveInheritedProtocolDecl(inherited, moduleDecl);
            if (parentProtoDecl != null)
                inheritedProtocolDecls.Add(parentProtoDecl);
        }

        if (inheritedProtocolDecls.Count == 0)
            return;

        writer.WriteLine();
        writer.WriteLine("// Inherited protocol interface implementations (stubs — dispatch via parent proxy)");

        // Tracks already-emitted explicit-interface impls so the covariant-return forwarder
        // doesn't double-write the same `Interface.Method(params)` line. Multiple inheritance
        // paths from the proxy class to the same base interface (common in WCDB's overlapping
        // refinement protocols) walk the same method via different parents and would otherwise
        // produce duplicate explicit impls — CS8646 ("explicitly implemented more than once")
        // and CS0111. The C# slot is satisfied by the first emission; later paths must skip.
        var emittedExplicitImplSignatures = new HashSet<string>(StringComparer.Ordinal);

        // Recursively collect inherited protocols (walk the chain)
        var allInherited = new List<ProtocolDecl>();
        var visited = new HashSet<string>();
        var queue = new Queue<ProtocolDecl>(inheritedProtocolDecls);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            var currentKey = current.SwiftTypeName?.ModuleQualifiedName ?? current.Name;
            if (!visited.Add(currentKey))
                continue;
            allInherited.Add(current);

            // Walk further up the chain (same filtering as GetInheritedInterfaceList)
            foreach (var grandparent in current.InheritedProtocols)
            {
                if (grandparent.Name is "Swift.AnyObject" or "AnyObject")
                    continue;
                if (grandparent.NameWithoutModule is "Sendable" or "Escapable" or "Copyable" or "SendableMetatype")
                    continue;

                // Skip PAT/Self protocols — their interfaces aren't inherited
                var gpSwiftName = SwiftTypeName.FromTypeSpec(grandparent);
                if (_typeDatabase.TryGetTypeRecord(gpSwiftName, out var gpRecord))
                {
                    if (gpRecord.Flags.HasFlag(TypeRecordFlags.HasAssociatedTypes) ||
                        gpRecord.Flags.HasFlag(TypeRecordFlags.HasSelfRequirement))
                        continue;
                }

                // Skip underscore-suppressed protocols
                if (gpSwiftName != null && _emissionContext.IsUnderscoreSuppressed(gpSwiftName.ToString()))
                    continue;

                // Same lookup policy as the direct-parent collection above: resolve from
                // local or dependency module, so cross-module ancestor chains are walked.
                var gpDecl = ResolveInheritedProtocolDecl(grandparent, moduleDecl);
                if (gpDecl != null)
                    queue.Enqueue(gpDecl);
            }
        }

        foreach (var inheritedProto in allInherited)
        {
            // Emit inherited property stubs
            foreach (var property in inheritedProto.Properties)
            {
                if (property.IsStatic)
                    continue;
                if (!emittedMembers.Add($"property:{property.Name}"))
                    continue;
                EmitInheritedPropertyStub(writer, property);
                // Track inherited property names so inherited methods with the same name
                // get renamed (e.g., RichText property + RichText(range) method collision).
                emittedCSharpPropertyNames.Add(NameProvider.GetPropertyName(property.Name));
            }

            // Resolve the inherited protocol's own emitted property-name set (cache hit
            // when the interface emitter already ran for it; conservative fallback to all
            // declared properties otherwise). The ancestor's projection key must be computed
            // with the ancestor's own collision set so a `Foo` method that the ancestor
            // emitted as `FooMethod` doesn't mismatch against the proxy's `Foo` key.
            var inheritedProtoQualifiedName = inheritedProto.SwiftTypeName?.ModuleQualifiedName
                                           ?? $"{inheritedProto.ModuleDecl?.Name ?? "Unknown"}.{inheritedProto.Name}";
            var inheritedOwnPropertyNames = _emissionContext.GetInterfacePropertyNames(inheritedProtoQualifiedName)
                ?? new HashSet<string>(inheritedProto.Properties.Select(p => NameProvider.GetPropertyName(p.Name)));

            // Emit inherited method stubs
            foreach (var method in inheritedProto.Methods)
            {
                if (method.IsConstructor || method.MethodType == MethodType.Static)
                    continue;

                var projectedKey = ProtocolSignatureHelper.GetProjectedCSharpMethodKey(method, _typeDatabase, inheritedProto, inheritedOwnPropertyNames);
                if (emittedCSharpKeys.TryGetValue(projectedKey, out var existingMethod))
                {
                    // Same C# overload key already emitted. If the inherited base method's
                    // projected return type differs from the already-emitted (refined) one,
                    // this is a covariant-return shadowing case (CS0738). Emit an explicit
                    // interface implementation forwarder so the base interface contract is
                    // satisfied. Equal return projections are legitimate dedup — skip silently.
                    TryEmitCovariantReturnForwarder(writer, inheritedProto, method, existingMethod, emittedExplicitImplSignatures, emittedCSharpPropertyNames);
                    continue;
                }
                emittedCSharpKeys[projectedKey] = method;
                EmitInheritedMethodStub(writer, method, emittedCSharpPropertyNames);
            }
        }
    }

    /// <summary>
    /// Resolves an inherited <see cref="NamedTypeSpec"/> reference to its <see cref="ProtocolDecl"/>,
    /// checking the local module first and falling back to dependency modules
    /// (populated when <c>--framework-dependency</c> was supplied at parse time).
    /// Returns <c>null</c> when no matching protocol is found in either source.
    /// </summary>
    private static ProtocolDecl? ResolveInheritedProtocolDecl(NamedTypeSpec inherited, ModuleDecl moduleDecl)
    {
        var inheritedModule = inherited.Module;
        var currentModule = moduleDecl.Name;
        if (string.IsNullOrEmpty(inheritedModule) || inheritedModule == currentModule)
        {
            return moduleDecl.Protocols.FirstOrDefault(p => p.Name == inherited.NameWithoutModule);
        }
        if (moduleDecl.DependencyProtocols.TryGetValue(inheritedModule, out var depProtos))
        {
            return depProtos.FirstOrDefault(p => p.Name == inherited.NameWithoutModule);
        }
        return null;
    }

    /// <summary>
    /// Emits a NotSupportedException stub for an inherited protocol property.
    /// </summary>
    private void EmitInheritedPropertyStub(CSharpWriter writer, PropertyDecl property)
    {
        var csharpTypeName = GetInterfaceCompatiblePropertyTypeName(property);
        var propertyName = NameProvider.GetPropertyName(property.Name);
        var hasGetter = property.Accessors.OfType<GetAccessorDecl>().Any();
        var hasSetter = property.Accessors.OfType<SetAccessorDecl>().Any();

        writer.WriteLine($"public {csharpTypeName} {propertyName}");
        writer.WriteLine("{");
        writer.Indent++;
        if (hasGetter)
            writer.WriteLine("get => throw new NotSupportedException(\"Inherited protocol member — dispatch via parent protocol proxy.\");");
        if (hasSetter)
            writer.WriteLine("set => throw new NotSupportedException(\"Inherited protocol member — dispatch via parent protocol proxy.\");");
        writer.Indent--;
        writer.WriteLine("}");
    }

    /// <summary>
    /// Emits a NotSupportedException stub for an inherited protocol method.
    /// </summary>
    private void EmitInheritedMethodStub(CSharpWriter writer, MethodDecl method,
        IReadOnlySet<string>? propertyNames = null)
    {
        var returnType = method.CSSignature.FirstOrDefault()?.SwiftTypeSpec;
        var hasReturn = returnType != null && !returnType.IsEmptyTuple;
        var returnTypeName = hasReturn ? GetCSharpTypeName(returnType!, isParameter: false) : "void";

        if (method.IsAsync)
            returnTypeName = returnTypeName == "void" ? "Task" : $"Task<{returnTypeName}>";

        var parameters = new List<string>();
        foreach (var param in method.CSSignature.Skip(1))
        {
            if (DefaultParameterOverloadEmitter.IsDebugParameter(param))
                continue;
            if (param.SwiftTypeSpec.IsEmptyTuple)
                continue;
            var paramTypeName = GetCSharpTypeName(param.SwiftTypeSpec, isParameter: true);
            var paramName = NameProvider.GetCSharpParameterName(param);
            parameters.Add($"{paramTypeName} {paramName}");
        }
        if (method.IsAsync)
            parameters.Add("global::System.Threading.CancellationToken cancellationToken = default");

        var isSelfReturning = MethodEnvironment.IsSelfReturningMethod(method);
        var methodName = NameProvider.GetPublicMethodName(method.Name, method.IsAsync, hasReturn,
            propertyNames: propertyNames,
            isSelfReturning: isSelfReturning,
            parameterCount: method.CSSignature.Skip(1).Count(a => !DefaultParameterOverloadEmitter.IsDebugParameter(a) && !a.SwiftTypeSpec.IsEmptyTuple));

        writer.WriteLine($"public {returnTypeName} {methodName}({string.Join(", ", parameters)}) => throw new NotSupportedException(\"Inherited protocol member — dispatch via parent protocol proxy.\");");
    }

    /// <summary>
    /// Emits an explicit interface implementation for an inherited base protocol method
    /// that shares a C# overload key with an already-emitted (refined-return) method on
    /// the proxy class. Without this, C# rejects the proxy with CS0738 because the base
    /// interface contract <c>Column _in(string)</c> is unimplemented — the refined
    /// <c>Property _in(string)</c> shadows but does not satisfy it.
    ///
    /// Pattern (Swift):
    /// <code>
    /// protocol Base { func in(_:String) -> Column }
    /// protocol Refined: Base { func in(_:String) -> Property }
    /// </code>
    /// Pattern (emitted C#):
    /// <code>
    /// public Property _in(string table) { /* refined body */ }
    /// Column IBase._in(string table) =&gt; (Column)this._in(table);   // when Property : Column
    /// Column IBase._in(string table) =&gt; throw new NotSupportedException(...);  // otherwise
    /// </code>
    ///
    /// Behavior:
    /// <list type="bullet">
    /// <item>Returns silently when return projections agree — legitimate dedup, no covariance.</item>
    /// <item>When the refined Swift type IS class-assignable to the base type via the
    ///   <see cref="ITypeDatabase"/> superclass chain, emits a cast forwarder so callers
    ///   through the base interface get up-cast results from the refined dispatch path.</item>
    /// <item>When the cast is NOT statically safe (e.g. WCDB's <c>Property</c> and <c>Column</c>
    ///   are sibling classes despite the protocol-level refinement), emits an explicit-interface
    ///   <c>NotSupportedException</c> stub. This satisfies the C# interface contract (no CS0738)
    ///   and gives consumers a clear runtime error directing them to the refined dispatch path.
    ///   Recorded to the binding report as <see cref="SkipReason.CovariantReturnNotRepresentable"/>
    ///   so the missing real implementation is auditable.</item>
    /// </list>
    /// </summary>
    private void TryEmitCovariantReturnForwarder(
        CSharpWriter writer,
        ProtocolDecl inheritedProto,
        MethodDecl inheritedMethod,
        MethodDecl refinedMethod,
        HashSet<string> emittedExplicitImplSignatures,
        IReadOnlySet<string> propertyNames)
    {
        // No covariant return when method projections agree — silent dedup.
        var inheritedReturnSpec = inheritedMethod.CSSignature.FirstOrDefault()?.SwiftTypeSpec;
        var refinedReturnSpec = refinedMethod.CSSignature.FirstOrDefault()?.SwiftTypeSpec;
        bool inheritedHasReturn = inheritedReturnSpec != null && !inheritedReturnSpec.IsEmptyTuple;
        bool refinedHasReturn = refinedReturnSpec != null && !refinedReturnSpec.IsEmptyTuple;
        if (!inheritedHasReturn || !refinedHasReturn)
            return;

        var inheritedReturnCsRaw = GetCSharpTypeName(inheritedReturnSpec!, isParameter: false);
        var refinedReturnCsRaw = GetCSharpTypeName(refinedReturnSpec!, isParameter: false);
        if (inheritedReturnCsRaw == refinedReturnCsRaw && inheritedMethod.IsAsync == refinedMethod.IsAsync)
            return;

        // Wrap async returns in Task<>/Task so the explicit-impl signature matches the
        // inherited interface slot exactly. The cast-forwarder body further down only runs
        // for the sync subclass case; async always falls through to the throwing stub
        // because Task<T> is invariant in C# (no static `Task<Refined> -> Task<Base>` cast).
        var inheritedReturnCs = inheritedMethod.IsAsync
            ? $"Task<{inheritedReturnCsRaw}>"
            : inheritedReturnCsRaw;
        var refinedReturnCs = refinedMethod.IsAsync
            ? $"Task<{refinedReturnCsRaw}>"
            : refinedReturnCsRaw;
        bool isAsyncCovariant = inheritedMethod.IsAsync || refinedMethod.IsAsync;

        // Build the explicit-interface signature from the *inherited* method. The slot we
        // satisfy (`IBase.Foo(...)`) is whatever the inherited interface emitted, and that
        // signature is dictated by the inherited method's projection — not the refined one.
        // GetProjectedCSharpMethodKey collapses sync vs async into the same key, so a sync
        // inherited method can pair with an async refined one (e.g. sync `fooAsync()` vs
        // async `foo()` both project to `FooAsync(...)`); using refined parameters would emit
        // an explicit impl with a CancellationToken the inherited slot doesn't declare.
        var parameters = new List<string>();
        var argNames = new List<string>();
        foreach (var param in inheritedMethod.CSSignature.Skip(1))
        {
            if (DefaultParameterOverloadEmitter.IsDebugParameter(param))
                continue;
            if (param.SwiftTypeSpec.IsEmptyTuple)
                continue;
            var paramTypeName = GetCSharpTypeName(param.SwiftTypeSpec, isParameter: true);
            var paramName = NameProvider.GetCSharpParameterName(param);
            parameters.Add($"{paramTypeName} {paramName}");
            argNames.Add(paramName);
        }
        if (inheritedMethod.IsAsync)
            parameters.Add("global::System.Threading.CancellationToken cancellationToken = default");

        var inheritedInterfaceName = NameProvider.GetInterfaceName(
            inheritedProto.Name, moduleName: inheritedProto.ModuleDecl?.Name ?? "");
        // Compute the inherited slot name using the inherited interface's own emitted
        // property-name set. Using the proxy's accumulated set would over-include
        // properties from sibling interfaces and from the refined protocol, predicting
        // a `Foo` -> `FooMethod` rename for a slot the inherited interface emitted as
        // plain `Foo` (CS0539/CS0535).
        //
        // First-choice source: the canonical set published by ProtocolHandler when it
        // emitted the inherited interface (matches the gate-evaluated set exactly).
        // Fallback: project from inheritedProto.Properties when the cache hasn't been
        // populated (cross-module inheritance, where the inherited interface's emission
        // ran in a different module). The fallback over-includes gate-skipped properties,
        // but cross-module inheritance is already filtered out at the caller's BFS.
        var inheritedProtoQualifiedName = inheritedProto.SwiftTypeName?.ModuleQualifiedName
                                       ?? $"{inheritedProto.ModuleDecl?.Name ?? "Unknown"}.{inheritedProto.Name}";
        var inheritedInterfacePropertyNames = _emissionContext.GetInterfacePropertyNames(inheritedProtoQualifiedName)
            ?? new HashSet<string>(inheritedProto.Properties.Select(p => NameProvider.GetPropertyName(p.Name)));
        var inheritedMethodName = NameProvider.GetPublicMethodName(
            inheritedMethod.Name, inheritedMethod.IsAsync, inheritedHasReturn,
            propertyNames: inheritedInterfacePropertyNames,
            isSelfReturning: MethodEnvironment.IsSelfReturningMethod(inheritedMethod),
            parameterCount: inheritedMethod.CSSignature.Skip(1)
                .Count(a => !DefaultParameterOverloadEmitter.IsDebugParameter(a) && !a.SwiftTypeSpec.IsEmptyTuple));

        var paramsString = string.Join(", ", parameters);

        // Dedupe by the explicit-impl C# slot signature. Multiple inheritance paths from the
        // proxy class to the same base interface (e.g. WCDB's PropertyConvertible reaches
        // ColumnConvertible through both ExpressionInOperable and a direct base) re-enter this
        // method with the same `Interface.Method(params)` shape. C# allows a single explicit
        // impl per slot — emit once per (interface, method, params) tuple.
        var explicitImplKey = $"{inheritedInterfaceName}.{inheritedMethodName}({paramsString})";
        if (!emittedExplicitImplSignatures.Add(explicitImplKey))
            return;

        // Verify the C# class hierarchy mirrors the Swift refinement. When it does, the
        // refined dispatch path can satisfy the base contract via a static up-cast. When it
        // doesn't (sibling classes coincidentally exposed through a refined protocol pair —
        // common in WCDB), no safe cast exists, so emit a NotSupportedException stub that
        // satisfies the C# interface contract while signaling the limitation at runtime.
        // Async covariant cases also fall through to the stub: Task<T> is invariant in C#
        // so no synchronous cast forwarder exists, and emitting an async cast wrapper would
        // change the public method's signature in ways the existing pipeline doesn't support.
        if (!isAsyncCovariant && IsSwiftClassAssignableTo(refinedReturnSpec!, inheritedReturnSpec!))
        {
            var refinedIsSelfReturning = MethodEnvironment.IsSelfReturningMethod(refinedMethod);
            var refinedParameterCount = refinedMethod.CSSignature.Skip(1)
                .Count(a => !DefaultParameterOverloadEmitter.IsDebugParameter(a) && !a.SwiftTypeSpec.IsEmptyTuple);
            var refinedMethodName = NameProvider.GetPublicMethodName(
                refinedMethod.Name, refinedMethod.IsAsync, refinedHasReturn,
                propertyNames: propertyNames,
                isSelfReturning: refinedIsSelfReturning,
                parameterCount: refinedParameterCount);
            var argsString = string.Join(", ", argNames);
            writer.WriteLine(
                $"{inheritedReturnCs} {inheritedInterfaceName}.{inheritedMethodName}({paramsString}) => " +
                $"({inheritedReturnCs})this.{refinedMethodName}({argsString});");
            return;
        }

        // Sibling-class case: emit a throwing explicit-interface stub. Satisfies CS0738.
        // The message names the refined interface so consumers can switch dispatch paths.
        var refinedInterfaceName = NameProvider.GetInterfaceName(
            refinedMethod.ParentDecl is ProtocolDecl refinedParent ? refinedParent.Name : "",
            moduleName: (refinedMethod.ParentDecl as ProtocolDecl)?.ModuleDecl?.Name ?? "");
        var redirectHint = string.IsNullOrEmpty(refinedInterfaceName)
            ? "Use the refined protocol's dispatch path."
            : $"Use {refinedInterfaceName} (the refined protocol) instead.";
        writer.WriteLine(
            $"{inheritedReturnCs} {inheritedInterfaceName}.{inheritedMethodName}({paramsString}) => " +
            $"throw new NotSupportedException(\"Refined return type ('{refinedReturnCs}') is not " +
            $"assignable to '{inheritedReturnCs}'. {redirectHint}\");");
        var stubReason = isAsyncCovariant
            ? $"async covariant return '{refinedReturnCs}' is not Task<>-castable to base '{inheritedReturnCs}' "
            : $"refined return '{refinedReturnCs}' is not assignable to base '{inheritedReturnCs}' ";
        ReportCollector.RecordMemberSkipped(
            inheritedMethod,
            SkipReason.CovariantReturnNotRepresentable,
            stubReason +
            $"(inherited from {inheritedProto.Name}); emitted explicit-interface stub that throws " +
            $"NotSupportedException to satisfy CS0738");
    }

    /// <summary>
    /// Returns true when the Swift type <paramref name="refined"/> is the same as, or a
    /// subclass of, <paramref name="base"/> as declared in the type database. Walks the
    /// resolved <see cref="TypeRecord.SuperclassTypeName"/> chain for class types; protocols,
    /// structs, and unresolved types are treated as non-assignable (no class-hierarchy
    /// relationship to walk). Used by the covariant-return forwarder to verify a static
    /// cast from the refined return type to the base return type would succeed.
    /// </summary>
    internal bool IsSwiftClassAssignableTo(TypeSpec refined, TypeSpec @base)
    {
        // Only NamedTypeSpec carries class identity. Closures, tuples, and protocol
        // compositions can't satisfy a class-hierarchy assignment.
        if (refined is not NamedTypeSpec refinedNamed || @base is not NamedTypeSpec baseNamed)
            return false;
        var refinedName = SwiftTypeName.FromTypeSpec(refinedNamed);
        var baseName = SwiftTypeName.FromTypeSpec(baseNamed);
        if (refinedName == null || baseName == null)
            return false;
        if (refinedName.ModuleQualifiedName == baseName.ModuleQualifiedName)
            return true;

        var visited = new HashSet<string>(StringComparer.Ordinal);
        var current = refinedName;
        while (current != null && visited.Add(current.ModuleQualifiedName))
        {
            if (!_typeDatabase.TryGetTypeRecord(current, out var record))
                return false;
            // Only class hierarchies have superclass relationships in C#. Structs and protocols
            // can't satisfy a covariant cast through inheritance.
            if (record.Kind != TypeRecordKind.Class)
                return false;
            if (record.SuperclassTypeName == null)
                return false;
            if (record.SuperclassTypeName.ModuleQualifiedName == baseName.ModuleQualifiedName)
                return true;
            current = record.SuperclassTypeName;
        }
        return false;
    }

    private void EmitPropertyImplementation(CSharpWriter writer, PropertyDecl property, ProtocolDecl protocolDecl, WitnessDispatchEmitter dispatchEmitter)
    {
        var hasGetter = property.Accessors.OfType<GetAccessorDecl>().Any();
        var hasSetter = property.Accessors.OfType<SetAccessorDecl>().Any();
        var csharpTypeName = GetInterfaceCompatiblePropertyTypeName(property);
        var propertyName = NameProvider.GetPropertyName(property.Name);
        var isGetterDispatchable = hasGetter && dispatchEmitter.IsPropertyGetterDispatchable(property);
        var isSetterDispatchable = hasSetter && dispatchEmitter.IsPropertySetterDispatchable(property);
        var isStringProperty = WitnessDispatchEmitter.IsStringDispatchType(property.SwiftTypeSpec);
        var isClassReturnGetter = hasGetter && !isGetterDispatchable && dispatchEmitter.IsPropertyClassReturn(property);
        var isStructReturnGetter = hasGetter && !isGetterDispatchable && !isClassReturnGetter && dispatchEmitter.IsPropertyStructReturn(property);
        var isCollectionReturnGetter = false;

        // Validate that the projected C# property type matches the dispatch strategy.
        // IsPropertyGetterDispatchable checks Swift-side dispatchability, but if the
        // projected type diverges (e.g. Swift.AnyType from incomplete TypeDatabase),
        // the generated return statement would be type-incompatible. Disable dispatch.
        // For blittable types: projected type must be a blittable primitive.
        // For String types: projected type must be SwiftString (not AnyType).
        if (isGetterDispatchable)
        {
            if (isStringProperty)
            {
                if (!IsSwiftStringProjectedType(csharpTypeName))
                    isGetterDispatchable = false;
            }
            else if (dispatchEmitter.IsSwiftClassType(property.SwiftTypeSpec) ||
                     dispatchEmitter.IsIndirectStructType(property.SwiftTypeSpec))
            {
                // Class/struct properties use ClassReturn/StructReturn getter path,
                // not the blittable dispatch path. Force off so they fall through
                // to isClassReturnGetter/isStructReturnGetter (computed above with !isGetterDispatchable).
                isGetterDispatchable = false;
            }
            else if (!WitnessDispatchEmitter.IsBlittablePrimitive(csharpTypeName))
            {
                isGetterDispatchable = false;
            }
        }
        // Re-evaluate ClassReturn/StructReturn now that class/struct types are forced off the blittable path
        if (!isGetterDispatchable && !isClassReturnGetter && !isStructReturnGetter)
        {
            isClassReturnGetter = hasGetter && dispatchEmitter.IsPropertyClassReturn(property);
            isStructReturnGetter = hasGetter && !isClassReturnGetter && dispatchEmitter.IsPropertyStructReturn(property);
        }
        if (isSetterDispatchable)
        {
            if (isStringProperty)
            {
                if (!IsSwiftStringProjectedType(csharpTypeName))
                    isSetterDispatchable = false;
            }
            else if (dispatchEmitter.IsSwiftClassType(property.SwiftTypeSpec) ||
                     dispatchEmitter.IsIndirectStructType(property.SwiftTypeSpec))
            {
                // No setter dispatch for class/struct types yet — defer
                isSetterDispatchable = false;
            }
            else if (!WitnessDispatchEmitter.IsBlittablePrimitive(csharpTypeName))
            {
                isSetterDispatchable = false;
            }
        }
        // Secondary validation for ClassReturn/StructReturn getters: reject if projected type is AnyType
        if (isClassReturnGetter || isStructReturnGetter)
        {
            if (csharpTypeName == "object" ||
                csharpTypeName == TypeDatabaseExtensions.AnyType.CSharpTypeName.FullyQualifiedName)
            {
                isClassReturnGetter = false;
                isStructReturnGetter = false;
            }
        }
        // Collection return getter: check after other paths are excluded
        if (hasGetter && !isGetterDispatchable && !isClassReturnGetter && !isStructReturnGetter
            && dispatchEmitter.IsPropertyCollectionReturn(property))
        {
            isCollectionReturnGetter = true;
        }

        // Optional<class> return getter: nullable direct-pointer (ClassReturn + nil guard).
        var isOptionalClassReturnGetter = false;
        if (hasGetter && !isGetterDispatchable && !isClassReturnGetter && !isStructReturnGetter
            && !isCollectionReturnGetter && dispatchEmitter.IsPropertyOptionalClassReturn(property)
            && csharpTypeName != "object"
            && csharpTypeName != TypeDatabaseExtensions.AnyType.CSharpTypeName.FullyQualifiedName)
        {
            isOptionalClassReturnGetter = true;
        }

        // Existential (any P / (any P)?) return getter: heap-cell carrier + proxy construction.
        var isExistentialReturnGetter = false;
        if (hasGetter && !isGetterDispatchable && !isClassReturnGetter && !isStructReturnGetter
            && !isCollectionReturnGetter && !isOptionalClassReturnGetter
            && dispatchEmitter.IsPropertyExistentialReturn(property))
        {
            isExistentialReturnGetter = true;
        }

        bool isGetterDispatched = isGetterDispatchable || isClassReturnGetter || isStructReturnGetter
            || isCollectionReturnGetter || isOptionalClassReturnGetter || isExistentialReturnGetter;
        bool isAnyAccessorNonDispatchable =
            (hasGetter && !isGetterDispatched) || (hasSetter && !isSetterDispatchable);
        if (isAnyAccessorNonDispatchable)
        {
            var propReason = dispatchEmitter.GetPropertyNonDispatchReason(property);
            var reasonSuffix = propReason != null
                ? $": {propReason}. Use"
                : ". Use";
            writer.WriteLine($"[Obsolete(\"This member cannot be called on protocol-typed values{reasonSuffix} a concrete type instead (SB0003).\",");
            writer.WriteLine("    DiagnosticId = \"SB0003\",");
            writer.WriteLine("    UrlFormat = \"https://github.com/justinwojo/swift-dotnet-bindings/wiki/Troubleshooting\")]");
        }

        writer.WriteLine($"public {csharpTypeName} {propertyName}");
        writer.WriteLine("{");
        writer.Indent++;

        if (hasGetter)
        {
            var accessorSymbol = WitnessDispatchEmitter.GetAccessorSymbol(protocolDecl.Name, "get", property.Name, 0);
            var freeSymbol = WitnessDispatchEmitter.GetFreeSymbol(protocolDecl.Name, "get", property.Name, 0);

            if (isGetterDispatchable && isStringProperty)
            {
                // String getter: decode SBW_Utf8Slice → string (or SwiftString if interface uses that)
                var returnExpr = csharpTypeName == "string" ? "str" : "new Swift.SwiftString(str)";
                writer.WriteLines($$"""
                    get
                    {
                        if (_disposed) throw new ObjectDisposedException(GetType().Name);
                        if (_csharpImpl != null)
                            return _csharpImpl.{{propertyName}};
                        fixed (ExistentialContainer1* containerPtr = &_swiftContainer)
                        {
                            IntPtr resultPtr = NativeMethods.{{accessorSymbol}}((IntPtr)containerPtr);
                            try
                            {
                                var slice = *(Utf8Slice*)resultPtr;
                                var str = slice.Len > 0
                                    ? global::System.Text.Encoding.UTF8.GetString((byte*)slice.Ptr, (int)slice.Len)
                                    : string.Empty;
                                return {{returnExpr}};
                            }
                            finally { NativeMethods.{{freeSymbol}}(resultPtr); }
                        }
                    }
                    """);
            }
            else if (isGetterDispatchable)
            {
                // Blittable getter: existing MarshalFromSwift path
                // Use the dispatch emitter's canonical blittable type for marshalling,
                // not the interface-projected type which may differ (e.g. Swift.AnyType)
                var marshalType = dispatchEmitter.GetBlittableCSharpType(property.SwiftTypeSpec) ?? csharpTypeName;
                // F1: If property type is narrowed (int/uint), MarshalFromSwift returns nint/nuint — add cast.
                var returnExpr = marshalType != csharpTypeName
                    ? $"({csharpTypeName})MarshalFromSwift<{marshalType}>(resultPtr)"
                    : $"MarshalFromSwift<{marshalType}>(resultPtr)";

                writer.WriteLines($$"""
                    get
                    {
                        if (_disposed) throw new ObjectDisposedException(GetType().Name);
                        if (_csharpImpl != null)
                            return _csharpImpl.{{propertyName}};
                        fixed (ExistentialContainer1* containerPtr = &_swiftContainer)
                        {
                            IntPtr resultPtr = NativeMethods.{{accessorSymbol}}((IntPtr)containerPtr);
                            try { return {{returnExpr}}; }
                            finally { NativeMethods.{{freeSymbol}}(resultPtr); }
                        }
                    }
                    """);
            }
            else if (isClassReturnGetter)
            {
                // ClassReturn getter: Unmanaged.passRetained on Swift side, direct MarshalFromSwift on C# side
                writer.WriteLines($$"""
                    get
                    {
                        if (_disposed) throw new ObjectDisposedException(GetType().Name);
                        if (_csharpImpl != null)
                            return _csharpImpl.{{propertyName}};
                        fixed (ExistentialContainer1* containerPtr = &_swiftContainer)
                        {
                            IntPtr resultPtr = NativeMethods.{{accessorSymbol}}((IntPtr)containerPtr);
                            try
                            {
                                return ({{csharpTypeName}})Swift.Runtime.InteropServices.SwiftMarshal.MarshalFromSwift<{{csharpTypeName}}>(resultPtr);
                            }
                            catch { Arc.Release(resultPtr); throw; }
                        }
                    }
                    """);
            }
            else if (isStructReturnGetter)
            {
                // StructReturn getter: pre-allocate buffer, Swift writes into it
                // Frozen+RefFields structs: NewFromPayload copies to a new buffer, so original must be freed on success
                bool isFrozenRefFields = dispatchEmitter.IsFrozenStructWithRefFields(property.SwiftTypeSpec);
                var cleanupKeyword = isFrozenRefFields ? "finally" : "catch";
                writer.WriteLines($$"""
                    get
                    {
                        if (_disposed) throw new ObjectDisposedException(GetType().Name);
                        if (_csharpImpl != null)
                            return _csharpImpl.{{propertyName}};
                        fixed (ExistentialContainer1* containerPtr = &_swiftContainer)
                        {
                            unsafe
                            {
                                var metadata = SwiftObjectHelper<{{csharpTypeName}}>.GetTypeMetadata();
                                IntPtr buffer = (IntPtr)NativeMemory.Alloc(metadata.Size);
                                try
                                {
                                    NativeMethods.{{accessorSymbol}}((IntPtr)containerPtr, buffer);
                                    return ({{csharpTypeName}})Swift.Runtime.InteropServices.SwiftMarshal.MarshalFromSwift<{{csharpTypeName}}>(buffer);
                                }
                                {{cleanupKeyword}} { NativeMemory.Free((void*)buffer);{{(isFrozenRefFields ? "" : " throw;")}} }
                            }
                        }
                    }
                    """);
            }
            else if (isCollectionReturnGetter)
            {
                // Collection return getter: heap-allocated pointer + typed free
                var marshalExpr = GetCollectionMarshalExpression(property.SwiftTypeSpec, "resultPtr");
                writer.WriteLines($$"""
                    get
                    {
                        if (_disposed) throw new ObjectDisposedException(GetType().Name);
                        if (_csharpImpl != null)
                            return _csharpImpl.{{propertyName}};
                        fixed (ExistentialContainer1* containerPtr = &_swiftContainer)
                        {
                            IntPtr resultPtr = NativeMethods.{{accessorSymbol}}((IntPtr)containerPtr);
                            try
                            {
                                return {{marshalExpr}};
                            }
                            finally { NativeMethods.{{freeSymbol}}(resultPtr); }
                        }
                    }
                    """);
            }
            else if (isOptionalClassReturnGetter)
            {
                // Optional<class> getter: nil → null pointer → return null; otherwise the
                // ClassReturn path (Swift returned a +1 instance; the SafeHandle adopts it).
                var innerClassType = csharpTypeName.EndsWith("?", StringComparison.Ordinal)
                    ? csharpTypeName[..^1]
                    : csharpTypeName;
                writer.WriteLines($$"""
                    get
                    {
                        if (_disposed) throw new ObjectDisposedException(GetType().Name);
                        if (_csharpImpl != null)
                            return _csharpImpl.{{propertyName}};
                        fixed (ExistentialContainer1* containerPtr = &_swiftContainer)
                        {
                            IntPtr resultPtr = NativeMethods.{{accessorSymbol}}((IntPtr)containerPtr);
                            if (resultPtr == IntPtr.Zero)
                                return null;
                            try
                            {
                                return ({{innerClassType}})Swift.Runtime.InteropServices.SwiftMarshal.MarshalFromSwift<{{innerClassType}}>(resultPtr);
                            }
                            catch { Arc.Release(resultPtr); throw; }
                        }
                    }
                    """);
            }
            else if (isExistentialReturnGetter)
            {
                EmitExistentialReturnPropertyGetterBody(writer, property, propertyName, csharpTypeName, accessorSymbol, freeSymbol);
            }
            else
            {
                writer.WriteLines($$"""
                    get
                    {
                        if (_disposed) throw new ObjectDisposedException(GetType().Name);
                        if (_csharpImpl != null)
                            return _csharpImpl.{{propertyName}};
                        throw new NotSupportedException(
                            "Cannot get property '{{propertyName}}' on a Swift-backed existential container. " +
                            "Protocol member access is only supported when wrapping a C# implementation.");
                    }
                    """);
            }
        }

        if (hasSetter)
        {
            var setterSymbol = WitnessDispatchEmitter.GetAccessorSymbol(protocolDecl.Name, "set", property.Name, 0);

            if (isSetterDispatchable && isStringProperty)
            {
                // String setter: encode to UTF-8, pass SBW_Utf8Slice to Swift
                writer.WriteLines($$"""
                    set
                    {
                        if (_disposed) throw new ObjectDisposedException(GetType().Name);
                        if (_csharpImpl != null)
                        {
                            _csharpImpl.{{propertyName}} = value;
                            return;
                        }
                        fixed (ExistentialContainer1* containerPtr = &_swiftContainer)
                        {
                            var str = value?.ToString() ?? string.Empty;
                            var utf8Bytes = global::System.Text.Encoding.UTF8.GetBytes(str);
                            fixed (byte* utf8Ptr = utf8Bytes)
                            {
                                var slice = new Utf8Slice { Ptr = (IntPtr)utf8Ptr, Len = (nint)utf8Bytes.Length };
                                NativeMethods.{{setterSymbol}}((IntPtr)containerPtr, (IntPtr)(&slice));
                            }
                        }
                    }
                    """);
            }
            else if (isSetterDispatchable)
            {
                // Blittable setter: pass value by pointer
                var marshalType = dispatchEmitter.GetBlittableCSharpType(property.SwiftTypeSpec) ?? csharpTypeName;

                writer.WriteLines($$"""
                    set
                    {
                        if (_disposed) throw new ObjectDisposedException(GetType().Name);
                        if (_csharpImpl != null)
                        {
                            _csharpImpl.{{propertyName}} = value;
                            return;
                        }
                        fixed (ExistentialContainer1* containerPtr = &_swiftContainer)
                        {
                            var valueCopy = ({{marshalType}})value;
                            NativeMethods.{{setterSymbol}}((IntPtr)containerPtr, (IntPtr)(&valueCopy));
                        }
                    }
                    """);
            }
            else
            {
                writer.WriteLines($$"""
                    set
                    {
                        if (_disposed) throw new ObjectDisposedException(GetType().Name);
                        if (_csharpImpl != null)
                        {
                            _csharpImpl.{{propertyName}} = value;
                            return;
                        }
                        throw new NotSupportedException(
                            "Cannot set property '{{propertyName}}' on a Swift-backed existential container. " +
                            "Protocol member access is only supported when wrapping a C# implementation.");
                    }
                    """);
            }
        }

        writer.Indent--;
        writer.WriteLine("}");
        writer.WriteLine();
    }

    private void EmitSubscriptImplementation(CSharpWriter writer, SubscriptDecl subscript, ProtocolDecl protocolDecl, int index)
    {
        var returnTypeName = GetCSharpTypeName(subscript.ReturnTypeSpec, isParameter: false);

        // Build parameter list
        var parameters = new List<string>();
        for (int i = 0; i < subscript.IndexParameters.Count; i++)
        {
            var param = subscript.IndexParameters[i];
            var paramTypeName = GetCSharpTypeName(param.SwiftTypeSpec, isParameter: true);
            var paramName = NameProvider.GetCSharpParameterName(param);
            parameters.Add($"{paramTypeName} {paramName}");
        }
        var parametersString = string.Join(", ", parameters);

        var argNames = subscript.IndexParameters.Select(p =>
            NameProvider.GetCSharpParameterName(p)).ToList();
        var argsString = string.Join(", ", argNames);

        writer.WriteLine("[Obsolete(\"This member cannot be called on protocol-typed values: subscript dispatch is not yet supported. Use a concrete type instead (SB0003).\",");
        writer.WriteLine("    DiagnosticId = \"SB0003\",");
        writer.WriteLine("    UrlFormat = \"https://github.com/justinwojo/swift-dotnet-bindings/wiki/Troubleshooting\")]");

        writer.WriteLine($"public {returnTypeName} this[{parametersString}]");
        writer.WriteLine("{");
        writer.Indent++;

        if (subscript.HasGetter)
        {
            writer.WriteLines($$"""
                get
                {
                    if (_disposed) throw new ObjectDisposedException(GetType().Name);
                    if (_csharpImpl != null)
                        return _csharpImpl[{{argsString}}];
                    throw new NotSupportedException(
                        "Cannot get subscript on a Swift-backed existential container. " +
                        "Protocol member access is only supported when wrapping a C# implementation.");
                }
                """);
        }

        if (subscript.HasSetter)
        {
            writer.WriteLines($$"""
                set
                {
                    if (_disposed) throw new ObjectDisposedException(GetType().Name);
                    if (_csharpImpl != null)
                    {
                        _csharpImpl[{{argsString}}] = value;
                        return;
                    }
                    throw new NotSupportedException(
                        "Cannot set subscript on a Swift-backed existential container. " +
                        "Protocol member access is only supported when wrapping a C# implementation.");
                }
                """);
        }

        writer.Indent--;
        writer.WriteLine("}");
        writer.WriteLine();
    }

    private void EmitMethodImplementation(CSharpWriter writer, MethodDecl method, ProtocolDecl protocolDecl, WitnessDispatchEmitter dispatchEmitter, int methodIndex, IReadOnlySet<string>? propertyNames = null)
    {
        var returnType = method.CSSignature.FirstOrDefault()?.SwiftTypeSpec;
        var hasReturn = returnType != null && !returnType.IsEmptyTuple;
        var isStringReturn = hasReturn && WitnessDispatchEmitter.IsStringDispatchType(returnType!);
        var returnTypeName = hasReturn ? GetCSharpTypeName(returnType!, isParameter: false) : "void";

        // Wrap return type for async methods to match interface declaration
        if (method.IsAsync)
        {
            if (returnTypeName == "void")
                returnTypeName = "Task";
            else
                returnTypeName = $"Task<{returnTypeName}>";
        }

        // Build parameter list
        var parameters = new List<string>();
        var argNames = new List<string>();
        var projectedParamTypes = new List<string>();
        var paramSwiftTypeSpecs = new List<TypeSpec?>();
        int argIndex = 0;
        foreach (var param in method.CSSignature.Skip(1))
        {
            // Skip debug params and empty tuple () params (zero-sized Void)
            if (DefaultParameterOverloadEmitter.IsDebugParameter(param))
                continue;
            if (param.SwiftTypeSpec.IsEmptyTuple)
                continue;
            var paramTypeName = GetCSharpTypeName(param.SwiftTypeSpec, isParameter: true);
            var paramName = NameProvider.GetCSharpParameterName(param);
            parameters.Add($"{paramTypeName} {paramName}");
            argNames.Add(paramName);
            projectedParamTypes.Add(paramTypeName);
            paramSwiftTypeSpecs.Add(param.SwiftTypeSpec);
            argIndex++;
        }
        // Add CancellationToken to async proxy methods (matches interface + WrapperEmitter emission)
        if (method.IsAsync)
        {
            parameters.Add("global::System.Threading.CancellationToken cancellationToken = default");
            argNames.Add("cancellationToken");
        }

        var parametersString = string.Join(", ", parameters);
        var argsString = string.Join(", ", argNames);

        var isSelfReturning = MethodEnvironment.IsSelfReturningMethod(method);
        var methodName = NameProvider.GetPublicMethodName(method.Name, method.IsAsync, hasReturn,
            propertyNames: propertyNames, isSelfReturning: isSelfReturning,
            parameterCount: method.CSSignature.Skip(1).Count(a => !DefaultParameterOverloadEmitter.IsDebugParameter(a) && !a.SwiftTypeSpec.IsEmptyTuple));
        var dispatchClassification = dispatchEmitter.ClassifyMethodDispatchWithReason(method);
        var dispatchKind = dispatchClassification.Kind;
        var dispatchReason = dispatchClassification.Reason;

        // Secondary C#-side validation for ExistentialReturn — verify proxy class exists
        // and projected return type is a valid interface (not "object" or "AnyType")
        if (dispatchKind == MethodDispatchKind.ExistentialReturn && hasReturn)
        {
            var existentialHandler = new ExistentialHandler(_typeDatabase) { CurrentModuleName = _moduleName };
            // Handle Optional<any Protocol> — unwrap before resolving protocol list
            bool isOptionalExistential = existentialHandler.IsOptionalExistential(returnType!);
            var protocolList = isOptionalExistential
                ? existentialHandler.UnwrapOptionalExistential(returnType!)
                : existentialHandler.ToProtocolListTypeSpec(returnType!);
            if (protocolList == null ||
                !existentialHandler.TryGetFilteredProxyClassName(protocolList, out _) ||
                returnTypeName == "object" ||
                returnTypeName == TypeDatabaseExtensions.AnyType.CSharpTypeName.FullyQualifiedName)
            {
                dispatchKind = MethodDispatchKind.NotDispatchable;
                dispatchReason ??= $"projected return type '{returnTypeName}' has no proxy class";
            }
        }

        // Validate projected types for BlittableOrString dispatch
        if (dispatchKind == MethodDispatchKind.BlittableOrString)
        {
            if (hasReturn)
            {
                if (isStringReturn)
                {
                    if (!IsIdiomaticStringType(returnTypeName))
                    {
                        dispatchKind = MethodDispatchKind.NotDispatchable;
                        dispatchReason ??= $"projected return type '{returnTypeName}' is not an idiomatic string type";
                    }
                }
                else if (!WitnessDispatchEmitter.IsBlittablePrimitive(returnTypeName))
                {
                    dispatchKind = MethodDispatchKind.NotDispatchable;
                    dispatchReason ??= $"projected return type '{returnTypeName}' is not a blittable primitive";
                }
            }

            if (dispatchKind == MethodDispatchKind.BlittableOrString)
            {
                if (!ValidateParamProjections(projectedParamTypes, paramSwiftTypeSpecs, dispatchEmitter))
                {
                    dispatchKind = MethodDispatchKind.NotDispatchable;
                    dispatchReason ??= "projected parameter type is not dispatchable";
                }
            }
        }

        // Validate projected types for ThrowingBlittableOrString dispatch (same return + param checks)
        if (dispatchKind == MethodDispatchKind.ThrowingBlittableOrString)
        {
            if (hasReturn)
            {
                if (isStringReturn)
                {
                    if (!IsIdiomaticStringType(returnTypeName))
                    {
                        dispatchKind = MethodDispatchKind.NotDispatchable;
                        dispatchReason ??= $"projected return type '{returnTypeName}' is not an idiomatic string type";
                    }
                }
                else if (!WitnessDispatchEmitter.IsBlittablePrimitive(returnTypeName))
                {
                    dispatchKind = MethodDispatchKind.NotDispatchable;
                    dispatchReason ??= $"projected return type '{returnTypeName}' is not a blittable primitive";
                }
            }

            if (dispatchKind == MethodDispatchKind.ThrowingBlittableOrString)
            {
                if (!ValidateParamProjections(projectedParamTypes, paramSwiftTypeSpecs, dispatchEmitter))
                {
                    dispatchKind = MethodDispatchKind.NotDispatchable;
                    dispatchReason ??= "projected parameter type is not dispatchable";
                }
            }
        }

        // Validate params for ExistentialReturn dispatch (same param validation)
        if (dispatchKind == MethodDispatchKind.ExistentialReturn)
        {
            if (!ValidateParamProjections(projectedParamTypes, paramSwiftTypeSpecs, dispatchEmitter))
            {
                dispatchKind = MethodDispatchKind.NotDispatchable;
                dispatchReason ??= "projected parameter type is not dispatchable";
            }
        }

        // Secondary C#-side validation for ClassReturn, StructReturn, and BoundGenericReturn:
        // Reject if projected return type is "object" or "AnyType" (TypeDatabase degradation)
        if (dispatchKind is MethodDispatchKind.ClassReturn or MethodDispatchKind.StructReturn or MethodDispatchKind.BoundGenericReturn)
        {
            if (returnTypeName == "object" ||
                returnTypeName == TypeDatabaseExtensions.AnyType.CSharpTypeName.FullyQualifiedName)
            {
                dispatchKind = MethodDispatchKind.NotDispatchable;
                dispatchReason ??= $"projected return type '{returnTypeName}' degraded to AnyType (incomplete type database)";
            }

            // Validate params
            if (dispatchKind is MethodDispatchKind.ClassReturn or MethodDispatchKind.StructReturn or MethodDispatchKind.BoundGenericReturn)
            {
                if (!ValidateParamProjections(projectedParamTypes, paramSwiftTypeSpecs, dispatchEmitter))
                {
                    dispatchKind = MethodDispatchKind.NotDispatchable;
                    dispatchReason ??= "projected parameter type is not dispatchable";
                }
            }
        }

        var isDispatchable = dispatchKind != MethodDispatchKind.NotDispatchable;

        if (!isDispatchable)
        {
            var reasonSuffix = dispatchReason != null
                ? $": {dispatchReason}. Use"
                : ". Use";
            writer.WriteLine($"[Obsolete(\"This member cannot be called on protocol-typed values{reasonSuffix} a concrete type instead (SB0003).\",");
            writer.WriteLine("    DiagnosticId = \"SB0003\",");
            writer.WriteLine("    UrlFormat = \"https://github.com/justinwojo/swift-dotnet-bindings/wiki/Troubleshooting\")]");
        }

        writer.WriteLine($"public {returnTypeName} {methodName}({parametersString})");
        writer.WriteLine("{");
        writer.Indent++;

        writer.WriteLine("if (_disposed) throw new ObjectDisposedException(GetType().Name);");

        if (dispatchKind == MethodDispatchKind.ExistentialReturn)
        {
            EmitExistentialReturnMethodBody(writer, method, protocolDecl, dispatchEmitter, methodIndex, methodName, argsString, argNames, paramSwiftTypeSpecs, returnType!, returnTypeName);
        }
        else if (dispatchKind == MethodDispatchKind.BlittableOrString)
        {
            var accessorSymbol = WitnessDispatchEmitter.GetAccessorSymbol(protocolDecl.Name, "method", method.Name, methodIndex);

            if (hasReturn)
            {
                var freeSymbol = WitnessDispatchEmitter.GetFreeSymbol(protocolDecl.Name, "method", method.Name, methodIndex);

                writer.WriteLines($$"""
                    if (_csharpImpl != null)
                        return _csharpImpl.{{methodName}}({{argsString}});
                    fixed (ExistentialContainer1* containerPtr = &_swiftContainer)
                    {
                    """);
                writer.Indent++;

                // Declare pin handles before try for exception-safe cleanup
                var pinHandles = EmitPinHandleDeclarations(writer, argNames, paramSwiftTypeSpecs);
                bool needsOuterTry = pinHandles.Count > 0;

                if (needsOuterTry)
                {
                    writer.WriteLine("try");
                    writer.WriteLine("{");
                    writer.Indent++;
                }

                // Marshal each parameter — String via GCHandle-pinned Utf8Slice, blittable via copy
                EmitMethodParameterMarshalling(writer, argNames, paramSwiftTypeSpecs, dispatchEmitter);

                // Build P/Invoke call
                var pInvokeArgs = new List<string> { "(IntPtr)containerPtr" };
                for (int i = 0; i < argNames.Count; i++)
                {
                    pInvokeArgs.Add($"(IntPtr)(&arg{i}Slice)");
                }
                var pInvokeArgsString = string.Join(", ", pInvokeArgs);

                if (isStringReturn)
                {
                    // String return: decode SBW_Utf8Slice → string
                    writer.WriteLines($$"""
                        IntPtr resultPtr = NativeMethods.{{accessorSymbol}}({{pInvokeArgsString}});
                        try
                        {
                            var slice = *(Utf8Slice*)resultPtr;
                            return slice.Len > 0
                                ? global::System.Text.Encoding.UTF8.GetString((byte*)slice.Ptr, (int)slice.Len)
                                : string.Empty;
                        }
                        finally
                        {
                            NativeMethods.{{freeSymbol}}(resultPtr);
                        }
                        """);
                }
                else
                {
                    // Blittable return: existing MarshalFromSwift path
                    var marshalReturnType = dispatchEmitter.GetBlittableCSharpType(returnType!) ?? GetCSharpTypeName(returnType!);

                    writer.WriteLines($$"""
                        IntPtr resultPtr = NativeMethods.{{accessorSymbol}}({{pInvokeArgsString}});
                        try { return MarshalFromSwift<{{marshalReturnType}}>(resultPtr); }
                        finally
                        {
                            NativeMethods.{{freeSymbol}}(resultPtr);
                        }
                        """);
                }

                if (needsOuterTry)
                {
                    writer.Indent--;
                    writer.WriteLine("}");
                    writer.WriteLine("finally");
                    writer.WriteLine("{");
                    writer.Indent++;
                    EmitPinHandleCleanup(writer, pinHandles);
                    writer.Indent--;
                    writer.WriteLine("}");
                }

                writer.Indent--;
                writer.WriteLine("}");
            }
            else
            {
                if (method.IsAsync)
                {
                    writer.WriteLines($$"""
                        if (_csharpImpl != null)
                            return _csharpImpl.{{methodName}}({{argsString}});
                        fixed (ExistentialContainer1* containerPtr = &_swiftContainer)
                        {
                        """);
                }
                else
                {
                    writer.WriteLines($$"""
                        if (_csharpImpl != null)
                        {
                            _csharpImpl.{{methodName}}({{argsString}});
                            return;
                        }
                        fixed (ExistentialContainer1* containerPtr = &_swiftContainer)
                        {
                        """);
                }
                writer.Indent++;

                // Declare pin handles before try for exception-safe cleanup
                var pinHandles = EmitPinHandleDeclarations(writer, argNames, paramSwiftTypeSpecs);
                bool needsOuterTry = pinHandles.Count > 0;

                if (needsOuterTry)
                {
                    writer.WriteLine("try");
                    writer.WriteLine("{");
                    writer.Indent++;
                }

                // Marshal each parameter — String via GCHandle-pinned Utf8Slice, blittable via copy
                EmitMethodParameterMarshalling(writer, argNames, paramSwiftTypeSpecs, dispatchEmitter);

                var pInvokeArgs = new List<string> { "(IntPtr)containerPtr" };
                for (int i = 0; i < argNames.Count; i++)
                {
                    pInvokeArgs.Add($"(IntPtr)(&arg{i}Slice)");
                }
                var pInvokeArgsString = string.Join(", ", pInvokeArgs);

                writer.WriteLine($"NativeMethods.{accessorSymbol}({pInvokeArgsString});");

                if (needsOuterTry)
                {
                    writer.Indent--;
                    writer.WriteLine("}");
                    writer.WriteLine("finally");
                    writer.WriteLine("{");
                    writer.Indent++;
                    EmitPinHandleCleanup(writer, pinHandles);
                    writer.Indent--;
                    writer.WriteLine("}");
                }

                writer.Indent--;
                writer.WriteLine("}");
            }
        }
        else if (dispatchKind == MethodDispatchKind.ThrowingBlittableOrString)
        {
            EmitThrowingBlittableMethodBody(writer, method, protocolDecl, dispatchEmitter, methodIndex, methodName, argsString, argNames, paramSwiftTypeSpecs, returnType, returnTypeName, hasReturn, isStringReturn);
        }
        else if (dispatchKind == MethodDispatchKind.ClassReturn)
        {
            EmitClassReturnMethodBody(writer, method, protocolDecl, dispatchEmitter, methodIndex, methodName, argsString, argNames, paramSwiftTypeSpecs, returnType!, returnTypeName);
        }
        else if (dispatchKind == MethodDispatchKind.StructReturn)
        {
            EmitStructReturnMethodBody(writer, method, protocolDecl, dispatchEmitter, methodIndex, methodName, argsString, argNames, paramSwiftTypeSpecs, returnType!, returnTypeName);
        }
        else if (dispatchKind == MethodDispatchKind.BoundGenericReturn)
        {
            EmitCollectionReturnMethodBody(writer, method, protocolDecl, dispatchEmitter, methodIndex, methodName, argsString, argNames, paramSwiftTypeSpecs, returnType!, returnTypeName);
        }
        else
        {
            // Non-dispatchable: keep NotSupportedException
            if (hasReturn)
            {
                writer.WriteLines($$"""
                    if (_csharpImpl != null)
                        return _csharpImpl.{{methodName}}({{argsString}});
                    throw new NotSupportedException(
                        "Cannot call method '{{methodName}}' on a Swift-backed existential container. " +
                        "Protocol member access is only supported when wrapping a C# implementation.");
                    """);
            }
            else
            {
                if (method.IsAsync)
                {
                    writer.WriteLines($$"""
                        if (_csharpImpl != null)
                            return _csharpImpl.{{methodName}}({{argsString}});
                        throw new NotSupportedException(
                            "Cannot call method '{{methodName}}' on a Swift-backed existential container. " +
                            "Protocol member access is only supported when wrapping a C# implementation.");
                        """);
                }
                else
                {
                    writer.WriteLines($$"""
                        if (_csharpImpl != null)
                        {
                            _csharpImpl.{{methodName}}({{argsString}});
                            return;
                        }
                        throw new NotSupportedException(
                            "Cannot call method '{{methodName}}' on a Swift-backed existential container. " +
                            "Protocol member access is only supported when wrapping a C# implementation.");
                        """);
                }
            }
        }

        writer.Indent--;
        writer.WriteLine("}");
        writer.WriteLine();
    }

    /// <summary>
    /// Emits the common C# dispatch body for methods that return heap-allocated pointer results
    /// (ExistentialReturn and BoundGenericReturn). Both use the same pattern:
    /// _csharpImpl delegation → fixed container → pin handles → marshal params → P/Invoke → error check → result.
    /// </summary>
    private void EmitHeapPointerMethodBody(
        CSharpWriter writer, MethodDecl method,
        WitnessDispatchEmitter dispatchEmitter,
        string methodName, string argsString,
        List<string> argNames, List<TypeSpec?> paramSwiftTypeSpecs,
        string accessorSymbol, string freeSymbol,
        string resultExpression,
        string? resultPreamble = null,
        bool isOptionalReturn = false)
    {
        writer.WriteLines($$"""
            if (_csharpImpl != null)
                return _csharpImpl.{{methodName}}({{argsString}});
            fixed (ExistentialContainer1* containerPtr = &_swiftContainer)
            {
            """);
        writer.Indent++;

        // Declare pin handles before try for exception-safe cleanup
        var pinHandles = EmitPinHandleDeclarations(writer, argNames, paramSwiftTypeSpecs);
        bool needsOuterTry = pinHandles.Count > 0;

        if (needsOuterTry)
        {
            writer.WriteLine("try");
            writer.WriteLine("{");
            writer.Indent++;
        }

        // Marshal each parameter
        EmitMethodParameterMarshalling(writer, argNames, paramSwiftTypeSpecs, dispatchEmitter);

        // Build P/Invoke call args
        var pInvokeArgs = new List<string> { "(IntPtr)containerPtr" };
        for (int i = 0; i < argNames.Count; i++)
        {
            pInvokeArgs.Add($"(IntPtr)(&arg{i}Slice)");
        }

        if (method.Throws)
        {
            pInvokeArgs.Add("(IntPtr)(&errorOut)");
            var pInvokeArgsString = string.Join(", ", pInvokeArgs);

            // Throwing pattern: error out-parameter, null result means error
            writer.WriteLines($$"""
                IntPtr errorOut = IntPtr.Zero;
                IntPtr resultPtr = NativeMethods.{{accessorSymbol}}({{pInvokeArgsString}});
                if (resultPtr == IntPtr.Zero)
                {
                """);
            writer.Indent++;
            EmitSwiftErrorHandling(writer);
            writer.Indent--;
            if (resultPreamble != null)
            {
                writer.WriteLines($$"""
                    }
                    try
                    {
                        {{resultPreamble}}
                        return {{resultExpression}};
                    }
                    finally { NativeMethods.{{freeSymbol}}(resultPtr); }
                    """);
            }
            else
            {
                writer.WriteLines($$"""
                    }
                    try
                    {
                        return {{resultExpression}};
                    }
                    finally { NativeMethods.{{freeSymbol}}(resultPtr); }
                    """);
            }
        }
        else if (isOptionalReturn)
        {
            var pInvokeArgsString = string.Join(", ", pInvokeArgs);

            // Optional existential pattern: IntPtr.Zero → return null
            if (resultPreamble != null)
            {
                writer.WriteLines($$"""
                    IntPtr resultPtr = NativeMethods.{{accessorSymbol}}({{pInvokeArgsString}});
                    if (resultPtr == IntPtr.Zero) return null;
                    try
                    {
                        {{resultPreamble}}
                        return {{resultExpression}};
                    }
                    finally { NativeMethods.{{freeSymbol}}(resultPtr); }
                    """);
            }
            else
            {
                writer.WriteLines($$"""
                    IntPtr resultPtr = NativeMethods.{{accessorSymbol}}({{pInvokeArgsString}});
                    if (resultPtr == IntPtr.Zero) return null;
                    try
                    {
                        return {{resultExpression}};
                    }
                    finally { NativeMethods.{{freeSymbol}}(resultPtr); }
                    """);
            }
        }
        else
        {
            var pInvokeArgsString = string.Join(", ", pInvokeArgs);

            // Non-throwing, non-optional pattern: direct allocation
            if (resultPreamble != null)
            {
                writer.WriteLines($$"""
                    IntPtr resultPtr = NativeMethods.{{accessorSymbol}}({{pInvokeArgsString}});
                    try
                    {
                        {{resultPreamble}}
                        return {{resultExpression}};
                    }
                    finally { NativeMethods.{{freeSymbol}}(resultPtr); }
                    """);
            }
            else
            {
                writer.WriteLines($$"""
                    IntPtr resultPtr = NativeMethods.{{accessorSymbol}}({{pInvokeArgsString}});
                    try
                    {
                        return {{resultExpression}};
                    }
                    finally { NativeMethods.{{freeSymbol}}(resultPtr); }
                    """);
            }
        }

        if (needsOuterTry)
        {
            writer.Indent--;
            writer.WriteLine("}");
            writer.WriteLine("finally");
            writer.WriteLine("{");
            writer.Indent++;
            EmitPinHandleCleanup(writer, pinHandles);
            writer.Indent--;
            writer.WriteLine("}");
        }

        writer.Indent--;
        writer.WriteLine("}");
    }

    /// <summary>
    /// Emits the C# getter body for a property returning a protocol existential
    /// (<c>any P</c> or <c>(any P)?</c>). Mirrors <see cref="EmitExistentialReturnMethodBody"/>:
    /// the Swift accessor returns a heap cell holding the existential value; the C# side reads
    /// the container, constructs a proxy, and frees the cell. Class-bound (single
    /// superclass-/AnyObject-constrained) existentials read the 2-word
    /// <c>ClassExistentialContainer1</c> carrier and take an independent retain-on-read so the
    /// adopting proxy owns the class reference (released on the proxy's class-bound
    /// Dispose/finalize). Opaque existentials read the 5-word container and adopt the +1 with
    /// the same ownership shape as the method path.
    /// </summary>
    private void EmitExistentialReturnPropertyGetterBody(
        CSharpWriter writer, PropertyDecl property, string propertyName,
        string csharpTypeName, string accessorSymbol, string freeSymbol)
    {
        var existentialHandler = new ExistentialHandler(_typeDatabase) { CurrentModuleName = _moduleName };
        bool isOptional = existentialHandler.IsOptionalExistential(property.SwiftTypeSpec);
        var protocolList = isOptional
            ? existentialHandler.UnwrapOptionalExistential(property.SwiftTypeSpec)
            : existentialHandler.ToProtocolListTypeSpec(property.SwiftTypeSpec);
        bool isClassBound = existentialHandler.IsClassBoundArity1Existential(protocolList!);
        existentialHandler.TryGetFilteredProxyClassName(protocolList!, out var proxyClassName);
        proxyClassName = existentialHandler.QualifyProxyClassName(proxyClassName, protocolList!);
        var publicType = existentialHandler.GetPublicExistentialType(protocolList!);

        writer.WriteLine("get");
        writer.WriteLine("{");
        writer.Indent++;
        writer.WriteLine("if (_disposed) throw new ObjectDisposedException(GetType().Name);");
        writer.WriteLine("if (_csharpImpl != null)");
        writer.Indent++;
        writer.WriteLine($"return _csharpImpl.{propertyName};");
        writer.Indent--;
        writer.WriteLine("fixed (ExistentialContainer1* containerPtr = &_swiftContainer)");
        writer.WriteLine("{");
        writer.Indent++;
        writer.WriteLine($"IntPtr resultPtr = NativeMethods.{accessorSymbol}((IntPtr)containerPtr);");
        if (isOptional)
            writer.WriteLine("if (resultPtr == IntPtr.Zero) return null;");
        writer.WriteLine("try");
        writer.WriteLine("{");
        writer.Indent++;
        // Same class-bound-aware read as the existential method-return path (shared helper):
        // class-bound cells read the 2-word ClassExistentialContainer1 + retain, opaque cells
        // read the full container. Only the surrounding try/finally scaffolding differs.
        var containerType = existentialHandler.GetCSharpExistentialType(protocolList!);
        var (preamble, expression) =
            BuildExistentialHeapCellReadAndConstruct(isClassBound, containerType, proxyClassName);
        writer.WriteLines(preamble);
        writer.WriteLine($"return ({publicType}){expression};");
        writer.Indent--;
        writer.WriteLine("}");
        writer.WriteLine($"finally {{ NativeMethods.{freeSymbol}(resultPtr); }}");
        writer.Indent--;
        writer.WriteLine("}");
        writer.Indent--;
        writer.WriteLine("}");
    }

    /// <summary>
    /// Emits the C# dispatch body for methods that return protocol existentials.
    /// Uses typed pointer allocation on the Swift side, Unsafe.Read on the C# side to
    /// recover the ExistentialContainer, and constructs a proxy class instance.
    /// Throwing methods use error out-parameter pattern matching GenericClosureBridgeEmitter.
    /// </summary>
    private void EmitExistentialReturnMethodBody(
        CSharpWriter writer, MethodDecl method, ProtocolDecl protocolDecl,
        WitnessDispatchEmitter dispatchEmitter,
        int methodIndex, string methodName, string argsString,
        List<string> argNames, List<TypeSpec?> paramSwiftTypeSpecs,
        TypeSpec returnType, string returnTypeName)
    {
        var accessorSymbol = WitnessDispatchEmitter.GetAccessorSymbol(protocolDecl.Name, "method", method.Name, methodIndex);
        var freeSymbol = WitnessDispatchEmitter.GetFreeSymbol(protocolDecl.Name, "method", method.Name, methodIndex);

        // Resolve the existential container type and proxy class name
        var existentialHandler = new ExistentialHandler(_typeDatabase) { CurrentModuleName = _moduleName };
        bool isOptionalExistential = existentialHandler.IsOptionalExistential(returnType);
        var protocolList = isOptionalExistential
            ? existentialHandler.UnwrapOptionalExistential(returnType)
            : existentialHandler.ToProtocolListTypeSpec(returnType);
        var containerType = existentialHandler.GetCSharpExistentialType(protocolList!);
        bool isClassBound = existentialHandler.IsClassBoundArity1Existential(protocolList!);
        existentialHandler.TryGetFilteredProxyClassName(protocolList!, out var proxyClassName);
        proxyClassName = existentialHandler.QualifyProxyClassName(proxyClassName, protocolList!);

        // The returned heap cell holds a class-bound (2-word) or opaque (5-word) existential.
        // Reading a class-bound cell as the 40-byte opaque container over-reads 24 bytes past
        // the 16-byte allocation, so the read width must follow class-boundedness — same shape
        // as the property getter.
        var (resultPreamble, resultExpression) =
            BuildExistentialHeapCellReadAndConstruct(isClassBound, containerType, proxyClassName);
        EmitHeapPointerMethodBody(writer, method, dispatchEmitter,
            methodName, argsString, argNames, paramSwiftTypeSpecs,
            accessorSymbol, freeSymbol, resultExpression, resultPreamble, isOptionalReturn: isOptionalExistential);
    }

    /// <summary>
    /// Builds the <c>(preamble, expression)</c> pair that reads a returned existential heap cell
    /// at <c>(void*)resultPtr</c> and constructs the proxy. Class-bound (single
    /// superclass-/<c>AnyObject</c>-constrained) existentials carry a 2-word
    /// <c>[classRef][witnessTable]</c> cell, so they read <c>ClassExistentialContainer1</c>
    /// (16 bytes) and take an independent retain on the class reference — reading the opaque
    /// 5-word <c>ExistentialContainer{N}</c> (40 bytes) over-reads 24 bytes past the heap
    /// allocation. Opaque existentials read the full container and adopt the returned +1.
    /// Single source of truth for the property-getter and method-return existential paths,
    /// which differ only in their surrounding try/finally scaffolding.
    /// </summary>
    private static (string preamble, string expression) BuildExistentialHeapCellReadAndConstruct(
        bool isClassBound, string containerType, string proxyClassName)
    {
        if (isClassBound)
        {
            // Read exactly two words, then take an independent +1 so the adopting proxy owns the
            // class reference for its lifetime (the heap-cell free releases the cell's own +1).
            // The implicit ClassExistentialContainer1 -> ExistentialContainer1 conversion repackages
            // [classRef][witnessTable] into the proxy's container fields. A class-bound `any P`
            // may carry an Objective-C object (a protocol refined by NSObjectProtocol / a UIKit
            // class), so the retain routes through the kind-dispatching unknown-object entry point
            // rather than swift_retain (native-only).
            var preamble =
                "var container = Unsafe.Read<Swift.Runtime.ClassExistentialContainer1>((void*)resultPtr);\n"
                + "Arc.UnknownObjectRetain(container.ClassRef);";
            return (preamble, $"new {proxyClassName}(container, ownsContainer: true)");
        }

        // Opaque (5-word) existential. The dispatched Swift accessor returns the existential at +1
        // in a heap cell that the generated free function deinitializes + deallocates (which
        // Destroys the returned +1). A bare bitwise Unsafe.Read would make the adopting proxy share
        // that exact payload, so the heap-cell free AND the proxy's Dispose would each Destroy it —
        // a double-release → UAF/SIGSEGV (audit P0-09). Take an INDEPENDENT +1 on read via the
        // existential value witness InitializeWithCopy (the opaque analogue of the class-bound
        // branch's Arc.UnknownObjectRetain above): the proxy then owns its own retained copy, the
        // cell free balances the returned +1, and the proxy's later Destroy balances this copy.
        // Gate the +1 on the SAME ownership predicate as ownsContainer:true — a non-owning proxy
        // must not take the extra retain (it never Destroys, so the +1 would leak).
        if (ExistentialHandler.IsOwnedExistentialContainerType(containerType))
        {
            var ownedPreamble =
                $"var container = Unsafe.Read<{containerType}>((void*)resultPtr);\n"
                + "var existentialMetadata = Swift.Runtime.TypeMetadata.GetExistentialTypeMetadata(container.Count);\n"
                + "Swift.Runtime.InteropServices.SwiftMarshal.CopyWireBufferRetains("
                + "(IntPtr)Unsafe.AsPointer(ref container), resultPtr, existentialMetadata);";
            return (ownedPreamble, $"new {proxyClassName}(container, ownsContainer: true)");
        }

        return ($"var container = Unsafe.Read<{containerType}>((void*)resultPtr);",
                $"new {proxyClassName}(container)");
    }

    /// <summary>
    /// Emits the C# dispatch body for throwing methods that return blittable/String/void types.
    /// Value-returning: resultPtr == IntPtr.Zero means error; otherwise MarshalFromSwift/Utf8Decode.
    /// Void: errorOut != IntPtr.Zero means error.
    /// </summary>
    private void EmitThrowingBlittableMethodBody(
        CSharpWriter writer, MethodDecl method, ProtocolDecl protocolDecl,
        WitnessDispatchEmitter dispatchEmitter,
        int methodIndex, string methodName, string argsString,
        List<string> argNames, List<TypeSpec?> paramSwiftTypeSpecs,
        TypeSpec? returnType, string returnTypeName, bool hasReturn, bool isStringReturn)
    {
        var accessorSymbol = WitnessDispatchEmitter.GetAccessorSymbol(protocolDecl.Name, "method", method.Name, methodIndex);

        if (hasReturn)
        {
            var freeSymbol = WitnessDispatchEmitter.GetFreeSymbol(protocolDecl.Name, "method", method.Name, methodIndex);

            writer.WriteLines($$"""
                if (_csharpImpl != null)
                    return _csharpImpl.{{methodName}}({{argsString}});
                fixed (ExistentialContainer1* containerPtr = &_swiftContainer)
                {
                """);
            writer.Indent++;

            // Declare pin handles before try for exception-safe cleanup
            var pinHandles = EmitPinHandleDeclarations(writer, argNames, paramSwiftTypeSpecs);
            bool needsOuterTry = pinHandles.Count > 0;

            if (needsOuterTry)
            {
                writer.WriteLine("try");
                writer.WriteLine("{");
                writer.Indent++;
            }

            // Marshal each parameter
            EmitMethodParameterMarshalling(writer, argNames, paramSwiftTypeSpecs, dispatchEmitter);

            // Build P/Invoke call args
            var pInvokeArgs = new List<string> { "(IntPtr)containerPtr" };
            for (int i = 0; i < argNames.Count; i++)
            {
                pInvokeArgs.Add($"(IntPtr)(&arg{i}Slice)");
            }
            pInvokeArgs.Add("(IntPtr)(&errorOut)");
            var pInvokeArgsString = string.Join(", ", pInvokeArgs);

            if (isStringReturn)
            {
                // String return with error check: resultPtr == IntPtr.Zero means error
                writer.WriteLines($$"""
                    IntPtr errorOut = IntPtr.Zero;
                    IntPtr resultPtr = NativeMethods.{{accessorSymbol}}({{pInvokeArgsString}});
                    if (resultPtr == IntPtr.Zero)
                    {
                    """);
                writer.Indent++;
                EmitSwiftErrorHandling(writer);
                writer.Indent--;
                writer.WriteLines($$"""
                    }
                    try
                    {
                        var slice = *(Utf8Slice*)resultPtr;
                        return slice.Len > 0
                            ? global::System.Text.Encoding.UTF8.GetString((byte*)slice.Ptr, (int)slice.Len)
                            : string.Empty;
                    }
                    finally
                    {
                        NativeMethods.{{freeSymbol}}(resultPtr);
                    }
                    """);
            }
            else
            {
                // Blittable return with error check
                var marshalReturnType = dispatchEmitter.GetBlittableCSharpType(returnType!) ?? GetCSharpTypeName(returnType!);

                writer.WriteLines($$"""
                    IntPtr errorOut = IntPtr.Zero;
                    IntPtr resultPtr = NativeMethods.{{accessorSymbol}}({{pInvokeArgsString}});
                    if (resultPtr == IntPtr.Zero)
                    {
                    """);
                writer.Indent++;
                EmitSwiftErrorHandling(writer);
                writer.Indent--;
                writer.WriteLines($$"""
                    }
                    try { return MarshalFromSwift<{{marshalReturnType}}>(resultPtr); }
                    finally
                    {
                        NativeMethods.{{freeSymbol}}(resultPtr);
                    }
                    """);
            }

            if (needsOuterTry)
            {
                writer.Indent--;
                writer.WriteLine("}");
                writer.WriteLine("finally");
                writer.WriteLine("{");
                writer.Indent++;
                EmitPinHandleCleanup(writer, pinHandles);
                writer.Indent--;
                writer.WriteLine("}");
            }

            writer.Indent--;
            writer.WriteLine("}");
        }
        else
        {
            // Void throwing: check errorOut after call
            writer.WriteLines($$"""
                if (_csharpImpl != null)
                {
                    _csharpImpl.{{methodName}}({{argsString}});
                    return;
                }
                fixed (ExistentialContainer1* containerPtr = &_swiftContainer)
                {
                """);
            writer.Indent++;

            // Declare pin handles before try for exception-safe cleanup
            var pinHandles = EmitPinHandleDeclarations(writer, argNames, paramSwiftTypeSpecs);
            bool needsOuterTry = pinHandles.Count > 0;

            if (needsOuterTry)
            {
                writer.WriteLine("try");
                writer.WriteLine("{");
                writer.Indent++;
            }

            EmitMethodParameterMarshalling(writer, argNames, paramSwiftTypeSpecs, dispatchEmitter);

            var pInvokeArgs = new List<string> { "(IntPtr)containerPtr" };
            for (int i = 0; i < argNames.Count; i++)
            {
                pInvokeArgs.Add($"(IntPtr)(&arg{i}Slice)");
            }
            pInvokeArgs.Add("(IntPtr)(&errorOut)");
            var pInvokeArgsString = string.Join(", ", pInvokeArgs);

            writer.WriteLines($$"""
                IntPtr errorOut = IntPtr.Zero;
                NativeMethods.{{accessorSymbol}}({{pInvokeArgsString}});
                if (errorOut != IntPtr.Zero)
                {
                """);
            writer.Indent++;
            EmitSwiftErrorHandling(writer);
            writer.Indent--;
            writer.WriteLines("""
                }
                """);

            if (needsOuterTry)
            {
                writer.Indent--;
                writer.WriteLine("}");
                writer.WriteLine("finally");
                writer.WriteLine("{");
                writer.Indent++;
                EmitPinHandleCleanup(writer, pinHandles);
                writer.Indent--;
                writer.WriteLine("}");
            }

            writer.Indent--;
            writer.WriteLine("}");
        }
    }

    /// <summary>
    /// Emits the C# dispatch body for methods that return a Swift class.
    /// Uses Unmanaged.passRetained on Swift side; C# wraps IntPtr in NativeMemory + SwiftMarshal.
    /// Matches ExtensionMarshallingHelper.SwiftClass pattern (try/catch, not try/finally).
    /// Throwing: resultPtr == IntPtr.Zero means error (same as ExistentialReturn throwing).
    /// </summary>
    private void EmitClassReturnMethodBody(
        CSharpWriter writer, MethodDecl method, ProtocolDecl protocolDecl,
        WitnessDispatchEmitter dispatchEmitter,
        int methodIndex, string methodName, string argsString,
        List<string> argNames, List<TypeSpec?> paramSwiftTypeSpecs,
        TypeSpec returnType, string returnTypeName)
    {
        var accessorSymbol = WitnessDispatchEmitter.GetAccessorSymbol(protocolDecl.Name, "method", method.Name, methodIndex);

        writer.WriteLines($$"""
            if (_csharpImpl != null)
                return _csharpImpl.{{methodName}}({{argsString}});
            fixed (ExistentialContainer1* containerPtr = &_swiftContainer)
            {
            """);
        writer.Indent++;

        // Declare pin handles before try for exception-safe cleanup
        var pinHandles = EmitPinHandleDeclarations(writer, argNames, paramSwiftTypeSpecs);
        bool needsOuterTry = pinHandles.Count > 0;

        if (needsOuterTry)
        {
            writer.WriteLine("try");
            writer.WriteLine("{");
            writer.Indent++;
        }

        // Marshal each parameter
        EmitMethodParameterMarshalling(writer, argNames, paramSwiftTypeSpecs, dispatchEmitter);

        // Build P/Invoke call args
        var pInvokeArgs = new List<string> { "(IntPtr)containerPtr" };
        for (int i = 0; i < argNames.Count; i++)
        {
            pInvokeArgs.Add($"(IntPtr)(&arg{i}Slice)");
        }

        if (method.Throws)
        {
            pInvokeArgs.Add("(IntPtr)(&errorOut)");
            var pInvokeArgsString = string.Join(", ", pInvokeArgs);

            // Throwing: error out-parameter, null result means error
            writer.WriteLines($$"""
                IntPtr errorOut = IntPtr.Zero;
                IntPtr resultPtr = NativeMethods.{{accessorSymbol}}({{pInvokeArgsString}});
                if (resultPtr == IntPtr.Zero)
                {
                """);
            writer.Indent++;
            EmitSwiftErrorHandling(writer);
            writer.Indent--;
            writer.WriteLines($$"""
                }
                try
                {
                    return ({{returnTypeName}})Swift.Runtime.InteropServices.SwiftMarshal.MarshalFromSwift<{{returnTypeName}}>(resultPtr);
                }
                catch { Arc.Release(resultPtr); throw; }
                """);
        }
        else
        {
            var pInvokeArgsString = string.Join(", ", pInvokeArgs);

            // Non-throwing: direct class return
            writer.WriteLines($$"""
                IntPtr resultPtr = NativeMethods.{{accessorSymbol}}({{pInvokeArgsString}});
                try
                {
                    return ({{returnTypeName}})Swift.Runtime.InteropServices.SwiftMarshal.MarshalFromSwift<{{returnTypeName}}>(resultPtr);
                }
                catch { Arc.Release(resultPtr); throw; }
                """);
        }

        if (needsOuterTry)
        {
            writer.Indent--;
            writer.WriteLine("}");
            writer.WriteLine("finally");
            writer.WriteLine("{");
            writer.Indent++;
            EmitPinHandleCleanup(writer, pinHandles);
            writer.Indent--;
            writer.WriteLine("}");
        }

        writer.Indent--;
        writer.WriteLine("}");
    }

    /// <summary>
    /// Emits the C# dispatch body for methods that return a non-frozen struct.
    /// C# pre-allocates buffer via NativeMemory.Alloc(metadata.Size), passes as resultBuf.
    /// Swift writes into buffer. SafeHandle takes ownership via SwiftMarshal.MarshalFromSwift.
    /// Non-frozen structs: try/catch (SafeHandle takes buffer ownership on success).
    /// Frozen+RefFields structs: try/finally (NewFromPayload copies to new buffer, original must be freed).
    /// Throwing: errorOut != IntPtr.Zero means error (same as void throwing pattern).
    /// </summary>
    private void EmitStructReturnMethodBody(
        CSharpWriter writer, MethodDecl method, ProtocolDecl protocolDecl,
        WitnessDispatchEmitter dispatchEmitter,
        int methodIndex, string methodName, string argsString,
        List<string> argNames, List<TypeSpec?> paramSwiftTypeSpecs,
        TypeSpec returnType, string returnTypeName)
    {
        var accessorSymbol = WitnessDispatchEmitter.GetAccessorSymbol(protocolDecl.Name, "method", method.Name, methodIndex);
        bool isFrozenRefFields = dispatchEmitter.IsFrozenStructWithRefFields(returnType);
        var cleanupKeyword = isFrozenRefFields ? "finally" : "catch";

        writer.WriteLines($$"""
            if (_csharpImpl != null)
                return _csharpImpl.{{methodName}}({{argsString}});
            fixed (ExistentialContainer1* containerPtr = &_swiftContainer)
            {
            """);
        writer.Indent++;

        // Declare pin handles before try for exception-safe cleanup
        var pinHandles = EmitPinHandleDeclarations(writer, argNames, paramSwiftTypeSpecs);
        bool needsOuterTry = pinHandles.Count > 0;

        if (needsOuterTry)
        {
            writer.WriteLine("try");
            writer.WriteLine("{");
            writer.Indent++;
        }

        // Marshal each parameter
        EmitMethodParameterMarshalling(writer, argNames, paramSwiftTypeSpecs, dispatchEmitter);

        // Build P/Invoke call args: containerPtr + resultBuf + params + errorOut
        var pInvokeArgs = new List<string> { "(IntPtr)containerPtr" };
        // resultBuf inserted below after buffer allocation
        var argPInvokeList = new List<string>();
        for (int i = 0; i < argNames.Count; i++)
        {
            argPInvokeList.Add($"(IntPtr)(&arg{i}Slice)");
        }

        if (method.Throws)
        {
            // Throwing struct return: errorOut check, void P/Invoke
            writer.WriteLines($$"""
                unsafe
                {
                    var metadata = SwiftObjectHelper<{{returnTypeName}}>.GetTypeMetadata();
                    IntPtr buffer = (IntPtr)NativeMemory.Alloc(metadata.Size);
                    try
                    {
                        var indirectResult = new SwiftIndirectResult((void*)buffer);
                        IntPtr errorOut = IntPtr.Zero;
                """);
            writer.Indent += 2;

            var throwingPInvokeArgs = new List<string> { "(IntPtr)containerPtr", "(IntPtr)indirectResult.Value" };
            throwingPInvokeArgs.AddRange(argPInvokeList);
            throwingPInvokeArgs.Add("(IntPtr)(&errorOut)");
            var throwingPInvokeArgsString = string.Join(", ", throwingPInvokeArgs);

            writer.WriteLines($$"""
                        NativeMethods.{{accessorSymbol}}({{throwingPInvokeArgsString}});
                        if (errorOut != IntPtr.Zero)
                        {
                """);
            writer.Indent += 3;
            EmitSwiftErrorHandling(writer);
            writer.Indent -= 3;
            writer.WriteLines($$"""
                        }
                        return ({{returnTypeName}})Swift.Runtime.InteropServices.SwiftMarshal.MarshalFromSwift<{{returnTypeName}}>(buffer);
                    }
                    {{cleanupKeyword}} { NativeMemory.Free((void*)buffer);{{(isFrozenRefFields ? "" : " throw;")}} }
                }
                """);
            writer.Indent -= 2;
        }
        else
        {
            // Non-throwing struct return
            var nonThrowingPInvokeArgs = new List<string> { "(IntPtr)containerPtr", "(IntPtr)indirectResult.Value" };
            nonThrowingPInvokeArgs.AddRange(argPInvokeList);
            var nonThrowingPInvokeArgsString = string.Join(", ", nonThrowingPInvokeArgs);

            writer.WriteLines($$"""
                unsafe
                {
                    var metadata = SwiftObjectHelper<{{returnTypeName}}>.GetTypeMetadata();
                    IntPtr buffer = (IntPtr)NativeMemory.Alloc(metadata.Size);
                    try
                    {
                        var indirectResult = new SwiftIndirectResult((void*)buffer);
                        NativeMethods.{{accessorSymbol}}({{nonThrowingPInvokeArgsString}});
                        return ({{returnTypeName}})Swift.Runtime.InteropServices.SwiftMarshal.MarshalFromSwift<{{returnTypeName}}>(buffer);
                    }
                    {{cleanupKeyword}} { NativeMemory.Free((void*)buffer);{{(isFrozenRefFields ? "" : " throw;")}} }
                }
                """);
        }

        if (needsOuterTry)
        {
            writer.Indent--;
            writer.WriteLine("}");
            writer.WriteLine("finally");
            writer.WriteLine("{");
            writer.Indent++;
            EmitPinHandleCleanup(writer, pinHandles);
            writer.Indent--;
            writer.WriteLine("}");
        }

        writer.Indent--;
        writer.WriteLine("}");
    }

    /// <summary>
    /// Emits parameter marshalling for dispatched methods.
    /// String params: encode to UTF-8 bytes, pin via GCHandle, wrap in Utf8Slice.
    /// Class/struct params: extract SafeHandle payload pointer via Payload.DangerousGetHandle().
    /// Blittable params: simple copy.
    /// All params end up as arg{i}Slice for uniform pointer passing.
    /// Handle variables must be pre-declared by EmitPinHandleDeclarations before the enclosing try block.
    /// </summary>
    private static void EmitMethodParameterMarshalling(CSharpWriter writer, List<string> argNames, List<TypeSpec?> paramSwiftTypeSpecs, WitnessDispatchEmitter? dispatchEmitter = null)
    {
        for (int i = 0; i < argNames.Count; i++)
        {
            if (WitnessDispatchEmitter.IsStringDispatchType(paramSwiftTypeSpecs[i]))
            {
                // String parameter: encode to UTF-8, pin via GCHandle, wrap in Utf8Slice
                var handleName = $"arg{i}Handle";
                writer.WriteLine($"var arg{i}Bytes = global::System.Text.Encoding.UTF8.GetBytes({argNames[i]} ?? string.Empty);");
                writer.WriteLine($"{handleName} = GCHandle.Alloc(arg{i}Bytes, GCHandleType.Pinned);");
                writer.WriteLine($"var arg{i}Slice = new Utf8Slice {{ Ptr = {handleName}.AddrOfPinnedObject(), Len = (nint)arg{i}Bytes.Length }};");
            }
            else if (dispatchEmitter != null &&
                     (dispatchEmitter.IsSwiftClassType(paramSwiftTypeSpecs[i]) ||
                      dispatchEmitter.IsIndirectStructType(paramSwiftTypeSpecs[i])))
            {
                // ObjC-rooted classes use .Handle (ObjC pointer), pure Swift classes use .Payload
                if (dispatchEmitter.IsObjCRootedClassType(paramSwiftTypeSpecs[i]))
                    writer.WriteLine($"var arg{i}Slice = {argNames[i]}.Handle;");
                else
                    writer.WriteLine($"var arg{i}Slice = {argNames[i]}.Payload.DangerousGetHandle();");
            }
            else
            {
                // Blittable parameter: simple copy
                writer.WriteLine($"var arg{i}Slice = {argNames[i]};");
            }
        }
    }

    /// <summary>
    /// Emits GCHandle.Free() calls for pinned string parameter handles.
    /// Uses IsAllocated check for exception-safe cleanup.
    /// </summary>
    private static void EmitPinHandleCleanup(CSharpWriter writer, List<string> pinHandles)
    {
        foreach (var handle in pinHandles)
        {
            writer.WriteLine($"if ({handle}.IsAllocated) {handle}.Free();");
        }
    }

    /// <summary>
    /// Emits GCHandle variable declarations initialized to default before try blocks.
    /// This ensures handles can be safely checked with IsAllocated in finally blocks
    /// even if an exception occurs during allocation of subsequent handles.
    /// </summary>
    private static List<string> EmitPinHandleDeclarations(CSharpWriter writer, List<string> argNames, List<TypeSpec?> paramSwiftTypeSpecs)
    {
        var pinHandles = new List<string>();
        for (int i = 0; i < argNames.Count; i++)
        {
            if (WitnessDispatchEmitter.IsStringDispatchType(paramSwiftTypeSpecs[i]))
            {
                var handleName = $"arg{i}Handle";
                writer.WriteLine($"var {handleName} = default(GCHandle);");
                pinHandles.Add(handleName);
            }
        }
        return pinHandles;
    }

    /// <summary>
    /// Validates that all param projections are compatible with witness dispatch.
    /// Returns false if any param has an incompatible projection.
    /// </summary>
    private static bool ValidateParamProjections(List<string> projectedParamTypes, List<TypeSpec?> paramSwiftTypeSpecs, WitnessDispatchEmitter dispatchEmitter)
    {
        for (int i = 0; i < projectedParamTypes.Count; i++)
        {
            if (!IsParamProjectionValid(paramSwiftTypeSpecs[i], projectedParamTypes[i], dispatchEmitter))
                return false;
        }
        return true;
    }

    /// <summary>
    /// Checks if a single param's projected C# type is valid for witness dispatch.
    /// String: must be idiomatic string type. Class/struct: must not be degraded to "object" or AnyType.
    /// Otherwise: must be blittable primitive.
    /// </summary>
    private static bool IsParamProjectionValid(TypeSpec? swiftType, string projectedType, WitnessDispatchEmitter dispatchEmitter)
    {
        if (WitnessDispatchEmitter.IsStringDispatchType(swiftType))
            return IsIdiomaticStringType(projectedType);

        if (dispatchEmitter.IsSwiftClassType(swiftType) || dispatchEmitter.IsIndirectStructType(swiftType))
        {
            // Class/struct param: reject if projected type degraded to "object" or AnyType
            return projectedType != "object" &&
                   projectedType != TypeDatabaseExtensions.AnyType.CSharpTypeName.FullyQualifiedName;
        }

        return WitnessDispatchEmitter.IsBlittablePrimitive(projectedType);
    }

    /// <summary>
    /// Gets the C# marshal expression for a collection return type using the TypeProjectionFactory.
    /// Composes MarshalFromSwift&lt;ContainerType&gt;(ptr) with the container conversion suffix.
    /// </summary>
    private string GetCollectionMarshalExpression(TypeSpec returnTypeSpec, string ptrVar)
    {
        var factory = new TypeProjectionFactory();
        var projection = factory.Project(returnTypeSpec, new ProjectionContext
        {
            TypeDatabase = _typeDatabase,
            IsParameter = false,
            GenericContext = GenericContext.Empty
        });
        if (projection == null)
            return $"Swift.Runtime.InteropServices.SwiftMarshal.MarshalFromSwift<object>({ptrVar})";

        var containerType = projection.ContainerTypeName;
        // Pass empty containerVar to get just the conversion suffix (e.g., ".AsProjected(e => ...)")
        var suffix = projection.GetReturnContainerConversion("");
        return $"Swift.Runtime.InteropServices.SwiftMarshal.MarshalFromSwift<{containerType}>({ptrVar}){suffix}";
    }

    /// <summary>
    /// Emits the C# dispatch body for methods that return collection types (Array, Dictionary, Set).
    /// Uses the same heap-allocated pointer + free function pattern as ExistentialReturn,
    /// but uses TypeProjectionFactory for the return conversion.
    /// </summary>
    private void EmitCollectionReturnMethodBody(
        CSharpWriter writer, MethodDecl method, ProtocolDecl protocolDecl,
        WitnessDispatchEmitter dispatchEmitter,
        int methodIndex, string methodName, string argsString,
        List<string> argNames, List<TypeSpec?> paramSwiftTypeSpecs,
        TypeSpec returnType, string returnTypeName)
    {
        var accessorSymbol = WitnessDispatchEmitter.GetAccessorSymbol(protocolDecl.Name, "method", method.Name, methodIndex);
        var freeSymbol = WitnessDispatchEmitter.GetFreeSymbol(protocolDecl.Name, "method", method.Name, methodIndex);

        var resultExpression = GetCollectionMarshalExpression(returnType, "resultPtr");
        EmitHeapPointerMethodBody(writer, method, dispatchEmitter,
            methodName, argsString, argNames, paramSwiftTypeSpecs,
            accessorSymbol, freeSymbol, resultExpression);
    }

    /// <summary>
    /// Emits the standard Swift error handling block that converts an error pointer
    /// to a SwiftException. Used by all throwing witness dispatch paths.
    /// Caller must set writer.Indent to the correct level (typically inside an if-error block).
    /// </summary>
    private static void EmitSwiftErrorHandling(CSharpWriter writer)
    {
        writer.WriteLines("""
            string _errorMessage;
            var _descPtr = NativeMethods.SBW_GetErrorDescription(errorOut);
            try
            {
                _errorMessage = _descPtr != IntPtr.Zero
                    ? global::System.Runtime.InteropServices.Marshal.PtrToStringUTF8(_descPtr) ?? "Unknown Swift error"
                    : "Unknown Swift error";
            }
            finally
            {
                if (_descPtr != IntPtr.Zero) NativeMethods.SBW_Free(_descPtr);
                NativeMethods.SBW_ReleaseError(errorOut);
            }
            throw new Swift.Runtime.SwiftException(_errorMessage);
            """);
    }

    /// <summary>
    /// Emits a NotSupportedException stub for a closure property that is in the interface
    /// but can't be dispatched by the proxy (closure marshalling not supported).
    /// </summary>
    private void EmitNotSupportedPropertyStub(CSharpWriter writer, PropertyDecl property)
    {
        var csharpTypeName = GetInterfaceCompatiblePropertyTypeName(property);
        var propertyName = NameProvider.GetPropertyName(property.Name);
        var hasGetter = property.Accessors.OfType<GetAccessorDecl>().Any();
        var hasSetter = property.Accessors.OfType<SetAccessorDecl>().Any();

        writer.WriteLine("[Obsolete(\"This member cannot be called on protocol-typed values: closure parameters cannot be marshalled. Use a concrete type instead (SB0003).\",");
        writer.WriteLine("    DiagnosticId = \"SB0003\",");
        writer.WriteLine("    UrlFormat = \"https://github.com/justinwojo/swift-dotnet-bindings/wiki/Troubleshooting\")]");
        writer.WriteLine($"public {csharpTypeName} {propertyName}");
        writer.WriteLine("{");
        writer.Indent++;

        if (hasGetter)
        {
            writer.WriteLines($$"""
                get
                {
                    if (_disposed) throw new ObjectDisposedException(GetType().Name);
                    if (_csharpImpl != null)
                        return _csharpImpl.{{propertyName}};
                    throw new NotSupportedException(
                        "Cannot get property '{{propertyName}}' on a Swift-backed existential container. " +
                        "Closure-typed properties cannot be marshalled in protocol proxy.");
                }
                """);
        }

        if (hasSetter)
        {
            writer.WriteLines($$"""
                set
                {
                    if (_disposed) throw new ObjectDisposedException(GetType().Name);
                    if (_csharpImpl != null)
                    {
                        _csharpImpl.{{propertyName}} = value;
                        return;
                    }
                    throw new NotSupportedException(
                        "Cannot set property '{{propertyName}}' on a Swift-backed existential container. " +
                        "Closure-typed properties cannot be marshalled in protocol proxy.");
                }
                """);
        }

        writer.Indent--;
        writer.WriteLine("}");
        writer.WriteLine();
    }

    /// <summary>
    /// Emits a NotSupportedException stub for a method that is in the interface
    /// but can't be dispatched by the proxy (e.g. closure or existential parameter marshalling).
    /// </summary>
    private void EmitNotSupportedMethodStub(CSharpWriter writer, MethodDecl method, string reason, IReadOnlySet<string>? propertyNames = null)
    {
        var returnType = method.CSSignature.FirstOrDefault()?.SwiftTypeSpec;
        var hasReturn = returnType != null && !returnType.IsEmptyTuple;
        var returnTypeName = hasReturn ? GetCSharpTypeName(returnType!, isParameter: false) : "void";

        if (method.IsAsync)
        {
            returnTypeName = returnTypeName == "void" ? "Task" : $"Task<{returnTypeName}>";
        }

        var parameters = new List<string>();
        var argNames = new List<string>();
        foreach (var param in method.CSSignature.Skip(1))
        {
            // Skip debug params and empty tuple () params (zero-sized Void)
            if (DefaultParameterOverloadEmitter.IsDebugParameter(param))
                continue;
            if (param.SwiftTypeSpec.IsEmptyTuple)
                continue;
            var paramTypeName = GetCSharpTypeName(param.SwiftTypeSpec, isParameter: true);
            var paramName = NameProvider.GetCSharpParameterName(param);
            parameters.Add($"{paramTypeName} {paramName}");
            argNames.Add(paramName);
        }
        if (method.IsAsync)
        {
            parameters.Add("global::System.Threading.CancellationToken cancellationToken = default");
            argNames.Add("cancellationToken");
        }

        var parametersString = string.Join(", ", parameters);
        var argsString = string.Join(", ", argNames);

        var isSelfReturning = MethodEnvironment.IsSelfReturningMethod(method);
        var methodName = NameProvider.GetPublicMethodName(method.Name, method.IsAsync, hasReturn,
            propertyNames: propertyNames, isSelfReturning: isSelfReturning,
            parameterCount: method.CSSignature.Skip(1).Count(a => !DefaultParameterOverloadEmitter.IsDebugParameter(a) && !a.SwiftTypeSpec.IsEmptyTuple));

        writer.WriteLine($"[Obsolete(\"This member cannot be called on protocol-typed values: {reason}. Use a concrete type instead (SB0003).\",");
        writer.WriteLine("    DiagnosticId = \"SB0003\",");
        writer.WriteLine("    UrlFormat = \"https://github.com/justinwojo/swift-dotnet-bindings/wiki/Troubleshooting\")]");
        writer.WriteLine($"public {returnTypeName} {methodName}({parametersString})");
        writer.WriteLine("{");
        writer.Indent++;

        writer.WriteLine("if (_disposed) throw new ObjectDisposedException(GetType().Name);");

        if (hasReturn || method.IsAsync)
        {
            writer.WriteLines($$"""
                if (_csharpImpl != null)
                    return _csharpImpl.{{methodName}}({{argsString}});
                throw new NotSupportedException(
                    "Cannot call method '{{methodName}}' on a Swift-backed existential container. " +
                    "{{reason}}");
                """);
        }
        else
        {
            writer.WriteLines($$"""
                if (_csharpImpl != null)
                {
                    _csharpImpl.{{methodName}}({{argsString}});
                    return;
                }
                throw new NotSupportedException(
                    "Cannot call method '{{methodName}}' on a Swift-backed existential container. " +
                    "{{reason}}");
                """);
        }

        writer.Indent--;
        writer.WriteLine("}");
        writer.WriteLine();
    }
}
