// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Text;
using Microsoft.Extensions.Logging;

namespace BindingsGeneration.ObjC;

public static class ApiDefinitionEmitter
{
    public static string Emit(ObjCModule module, string outputDir, string resolvedNamespace, ILogger logger, ObjCBindingDiagnostics? diagnostics = null, PlatformInfo? platformInfo = null)
    {
        var typedefMap = ObjCTypeMapper.BuildResolvedTypedefMap(module);
        var blockTypedefMap = ObjCTypeMapper.BuildBlockTypedefMap(module);

        // Build known types for source-aware type resolvability.
        // Types not in this set AND not in Apple SDK type names will be skipped.
        var knownTypes = ObjCTypeMapper.BuildKnownMappedTypes();
        foreach (var e in module.Enums) knownTypes.Add(e.Name);
        foreach (var s in module.Structs) knownTypes.Add(s.Name);
        foreach (var cls in module.Classes)
        {
            knownTypes.Add(cls.Name);
            knownTypes.Add(ObjCTypeMapper.MapClassName(cls.Name));
        }

        // Names present as BOTH a class and a protocol in this module. In ObjC these are two
        // distinct runtime entities that share a spelling (e.g. a `Foo` class and a `Foo` protocol).
        // bgen would emit two `partial interface Foo` blocks — one for the class (with its real
        // [BaseType]) and one for the protocol — colliding on the [BaseType] attribute (CS0579),
        // member declarations (CS0102/CS0111), and the class's own conformance to the protocol
        // listing `Foo` in `Foo`'s inheritance list (CS0529 self-cycle). The class keeps the bare
        // name; the protocol's managed interface is renamed `FooProtocol` with `[Protocol(Name="Foo")]`
        // (the canonical dotnet/macios disambiguation) so both entities survive losslessly.
        var classNamesForClash = new HashSet<string>(module.Classes.Select(c => c.Name), StringComparer.Ordinal);
        var classProtocolClashNames = new HashSet<string>(
            module.Protocols.Select(p => p.Name).Where(classNamesForClash.Contains), StringComparer.Ordinal);

        // knownTypes is consulted by the resolvability gate, which re-maps a member's type and asks
        // whether the RESULT is a name this binding declares. So it must hold every spelling a
        // reference site can produce, and a protocol has more than one: `I{mapped}` for a member
        // typed by the protocol (MapType synthesizes the interface prefix), and bare `{mapped}` for
        // a conformance/inheritance entry and for the [Model] class a [Wrap] delegate property is
        // typed by. The raw ObjC name is seeded too because a member can be typed by a protocol the
        // acronym convention does not rename, where raw and mapped coincide — and because dropping
        // it would silently change the gate for every non-NS-prefixed protocol.
        //
        // Removing a spelling is only safe alongside an audit of the reference sites that can emit
        // it; a member typed by a local protocol that fails this gate vanishes with only a debug log.
        foreach (var proto in module.Protocols)
        {
            knownTypes.Add(proto.Name);
            knownTypes.Add($"I{proto.Name}");
            knownTypes.Add($"I{ObjCTypeMapper.MapProtocolName(proto.Name)}");
            // For a class/protocol clash, the protocol is referenced as the renamed interface
            // (I-form for member types, bare for conformance/inheritance lists); seed both so the
            // resolvability gate accepts members typed by the renamed protocol.
            if (classProtocolClashNames.Contains(proto.Name))
            {
                knownTypes.Add($"I{ObjCTypeMapper.MapProtocolName(proto.Name, classProtocolClashNames)}");
                knownTypes.Add(ObjCTypeMapper.MapProtocolName(proto.Name, classProtocolClashNames));
            }
        }
        // Apple SDK provenance: keys (type names) drive the resolvability gate below; values
        // (owning .NET namespaces) drive the `using` set so a referenced framework not in the
        // curated baseline still resolves. Null in -fmodules mode (no AST-expanded SDK types).
        var appleSdkTypeNamespaces = module.AppleSdkTypeNamespaces;
        var appleSdkTypes = appleSdkTypeNamespaces is { } nsMap
            ? new HashSet<string>(nsMap.Keys, StringComparer.Ordinal)
            : null;

        // Every protocol DECLARED in this binding. Protocol-typed references to these are emitted
        // by their BARE name: bgen generates the `IFoo` interface itself, and the api-definition
        // contract compile (a plain csc pass over ApiDefinition.cs, before bgen runs) only sees the
        // bare `[Protocol] interface Foo` declaration — an `IFoo` reference would be undefined
        // (CS0246). SDK protocols keep the `I` prefix because their interface already ships in the
        // platform assembly. This is the source of the bare-vs-I decision in MapType (step 3) and
        // ProtocolInterfaceReference, replacing the former whole-file IFoo→Foo regex post-process.
        var localProtocolNames = new HashSet<string>(module.Protocols.Select(p => p.Name), StringComparer.Ordinal);

        // Delegate/data-source protocols (a subset of the above) drive the WeakDelegate/Wrap pattern.
        var delegateProtocolNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var proto in module.Protocols)
        {
            if (proto.IsDelegateProtocol)
                delegateProtocolNames.Add(proto.Name);
        }

        // Build set of enum names for out-param detection (enum pointer → out T)
        var enumNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in module.Enums)
            enumNames.Add(e.Name);

        var sb = new StringBuilder();
        var referencedAppleNamespaces =
            ObjCUsingsEmitter.CollectReferencedNamespaces(module, appleSdkTypeNamespaces);
        ObjCUsingsEmitter.EmitApiDefinitionHeader(sb, platformInfo, referencedAppleNamespaces);
        sb.AppendLine();
        sb.AppendLine($"namespace {resolvedNamespace}");
        sb.AppendLine("{");

        // Forward-declare each own protocol's consumer interface as an empty `interface IFoo {}`.
        // Member signatures reference an own protocol by its INTERFACE `IFoo` (see MapType step 3)
        // so bgen binds them to the protocol interface, not the generated Model class — a bare
        // reference makes bgen pick `Foo : NSObject`, and a conforming subclass then fails
        // `GetNSObject<Foo>` with an InvalidCastException. But the api-definition contract compile (a
        // plain csc pass over ApiDefinition.cs, before bgen runs) has no `IFoo` in scope, so an
        // `IFoo` member reference would be CS0246. These empty placeholders satisfy that compile;
        // bgen treats an attribute-less interface as a forward declaration and emits the real `IFoo`
        // from the `[Protocol] interface Foo` below. Matches the dotnet/macios hand-binding idiom.
        foreach (var proto in module.Protocols)
            sb.AppendLine($"    interface I{ObjCTypeMapper.MapProtocolName(proto.Name, classProtocolClashNames)} {{ }}");
        if (module.Protocols.Count > 0)
            sb.AppendLine();

        var protocolsByName = module.Protocols.ToDictionary(p => p.Name, p => p, StringComparer.Ordinal);
        foreach (var proto in module.Protocols)
            EmitProtocol(sb, proto, typedefMap, blockTypedefMap, knownTypes, appleSdkTypes, enumNames, protocolsByName, localProtocolNames, classProtocolClashNames, logger, diagnostics);

        // Drop classes whose base-type chain isn't resolvable in the binding context (e.g. an
        // external superclass with no .NET binding). Computed as a fixpoint BEFORE emission so a
        // subclass of a dropped class is also dropped: every class name was seeded into knownTypes
        // above, so a dropped class still satisfies the per-class resolvability check for its
        // descendants — only the precomputed transitive set catches MySpec : MyBaseSpec :
        // XCTestCase. A category on a dropped class must also be skipped — its
        // [BaseType(typeof(X))] would otherwise reference a missing type.
        var droppedClassReasons = ComputeUnresolvableBaseClasses(module, knownTypes, appleSdkTypes);
        var droppedClassNames = new HashSet<string>(droppedClassReasons.Keys, StringComparer.Ordinal);
        // Superclass lookup for the inherited-property walk in EmitClass. First declaration wins so
        // the map agrees with emission, which also takes the first of a duplicated name.
        var classesByName = new Dictionary<string, ObjCClassDecl>(StringComparer.Ordinal);
        foreach (var cls in module.Classes)
            classesByName.TryAdd(cls.Name, cls);
        // Collected while classes emit; written out below as the partial-class array overloads that
        // pair with the [Internal] pointer+count members emitted here.
        var arrayOverloads = new List<ObjCArrayOverload>();
        foreach (var cls in module.Classes)
            EmitClass(sb, cls, typedefMap, blockTypedefMap, knownTypes, appleSdkTypes, localProtocolNames, classProtocolClashNames, delegateProtocolNames, enumNames, droppedClassReasons, protocolsByName, classesByName, arrayOverloads, logger, diagnostics);

        // Collected while categories emit; written out below as the receiver-free overloads that
        // pair with the [Static] members emitted here.
        var categoryStaticForwarders = new List<ObjCCategoryStaticForwarder>();
        foreach (var cat in module.Categories)
            EmitCategory(sb, cat, typedefMap, blockTypedefMap, knownTypes, appleSdkTypes, enumNames, droppedClassNames, localProtocolNames, classProtocolClashNames, categoryStaticForwarders, logger, diagnostics);

        // Constants belong in this file specifically: bgen only generates the Dlfcn reader backing a
        // [Field] needs when it parses the declaration out of an ObjcBindingApiDefinition input.
        ObjCConstantsEmitter.Emit(sb, module, typedefMap, diagnostics);

        sb.AppendLine("}");

        // Protocols declared in this binding are referenced by their bare name; SDK protocols keep
        // the `I` prefix. That decision is made at the emission source — MapType step 3 and the
        // conformance/inheritance lists consult localProtocolNames — instead of a blunt whole-file
        // IFoo→Foo regex post-process, which also rewrote unrelated text (doc comments, substrings)
        // and depended on bare-name and I-name forms staying in sync across the entire file.
        var result = sb.ToString();

        Directory.CreateDirectory(outputDir);
        var filePath = Path.Combine(outputDir, "ApiDefinition.cs");
        File.WriteAllText(filePath, result);

        logger.LogInformation("Wrote {FilePath}", filePath);

        // Always called, including with an empty list: it clears a stale file from a previous
        // generate, which would otherwise reference internal members this run no longer declares.
        ObjCArrayOverloadsEmitter.Emit(arrayOverloads, outputDir, resolvedNamespace, platformInfo, referencedAppleNamespaces, logger);
        ObjCCategoryStaticsEmitter.Emit(categoryStaticForwarders, outputDir, resolvedNamespace, platformInfo, referencedAppleNamespaces, logger);

        return filePath;
    }

    /// <summary>
    /// Computes the transitive closure of classes that must be dropped because their base-type
    /// chain isn't resolvable in the binding context, mapping each dropped class to a human-readable
    /// drop reason. A class is dropped when its mapped superclass isn't resolvable (an external
    /// superclass with no .NET binding, e.g. a Swift test framework's QuickSpec : XCTestCase) OR
    /// when its raw superclass is itself an already-dropped class. The transitive case can't be
    /// caught by the per-class base-type check alone: every class name is seeded into
    /// <paramref name="knownTypes"/> before emission, so a dropped class still satisfies
    /// IsApiDefinitionTypeResolvable for its subclasses — only this precomputed set catches
    /// MySpec : MyBaseSpec : XCTestCase. Iterated to a fixpoint so a chain drops top-to-bottom
    /// regardless of declaration order. The transitive check keys on the RAW superclass name (not
    /// the mapped/resolvability form) precisely because the dropped base is still "known".
    /// </summary>
    static Dictionary<string, string> ComputeUnresolvableBaseClasses(ObjCModule module, HashSet<string> knownTypes, HashSet<string>? appleSdkTypes)
    {
        var dropped = new Dictionary<string, string>(StringComparer.Ordinal);
        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (var cls in module.Classes)
            {
                if (dropped.ContainsKey(cls.Name))
                    continue;
                var superName = cls.SuperclassName ?? "NSObject";
                if (dropped.ContainsKey(superName))
                {
                    dropped[cls.Name] = $"base class '{superName}' was dropped (unresolvable base type)";
                    changed = true;
                    continue;
                }
                var baseType = ObjCTypeMapper.MapClassName(superName);
                // A class name, never a protocol interface — nothing can have synthesized an `I`.
                if (!ObjCTypeMapper.IsApiDefinitionTypeResolvable(baseType, knownTypes, appleSdkTypes, NoSynthesizedProtocolInterfaces))
                {
                    dropped[cls.Name] = $"unresolvable base type '{baseType}'";
                    changed = true;
                }
            }
        }
        return dropped;
    }

    static void EmitProtocol(StringBuilder sb, ObjCProtocolDecl proto, Dictionary<string, ObjCTypeRef> typedefMap, Dictionary<string, ObjCTypeRef> blockTypedefMap, HashSet<string> knownTypes, HashSet<string>? appleSdkTypes, HashSet<string>? enumNames, Dictionary<string, ObjCProtocolDecl>? protocolsByName, HashSet<string>? localProtocolNames, HashSet<string>? classProtocolClashNames, ILogger logger, ObjCBindingDiagnostics? diagnostics)
    {
        ObjCDocCommentEmitter.EmitDocComment(sb, proto.DocComment, null, "    ");
        ObjCAvailabilityEmitter.EmitAvailabilityAttributes(sb, proto.Availability, "    ");

        // The managed declaration name is whatever every reference to this protocol resolves to —
        // `MapProtocolName`, which folds in BOTH renames that can apply: the .NET acronym convention
        // (`NSURLThing` -> `NSUrlThing`) and the class/protocol-clash `{Name}Protocol` suffix. Any
        // time that differs from the ObjC name, `Name = "{raw}"` preserves the native selector
        // registration so the rename stays C#-side only.
        //
        // Deriving the declaration from the same function as the references is what keeps them in
        // agreement. Emitting the raw name here while references normalize it leaves bgen generating
        // `INSURLThing` from the declaration while members are typed `INSUrlThing` — which resolves
        // only against the empty forward declaration, so the member silently binds to a placeholder
        // interface instead of the protocol (and for a class, fails outright with CS0246).
        // The registration argument is the name the ObjC runtime knows, which is the raw declaration
        // spelling — NOT `proto.Name`, which may already carry the type's Swift-import rename.
        var protocolRuntimeName = proto.RawObjCName ?? proto.Name;
        var declarationName = ObjCTypeMapper.MapProtocolName(proto.Name, classProtocolClashNames);
        var protocolAttrArgs = !string.Equals(declarationName, protocolRuntimeName, StringComparison.Ordinal)
            ? $"(Name = \"{protocolRuntimeName}\")"
            : "";

        // A protocol that declares a parameterless init requirement otherwise gets BOTH a
        // synthesized default constructor (exporting `init`) on bgen's concrete adapter class AND the
        // requirement re-emitted as an abstract `Init()` method that also exports `init` — two members
        // carrying the same ObjC selector on one registered type, which aborts the .NET registrar at
        // launch. The abstract `Init()` additionally compiles to `public virtual NSObject Init()` on a
        // class deriving from NSObject, which hides NSObject.Init() (CS0108). Both are resolved by
        // fully mirroring EmitClass's parameterless-init handling: emit [DisableDefaultCtor] (suppress
        // the synthesized ctor) AND suppress the parameterless `init` method in the loop below (the
        // "Fix #6" filter) so neither member carries the selector and no NSObject.Init() shadow is
        // emitted. Parameterless `initWith…` requirements export a distinct selector and do not
        // collide, but they still warrant [DisableDefaultCtor] for the same reason EmitClass does.
        var protocolDeclaresParameterlessInit =
            proto.Methods.Any(m => m.Selector == "init" && m.Parameters.Count == 0)
            || proto.Methods.Any(m => m.Selector.StartsWith("initWith", StringComparison.Ordinal) && m.Parameters.Count == 0);

        // Delegate/data-source protocols get [Model] attribute.
        // With [Model], the Xamarin convention uses the bare protocol name (not I-prefixed).
        if (proto.IsDelegateProtocol)
        {
            sb.AppendLine($"    [Protocol{protocolAttrArgs}, Model]");
        }
        else
        {
            sb.AppendLine($"    [Protocol{protocolAttrArgs}]");
        }
        if (protocolDeclaresParameterlessInit)
            sb.AppendLine("    [DisableDefaultCtor]");
        sb.AppendLine("    [BaseType(typeof(NSObject))]");

        // Filter out implicit protocols from inheritance — NSObject is implicit in .NET MAUI bindings,
        // NSFastEnumeration maps to IEnumerable but isn't a binding interface. Also drop any
        // inherited protocol whose interface isn't resolvable in the binding context (external
        // protocol with no .NET binding) — emitting `: IExternalProto` would fail with CS0246.
        var filteredInherited = proto.InheritedProtocolNames
            .Where(n => n != "NSObject" && n != "NSFastEnumeration")
            .Where(n => IsProtocolInterfaceResolvable(n, knownTypes, appleSdkTypes, classProtocolClashNames))
            .ToList();
        var inheritList = filteredInherited.Count > 0
            ? $" : {string.Join(", ", filteredInherited.Select(n => ProtocolInterfaceReference(n, localProtocolNames, classProtocolClashNames)))}"
            : "";

        // bgen derives the interface spelling from the protocol declaration: a protocol
        // declared as `partial interface Foo` produces the consumer-facing `IFoo` interface
        // (and, for [Model] protocols, the `Foo` Model class). Declaring it as `IFoo` here
        // makes bgen generate `IIFoo` plus an orphan `Foo` — so always emit the UNPREFIXED name and
        // let bgen apply its own "I" prefix exactly once.
        sb.AppendLine($"    partial interface {declarationName}{inheritList}");
        sb.AppendLine("    {");

        // Protocols don't declare ObjC lightweight generics — only pass the common fallback set.
        // Two-set name tracking:
        //   emittedMemberNames   = every emitted name (methods + properties). EmitProperty uses
        //                          this to drop a property whose name collides with anything
        //                          already emitted.
        //   emittedPropertyNames = property names only. EmitMethod's dedup uses this to detect
        //                          method-vs-property name collisions (CS0102) while still
        //                          allowing legal method overloads with the same short name.
        var emittedMethodSignatures = new HashSet<string>();
        var emittedMemberNames = new HashSet<string>();
        var emittedPropertyNames = new HashSet<string>();
        // Pre-seed with method signatures, all member names, and property-only names from
        // transitively-inherited protocols. bgen flattens inherited protocols into the concrete
        // class, so a CS0111 (sig collision) or CS0102 (name collision with a property) in the
        // generated *.g.cs would otherwise slip through. Seeding triggers the rename-to-full-
        // selector path on methods; for properties — which the emitter cannot rename — the
        // colliding child property is dropped (consistent with intra-protocol collision handling).
        if (protocolsByName != null && proto.InheritedProtocolNames.Count > 0)
        {
            SeedInheritedProtocolSignatures(emittedMethodSignatures, emittedMemberNames, emittedPropertyNames, proto, protocolsByName, typedefMap, blockTypedefMap, knownTypes, appleSdkTypes, enumNames, localProtocolNames, classProtocolClashNames);
        }

        // Pre-seed this protocol's OWN emittable property names + accessor selectors before the
        // method loop — same method-vs-property collision rationale as EmitClass (ancestor names
        // were already seeded above). ComputeProtocolEmissionSet mirrors this ordering so the
        // signatures that seed descendant protocols agree with what is emitted here.
        var emittableProtocolProperties = proto.Properties
            .Where(p => WouldEmitProperty(p, null, null, typedefMap, blockTypedefMap, localProtocolNames, classProtocolClashNames, knownTypes, appleSdkTypes))
            .ToList();
        foreach (var prop in emittableProtocolProperties)
            emittedPropertyNames.Add(ToPascalCase(prop.Name));
        var protocolAccessorSelectors = BuildPropertyAccessorSelectors(emittableProtocolProperties);

        foreach (var method in proto.Methods)
        {
            // Suppress the parameterless `init` requirement when [DisableDefaultCtor] is emitted —
            // mirrors EmitClass's "Fix #6". Keeping it would re-register selector `init` (colliding
            // with bgen's synthesized adapter ctor) and emit a `public virtual NSObject Init()` that
            // hides NSObject.Init() (CS0108).
            if (protocolDeclaresParameterlessInit && method.Selector == "init" && method.Parameters.Count == 0)
            {
                logger.LogDebug("Skipping parameterless init on protocol {Protocol}: [DisableDefaultCtor] covers the selector", proto.Name);
                diagnostics?.RecordSkip("Method", method.Selector, ObjCSkipReason.DuplicateSelector, $"parameterless init on protocol '{proto.Name}' is covered by [DisableDefaultCtor] (avoids duplicate selector + NSObject.Init() shadow)");
                continue;
            }
            if (CollidesWithPropertyAccessor(method, protocolAccessorSelectors))
            {
                logger.LogDebug("Skipping method {Selector} on protocol {Protocol}: selector also exported by a property accessor", method.Selector, proto.Name);
                diagnostics?.RecordSkip("Method", method.Selector, ObjCSkipReason.DuplicateSelector, $"selector also exported by a property accessor on protocol '{proto.Name}' (kept the property)");
                continue;
            }
            var emittedName = EmitMethod(sb, method, declaringClassName: null, isProtocol: true, genericTypeParams: null, typedefMap: typedefMap, blockTypedefMap: blockTypedefMap, emittedMethodSignatures: emittedMethodSignatures, emittedPropertyNames: emittedPropertyNames, knownTypes: knownTypes, appleSdkTypes: appleSdkTypes, enumNames: enumNames, isDelegateProtocol: proto.IsDelegateProtocol, delegateProtocolName: proto.RawObjCName ?? proto.Name, localProtocolNames: localProtocolNames, classProtocolClashNames: classProtocolClashNames, logger: logger, diagnostics: diagnostics);
            if (emittedName != null) emittedMemberNames.Add(emittedName);
        }

        foreach (var prop in proto.Properties)
            EmitProperty(sb, prop, declaringClassName: null, genericTypeParams: null, typedefMap: typedefMap, blockTypedefMap: blockTypedefMap, emittedMemberNames: emittedMemberNames, emittedPropertyNames: emittedPropertyNames, knownTypes: knownTypes, appleSdkTypes: appleSdkTypes, localProtocolNames: localProtocolNames, classProtocolClashNames: classProtocolClashNames, logger: logger, diagnostics: diagnostics);

        sb.AppendLine("    }");
        sb.AppendLine();
    }

    static void EmitClass(StringBuilder sb, ObjCClassDecl cls, Dictionary<string, ObjCTypeRef> typedefMap, Dictionary<string, ObjCTypeRef> blockTypedefMap, HashSet<string> knownTypes, HashSet<string>? appleSdkTypes, HashSet<string>? localProtocolNames, HashSet<string>? classProtocolClashNames, HashSet<string>? delegateProtocolNames, HashSet<string>? enumNames, IReadOnlyDictionary<string, string>? droppedClassReasons, Dictionary<string, ObjCProtocolDecl>? protocolsByName, IReadOnlyDictionary<string, ObjCClassDecl>? classesByName, List<ObjCArrayOverload> arrayOverloads, ILogger logger, ObjCBindingDiagnostics? diagnostics)
    {
        // Resolvability gate (must run before any emission): drop the class if it's in the
        // precomputed unresolvable-base-class set — the fixpoint closure of classes whose base type
        // isn't resolvable in the binding context (e.g. a Swift test framework's
        // QuickSpec : XCTestCase) PLUS their transitive subclasses (MySpec : QuickSpec). Emitting
        // [BaseType(typeof(QuickSpec))] for a dropped QuickSpec would dangle → CS0246.
        if (droppedClassReasons != null && droppedClassReasons.TryGetValue(cls.Name, out var dropReason))
        {
            logger.LogDebug("Skipping class {Name}: {Reason}", cls.Name, dropReason);
            diagnostics?.RecordSkip("Class", cls.Name, ObjCSkipReason.UnresolvableType, dropReason);
            return;
        }
        var baseType = ObjCTypeMapper.MapClassName(cls.SuperclassName ?? "NSObject");

        ObjCDocCommentEmitter.EmitDocComment(sb, cls.DocComment, null, "    ");
        ObjCAvailabilityEmitter.EmitAvailabilityAttributes(sb, cls.Availability, "    ");

        // Disable default constructor if the class declares any parameterless init
        // to avoid bgen generating a duplicate parameterless constructor.
        // When DisableDefaultCtor is set, we also suppress the explicit init constructor
        // to avoid contradicting attributes (Fix #6).
        var hasExplicitParameterlessInit = cls.Methods.Any(m =>
            m.Selector == "init" && m.Parameters.Count == 0);
        var hasParameterlessInitWith = cls.Methods.Any(m =>
            m.Selector.StartsWith("initWith", StringComparison.Ordinal)
            && m.Parameters.Count == 0);
        var disableDefaultCtor = hasExplicitParameterlessInit || hasParameterlessInitWith;
        if (disableDefaultCtor)
            sb.AppendLine("    [DisableDefaultCtor]");

        // Same declaration/reference agreement as EmitProtocol: a member typed by this class maps
        // through MapClassName, so the declaration must carry that name too. `Name = "{raw}"` keeps
        // the native registration on the ObjC spelling. (Only NS-prefixed names with a convention
        // acronym differ at all — for every other class the two spellings are identical.)
        var classRuntimeName = cls.RawObjCName ?? cls.Name;
        var classDeclarationName = ObjCTypeMapper.MapClassName(cls.Name);
        var classNameArg = !string.Equals(classDeclarationName, classRuntimeName, StringComparison.Ordinal)
            ? $", Name = \"{classRuntimeName}\""
            : "";
        sb.AppendLine($"    [BaseType(typeof({baseType}){classNameArg})]");

        // Drop conformances to NSObject/NSFastEnumeration (implicit / non-binding) and to any
        // protocol whose interface isn't resolvable in the binding context — emitting
        // `: IExternalProto` for an unbound protocol would fail with CS0246.
        var filteredProtocols = cls.ProtocolNames
            .Where(n => n != "NSObject" && n != "NSFastEnumeration")
            .Where(n => IsProtocolInterfaceResolvable(n, knownTypes, appleSdkTypes, classProtocolClashNames))
            .ToList();
        // A conformance to a protocol that clashes with this (or another) class name resolves to the
        // renamed `{Name}Protocol` reference — never the bare class name, which would make the class
        // list itself in its own inheritance (CS0529 self-cycle).
        var protocols = filteredProtocols.Count > 0
            ? $" : {string.Join(", ", filteredProtocols.Select(n => ProtocolInterfaceReference(n, localProtocolNames, classProtocolClashNames)))}"
            : "";
        sb.AppendLine($"    partial interface {classDeclarationName}{protocols}");
        sb.AppendLine("    {");

        // Scope generic type params to THIS class only — avoids cross-type collisions
        // where one class's generic param name matches a real type used elsewhere.
        var classGenericParams = cls.GenericTypeParamNames.Count > 0
            ? new HashSet<string>(cls.GenericTypeParamNames)
            : null;

        // bgen auto-generates initWithCoder: for classes conforming to NSCoding/NSSecureCoding.
        // Skip our explicit emission to avoid CS0111 duplicate constructor.
        var conformsToNSCoding = cls.ProtocolNames.Any(p =>
            p is "NSCoding" or "NSSecureCoding");

        // Track emitted signatures + names to detect duplicates (see EmitProtocol for the
        // two-set rationale; classes don't have inherited-protocol seeding but the same
        // method-vs-property and overload-friendly rules apply).
        var emittedConstructorSignatures = new HashSet<string>();
        var emittedMethodSignatures = new HashSet<string>();
        var emittedMemberNames = new HashSet<string>();
        var emittedPropertyNames = new HashSet<string>();

        // Pre-compute, BEFORE the method loop, the names and accessor selectors of the properties
        // this class will actually emit. Methods are emitted before properties, so without this:
        //   * a synthesized method whose short name equals a later property's name produces CS0102
        //     in the bgen-flattened output (e.g. method camera:fittingCoordinateBounds:edgePadding:
        //     → `Camera` vs property camera → `Camera`) — seeding emittedPropertyNames routes that
        //     method through ResolveMethodNameWithDedup's full-selector rename; and
        //   * a method whose selector equals a property accessor selector (e.g. method `setURL:`
        //     vs property `URL`'s setter) would export that ObjC selector twice and abort the
        //     runtime registrar at launch — such methods are dropped in favour of the property.
        // Seed only emittedPropertyNames (not emittedMemberNames), so the property itself is not
        // self-dropped by EmitProperty's name-claim check.
        var emittableProperties = cls.Properties
            .Where(p => WouldEmitProperty(p, classDeclarationName, classGenericParams, typedefMap, blockTypedefMap, localProtocolNames, classProtocolClashNames, knownTypes, appleSdkTypes))
            .ToList();
        foreach (var prop in emittableProperties)
        {
            emittedPropertyNames.Add(ToPascalCase(prop.Name));
            if (IsDelegateProperty(prop, delegateProtocolNames))
                emittedPropertyNames.Add($"Weak{ToPascalCase(prop.Name)}");
        }
        var propertyAccessorSelectors = BuildPropertyAccessorSelectors(emittableProperties);

        // Also drop a class method whose selector matches a property accessor flattened in from a
        // conformed [Protocol]. The .NET registrar registers a conforming class's REQUIRED protocol
        // members on the class itself, so such a method would export that selector twice (the method
        // + the flattened property accessor) and abort the registrar at launch. The within-class set
        // above only covers this class's OWN properties; this seeds the transitively-inherited
        // conformed-protocol accessor selectors so CollidesWithPropertyAccessor catches them too.
        var inheritedAccessorSelectors = BuildInheritedProtocolAccessorSelectors(
            cls.ProtocolNames, protocolsByName, typedefMap, blockTypedefMap, knownTypes, appleSdkTypes,
            localProtocolNames, classProtocolClashNames);
        propertyAccessorSelectors.Instance.UnionWith(inheritedAccessorSelectors.Instance);
        propertyAccessorSelectors.Class.UnionWith(inheritedAccessorSelectors.Class);

        // Members the RESOLVED superclass chain already emits. bgen generates a class binding as a
        // real C# class deriving from its [BaseType], so a property this class re-declares (ObjC
        // headers routinely re-declare an inherited property to restate a protocol conformance)
        // becomes a member HIDING the inherited one: CS0108 in every consumer build, and — when the
        // re-declaration is read-only over a read-write base — the setter becomes unreachable
        // through a subclass-typed variable. The chain is walked here so the property loop below can
        // either defer to the inherited member — when the re-declaration adds nothing it doesn't
        // already offer — or hide it deliberately, recording whatever the hiding costs.
        var inheritedProperties = BuildInheritedClassPropertyMap(cls, classesByName, typedefMap, blockTypedefMap, knownTypes, appleSdkTypes, localProtocolNames, classProtocolClashNames, delegateProtocolNames);

        // Deferring to the inherited member is only enough while nothing else re-introduces a
        // narrower view of it. bgen inlines the members of the protocols in THIS class's conformance
        // list into the generated class, so a protocol restating an ancestor's read-write property
        // as read-only puts a getter-only member on the subclass that hides the inherited setter
        // (CS0200 for any consumer assigning through a subclass-typed variable). An explicit
        // re-declaration carrying the ancestor's accessor set pre-empts that inline; these are the
        // members that need one.
        var protocolNarrowedProperties = PlanProtocolNarrowedRedeclarations(
            filteredProtocols, protocolsByName, inheritedProperties, classDeclarationName, classGenericParams,
            typedefMap, blockTypedefMap,
            knownTypes, appleSdkTypes, localProtocolNames, classProtocolClashNames, delegateProtocolNames);
        var protocolNarrowedKeys = new HashSet<(string Name, bool IsClass)>();
        var protocolNarrowedNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var plan in protocolNarrowedProperties)
        {
            protocolNarrowedKeys.Add((plan.PropName, plan.IsClass));
            protocolNarrowedNames.Add(plan.PropName);
            // A planned re-declaration claims its C# name and exports its accessor selectors exactly
            // as an own property does, so it joins both collision sets BEFORE the method loop — a
            // method sharing either would otherwise export a selector twice (registrar abort) or
            // collide on the name (CS0102).
            emittedPropertyNames.Add(plan.PropName);
            var accessorTarget = plan.IsClass ? propertyAccessorSelectors.Class : propertyAccessorSelectors.Instance;
            var (planGetter, planSetter) = PropertyAccessorSelectors(plan.Declaration);
            accessorTarget.Add(planGetter);
            if (planSetter != null)
                accessorTarget.Add(planSetter);
        }

        foreach (var method in cls.Methods.Where(m =>
            !(conformsToNSCoding && m.Selector == "initWithCoder:")
            // Suppress explicit parameterless init when DisableDefaultCtor is emitted (Fix #6)
            && !(disableDefaultCtor && m.Selector == "init" && m.Parameters.Count == 0)))
        {
            if (CollidesWithPropertyAccessor(method, propertyAccessorSelectors))
            {
                logger.LogDebug("Skipping method {Selector} on {Class}: selector also exported by a property accessor", method.Selector, cls.Name);
                diagnostics?.RecordSkip("Method", method.Selector, ObjCSkipReason.DuplicateSelector, $"selector also exported by a property accessor on '{cls.Name}' (kept the property)");
                continue;
            }
            // `declaringClassName` is what `instancetype` resolves to, so it must be the DECLARATION
            // name, not the ObjC one — a class renamed by the acronym convention would otherwise
            // return the undeclared raw spelling (CS0246), and the resolvability gate would wave it
            // through because both spellings are seeded into knownTypes.
            // A class declaring ObjC lightweight generics is excluded from the array projection: the
            // overload has to be written against the exact class shape bgen generates, and an ObjC
            // generic parameter has no stable counterpart there to write against.
            var emittedName = EmitMethod(sb, method, declaringClassName: classDeclarationName, isProtocol: false, genericTypeParams: classGenericParams, typedefMap: typedefMap, blockTypedefMap: blockTypedefMap, emittedConstructorSignatures: emittedConstructorSignatures, emittedMethodSignatures: emittedMethodSignatures, emittedPropertyNames: emittedPropertyNames, knownTypes: knownTypes, appleSdkTypes: appleSdkTypes, enumNames: enumNames, localProtocolNames: localProtocolNames, classProtocolClashNames: classProtocolClashNames, arrayOverloads: classGenericParams == null ? arrayOverloads : null, logger: logger, diagnostics: diagnostics);
            if (emittedName != null) emittedMemberNames.Add(emittedName);
        }

        // Own declarations that already carry a planned re-declaration's member, so the post-pass
        // below doesn't emit a second copy of it.
        var supersededNarrowedKeys = new HashSet<(string Name, bool IsClass)>();

        // Emit properties, with WeakDelegate/Wrap pattern for delegate properties (Fix #8)
        foreach (var prop in cls.Properties)
        {
            var propName = ToPascalCase(prop.Name);
            // The C# type the emitted member will carry — the [Wrap]'d protocol name for a delegate
            // property, the mapped property type otherwise. The re-declaration comparison must be
            // made against what is EMITTED, so both member shapes classify on the same axis.
            var delegateProtocol = IsDelegateProperty(prop, delegateProtocolNames)
                ? ResolveDelegateProtocolName(prop, delegateProtocolNames)
                : null;
            var mappedType = delegateProtocol != null
                ? ObjCTypeMapper.MapProtocolName(delegateProtocol, classProtocolClashNames)
                : ObjCTypeMapper.MapType(prop.Type, classDeclarationName, classGenericParams, typedefMap, blockTypedefMap, localProtocolNames, classProtocolClashNames);

            var emitNew = false;
            if (inheritedProperties.TryGetValue(propName, out var inheritedMembers))
            {
                // C# hiding is name-based, so ANY inherited member of this name means the
                // declaration needs `new`; the defer/widest-surface decision, in contrast, is made
                // only against the member of the SAME ObjC dispatch kind — an inherited `+foo`
                // cannot stand in for a declared `-foo`, so deferring across kinds would delete
                // reachable API.
                emitNew = true;
                if (inheritedMembers.OfKind(prop.IsClass) is { } inherited)
                {
                    var (getterSelector, setterSelector) = PropertyAccessorSelectors(prop);
                    if (ClassifyRedeclaration(inherited, mappedType, prop.IsReadonly, getterSelector, setterSelector) == RedeclarationDisposition.Defer)
                    {
                        // Deferring is only safe while nothing re-introduces a narrower view of the
                        // inherited member. When a conformed protocol does, the post-pass emits the
                        // ancestor's full accessor set in this declaration's place — which is at
                        // least as wide as this declaration (that is what Defer means), so the
                        // member is superseded, not skipped, and nothing is recorded as lost.
                        if (protocolNarrowedKeys.Contains((propName, prop.IsClass)))
                        {
                            logger.LogDebug("Property {PropName} on {Class} is superseded by an explicit re-declaration of the member inherited from {Owner}", propName, cls.Name, inherited.OwnerClassName);
                            continue;
                        }
                        logger.LogDebug("Skipping property {PropName} on {Class}: re-declaration of the member inherited from {Owner}", propName, cls.Name, inherited.OwnerClassName);
                        diagnostics?.RecordSkip("Property", propName, ObjCSkipReason.DuplicateSelector, $"re-declaration of inherited '{inherited.OwnerClassName}.{propName}' on '{cls.Name}' (kept the inherited member, which is at least as wide)");
                        continue;
                    }
                    // Hiding a read-write ancestor member with a read-only one makes the setter
                    // unreachable through a subclass-typed variable. It cannot be re-exported here:
                    // this member differs from the inherited one in type or accessor selector (an
                    // otherwise-identical re-declaration would have deferred above), so re-using
                    // the ancestor's setter selector would send a message its implementation cannot
                    // decode. Record the lost accessor instead of silently narrowing.
                    if (prop.IsReadonly && !inherited.IsReadonly)
                    {
                        logger.LogDebug("Setter for {PropName} on {Class} is not projected: read-only re-declaration hides the read-write member inherited from {Owner}", propName, cls.Name, inherited.OwnerClassName);
                        diagnostics?.RecordSkip("Property", $"set{propName}", ObjCSkipReason.UnsupportedConstruct, $"read-only re-declaration of '{propName}' on '{cls.Name}' hides the read-write '{inherited.OwnerClassName}.{propName}', whose setter takes the inherited declaration's type and selector (set through '{inherited.OwnerClassName}')");
                    }
                }
            }

            // A planned re-declaration means a conformed protocol emits a member of this C# name, so
            // this declaration hides it right here in the ApiDefinition — the same hiding the plan
            // states with the `new` keyword, and the same CS0108 without it. Keyed on the name alone
            // because C# hiding is name-based: a class member hides a same-named instance one.
            var hidesConformedMember = protocolNarrowedNames.Contains(propName);

            var emitted = delegateProtocol != null
                ? EmitWeakDelegatePattern(sb, prop, delegateProtocolNames, classProtocolClashNames, emittedMemberNames, emittedPropertyNames, inheritedProperties, emitNew)
                : EmitProperty(sb, prop, declaringClassName: classDeclarationName, genericTypeParams: classGenericParams, typedefMap: typedefMap, blockTypedefMap: blockTypedefMap, emittedMemberNames: emittedMemberNames, emittedPropertyNames: emittedPropertyNames, knownTypes: knownTypes, appleSdkTypes: appleSdkTypes, localProtocolNames: localProtocolNames, classProtocolClashNames: classProtocolClashNames, logger: logger, diagnostics: diagnostics, emitNew: emitNew, hidesInheritedInterfaceMember: hidesConformedMember);

            // Only a declaration that REACHED emission carries the planned member. Both emit helpers
            // can still drop it (unresolvable type, name already claimed), and marking the plan
            // superseded on the attempt would leave the class with no declaration at all — exactly
            // the narrowed-by-the-inline shape the plan exists to pre-empt.
            if (emitted)
                supersededNarrowedKeys.Add((propName, prop.IsClass));
        }

        // Re-declare the inherited members a conformed protocol would otherwise narrow. Emitting the
        // ancestor's declaration verbatim keeps its accessor set and its ObjC selectors, and [New]
        // states the C#-level hiding of the ancestor member it re-exports.
        //
        // The `new` KEYWORD is a second, separate need: this interface inherits the interface of the
        // protocol that declares the narrower view, so the re-declaration hides an interface member
        // right here in the ApiDefinition source, which is a CS0108 in the binding's own build unless
        // the hiding is stated. (`[New]` only reaches bgen's generated class.) It is never spurious
        // here — the member is only planned when a conformed protocol emits the same C# name.
        foreach (var plan in protocolNarrowedProperties)
        {
            if (supersededNarrowedKeys.Contains((plan.PropName, plan.IsClass)))
                continue;
            logger.LogDebug("Re-declaring inherited property {PropName} on {Class}: conformance to {Protocol} would otherwise narrow the member inherited from {Owner}", plan.PropName, cls.Name, plan.ProtocolName, plan.OwnerClassName);
            EmitProperty(sb, plan.Declaration, declaringClassName: classDeclarationName, genericTypeParams: classGenericParams, typedefMap: typedefMap, blockTypedefMap: blockTypedefMap, emittedMemberNames: emittedMemberNames, emittedPropertyNames: emittedPropertyNames, knownTypes: knownTypes, appleSdkTypes: appleSdkTypes, localProtocolNames: localProtocolNames, classProtocolClashNames: classProtocolClashNames, logger: logger, diagnostics: diagnostics, emitNew: true, hidesInheritedInterfaceMember: true);
        }

        sb.AppendLine("    }");
        sb.AppendLine();
    }

    static void EmitCategory(StringBuilder sb, ObjCCategoryDecl cat, Dictionary<string, ObjCTypeRef> typedefMap, Dictionary<string, ObjCTypeRef> blockTypedefMap, HashSet<string> knownTypes, HashSet<string>? appleSdkTypes, HashSet<string>? enumNames, HashSet<string>? droppedClassNames, HashSet<string>? localProtocolNames, HashSet<string>? classProtocolClashNames, List<ObjCCategoryStaticForwarder> categoryStaticForwarders, ILogger logger, ObjCBindingDiagnostics? diagnostics)
    {
        // Skip categories whose base class was dropped for an unresolvable base type — a
        // [Category][BaseType(typeof(X))] on a removed class X would dangle (CS0246).
        if (droppedClassNames != null && droppedClassNames.Contains(cat.ClassName))
        {
            diagnostics?.RecordSkip("Category", $"{cat.ClassName}.{cat.CategoryName}", ObjCSkipReason.UnresolvableType, $"base class '{cat.ClassName}' was dropped (unresolvable base type)");
            return;
        }

        // MAUI bgen compiles [Category] interfaces into static extension classes.
        // Constraints: static classes cannot implement interfaces (CS0714) and
        // cannot have instance properties (CS0708). Only instance methods and
        // class (static) properties are valid members — an instance PROPERTY is
        // recovered as instance accessor METHODS instead (see the projection below),
        // which are legal members of a static extension class.
        // Filter out init methods — MAUI category interfaces cannot declare constructors.
        var emittableMethods = cat.Methods
            .Where(m => m.Selector != "init" && !m.Selector.StartsWith("initWith", StringComparison.Ordinal))
            .ToList();
        var emittableClassProperties = cat.Properties.Where(p => p.IsClass).ToList();
        var categoryGenericParams = cat.GenericTypeParamNames.Count > 0
            ? new HashSet<string>(cat.GenericTypeParamNames)
            : null;
        // One mapped spelling for the extended class, used by every reference below: the [BaseType]
        // target, the generated interface name, and `declaringClassName` (which is what `instancetype`
        // resolves to). Passing the raw ObjC name to any of them dangles once the acronym convention
        // renames the class declaration.
        var categoryClassName = ObjCTypeMapper.MapClassName(cat.ClassName);
        // Resolve the instance-property projection BEFORE anything is emitted: the accessors it
        // will actually produce decide both whether this category has any content at all and which
        // ObjC selectors it claims (which the method loop below must not export a second time).
        var instanceAccessorPlans = PlanCategoryInstancePropertyAccessors(cat, categoryClassName, categoryGenericParams, typedefMap, blockTypedefMap, knownTypes, appleSdkTypes, enumNames, localProtocolNames, classProtocolClashNames, logger, diagnostics);

        // Skip category entirely if it has no emittable content
        if (emittableMethods.Count == 0 && emittableClassProperties.Count == 0 && instanceAccessorPlans.Count == 0)
        {
            diagnostics?.RecordSkip("Category", $"{cat.ClassName}.{cat.CategoryName}", ObjCSkipReason.EmptyCategory,
                cat.ProtocolNames.Count > 0
                    ? $"protocol-only category ({string.Join(", ", cat.ProtocolNames)}) — static classes cannot implement interfaces"
                    : "no emittable members");
            return;
        }

        ObjCAvailabilityEmitter.EmitAvailabilityAttributes(sb, cat.Availability, "    ");
        sb.AppendLine("    [Category]");
        sb.AppendLine($"    [BaseType(typeof({categoryClassName}))]");

        // Strip protocol conformance — static classes cannot implement interfaces (CS0714)
        var interfaceName = GenerateCategoryInterfaceName(categoryClassName, cat.CategoryName);
        sb.AppendLine($"    partial interface {interfaceName}");
        sb.AppendLine("    {");

        // Every member emitted below is recorded here under the signature bgen will GENERATE for it
        // — which carries a receiver this declaration does not — so the receiver-free overloads
        // planned for the class members can be checked against it once the category is complete.
        var statics = new CategoryStaticsCollector(interfaceName, categoryClassName);

        var emittedMethodSignatures = new HashSet<string>();
        var emittedMemberNames = new HashSet<string>();
        var emittedPropertyNames = new HashSet<string>();

        // Categories emit class (static) properties AS properties; pre-seed their names + accessor
        // selectors before the method loop for the same method-vs-property collision reasons as
        // EmitClass.
        var emittableCategoryProperties = emittableClassProperties
            .Where(p => WouldEmitProperty(p, categoryClassName, categoryGenericParams, typedefMap, blockTypedefMap, localProtocolNames, classProtocolClashNames, knownTypes, appleSdkTypes))
            .ToList();
        foreach (var prop in emittableCategoryProperties)
            emittedPropertyNames.Add(ToPascalCase(prop.Name));
        var categoryAccessorSelectors = BuildPropertyAccessorSelectors(emittableCategoryProperties);

        // The projected instance accessors export the very selectors the instance property would
        // have exported, so they claim them exactly like a property does. Folding them into the
        // collision set BEFORE the method loop is what keeps a category that declares both a method
        // `-tintColor` and a property `tintColor` from exporting that selector twice — a duplicate
        // registration that aborts the .NET registrar at launch.
        foreach (var plan in instanceAccessorPlans)
        {
            categoryAccessorSelectors.Instance.Add(plan.GetterSelector);
            if (plan.SetterSelector != null)
                categoryAccessorSelectors.Instance.Add(plan.SetterSelector);
        }

        foreach (var method in emittableMethods)
        {
            if (CollidesWithPropertyAccessor(method, categoryAccessorSelectors))
            {
                logger.LogDebug("Skipping method {Selector} on category {Class}_{Category}: selector also exported by a property accessor", method.Selector, cat.ClassName, cat.CategoryName);
                diagnostics?.RecordSkip("Method", method.Selector, ObjCSkipReason.DuplicateSelector, $"selector also exported by a property accessor on category '{cat.ClassName}.{cat.CategoryName}' (kept the property)");
                continue;
            }
            var emittedName = EmitMethod(sb, method, declaringClassName: categoryClassName, isProtocol: false, genericTypeParams: categoryGenericParams, typedefMap: typedefMap, blockTypedefMap: blockTypedefMap, emittedMethodSignatures: emittedMethodSignatures, emittedPropertyNames: emittedPropertyNames, knownTypes: knownTypes, appleSdkTypes: appleSdkTypes, enumNames: enumNames, localProtocolNames: localProtocolNames, classProtocolClashNames: classProtocolClashNames, categoryStatics: statics, logger: logger, diagnostics: diagnostics);
            if (emittedName != null) emittedMemberNames.Add(emittedName);
        }

        // Only [Static] properties can be emitted AS properties — a static extension class cannot
        // carry instance members (CS0708).
        foreach (var prop in emittableClassProperties)
            EmitProperty(sb, prop, declaringClassName: categoryClassName, genericTypeParams: categoryGenericParams, typedefMap: typedefMap, blockTypedefMap: blockTypedefMap, emittedMemberNames: emittedMemberNames, emittedPropertyNames: emittedPropertyNames, knownTypes: knownTypes, appleSdkTypes: appleSdkTypes, localProtocolNames: localProtocolNames, classProtocolClashNames: classProtocolClashNames, logger: logger, diagnostics: diagnostics);

        // An instance property survives as instance accessor METHODS. bgen compiles a [Category]
        // interface's instance method into a static extension method carrying the receiver, so the
        // CS0708 constraint that bars an instance property does not bar its accessors — the ObjC
        // getter/setter selectors are the same ones the property would have exported, only reached
        // through Get{Name}()/Set{Name}(). Dropping them instead loses genuinely public API (a
        // boxing category whose class-method half emits while its unboxing half silently vanishes),
        // so the projection is the recovery and any accessor that cannot be projected soundly is
        // recorded as a skip rather than dropped in silence.
        foreach (var plan in instanceAccessorPlans)
            EmitCategoryInstancePropertyAccessors(sb, plan, categoryClassName, categoryGenericParams, typedefMap, blockTypedefMap, emittedMethodSignatures, emittedMemberNames, emittedPropertyNames, knownTypes, appleSdkTypes, enumNames, localProtocolNames, classProtocolClashNames, statics, logger, diagnostics);

        sb.AppendLine("    }");
        sb.AppendLine();

        statics.CollectInto(categoryStaticForwarders, cat, logger, diagnostics);
    }

    /// <summary>
    /// Per-category collection point for the receiver-free overloads of its class (<c>+</c>) members.
    ///
    /// bgen compiles a <c>[Category]</c> interface into a static extension class and prepends a
    /// receiver parameter to EVERY member it generates, <c>[Static]</c> included — so the class
    /// bgen produces holds <c>Name(X This, …)</c> where the declaration says <c>Name(…)</c>. The
    /// overloads planned here restore the declared shape by dropping the receiver, which means a
    /// planned overload can land on the signature of some OTHER member of the same class once that
    /// member's receiver is counted (an instance <c>-foo</c> generates <c>Foo(X)</c>; a class
    /// <c>+foo:</c> taking an <c>X</c> would want <c>Foo(X)</c> too). Recording each emitted
    /// member's GENERATED signature is what lets that be detected before it becomes a duplicate
    /// member in the consumer's build.
    /// </summary>
    sealed class CategoryStaticsCollector(string generatedClassName, string receiverType)
    {
        readonly HashSet<string> _generatedSignatures = new(StringComparer.Ordinal);
        readonly List<(string Key, string Selector, ObjCCategoryStaticForwarder Forwarder)> _candidates = [];

        public string GeneratedClassName { get; } = generatedClassName;
        public string ReceiverType { get; } = receiverType;

        /// <summary>Records the signature bgen will generate for an emitted member of this category.</summary>
        public void RecordGeneratedMember(string methodName, string paramTypes) =>
            _generatedSignatures.Add(BuildKey(methodName, paramTypes.Length == 0 ? ReceiverType : $"{ReceiverType},{paramTypes}"));

        public void AddCandidate(string methodName, string paramTypes, string selector, ObjCCategoryStaticForwarder forwarder) =>
            _candidates.Add((BuildKey(methodName, paramTypes), selector, forwarder));

        /// <summary>
        /// Moves every planned overload whose signature is free into <paramref name="forwarders"/>,
        /// recording a skip for one that is not. Runs once the category is fully emitted so the
        /// check sees every member, including the instance-property accessors emitted last.
        /// </summary>
        public void CollectInto(List<ObjCCategoryStaticForwarder> forwarders, ObjCCategoryDecl cat, ILogger logger, ObjCBindingDiagnostics? diagnostics)
        {
            foreach (var (key, selector, forwarder) in _candidates)
            {
                if (_generatedSignatures.Contains(key))
                {
                    logger.LogDebug("Skipping receiver-free overload {Key} on {Class}: another member of the generated category class already has that signature", key, GeneratedClassName);
                    diagnostics?.RecordSkip("Method", selector, ObjCSkipReason.DuplicateSignature,
                        $"receiver-free overload '{key}' on '{cat.ClassName}.{cat.CategoryName}' would duplicate another generated member's signature (the class method is still reachable through the receiver-carrying overload)");
                    continue;
                }
                forwarders.Add(forwarder);
            }
        }

        static string BuildKey(string methodName, string paramTypes) => $"{methodName}({paramTypes})";
    }

    /// <summary>
    /// One category instance property resolved to the accessor methods it will actually be emitted
    /// as, together with the ObjC selectors those accessors claim. <c>SetterSelector</c> is null for
    /// a read-only property and for a read-write property whose setter could not be projected.
    /// </summary>
    readonly record struct CategoryAccessorPlan(ObjCPropertyDecl Property, string PropName, string GetterSelector, string? SetterSelector);

    /// <summary>
    /// Decides, for every INSTANCE property on a category, which accessor methods are soundly
    /// projectable — and records a skip for each half that is not. Runs as a pre-pass because the
    /// answer feeds two decisions made before any member is written: whether the category has any
    /// content at all, and which ObjC selectors it claims (a method exporting a claimed selector
    /// must be dropped, exactly as on the class path).
    /// </summary>
    static List<CategoryAccessorPlan> PlanCategoryInstancePropertyAccessors(ObjCCategoryDecl cat, string categoryClassName, HashSet<string>? categoryGenericParams, Dictionary<string, ObjCTypeRef> typedefMap, Dictionary<string, ObjCTypeRef> blockTypedefMap, HashSet<string> knownTypes, HashSet<string>? appleSdkTypes, HashSet<string>? enumNames, HashSet<string>? localProtocolNames, HashSet<string>? classProtocolClashNames, ILogger logger, ObjCBindingDiagnostics? diagnostics)
    {
        var plans = new List<CategoryAccessorPlan>();
        var categoryLabel = $"{cat.ClassName}.{cat.CategoryName}";

        foreach (var prop in cat.Properties)
        {
            if (prop.IsClass)
                continue;

            var propName = ToPascalCase(prop.Name);

            // Same resolvability gate EmitProperty applies, asked here so the skip record names the
            // PROPERTY the consumer was looking for rather than the synthesized accessor selector.
            var synthesized = new HashSet<string>(StringComparer.Ordinal);
            var mappedType = ObjCTypeMapper.MapType(prop.Type, categoryClassName, categoryGenericParams, typedefMap, blockTypedefMap, localProtocolNames, classProtocolClashNames, synthesized);
            if (!ObjCTypeMapper.IsApiDefinitionTypeResolvable(mappedType, knownTypes, appleSdkTypes, synthesized))
            {
                logger.LogDebug("Skipping category instance property {PropName} on {Category}: unresolvable type '{TypeName}'", propName, categoryLabel, mappedType);
                diagnostics?.RecordSkip("Property", propName, ObjCSkipReason.UnresolvableType, $"unresolvable type '{mappedType}' on category '{categoryLabel}'");
                continue;
            }

            string? setterSelector = null;
            if (!prop.IsReadonly)
            {
                // A setter parameter that would project as `out T` (the value-type-pointer path) or
                // as the NSError out-parameter cannot carry the value INTO the call — C# assigns
                // `default` to an `out` argument before the callee runs. There is no sound
                // projection for such a setter, so it drops with a record while the getter emits.
                if (ObjCTypeMapper.IsNSErrorOutParameter(prop.Type)
                    || ObjCTypeMapper.IsValueTypePointerParameter(prop.Type, typedefMap, enumNames))
                {
                    logger.LogDebug("Skipping setter for category instance property {PropName} on {Category}: pointer-typed setter value projects as an out-parameter", propName, categoryLabel);
                    diagnostics?.RecordSkip("Property", $"set{propName}", ObjCSkipReason.UnsupportedConstruct, $"setter for category instance property '{propName}' on '{categoryLabel}' takes a pointer value that projects as an out-parameter (kept the getter)");
                }
                else
                {
                    setterSelector = prop.SetterSelector ?? $"set{propName}:";
                }
            }

            plans.Add(new CategoryAccessorPlan(prop, propName, prop.GetterSelector ?? prop.Name, setterSelector));
        }

        return plans;
    }

    /// <summary>
    /// Emits a planned category instance property as instance accessor methods — a getter
    /// <c>Get{Name}()</c> exporting the property's getter selector, plus, when the plan kept it, a
    /// setter <c>Set{Name}(value)</c> exporting its setter selector. Both are ordinary instance
    /// methods, which a bgen static extension class accepts; the property form would not compile
    /// (CS0708).
    ///
    /// Both exports carry the property's declared memory semantic. The projection changes the C#
    /// SHAPE the accessors take, and nothing about the ObjC declaration behind them: the setter
    /// still hands its argument to a selector the header declared <c>copy</c> (or <c>weak</c>, or
    /// <c>assign</c>), so the attribute that states which of those it is belongs on the emitted
    /// export exactly as it would on the property this projection replaces.
    /// </summary>
    static void EmitCategoryInstancePropertyAccessors(StringBuilder sb, CategoryAccessorPlan plan, string categoryClassName, HashSet<string>? categoryGenericParams, Dictionary<string, ObjCTypeRef> typedefMap, Dictionary<string, ObjCTypeRef> blockTypedefMap, HashSet<string> emittedMethodSignatures, HashSet<string> emittedMemberNames, HashSet<string> emittedPropertyNames, HashSet<string> knownTypes, HashSet<string>? appleSdkTypes, HashSet<string>? enumNames, HashSet<string>? localProtocolNames, HashSet<string>? classProtocolClashNames, CategoryStaticsCollector statics, ILogger logger, ObjCBindingDiagnostics? diagnostics)
    {
        var prop = plan.Property;
        var argSemantic = FormatArgumentSemantic(prop.MemorySemantic);

        var getter = new ObjCMethodDecl
        {
            Selector = plan.GetterSelector,
            ReturnType = prop.Type,
            IsInstanceMethod = true,
            DocComment = prop.DocComment,
            Availability = prop.Availability,
        };
        var getterName = EmitMethod(sb, getter, declaringClassName: categoryClassName, isProtocol: false, genericTypeParams: categoryGenericParams, typedefMap: typedefMap, blockTypedefMap: blockTypedefMap, emittedMethodSignatures: emittedMethodSignatures, emittedPropertyNames: emittedPropertyNames, knownTypes: knownTypes, appleSdkTypes: appleSdkTypes, enumNames: enumNames, localProtocolNames: localProtocolNames, classProtocolClashNames: classProtocolClashNames, categoryStatics: statics, logger: logger, diagnostics: diagnostics, nameOverride: $"Get{plan.PropName}", exportArgumentSemantic: argSemantic);
        if (getterName != null)
            emittedMemberNames.Add(getterName);

        if (plan.SetterSelector == null)
            return;

        var setter = new ObjCMethodDecl
        {
            Selector = plan.SetterSelector,
            ReturnType = new ObjCTypeRef { Name = "void" },
            IsInstanceMethod = true,
            Parameters = [new ObjCParameterDecl { Name = "value", Type = prop.Type }],
            Availability = prop.Availability,
        };
        var setterName = EmitMethod(sb, setter, declaringClassName: categoryClassName, isProtocol: false, genericTypeParams: categoryGenericParams, typedefMap: typedefMap, blockTypedefMap: blockTypedefMap, emittedMethodSignatures: emittedMethodSignatures, emittedPropertyNames: emittedPropertyNames, knownTypes: knownTypes, appleSdkTypes: appleSdkTypes, enumNames: enumNames, localProtocolNames: localProtocolNames, classProtocolClashNames: classProtocolClashNames, categoryStatics: statics, logger: logger, diagnostics: diagnostics, nameOverride: $"Set{plan.PropName}", exportArgumentSemantic: argSemantic);
        if (setterName != null)
            emittedMemberNames.Add(setterName);
    }

    internal static string GenerateCategoryInterfaceName(string className, string categoryName)
    {
        return string.IsNullOrEmpty(categoryName)
            ? $"{className}_Extensions"
            : $"{className}_{categoryName}";
    }

    /// <summary>
    /// Emits a method and returns the final emitted C# method name (after any dedup renaming),
    /// or null for constructors. Callers use this to track method-property name collisions.
    /// <paramref name="nameOverride"/> replaces the selector-derived starting name (used by the
    /// category instance-property projection, whose members are named after the property rather
    /// than the accessor selector); dedup still applies on top of it.
    /// <paramref name="exportArgumentSemantic"/> is appended inside the <c>[Export]</c> — a
    /// pre-formatted <see cref="FormatArgumentSemantic"/> result, used by that same projection to
    /// keep the property's declared memory semantic on the accessors that replace it.
    /// <paramref name="categoryStatics"/> collects the receiver-free overloads planned for a
    /// category's class members, and every emitted member's generated signature alongside them.
    /// </summary>
    static string? EmitMethod(StringBuilder sb, ObjCMethodDecl method, string? declaringClassName, bool isProtocol, HashSet<string>? genericTypeParams, Dictionary<string, ObjCTypeRef>? typedefMap = null, Dictionary<string, ObjCTypeRef>? blockTypedefMap = null, HashSet<string>? emittedConstructorSignatures = null, HashSet<string>? emittedMethodSignatures = null, HashSet<string>? emittedPropertyNames = null, HashSet<string>? knownTypes = null, HashSet<string>? appleSdkTypes = null, HashSet<string>? enumNames = null, bool isDelegateProtocol = false, string? delegateProtocolName = null, HashSet<string>? localProtocolNames = null, HashSet<string>? classProtocolClashNames = null, List<ObjCArrayOverload>? arrayOverloads = null, CategoryStaticsCollector? categoryStatics = null, ILogger? logger = null, ObjCBindingDiagnostics? diagnostics = null, string? nameOverride = null, string exportArgumentSemantic = "")
    {
        // Pre-check: skip methods with types not resolvable in ApiDefinition context.
        //
        // This mapping must be given the SAME inputs the emission below uses, or the gate answers a
        // question about a different string than the one that ships. `localProtocolNames` is the one
        // that bites: it is what routes a member typed by the bare name of an own protocol to that
        // protocol's interface (MapType step 10b). Omit it and the mapping falls through to the
        // plain .NET-acronym fallback instead — a different spelling, checked against a set that
        // never contained it, so the method is dropped with only a debug log while the property
        // mirror (which does pass the set) emits the same type fine.
        if (knownTypes != null)
        {
            var returnSynthesized = new HashSet<string>(StringComparer.Ordinal);
            var checkReturn = ObjCTypeMapper.MapType(method.ReturnType, declaringClassName, genericTypeParams, typedefMap, blockTypedefMap, localProtocolNames, classProtocolClashNames, returnSynthesized);
            if (!ObjCTypeMapper.IsApiDefinitionTypeResolvable(checkReturn, knownTypes, appleSdkTypes, returnSynthesized))
            {
                logger?.LogDebug("Skipping method {Selector}: unresolvable return type '{TypeName}'", method.Selector, checkReturn);
                diagnostics?.RecordSkip("Method", method.Selector, ObjCSkipReason.UnresolvableType, $"unresolvable return type '{checkReturn}'");
                return null;
            }
            foreach (var param in method.Parameters)
            {
                var paramSynthesized = new HashSet<string>(StringComparer.Ordinal);
                var checkParam = ObjCTypeMapper.MapType(param.Type, genericTypeParams: genericTypeParams, typedefMap: typedefMap, blockTypedefMap: blockTypedefMap, localProtocolNames: localProtocolNames, classProtocolClashNames: classProtocolClashNames, synthesizedProtocolInterfaces: paramSynthesized);
                if (!ObjCTypeMapper.IsApiDefinitionTypeResolvable(checkParam, knownTypes, appleSdkTypes, paramSynthesized))
                {
                    logger?.LogDebug("Skipping method {Selector}: unresolvable param type '{TypeName}'", method.Selector, checkParam);
                    diagnostics?.RecordSkip("Method", method.Selector, ObjCSkipReason.UnresolvableType, $"unresolvable param type '{checkParam}'");
                    return null;
                }
            }
        }

        var isConstructor = !isProtocol && (method.Selector == "init" || method.Selector.StartsWith("initWith", StringComparison.Ordinal));

        // Decide how this method's value-type pointer parameters project BEFORE emitting any text,
        // so a member that turns out to have no sound projection leaves nothing behind.
        //
        // A pointer to a value type is structurally identical whether it addresses one value or the
        // first element of an array, and the two want opposite projections. The `count:` keyword is
        // the signal that separates them: with it the pointer + count pair becomes a single C# array
        // parameter (an `[Internal]` pointer+count member here plus a pinning overload in the
        // generated partial class); without it the pointer addresses one value and a MUTABLE pointee
        // is a legitimate `out T`.
        //
        // Two pointees are neither. A CONST pointee is read-only by construction, so it can never be
        // an `out` — C# `out` zeroes the caller's storage before the call, silently destroying the
        // very data the callee was handed to read. And a pointee the selector itself declares to be a
        // RUN of values, by naming a `count:` right after it, needs storage for `count` elements
        // where `out T` supplies exactly one — the callee then reads or writes past the end of it.
        // Neither has a sound single-value signature, so when no array overload can be built for it
        // the member drops with a recorded skip rather than shipping as a callable that corrupts its
        // own arguments. A mutable pointer with no count sibling is untouched: that one really does
        // address a single value, and `out T` is the right projection for it.
        //
        // The array projection needs somewhere to hang the overload: a protocol has no implementation
        // to extend, and a constructor cannot be forwarded from a partial-class member. In those
        // contexts, and wherever the pair itself cannot be planned, the refusal below is what fails
        // closed.
        //
        // A category has no partial CLASS to extend — bgen compiles it to a static extension class —
        // so its array overload rides the receiver-free forwarder instead, which is already an extra
        // partial part of that same static class. That covers a category's CLASS (+) members only:
        // an instance member of a category has no forwarder to carry it (its receiver is the point),
        // and the static class cannot hold the instance overload one would need, so an instance
        // member with an array pair still fails closed below.
        var canProjectArrayViaForwarder = categoryStatics != null && !method.IsInstanceMethod;
        var canProjectArray = (arrayOverloads != null || canProjectArrayViaForwarder)
            && declaringClassName != null && !isProtocol && !isConstructor;
        var arrayPlan = canProjectArray
            ? ObjCArrayParameterProjection.TryPlan(method, genericTypeParams, typedefMap, blockTypedefMap, enumNames, localProtocolNames, classProtocolClashNames)
            : null;

        for (var i = 0; i < method.Parameters.Count; i++)
        {
            if (arrayPlan != null && i == arrayPlan.PointerParameterIndex)
                continue;
            var param = method.Parameters[i];
            if (!ObjCTypeMapper.IsValueTypePointerShape(param.Type, typedefMap, enumNames))
                continue;

            var isArrayShaped = ObjCArrayParameterProjection.IsArrayShapedPointerParameter(method, i, typedefMap, enumNames);
            if (!isArrayShaped && !ObjCTypeMapper.IsConstValueTypePointerParameter(param.Type, typedefMap, enumNames))
                continue;

            var detail = DescribeUnprojectablePointer(param, isArrayShaped, canProjectArray);
            logger?.LogDebug("Skipping method {Selector}: {Detail}", method.Selector, detail);
            diagnostics?.RecordSkip("Method", method.Selector, ObjCSkipReason.UnsupportedConstruct, detail);
            return null;
        }

        ObjCDocCommentEmitter.EmitDocComment(sb, method.DocComment, method.DocParams, "        ");
        ObjCAvailabilityEmitter.EmitAvailabilityAttributes(sb, method.Availability, "        ");

        // Duplicate constructor detection: if the parameter signature has already been emitted,
        // emit this one as a named instance method instead
        if (isConstructor && emittedConstructorSignatures != null)
        {
            var paramSignature = string.Join(",", method.Parameters.Select(p => ObjCTypeMapper.MapType(p.Type, genericTypeParams: genericTypeParams, typedefMap: typedefMap, blockTypedefMap: blockTypedefMap)));
            if (!emittedConstructorSignatures.Add(paramSignature))
                isConstructor = false; // Duplicate — emit as named method
        }

        if (isProtocol && !method.IsOptional)
            sb.AppendLine("        [Abstract]");

        // An array-projected member is the raw pointer+count half of the pair: consumers call the
        // array overload in the generated partial class, never this one.
        if (method.IsVariadic || arrayPlan != null)
            sb.AppendLine("        [Internal]");

        if (!method.IsInstanceMethod && !isConstructor)
            sb.AppendLine("        [Static]");

        if (isConstructor && method.IsDesignatedInitializer)
            sb.AppendLine("        [DesignatedInitializer]");

        if (method.IsVariadic)
            sb.AppendLine($"        [Export(\"{method.Selector}\"{exportArgumentSemantic}, IsVariadic = true)]");
        else
            sb.AppendLine($"        [Export(\"{method.Selector}\"{exportArgumentSemantic})]");

        var returnType = isConstructor
            ? "NativeHandle"
            : ObjCTypeMapper.MapType(method.ReturnType, declaringClassName, genericTypeParams, typedefMap, blockTypedefMap, localProtocolNames, classProtocolClashNames);

        if (!isConstructor && ObjCTypeMapper.IsNullableAttribute(method.ReturnType))
            sb.AppendLine("        [return: NullAllowed]");

        var methodName = isConstructor
            ? "Constructor"
            : nameOverride
              ?? (isDelegateProtocol ? SelectorToDelegateMethodName(method.Selector, delegateProtocolName) : SelectorToMethodName(method.Selector));

        // Duplicate method signature detection: rename with full selector parts if collision.
        // Also rename if the short name collides with an already-emitted PROPERTY name (CS0102) —
        // bgen flattens ancestor protocols into the concrete class, so a child method named `Foo`
        // colliding with an ancestor property `Foo` produces CS0102. Method-vs-method same-name
        // collisions with different signatures are legal C# overloads and intentionally not
        // blocked here (only identical signatures collide via emittedMethodSignatures).
        if (!isConstructor && emittedMethodSignatures != null)
        {
            // For an array-projected member the name that has to survive dedup is the PUBLIC
            // overload's, so the collision is computed against the signature consumers see
            // (`CGPoint[]`), not against the internal pointer+count one.
            methodName = ResolveMethodNameWithDedup(methodName, method, genericTypeParams, typedefMap, blockTypedefMap, emittedMethodSignatures, emittedPropertyNames,
                paramSignatureOverride: arrayPlan == null ? null : BuildProjectedParamSignature(method, arrayPlan));
        }

        // Emit generic type hints as remarks
        EmitGenericTypeHints(sb, method.ReturnType, method.Parameters, declaringClassName, genericTypeParams, typedefMap, blockTypedefMap);

        var parameters = EmitParameters(method.Parameters, genericTypeParams, typedefMap, blockTypedefMap, enumNames, localProtocolNames, classProtocolClashNames, arrayPlan);
        if (method.IsVariadic)
        {
            // Variadic methods get an IntPtr varArgs parameter for the variable arguments
            if (parameters.Length > 0)
                parameters += ", ";
            parameters += "IntPtr varArgs";
        }

        // The internal pointer+count member takes an underscored name so the public array overload
        // in the partial class can claim the natural one (the dotnet/macios convention for exactly
        // this split). Its own signature is registered too, so nothing else can land on it.
        var declaredName = arrayPlan == null ? methodName : $"_{methodName}";
        if (arrayPlan != null)
            emittedMethodSignatures?.Add($"{declaredName}({parameters})");

        sb.AppendLine($"        {returnType} {declaredName}({parameters});");
        sb.AppendLine();

        if (arrayPlan != null && arrayOverloads != null)
            arrayOverloads.Add(BuildArrayOverload(method, arrayPlan, declaringClassName!, methodName, declaredName, returnType));

        // A variadic member stays excluded from the forwarder: its trailing `IntPtr varArgs` is the
        // raw argument list, and no C# signature can shape that faithfully — an overload of it would
        // publish a member whose arguments a consumer has no sound way to build. An array-projected
        // member is different: its pointer+count pair HAS a faithful shape, and the forwarder builds
        // it, so it is no longer treated as internal here.
        if (categoryStatics != null && !isConstructor)
            RecordCategoryStaticForwarder(categoryStatics, method, declaredName, methodName, returnType, isInternalMember: method.IsVariadic, genericTypeParams, typedefMap, blockTypedefMap, enumNames, localProtocolNames, classProtocolClashNames, arrayPlan);

        return isConstructor ? null : methodName;
    }

    /// <summary>
    /// Records an emitted category member with the collector, and — when it is a class
    /// (<c>+</c>) member consumers are meant to call — plans the receiver-free overload for it.
    ///
    /// A VARIADIC member is deliberately passed over: bgen renders it <c>[Internal]</c> because its
    /// trailing raw argument list has no faithful C# shape, and an overload of it would publish a
    /// member a consumer has no sound way to call. An ARRAY-projected member is also <c>[Internal]</c>
    /// but for the opposite reason — its pointer+count pair has a faithful shape and something else
    /// is meant to publish it — so here the forwarder IS that something: it declares the array-shaped
    /// signature, pins, and calls the underscored member.
    /// </summary>
    static void RecordCategoryStaticForwarder(CategoryStaticsCollector statics, ObjCMethodDecl method, string declaredName, string methodName, string returnType, bool isInternalMember, HashSet<string>? genericTypeParams, Dictionary<string, ObjCTypeRef>? typedefMap, Dictionary<string, ObjCTypeRef>? blockTypedefMap, HashSet<string>? enumNames, HashSet<string>? localProtocolNames, HashSet<string>? classProtocolClashNames, ObjCArrayParameterPlan? arrayPlan)
    {
        var paramTypes = BuildCategorySignatureKeyTypes(method, genericTypeParams, typedefMap, blockTypedefMap, enumNames, localProtocolNames, classProtocolClashNames, arrayPlan);
        statics.RecordGeneratedMember(declaredName, paramTypes);

        if (method.IsInstanceMethod || isInternalMember)
            return;

        var arrayParamName = arrayPlan == null
            ? null
            : EscapeCSharpKeyword(method.Parameters[arrayPlan.PointerParameterIndex].Name);
        var signatureParts = new List<string>();
        var callArguments = new List<string>();
        for (var index = 0; index < method.Parameters.Count; index++)
        {
            var param = method.Parameters[index];
            var safeName = EscapeCSharpKeyword(param.Name);
            // The bgen attributes EmitParameters writes ([NullAllowed]) belong to the api-definition
            // contract, not to a plain C# member — the nullable annotation carries the same
            // statement here, and matches the signature bgen generated for what this forwards to.
            if (arrayPlan != null && index == arrayPlan.PointerParameterIndex)
            {
                // A nullable ObjC pointer stays nullable as an array: pinning null yields a null
                // pointer, which pairs with the zero count computed below.
                var nullSuffix = ObjCTypeMapper.IsNullableAttribute(param.Type) ? "?" : "";
                signatureParts.Add($"{arrayPlan.ElementType}[]{nullSuffix} {safeName}");
                callArguments.Add($"(IntPtr){ObjCArrayOverloadsEmitter.PinnedPointerName}");
            }
            else if (arrayPlan != null && index == arrayPlan.CountParameterIndex)
            {
                // `checked` because the declared count type may be narrower than the array length
                // (`uint8_t count` caps the run at 255): an unchecked narrowing conversion would wrap
                // a longer array to a small — or negative — count and quietly pass the callee a
                // length that does not describe the buffer it was given.
                callArguments.Add($"checked(({arrayPlan.CountType})({arrayParamName}?.Length ?? 0))");
            }
            else if (ObjCTypeMapper.IsNSErrorOutParameter(param.Type))
            {
                signatureParts.Add("out NSError error");
                callArguments.Add("out error");
            }
            else if (ObjCTypeMapper.IsValueTypePointerParameter(param.Type, typedefMap, enumNames))
            {
                var pointeeType = ObjCTypeMapper.MapValueTypePointerParameterType(param.Type, typedefMap);
                signatureParts.Add($"out {pointeeType} {safeName}");
                callArguments.Add($"out {safeName}");
            }
            else
            {
                var mappedType = ObjCTypeMapper.MapType(param.Type, genericTypeParams: genericTypeParams, typedefMap: typedefMap, blockTypedefMap: blockTypedefMap, localProtocolNames: localProtocolNames, classProtocolClashNames: classProtocolClashNames);
                signatureParts.Add($"{mappedType}{NullableSuffix(param.Type, mappedType)} {safeName}");
                callArguments.Add(safeName);
            }
        }

        // The overload's OWN signature is the array-shaped one when there is a plan, so that is what
        // the free-signature check has to be asked about — keying it on the pointer+count shape would
        // ask whether a signature it never declares is free.
        var candidateKey = arrayPlan == null
            ? paramTypes
            : BuildCategorySignatureKeyTypes(method, genericTypeParams, typedefMap, blockTypedefMap, enumNames, localProtocolNames, classProtocolClashNames, arrayPlan, publicArrayShape: true);

        statics.AddCandidate(methodName, candidateKey, method.Selector, new ObjCCategoryStaticForwarder
        {
            DeclaringClassName = statics.GeneratedClassName,
            MethodName = methodName,
            ReturnType = $"{returnType}{NullableSuffix(method.ReturnType, returnType)}",
            ReceiverType = statics.ReceiverType,
            SignatureParts = signatureParts,
            CallArguments = callArguments,
            // An array-projected selector is declared `[Internal]` under an underscored name, so the
            // forwarder is the only member publishing it and has to name that member explicitly —
            // there is no same-named receiver-carrying sibling for overload resolution to find.
            ForwardTargetName = arrayPlan == null ? null : declaredName,
            ArrayElementType = arrayPlan?.ElementType,
            ArrayParameterName = arrayParamName,
            Selector = method.Selector,
            // The overload is the member consumers actually call, so it has to carry the same
            // platform-availability annotations as the member it forwards to — without them the
            // platform analyzer sees an unconditionally-available API and stops warning about
            // calling a newer selector from an older deployment target.
            Availability = method.Availability,
        });
    }

    /// <summary>
    /// The <c>?</c> a nullable ObjC pointer needs in a plain C# signature. bgen renders the
    /// api-definition's <c>[NullAllowed]</c> as exactly this on the member being forwarded to, so
    /// stating it keeps the two signatures agreeing instead of trading a nullability warning at
    /// every call site.
    /// </summary>
    static string NullableSuffix(ObjCTypeRef typeRef, string mappedType) =>
        ObjCTypeMapper.IsNullableAttribute(typeRef) && !mappedType.EndsWith('?') ? "?" : "";

    /// <summary>
    /// The recorded-skip detail for a value-type pointer parameter that has no sound projection —
    /// says which of the two unsound shapes it is and why no array overload rescued it.
    /// </summary>
    static string DescribeUnprojectablePointer(ObjCParameterDecl param, bool isArrayShaped, bool canProjectArray)
    {
        var lead = $"parameter '{param.Name}' ('{param.Type.RawQualType}')";
        if (isArrayShaped)
        {
            var why = canProjectArray
                ? "no array overload could be built for this selector"
                : "an array overload cannot be generated for this declaration";
            return $"{lead} is a pointer to a run of value types — its 'count:' keyword supplies the element count, so a single out parameter would give the callee storage for one element of the run — and {why}";
        }

        var noCount = canProjectArray
            ? "no adjacent 'count:' keyword identifies it as an array"
            : "an array overload cannot be generated for this declaration";
        return $"{lead} is a const pointer to a value type — read-only, so it cannot be an out parameter, and {noCount}";
    }

    /// <summary>
    /// The parameter-type signature the PUBLIC array overload will have: the pointer parameter as an
    /// array, the count parameter gone, everything else unchanged. Dedup keys on this because the
    /// name being deduped is the overload's.
    /// </summary>
    static string BuildProjectedParamSignature(ObjCMethodDecl method, ObjCArrayParameterPlan plan)
    {
        var types = new List<string>();
        for (var i = 0; i < method.Parameters.Count; i++)
        {
            if (i == plan.CountParameterIndex)
                continue;
            types.Add(i == plan.PointerParameterIndex ? $"{plan.ElementType}[]" : plan.PassThroughTypes[i] ?? "");
        }
        return string.Join(",", types);
    }

    /// <summary>
    /// Builds the public array overload that forwards to the emitted <c>[Internal]</c> member: the
    /// pointer parameter becomes a C# array, the count is supplied from its length, and every other
    /// parameter passes straight through.
    /// </summary>
    static ObjCArrayOverload BuildArrayOverload(ObjCMethodDecl method, ObjCArrayParameterPlan plan, string declaringClassName, string publicName, string internalName, string returnType)
    {
        var signatureParts = new List<string>();
        var callArguments = new List<string>();
        var arrayParamName = EscapeCSharpKeyword(method.Parameters[plan.PointerParameterIndex].Name);

        for (var i = 0; i < method.Parameters.Count; i++)
        {
            var param = method.Parameters[i];
            var safeName = EscapeCSharpKeyword(param.Name);
            if (i == plan.PointerParameterIndex)
            {
                // A nullable ObjC pointer stays nullable as an array: pinning null yields a null
                // pointer, which pairs with the zero count computed below.
                var nullSuffix = ObjCTypeMapper.IsNullableAttribute(param.Type) ? "?" : "";
                signatureParts.Add($"{plan.ElementType}[]{nullSuffix} {safeName}");
                callArguments.Add($"(IntPtr){ObjCArrayOverloadsEmitter.PinnedPointerName}");
            }
            else if (i == plan.CountParameterIndex)
            {
                // `checked` because the declared count type may be narrower than the array length
                // (`uint8_t count` caps the run at 255): an unchecked narrowing conversion would
                // wrap a longer array to a small — or negative — count and quietly pass the callee a
                // length that does not describe the buffer it was given. Throwing is the honest
                // outcome; on a count at least as wide as `int` the conversion cannot overflow and
                // `checked` costs nothing.
                callArguments.Add($"checked(({plan.CountType})({arrayParamName}?.Length ?? 0))");
            }
            else
            {
                signatureParts.Add($"{plan.PassThroughTypes[i]} {safeName}");
                callArguments.Add(safeName);
            }
        }

        return new ObjCArrayOverload
        {
            DeclaringClassName = declaringClassName,
            PublicName = publicName,
            InternalName = internalName,
            ReturnType = returnType,
            IsStatic = !method.IsInstanceMethod,
            ElementType = plan.ElementType,
            SignatureParts = signatureParts,
            CallArguments = callArguments,
            ArrayParameterName = arrayParamName,
            Selector = method.Selector,
            // The overload is the member consumers actually call, so it has to carry the same
            // platform-availability annotations as the internal member it forwards to — without them
            // the platform analyzer sees an unconditionally-available API and stops warning about
            // calling a newer selector from an older deployment target.
            Availability = method.Availability,
        };
    }

    /// <summary>
    /// Emits one property. <paramref name="emitNew"/> marks it <c>[New]</c> so bgen adds the
    /// <c>new</c> keyword to the generated member — required whenever it deliberately hides a
    /// member of the generated base class, which is otherwise a CS0108 in every consumer build.
    /// <paramref name="hidesInheritedInterfaceMember"/> is the separate ApiDefinition-source-level
    /// case: the declaration hides a member of an interface THIS interface inherits (a conformed
    /// protocol's), so the C# <c>new</c> keyword goes on the declaration itself. Pass it only when
    /// the hidden member is really there — a spurious <c>new</c> is a CS0109.
    /// Returns whether a member was actually emitted.
    /// </summary>
    static bool EmitProperty(StringBuilder sb, ObjCPropertyDecl prop, string? declaringClassName, HashSet<string>? genericTypeParams, Dictionary<string, ObjCTypeRef>? typedefMap = null, Dictionary<string, ObjCTypeRef>? blockTypedefMap = null, HashSet<string>? emittedMemberNames = null, HashSet<string>? emittedPropertyNames = null, HashSet<string>? knownTypes = null, HashSet<string>? appleSdkTypes = null, HashSet<string>? localProtocolNames = null, HashSet<string>? classProtocolClashNames = null, ILogger? logger = null, ObjCBindingDiagnostics? diagnostics = null, bool emitNew = false, bool hidesInheritedInterfaceMember = false)
    {
        var propName = ToPascalCase(prop.Name);

        // Skip properties with types not resolvable in ApiDefinition context.
        // Check BEFORE dedup tracking so a skipped property doesn't reserve the name.
        if (knownTypes != null)
        {
            var synthesized = new HashSet<string>(StringComparer.Ordinal);
            var checkType = ObjCTypeMapper.MapType(prop.Type, declaringClassName, genericTypeParams, typedefMap, blockTypedefMap, localProtocolNames, classProtocolClashNames, synthesized);
            if (!ObjCTypeMapper.IsApiDefinitionTypeResolvable(checkType, knownTypes, appleSdkTypes, synthesized))
            {
                logger?.LogDebug("Skipping property {PropName}: unresolvable type '{TypeName}'", propName, checkType);
                diagnostics?.RecordSkip("Property", propName, ObjCSkipReason.UnresolvableType, $"unresolvable type '{checkType}'");
                return false;
            }
        }

        // Drop if any prior member (method or property) already claimed this name — properties
        // can't be renamed (CS0102 in bgen-flattened output otherwise).
        if (emittedMemberNames != null && !emittedMemberNames.Add(propName))
            return false;
        emittedPropertyNames?.Add(propName);

        ObjCDocCommentEmitter.EmitDocComment(sb, prop.DocComment, null, "        ");
        ObjCAvailabilityEmitter.EmitAvailabilityAttributes(sb, prop.Availability, "        ");

        if (!prop.IsOptional)
        {
            // Only emit [Abstract] for protocol properties (no declaringClassName)
            // Actually, IsOptional is only set on protocol members, so we need to check context.
            // For protocol properties that are required (not optional), emit [Abstract].
            // We use declaringClassName == null as the protocol indicator.
            if (declaringClassName == null)
                sb.AppendLine("        [Abstract]");
        }

        if (prop.IsClass)
            sb.AppendLine("        [Static]");

        // Deliberate shadowing of a generated-base member: bgen renders `new` on the member.
        if (emitNew)
            sb.AppendLine("        [New]");

        var getterSelector = prop.GetterSelector ?? prop.Name;
        var argSemantic = FormatArgumentSemantic(prop.MemorySemantic);
        sb.AppendLine($"        [Export(\"{getterSelector}\"{argSemantic})]");

        if (ObjCTypeMapper.IsNullableAttribute(prop.Type))
            sb.AppendLine("        [NullAllowed]");

        var propGenericHint = ObjCTypeMapper.FormatGenericTypeHint(prop.Type, declaringClassName, genericTypeParams, typedefMap, blockTypedefMap);
        if (propGenericHint != null)
            sb.AppendLine($"        // {propGenericHint}");

        var mappedType = ObjCTypeMapper.MapType(prop.Type, declaringClassName, genericTypeParams, typedefMap, blockTypedefMap, localProtocolNames, classProtocolClashNames);

        // Emit [Bind] when getter selector differs from property name (e.g., isAutoInitEnabled vs autoInitEnabled)
        var hasCustomGetter = prop.GetterSelector != null && prop.GetterSelector != prop.Name;

        var newKeyword = hidesInheritedInterfaceMember ? "new " : "";

        if (prop.IsReadonly)
        {
            if (hasCustomGetter)
            {
                sb.AppendLine($"        {newKeyword}{mappedType} {ToPascalCase(prop.Name)} {{");
                sb.AppendLine($"            [Bind(\"{prop.GetterSelector}\")] get;");
                sb.AppendLine($"        }}");
            }
            else
            {
                sb.AppendLine($"        {newKeyword}{mappedType} {ToPascalCase(prop.Name)} {{ get; }}");
            }
        }
        else
        {
            // Emit setter with custom selector if present
            var setterSelector = prop.SetterSelector ?? $"set{ToPascalCase(prop.Name)}:";
            sb.AppendLine($"        {newKeyword}{mappedType} {ToPascalCase(prop.Name)} {{");
            if (hasCustomGetter)
                sb.AppendLine($"            [Bind(\"{prop.GetterSelector}\")] get;");
            else
                sb.AppendLine($"            get;");
            sb.AppendLine($"            [Export(\"{setterSelector}\")] set;");
            sb.AppendLine($"        }}");
        }
        sb.AppendLine();
        return true;
    }

    /// <summary>
    /// One property member the superclass chain emits, as seen from a subclass: which ancestor
    /// declared it, the C# type it is emitted with, whether it is read-only there, and the ObjC
    /// accessor selectors it exports (<see cref="SetterSelector"/> is null when it is read-only).
    /// <see cref="Declaration"/> is the ancestor declaration itself, kept so a subclass that has to
    /// re-declare the member can re-emit exactly what the ancestor emits.
    /// </summary>
    readonly record struct InheritedProperty(string OwnerClassName, string MappedType, bool IsReadonly, string GetterSelector, string? SetterSelector, ObjCPropertyDecl Declaration);

    /// <summary>
    /// The inherited members carrying ONE emitted C# name. ObjC keeps class and instance members in
    /// separate dispatch namespaces (`+foo` and `-foo` are different selectors, and bgen emits them
    /// as a static and an instance member), so both can be inherited under the same name and each
    /// must be classified against its own kind. C# hiding, by contrast, is name-based — a
    /// re-declaration of EITHER kind needs <c>new</c>.
    /// </summary>
    readonly record struct InheritedMembers(InheritedProperty? Instance, InheritedProperty? ClassMember)
    {
        public InheritedProperty? OfKind(bool isClass) => isClass ? ClassMember : Instance;

        public InheritedMembers With(InheritedProperty member, bool isClass)
            => isClass ? this with { ClassMember = member } : this with { Instance = member };
    }

    /// <summary>What a re-declaration of an inherited property member should do.</summary>
    enum RedeclarationDisposition
    {
        /// <summary>Emit nothing — the inherited member already covers this declaration.</summary>
        Defer,

        /// <summary>Emit, marked <c>[New]</c>, deliberately hiding the inherited member.</summary>
        HideWithNew,
    }

    /// <summary>
    /// Decides whether a re-declaration of a same-kind member the superclass chain already emits
    /// should defer to the inherited member or hide it. Widest surface wins: a re-declaration that
    /// offers nothing the inherited member doesn't already have — same C# type, same ObjC accessor
    /// selectors, no extra accessor — is pure shadowing (CS0108 with no gain) and defers, keeping
    /// the inherited member directly reachable. Anything else is a real difference and hides
    /// deliberately: notably a re-declaration that renames an accessor exports a DIFFERENT selector,
    /// so the inherited member cannot stand in for it and deferring would delete reachable API.
    /// </summary>
    static RedeclarationDisposition ClassifyRedeclaration(in InheritedProperty inherited, string mappedType, bool isReadonly, string getterSelector, string? setterSelector)
    {
        if (!string.Equals(mappedType, inherited.MappedType, StringComparison.Ordinal)
            || !string.Equals(getterSelector, inherited.GetterSelector, StringComparison.Ordinal))
            return RedeclarationDisposition.HideWithNew;

        // Wider than the inherited member (it adds a setter the ancestor lacks) — must emit.
        var widestIsReadonly = isReadonly && inherited.IsReadonly;
        if (widestIsReadonly != inherited.IsReadonly)
            return RedeclarationDisposition.HideWithNew;

        // Both read-write: a renamed setter is again a distinct selector the inherited member does
        // not export.
        return setterSelector != null && !string.Equals(setterSelector, inherited.SetterSelector, StringComparison.Ordinal)
            ? RedeclarationDisposition.HideWithNew
            : RedeclarationDisposition.Defer;
    }

    /// <summary>
    /// One inherited property member a class has to re-declare EXPLICITLY, carrying the ancestor's
    /// full accessor set, because a protocol the class itself conforms to states the same selector
    /// more narrowly. <see cref="Declaration"/> is the ancestor's declaration, re-emitted verbatim
    /// (marked <c>[New]</c>) on the subclass.
    /// </summary>
    readonly record struct ProtocolNarrowedProperty(string PropName, bool IsClass, ObjCPropertyDecl Declaration, string OwnerClassName, string ProtocolName);

    /// <summary>
    /// Finds the inherited property members a conformed protocol would NARROW on this class. bgen
    /// inlines the members of every protocol in a class's own conformance list (and their
    /// transitive protocol parents) into the generated class, so when such a protocol restates an
    /// ancestor's read-write property as read-only, that inlined read-only member hides the
    /// inherited setter: assigning through a subclass-typed variable stops compiling (CS0200) even
    /// though the ObjC object implements the setter. Declaring the member explicitly on the class
    /// pre-empts the inline, so each hit here is re-emitted with the ancestor's accessor set.
    ///
    /// Deliberately narrow, since each condition is what makes the re-declaration SOUND: the
    /// protocol view must be read-only over a read-write ancestor (otherwise nothing narrows), on
    /// the same ObjC getter selector and the same bound C# type (otherwise the ancestor's accessors
    /// do not implement what the protocol asks for, and re-exporting its setter selector would send
    /// a message the implementation cannot decode). Delegate properties are excluded: they emit as a
    /// [Wrap]/weak PAIR whose accessor set is not the declaration's own.
    ///
    /// The ancestor's declaration is re-emitted in the SUBCLASS's declaration context, so it is only
    /// planned when it maps to the same C# type there: a type written against the ancestor —
    /// <c>instancetype</c>, or an ObjC lightweight-generic parameter the subclass does not carry —
    /// means something else (or nothing) here, and the re-declaration would either change the bound
    /// type or be dropped as unresolvable, leaving the class with no declaration at all.
    /// </summary>
    static List<ProtocolNarrowedProperty> PlanProtocolNarrowedRedeclarations(
        IEnumerable<string> conformedProtocolNames,
        Dictionary<string, ObjCProtocolDecl>? protocolsByName,
        IReadOnlyDictionary<string, InheritedMembers> inheritedProperties,
        string emittingClassDeclName, HashSet<string>? emittingClassGenericParams,
        Dictionary<string, ObjCTypeRef> typedefMap, Dictionary<string, ObjCTypeRef> blockTypedefMap,
        HashSet<string> knownTypes, HashSet<string>? appleSdkTypes,
        HashSet<string>? localProtocolNames, HashSet<string>? classProtocolClashNames,
        HashSet<string>? delegateProtocolNames)
    {
        var plans = new List<ProtocolNarrowedProperty>();
        if (protocolsByName == null || inheritedProperties.Count == 0)
            return plans;

        var planned = new HashSet<(string Name, bool IsClass)>();
        var visited = new HashSet<string>(StringComparer.Ordinal);

        void Walk(string protoName)
        {
            if (!visited.Add(protoName))
                return;
            // External/SDK protocol — its members aren't visible here, so what bgen would inline
            // from it can't be enumerated.
            if (!protocolsByName.TryGetValue(protoName, out var proto))
                return;

            foreach (var prop in proto.Properties)
            {
                // Only a read-only protocol view can narrow; a read-write one carries the setter.
                if (!prop.IsReadonly)
                    continue;
                // Same gate the protocol's own emission applies — an unemittable member is never
                // inlined, so it cannot narrow anything.
                if (!WouldEmitProperty(prop, null, null, typedefMap, blockTypedefMap, localProtocolNames, classProtocolClashNames, knownTypes, appleSdkTypes))
                    continue;

                var propName = ToPascalCase(prop.Name);
                if (!inheritedProperties.TryGetValue(propName, out var inheritedMembers)
                    || inheritedMembers.OfKind(prop.IsClass) is not { } inherited
                    || inherited.IsReadonly)
                    continue;
                if (!string.Equals(prop.GetterSelector ?? prop.Name, inherited.GetterSelector, StringComparison.Ordinal))
                    continue;
                var protocolType = ObjCTypeMapper.MapType(prop.Type, declaringClassName: null, genericTypeParams: null, typedefMap, blockTypedefMap, localProtocolNames, classProtocolClashNames);
                if (!string.Equals(protocolType, inherited.MappedType, StringComparison.Ordinal))
                    continue;
                if (IsDelegateProperty(inherited.Declaration, delegateProtocolNames))
                    continue;
                // What the ancestor's declaration means in THIS class's context — the context the
                // re-declaration is emitted in.
                var reEmittedType = ObjCTypeMapper.MapType(inherited.Declaration.Type, emittingClassDeclName, emittingClassGenericParams, typedefMap, blockTypedefMap, localProtocolNames, classProtocolClashNames);
                if (!string.Equals(reEmittedType, inherited.MappedType, StringComparison.Ordinal))
                    continue;

                if (planned.Add((propName, prop.IsClass)))
                    plans.Add(new ProtocolNarrowedProperty(propName, prop.IsClass, inherited.Declaration, inherited.OwnerClassName, protoName));
            }

            foreach (var parent in proto.InheritedProtocolNames)
                Walk(parent);
        }

        foreach (var name in conformedProtocolNames)
            Walk(name);
        return plans;
    }

    /// <summary>
    /// The ObjC accessor selectors a property declaration exports, matching what
    /// <see cref="EmitProperty"/> and <see cref="EmitWeakDelegatePattern"/> actually emit. The
    /// setter is null for a read-only declaration.
    /// </summary>
    static (string Getter, string? Setter) PropertyAccessorSelectors(ObjCPropertyDecl prop)
        => (prop.GetterSelector ?? prop.Name,
            prop.IsReadonly ? null : prop.SetterSelector ?? $"set{ToPascalCase(prop.Name)}:");

    /// <summary>
    /// Collects the property members the RESOLVED superclass chain of <paramref name="cls"/> emits,
    /// keyed by emitted C# name. Walks only superclasses declared in this module: a foreign/SDK base
    /// has no visible members here, so the walk stops there and the caller degrades to the un-seeded
    /// behaviour rather than guessing. Mirrors what each ancestor actually emits — the same
    /// resolvability gate, the delegate WeakDelegate/Wrap pair, the first-name-wins drop for two
    /// properties that project to one C# name, and (by walking ROOT-first and re-applying
    /// <see cref="ClassifyRedeclaration"/> at each level) that ancestor's own re-declaration
    /// decisions. Replaying those decisions is what keeps the map describing the member a subclass
    /// would really inherit: a middle class that deferred emits nothing, so the name must keep
    /// resolving to the ancestor it deferred to, not to the middle declaration. An ancestor METHOD
    /// can't take a property's name away from it (method dedup renames off an already-seeded
    /// property name), so only properties are replayed.
    /// </summary>
    static Dictionary<string, InheritedMembers> BuildInheritedClassPropertyMap(ObjCClassDecl cls, IReadOnlyDictionary<string, ObjCClassDecl>? classesByName, Dictionary<string, ObjCTypeRef> typedefMap, Dictionary<string, ObjCTypeRef> blockTypedefMap, HashSet<string> knownTypes, HashSet<string>? appleSdkTypes, HashSet<string>? localProtocolNames, HashSet<string>? classProtocolClashNames, HashSet<string>? delegateProtocolNames)
    {
        var map = new Dictionary<string, InheritedMembers>(StringComparer.Ordinal);
        if (classesByName == null)
            return map;

        // Collect the chain nearest-first, then replay it root-first so each ancestor is classified
        // against what ITS ancestors emit, exactly as EmitClass classifies `cls`.
        var chain = new List<ObjCClassDecl>();
        var visited = new HashSet<string>(StringComparer.Ordinal) { cls.Name };
        var superName = cls.SuperclassName;
        while (superName != null && visited.Add(superName) && classesByName.TryGetValue(superName, out var ancestor))
        {
            chain.Add(ancestor);
            superName = ancestor.SuperclassName;
        }
        chain.Reverse();

        foreach (var ancestor in chain)
        {
            var ancestorDeclName = ObjCTypeMapper.MapClassName(ancestor.Name);
            var ancestorGenericParams = ancestor.GenericTypeParamNames.Count > 0
                ? new HashSet<string>(ancestor.GenericTypeParamNames)
                : null;
            // Names already emitted within THIS ancestor: its own emission drops a property whose
            // C# name a previous member of the same class already claimed.
            var claimedInAncestor = new HashSet<string>(StringComparer.Ordinal);

            foreach (var prop in ancestor.Properties)
            {
                if (!WouldEmitProperty(prop, ancestorDeclName, ancestorGenericParams, typedefMap, blockTypedefMap, localProtocolNames, classProtocolClashNames, knownTypes, appleSdkTypes))
                    continue;
                var propName = ToPascalCase(prop.Name);

                var delegateProtocol = IsDelegateProperty(prop, delegateProtocolNames)
                    ? ResolveDelegateProtocolName(prop, delegateProtocolNames)
                    : null;
                var mappedType = delegateProtocol != null
                    ? ObjCTypeMapper.MapProtocolName(delegateProtocol, classProtocolClashNames)
                    : ObjCTypeMapper.MapType(prop.Type, ancestorDeclName, ancestorGenericParams, typedefMap, blockTypedefMap, localProtocolNames, classProtocolClashNames);
                var (getterSelector, setterSelector) = PropertyAccessorSelectors(prop);
                var entry = new InheritedProperty(ancestor.Name, mappedType, prop.IsReadonly, getterSelector, setterSelector, prop);

                map.TryGetValue(propName, out var alreadyInherited);
                if (alreadyInherited.OfKind(prop.IsClass) is { } sameKind
                    && ClassifyRedeclaration(sameKind, mappedType, prop.IsReadonly, getterSelector, setterSelector) == RedeclarationDisposition.Defer)
                {
                    // This ancestor emits nothing for the name, so the entry it deferred to stays.
                    claimedInAncestor.Add(propName);
                    if (delegateProtocol != null)
                        claimedInAncestor.Add($"Weak{propName}");
                    continue;
                }

                if (delegateProtocol != null)
                {
                    // The Weak/Wrap pair emits two members or none.
                    var weakPropName = $"Weak{propName}";
                    if (claimedInAncestor.Contains(propName) || claimedInAncestor.Contains(weakPropName))
                        continue;
                    claimedInAncestor.Add(propName);
                    claimedInAncestor.Add(weakPropName);
                    map[propName] = alreadyInherited.With(entry, prop.IsClass);
                    map.TryGetValue(weakPropName, out var alreadyWeak);
                    map[weakPropName] = alreadyWeak.With(entry with { MappedType = "NSObject" }, prop.IsClass);
                    continue;
                }

                if (!claimedInAncestor.Add(propName))
                    continue;
                map[propName] = alreadyInherited.With(entry, prop.IsClass);
            }
        }
        return map;
    }

    /// <summary>
    /// Checks whether a property references a delegate protocol type and should use
    /// the WeakDelegate/Wrap pattern instead of normal property emission.
    /// </summary>
    static bool IsDelegateProperty(ObjCPropertyDecl prop, HashSet<string>? delegateProtocolNames)
    {
        if (delegateProtocolNames == null || delegateProtocolNames.Count == 0)
            return false;

        // Check protocol-qualified id (e.g., id<WKNavigationDelegate>)
        if (prop.Type.ProtocolQualifications.Count > 0
            && prop.Type.ProtocolQualifications.Any(p => delegateProtocolNames.Contains(p)))
            return true;

        // Check direct protocol name (e.g., WKNavigationDelegate *)
        if (prop.Type.IsPointer && delegateProtocolNames.Contains(prop.Type.Name))
            return true;

        return false;
    }

    /// <summary>
    /// Resolves the protocol type name from a delegate property's type reference.
    /// </summary>
    static string? ResolveDelegateProtocolName(ObjCPropertyDecl prop, HashSet<string>? delegateProtocolNames)
    {
        if (delegateProtocolNames == null) return null;

        if (prop.Type.ProtocolQualifications.Count > 0)
        {
            var match = prop.Type.ProtocolQualifications.FirstOrDefault(p => delegateProtocolNames.Contains(p));
            if (match != null) return match;
        }

        if (prop.Type.IsPointer && delegateProtocolNames.Contains(prop.Type.Name))
            return prop.Type.Name;

        return null;
    }

    /// <summary>
    /// Emits the Xamarin WeakDelegate/Wrap two-property pattern for delegate/dataSource properties.
    /// Preserves the original property's doc comments, static, readonly shape, and argument
    /// semantics. The strong-typed half is marked <c>[New]</c> when the caller classified this
    /// declaration as deliberately hiding an inherited member; the weak half is marked
    /// independently, from the inherited map, so neither claims to hide something that isn't there
    /// (which would be CS0109) nor hides silently (CS0108).
    /// </summary>
    static bool EmitWeakDelegatePattern(StringBuilder sb, ObjCPropertyDecl prop, HashSet<string>? delegateProtocolNames, HashSet<string>? classProtocolClashNames, HashSet<string>? emittedMemberNames, HashSet<string>? emittedPropertyNames, IReadOnlyDictionary<string, InheritedMembers>? inheritedProperties = null, bool emitNew = false)
    {
        var rawProtocolName = ResolveDelegateProtocolName(prop, delegateProtocolNames);
        if (rawProtocolName == null) return false;
        // The strong-typed [Wrap] property is typed by the delegate protocol's bare managed name
        // (Xamarin [Model] convention) — i.e. the Model CLASS bgen generates from the `[Protocol,
        // Model] interface {Name}` declaration. So it must be the same spelling that declaration
        // carries: MapProtocolName, which folds in BOTH the class/protocol-clash `{Name}Protocol`
        // suffix and the .NET acronym convention. Special-casing the clash and otherwise keeping the
        // raw ObjC name leaves an acronym-renamed delegate protocol declared `NSUrlThingDelegate`
        // while this property is typed `NSURLThingDelegate` — undefined (CS0246).
        var protocolName = ObjCTypeMapper.MapProtocolName(rawProtocolName, classProtocolClashNames);

        var propName = ToPascalCase(prop.Name);
        var weakPropName = $"Weak{propName}";
        var selector = prop.GetterSelector ?? prop.Name;

        // Drop if either name is already claimed by a prior method or property; the Weak/Wrap
        // pattern emits two members (PropName + WeakPropName) so both must be free.
        if (emittedMemberNames != null)
        {
            if (emittedMemberNames.Contains(propName) || emittedMemberNames.Contains(weakPropName))
                return false;
            emittedMemberNames.Add(propName);
            emittedMemberNames.Add(weakPropName);
        }
        // Mirror into the narrow property-only set so descendant method dedup sees these names.
        emittedPropertyNames?.Add(propName);
        emittedPropertyNames?.Add(weakPropName);

        // Preserve doc comment from original property
        ObjCDocCommentEmitter.EmitDocComment(sb, prop.DocComment, null, "        ");
        ObjCAvailabilityEmitter.EmitAvailabilityAttributes(sb, prop.Availability, "        ");

        // 1. Strong-typed property with [Wrap]
        if (prop.IsClass)
            sb.AppendLine("        [Static]");
        if (emitNew)
            sb.AppendLine("        [New]");
        sb.AppendLine($"        [Wrap(\"{weakPropName}\")]");
        sb.AppendLine("        [NullAllowed]");
        if (prop.IsReadonly)
            sb.AppendLine($"        {protocolName} {propName} {{ get; }}");
        else
            sb.AppendLine($"        {protocolName} {propName} {{ get; set; }}");
        sb.AppendLine();

        // 2. Weak NSObject property with [Export]
        // Re-emit availability: the weak backing property is a distinct C# member a consumer can
        // touch directly (WeakDelegate), so it needs the same platform analyzer guard as the [Wrap]
        // property above (a no-op when the property carried no availability macro).
        ObjCAvailabilityEmitter.EmitAvailabilityAttributes(sb, prop.Availability, "        ");
        // Use the original property's ArgumentSemantic if set, otherwise default to Weak
        var argSemantic = prop.MemorySemantic != ObjCMemorySemantic.None
            ? FormatArgumentSemantic(prop.MemorySemantic)
            : ", ArgumentSemantic.Weak";
        if (prop.IsClass)
            sb.AppendLine("        [Static]");
        if (inheritedProperties != null && inheritedProperties.ContainsKey(weakPropName))
            sb.AppendLine("        [New]");
        sb.AppendLine($"        [NullAllowed, Export(\"{selector}\"{argSemantic})]");
        if (prop.IsReadonly)
        {
            sb.AppendLine($"        NSObject {weakPropName} {{ get; }}");
        }
        else
        {
            var setterSelector = prop.SetterSelector ?? $"set{ToPascalCase(prop.Name)}:";
            sb.AppendLine($"        NSObject {weakPropName} {{");
            sb.AppendLine($"            get;");
            sb.AppendLine($"            [Export(\"{setterSelector}\")] set;");
            sb.AppendLine($"        }}");
        }
        sb.AppendLine();
        return true;
    }

    static void EmitGenericTypeHints(StringBuilder sb, ObjCTypeRef returnType, List<ObjCParameterDecl> parameters, string? declaringClassName, HashSet<string>? genericTypeParams, Dictionary<string, ObjCTypeRef>? typedefMap, Dictionary<string, ObjCTypeRef>? blockTypedefMap)
    {
        var hints = new List<string>();

        var returnHint = ObjCTypeMapper.FormatGenericTypeHint(returnType, declaringClassName, genericTypeParams, typedefMap, blockTypedefMap);
        if (returnHint != null)
            hints.Add($"Return: {returnHint}");

        foreach (var param in parameters)
        {
            var paramHint = ObjCTypeMapper.FormatGenericTypeHint(param.Type, declaringClassName, genericTypeParams, typedefMap, blockTypedefMap);
            if (paramHint != null)
                hints.Add($"Parameter '{param.Name}': {paramHint}");
        }

        if (hints.Count > 0)
        {
            foreach (var hint in hints)
                sb.AppendLine($"        // {hint}");
        }
    }

    /// <summary>
    /// Returns the C# type reference for a protocol used in an inheritance/conformance list.
    /// Delegate ([Model]) protocols are emitted with their bare name (Xamarin convention: bgen
    /// declares them as <c>interface Foo</c>), all other protocols use the <c>IFoo</c> spelling.
    /// This replaces the former whole-file <c>IFoo</c>→<c>Foo</c> post-emission regex by deciding
    /// the spelling per-reference from the known set of delegate-protocol names.
    /// </summary>
    // A protocol declared in THIS binding is named bare in an inheritance/conformance list (bgen
    // generates its `IFoo` interface; the contract compile only sees the bare `[Protocol] interface
    // Foo`). An SDK protocol is named `IFoo` — its interface already ships in the platform assembly.
    static string ProtocolInterfaceReference(string protocolName, HashSet<string>? localProtocolNames, HashSet<string>? classProtocolClashNames)
        => localProtocolNames != null && localProtocolNames.Contains(protocolName)
            ? ObjCTypeMapper.MapProtocolName(protocolName, classProtocolClashNames)
            : $"I{ObjCTypeMapper.MapProtocolName(protocolName, classProtocolClashNames)}";

    /// <summary>
    /// The parameter-type list that decides C# SIGNATURE IDENTITY for a member of a generated
    /// category class. It makes the same shaping decisions <see cref="EmitParameters"/> does — the
    /// array pair's pointer half as a raw address, an NSError out parameter, a value-type pointer's
    /// <c>out T</c>, a variadic member's trailing argument list — and drops the parts C# does not
    /// count: parameter names, the <c>[NullAllowed]</c> attribute, and the nullable annotation.
    ///
    /// The receiver-free overload's signature is checked against this, so keying on the bare mapped
    /// type instead would answer that question with the wrong signatures in both directions: it
    /// calls <c>out NSError</c> and <c>NSError</c> the same parameter (over-skipping an overload
    /// that is actually free) while spelling a protocol-typed parameter differently from the member
    /// bgen generates (missing a collision that then fails the consumer's build as CS0111).
    /// </summary>
    /// <param name="publicArrayShape">Key the ARRAY-projected shape a consumer sees — the pointer
    /// half as <c>T[]</c> and the count half gone — instead of the pointer+count shape bgen
    /// generates. The receiver-free overload of an array-projected class method declares that shape,
    /// so its own signature has to be keyed by it while the member it forwards to is keyed by the
    /// generated one.</param>
    static string BuildCategorySignatureKeyTypes(ObjCMethodDecl method, HashSet<string>? genericTypeParams, Dictionary<string, ObjCTypeRef>? typedefMap, Dictionary<string, ObjCTypeRef>? blockTypedefMap, HashSet<string>? enumNames, HashSet<string>? localProtocolNames, HashSet<string>? classProtocolClashNames, ObjCArrayParameterPlan? arrayPlan, bool publicArrayShape = false)
    {
        var types = new List<string>();
        for (var index = 0; index < method.Parameters.Count; index++)
        {
            var param = method.Parameters[index];
            if (arrayPlan != null && index == arrayPlan.PointerParameterIndex)
                types.Add(publicArrayShape ? $"{arrayPlan.ElementType}[]" : "IntPtr");
            else if (arrayPlan != null && publicArrayShape && index == arrayPlan.CountParameterIndex)
                continue;
            else if (ObjCTypeMapper.IsNSErrorOutParameter(param.Type))
                types.Add("out NSError");
            else if (ObjCTypeMapper.IsValueTypePointerParameter(param.Type, typedefMap, enumNames))
                types.Add($"out {ObjCTypeMapper.MapValueTypePointerParameterType(param.Type, typedefMap)}");
            else
                types.Add(ObjCTypeMapper.MapType(param.Type, genericTypeParams: genericTypeParams, typedefMap: typedefMap, blockTypedefMap: blockTypedefMap, localProtocolNames: localProtocolNames, classProtocolClashNames: classProtocolClashNames));
        }

        // EmitMethod appends the variadic argument list after EmitParameters returns, so it is part
        // of the signature without ever having been an ObjC parameter.
        if (method.IsVariadic)
            types.Add("IntPtr");

        return string.Join(",", types);
    }

    static string EmitParameters(List<ObjCParameterDecl> parameters, HashSet<string>? genericTypeParams, Dictionary<string, ObjCTypeRef>? typedefMap = null, Dictionary<string, ObjCTypeRef>? blockTypedefMap = null, HashSet<string>? enumNames = null, HashSet<string>? localProtocolNames = null, HashSet<string>? classProtocolClashNames = null, ObjCArrayParameterPlan? arrayPlan = null)
    {
        var parts = new List<string>();
        for (var index = 0; index < parameters.Count; index++)
        {
            var param = parameters[index];
            // The array pair's pointer half is declared as a raw buffer address; the public overload
            // pins a managed array and passes it. No [NullAllowed] — the parameter is now a value.
            if (arrayPlan != null && index == arrayPlan.PointerParameterIndex)
            {
                parts.Add($"IntPtr {EscapeCSharpKeyword(param.Name)}");
            }
            else if (ObjCTypeMapper.IsNSErrorOutParameter(param.Type))
            {
                parts.Add("[NullAllowed] out NSError error");
            }
            else if (ObjCTypeMapper.IsValueTypePointerParameter(param.Type, typedefMap, enumNames))
            {
                // Value-type pointer parameters become `out T` (e.g., _Bool * → out bool, CGPoint * → out CGPoint)
                var pointeeType = ObjCTypeMapper.MapValueTypePointerParameterType(param.Type, typedefMap);
                var safeName = EscapeCSharpKeyword(param.Name);
                parts.Add($"out {pointeeType} {safeName}");
            }
            else
            {
                var mappedType = ObjCTypeMapper.MapType(param.Type, genericTypeParams: genericTypeParams, typedefMap: typedefMap, blockTypedefMap: blockTypedefMap, localProtocolNames: localProtocolNames, classProtocolClashNames: classProtocolClashNames);
                var nullAttr = ObjCTypeMapper.IsNullableAttribute(param.Type)
                    ? "[NullAllowed] "
                    : "";
                var safeName = EscapeCSharpKeyword(param.Name);
                parts.Add($"{nullAttr}{mappedType} {safeName}");
            }
        }
        return string.Join(", ", parts);
    }

    // C# reserved keywords that cannot be used as identifiers without '@' prefix
    static readonly HashSet<string> CSharpKeywords = new(StringComparer.Ordinal)
    {
        "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char",
        "checked", "class", "const", "continue", "decimal", "default", "delegate",
        "do", "double", "else", "enum", "event", "explicit", "extern", "false",
        "finally", "fixed", "float", "for", "foreach", "goto", "if", "implicit",
        "in", "int", "interface", "internal", "is", "lock", "long", "namespace",
        "new", "null", "object", "operator", "out", "override", "params", "private",
        "protected", "public", "readonly", "ref", "return", "sbyte", "sealed",
        "short", "sizeof", "stackalloc", "static", "string", "struct", "switch",
        "this", "throw", "true", "try", "typeof", "uint", "ulong", "unchecked",
        "unsafe", "ushort", "using", "virtual", "void", "volatile", "while",
    };

    internal static string EscapeCSharpKeyword(string name) =>
        CSharpKeywords.Contains(name) ? $"@{name}" : name;

    internal static string SelectorToMethodName(string selector)
    {
        // Take text before first ':', PascalCase it
        var colonIndex = selector.IndexOf(':');
        var baseName = colonIndex >= 0 ? selector[..colonIndex] : selector;
        return ToPascalCase(baseName);
    }

    /// <summary>
    /// Applies the same dedup-rename logic that <see cref="EmitMethod"/> runs inline: take the
    /// starting <paramref name="methodName"/> (already PascalCased), check for a sig collision in
    /// <paramref name="emittedMethodSignatures"/> AND a name collision against property names in
    /// <paramref name="emittedPropertyNames"/>, and on either collision rename via
    /// <see cref="SelectorToFullMethodName"/> then numeric suffix. Method-vs-method same-name
    /// different-signature is a legal C# overload and is NOT treated as a clash (only the
    /// signature set catches identical-sig collisions). Mutates the signature set and returns the
    /// final method name. Pure with respect to the StringBuilder so the seeding path can reuse it.
    /// </summary>
    static string ResolveMethodNameWithDedup(string methodName, ObjCMethodDecl method, HashSet<string>? genericTypeParams, Dictionary<string, ObjCTypeRef>? typedefMap, Dictionary<string, ObjCTypeRef>? blockTypedefMap, HashSet<string> emittedMethodSignatures, HashSet<string>? emittedPropertyNames = null, string? paramSignatureOverride = null)
    {
        var paramSignature = paramSignatureOverride
            ?? string.Join(",", method.Parameters.Select(p => ObjCTypeMapper.MapType(p.Type, genericTypeParams: genericTypeParams, typedefMap: typedefMap, blockTypedefMap: blockTypedefMap)));
        // Include the variadic IntPtr param in the signature to detect collisions
        // with explicit args: variants (e.g., objectsWhere: + objectsWhere:args:)
        if (method.IsVariadic)
            paramSignature = paramSignature.Length > 0 ? $"{paramSignature},IntPtr" : "IntPtr";

        bool Clashes(string name) =>
            emittedMethodSignatures.Contains($"{name}({paramSignature})")
            || (emittedPropertyNames != null && emittedPropertyNames.Contains(name));

        if (!Clashes(methodName))
        {
            emittedMethodSignatures.Add($"{methodName}({paramSignature})");
            return methodName;
        }
        methodName = SelectorToFullMethodName(method.Selector);
        if (!Clashes(methodName))
        {
            emittedMethodSignatures.Add($"{methodName}({paramSignature})");
            return methodName;
        }
        var suffix = 2;
        while (Clashes($"{methodName}{suffix}"))
            suffix++;
        var finalName = $"{methodName}{suffix}";
        emittedMethodSignatures.Add($"{finalName}({paramSignature})");
        return finalName;
    }

    /// <summary>
    /// Empty provenance set for a resolvability check whose mapped name cannot possibly be an
    /// emitter-synthesized protocol interface (a class name, an enum name, …). Passing this is a
    /// statement of fact, not a shortcut: nothing on that path ran the protocol-interface synthesis
    /// that prepends the <c>I</c>, so no name it produces is entitled to the I-strip fallback.
    /// </summary>
    static readonly IReadOnlySet<string> NoSynthesizedProtocolInterfaces =
        new HashSet<string>(StringComparer.Ordinal);

    /// <summary>
    /// Resolvability of the <c>I{Protocol}</c> interface reference an inheritance clause would emit.
    /// The caller synthesizes the <c>I</c> itself rather than going through
    /// <see cref="ObjCTypeMapper.MapType"/>, so it is its own provenance: the one name it builds is
    /// exactly the synthesized interface, and passing it lets the I-strip fallback consider it.
    /// </summary>
    static bool IsProtocolInterfaceResolvable(string protocolName, HashSet<string>? knownTypes, HashSet<string>? appleSdkTypes, HashSet<string>? classProtocolClashNames)
    {
        if (knownTypes == null)
            return true;
        var interfaceName = $"I{ObjCTypeMapper.MapProtocolName(protocolName, classProtocolClashNames)}";
        return ObjCTypeMapper.IsApiDefinitionTypeResolvable(
            interfaceName, knownTypes, appleSdkTypes,
            new HashSet<string>(StringComparer.Ordinal) { interfaceName });
    }

    /// <summary>
    /// Resolvability of a property's mapped type, capturing which <c>I</c>-prefixed names in it the
    /// mapper actually synthesized. Extracted from the inline predicate it replaces so the mapping
    /// and the check can share one provenance sink — a lambda cannot thread an out-parameter.
    /// </summary>
    static bool IsPropertyTypeResolvable(ObjCPropertyDecl prop, HashSet<string>? knownTypes, HashSet<string>? appleSdkTypes, Dictionary<string, ObjCTypeRef>? typedefMap, Dictionary<string, ObjCTypeRef>? blockTypedefMap, HashSet<string>? localProtocolNames, HashSet<string>? classProtocolClashNames)
    {
        if (knownTypes == null)
            return true;
        var synthesized = new HashSet<string>(StringComparer.Ordinal);
        var checkType = ObjCTypeMapper.MapType(prop.Type, declaringClassName: null, genericTypeParams: null, typedefMap, blockTypedefMap, localProtocolNames, classProtocolClashNames, synthesized);
        return ObjCTypeMapper.IsApiDefinitionTypeResolvable(checkType, knownTypes, appleSdkTypes, synthesized);
    }

    /// <summary>
    /// Checks the same gates <see cref="EmitMethod"/> uses to decide whether to emit a method: the
    /// return type and every parameter type must be resolvable in the ApiDefinition context, and no
    /// parameter may be a value-type pointer without a sound projection. Only ever asked about
    /// PROTOCOL requirements, where no array overload can be built, so every const or array-shaped
    /// value-type pointer is refused — matching what <see cref="EmitMethod"/> does with
    /// <c>isProtocol: true</c>. Letting a refused requirement look emitted here would reserve a name
    /// and signature nothing occupies, renaming a descendant's identical member for no reason.
    /// </summary>
    static bool WouldEmitMethod(ObjCMethodDecl method, HashSet<string>? knownTypes, HashSet<string>? appleSdkTypes, Dictionary<string, ObjCTypeRef>? typedefMap, Dictionary<string, ObjCTypeRef>? blockTypedefMap, HashSet<string>? enumNames, HashSet<string>? localProtocolNames, HashSet<string>? classProtocolClashNames)
    {
        for (var i = 0; i < method.Parameters.Count; i++)
        {
            var param = method.Parameters[i];
            if (!ObjCTypeMapper.IsValueTypePointerShape(param.Type, typedefMap, enumNames))
                continue;
            if (ObjCArrayParameterProjection.IsArrayShapedPointerParameter(method, i, typedefMap, enumNames)
                || ObjCTypeMapper.IsConstValueTypePointerParameter(param.Type, typedefMap, enumNames))
            {
                return false;
            }
        }

        if (knownTypes != null)
        {
            var returnSynthesized = new HashSet<string>(StringComparer.Ordinal);
            var checkReturn = ObjCTypeMapper.MapType(method.ReturnType, declaringClassName: null, genericTypeParams: null, typedefMap, blockTypedefMap, localProtocolNames, classProtocolClashNames, returnSynthesized);
            if (!ObjCTypeMapper.IsApiDefinitionTypeResolvable(checkReturn, knownTypes, appleSdkTypes, returnSynthesized))
                return false;
            foreach (var param in method.Parameters)
            {
                var paramSynthesized = new HashSet<string>(StringComparer.Ordinal);
                var checkParam = ObjCTypeMapper.MapType(param.Type, genericTypeParams: null, typedefMap: typedefMap, blockTypedefMap: blockTypedefMap, localProtocolNames: localProtocolNames, classProtocolClashNames: classProtocolClashNames, synthesizedProtocolInterfaces: paramSynthesized);
                if (!ObjCTypeMapper.IsApiDefinitionTypeResolvable(checkParam, knownTypes, appleSdkTypes, paramSynthesized))
                    return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Mirror of <see cref="EmitProperty"/>'s resolvability gate: a property is emitted only when
    /// its mapped type resolves in the ApiDefinition context. Lets a caller pre-compute, before the
    /// method loop, exactly which properties' names and accessor selectors the method dedup must
    /// account for (methods are emitted before properties, so the loop can't otherwise see them).
    /// </summary>
    static bool WouldEmitProperty(ObjCPropertyDecl prop, string? declaringClassName, HashSet<string>? genericTypeParams, Dictionary<string, ObjCTypeRef>? typedefMap, Dictionary<string, ObjCTypeRef>? blockTypedefMap, HashSet<string>? localProtocolNames, HashSet<string>? classProtocolClashNames, HashSet<string>? knownTypes, HashSet<string>? appleSdkTypes)
    {
        if (knownTypes == null)
            return true;
        var synthesized = new HashSet<string>(StringComparer.Ordinal);
        var checkType = ObjCTypeMapper.MapType(prop.Type, declaringClassName, genericTypeParams, typedefMap, blockTypedefMap, localProtocolNames, classProtocolClashNames, synthesized);
        return ObjCTypeMapper.IsApiDefinitionTypeResolvable(checkType, knownTypes, appleSdkTypes, synthesized);
    }

    /// <summary>
    /// Computes the ObjC accessor selectors a set of (emittable) properties exports, split by
    /// instance vs class membership. A getter always exports <c>GetterSelector ?? Name</c>; a
    /// read-write property additionally exports <c>SetterSelector ?? "set{Name}:"</c> — matching
    /// what <see cref="EmitProperty"/> and <see cref="EmitWeakDelegatePattern"/> emit. A method
    /// whose <c>[Export]</c> selector matches one of these, of the same instance/class kind, is the
    /// SAME ObjC selector as the property accessor; emitting both registers a duplicate selector and
    /// aborts the runtime registrar at launch, so the method is dropped in favour of the property.
    /// </summary>
    static (HashSet<string> Instance, HashSet<string> Class) BuildPropertyAccessorSelectors(IEnumerable<ObjCPropertyDecl> emittableProperties)
    {
        var instance = new HashSet<string>(StringComparer.Ordinal);
        var klass = new HashSet<string>(StringComparer.Ordinal);
        foreach (var prop in emittableProperties)
        {
            var target = prop.IsClass ? klass : instance;
            target.Add(prop.GetterSelector ?? prop.Name);
            if (!prop.IsReadonly)
                target.Add(prop.SetterSelector ?? $"set{ToPascalCase(prop.Name)}:");
        }
        return (instance, klass);
    }

    /// <summary>
    /// Collects the ObjC property-accessor selectors that bgen/the registrar flatten onto a class
    /// from the protocols it conforms to, transitively. The .NET registrar treats a conforming
    /// class's REQUIRED protocol members as registered on the class itself, so a class method whose
    /// <c>[Export]</c> selector equals one of these accessor selectors registers that selector twice
    /// and aborts the registrar at launch. Only required (non-optional) properties are flattened — an
    /// optional protocol member is reached through the generated interface's extension methods, never
    /// registered on the class — and only resolvable-typed ones, matching what
    /// <see cref="EmitProperty"/> actually emits. Protocols not declared in this module (SDK/external)
    /// have no visible members and are skipped. Split by instance vs class membership so the result
    /// composes with <see cref="BuildPropertyAccessorSelectors"/>.
    /// </summary>
    static (HashSet<string> Instance, HashSet<string> Class) BuildInheritedProtocolAccessorSelectors(
        IEnumerable<string> conformedProtocolNames,
        Dictionary<string, ObjCProtocolDecl>? protocolsByName,
        Dictionary<string, ObjCTypeRef> typedefMap, Dictionary<string, ObjCTypeRef> blockTypedefMap,
        HashSet<string>? knownTypes, HashSet<string>? appleSdkTypes,
        HashSet<string>? localProtocolNames, HashSet<string>? classProtocolClashNames)
    {
        var instance = new HashSet<string>(StringComparer.Ordinal);
        var klass = new HashSet<string>(StringComparer.Ordinal);
        if (protocolsByName == null)
            return (instance, klass);

        var visited = new HashSet<string>(StringComparer.Ordinal);
        void Walk(string protoName)
        {
            if (!visited.Add(protoName))
                return;
            // External/SDK protocol — its members aren't visible here, so its accessor selectors
            // can't be enumerated. The registrar may still flatten them, but that's outside the
            // confirmed local-protocol defect surface and would need SDK header introspection.
            if (!protocolsByName.TryGetValue(protoName, out var proto))
                return;
            foreach (var prop in proto.Properties)
            {
                // Optional members aren't auto-implemented on a conforming class, so they're never
                // registered on it — only required members are flattened.
                if (prop.IsOptional)
                    continue;
                if (knownTypes != null)
                {
                    var synthesized = new HashSet<string>(StringComparer.Ordinal);
                    var checkType = ObjCTypeMapper.MapType(prop.Type, declaringClassName: null, genericTypeParams: null, typedefMap, blockTypedefMap, localProtocolNames, classProtocolClashNames, synthesized);
                    if (!ObjCTypeMapper.IsApiDefinitionTypeResolvable(checkType, knownTypes, appleSdkTypes, synthesized))
                        continue;
                }
                var target = prop.IsClass ? klass : instance;
                target.Add(prop.GetterSelector ?? prop.Name);
                if (!prop.IsReadonly)
                    target.Add(prop.SetterSelector ?? $"set{ToPascalCase(prop.Name)}:");
            }
            foreach (var inherited in proto.InheritedProtocolNames)
                Walk(inherited);
        }
        foreach (var name in conformedProtocolNames)
            Walk(name);
        return (instance, klass);
    }

    /// <summary>
    /// True when a method shares an ObjC selector with a property accessor of the same instance/
    /// class kind (the B1 duplicate-export hazard). Instance methods only collide with instance
    /// property accessors and class methods only with class property accessors, because ObjC
    /// dispatches instance and class selectors through separate method lists.
    /// </summary>
    static bool CollidesWithPropertyAccessor(ObjCMethodDecl method, (HashSet<string> Instance, HashSet<string> Class) accessorSelectors)
    {
        var set = method.IsInstanceMethod ? accessorSelectors.Instance : accessorSelectors.Class;
        return set.Contains(method.Selector);
    }

    /// <summary>
    /// Pre-seeds the child protocol's dedup sets with the actual signatures + member names its
    /// transitively-inherited ancestors would emit. Each ancestor's emission is computed
    /// recursively (with memoization) so that rename decisions induced by a grandparent are
    /// reflected when the parent's signatures land in the child's seed. Property names are
    /// tracked in a separate set (<paramref name="emittedPropertyNames"/>) so the child's method
    /// dedup blocks only ancestor PROPERTY name collisions (CS0102) while still permitting legal
    /// method overloads against ancestor methods of the same short name.
    /// </summary>
    static void SeedInheritedProtocolSignatures(HashSet<string> emittedMethodSignatures, HashSet<string> emittedMemberNames, HashSet<string> emittedPropertyNames, ObjCProtocolDecl proto, Dictionary<string, ObjCProtocolDecl> protocolsByName, Dictionary<string, ObjCTypeRef> typedefMap, Dictionary<string, ObjCTypeRef> blockTypedefMap, HashSet<string>? knownTypes, HashSet<string>? appleSdkTypes, HashSet<string>? enumNames, HashSet<string>? localProtocolNames, HashSet<string>? classProtocolClashNames)
    {
        var cache = new Dictionary<string, ProtocolEmissionSet>(StringComparer.Ordinal);
        foreach (var name in proto.InheritedProtocolNames)
        {
            if (!protocolsByName.TryGetValue(name, out var parent)) continue;
            var parentSet = ComputeProtocolEmissionSet(parent, protocolsByName, cache, typedefMap, blockTypedefMap, knownTypes, appleSdkTypes, enumNames, localProtocolNames, classProtocolClashNames);
            foreach (var s in parentSet.Signatures) emittedMethodSignatures.Add(s);
            foreach (var m in parentSet.MemberNames) emittedMemberNames.Add(m);
            foreach (var p in parentSet.PropertyNames) emittedPropertyNames.Add(p);
        }
    }

    readonly record struct ProtocolEmissionSet(HashSet<string> Signatures, HashSet<string> MemberNames, HashSet<string> PropertyNames);

    /// <summary>
    /// Recursively computes the signatures, all-member names, and property-only names a protocol
    /// would actually emit after dedup, including the transitive contribution of its own
    /// ancestors. Results are cached per protocol name. Defensive against cycles via a placeholder
    /// entry in <paramref name="cache"/>.
    /// </summary>
    static ProtocolEmissionSet ComputeProtocolEmissionSet(ObjCProtocolDecl proto, Dictionary<string, ObjCProtocolDecl> protocolsByName, Dictionary<string, ProtocolEmissionSet> cache, Dictionary<string, ObjCTypeRef> typedefMap, Dictionary<string, ObjCTypeRef> blockTypedefMap, HashSet<string>? knownTypes, HashSet<string>? appleSdkTypes, HashSet<string>? enumNames, HashSet<string>? localProtocolNames, HashSet<string>? classProtocolClashNames)
    {
        if (cache.TryGetValue(proto.Name, out var cached)) return cached;

        var sigs = new HashSet<string>(StringComparer.Ordinal);
        var memberNames = new HashSet<string>(StringComparer.Ordinal);
        var propertyNames = new HashSet<string>(StringComparer.Ordinal);
        var result = new ProtocolEmissionSet(sigs, memberNames, propertyNames);
        // Placeholder break-cycle entry (protocols normally don't cycle, but be defensive).
        cache[proto.Name] = result;

        // Seed with every ancestor's resolved emission (transitive).
        foreach (var name in proto.InheritedProtocolNames)
        {
            if (!protocolsByName.TryGetValue(name, out var parent)) continue;
            var parentSet = ComputeProtocolEmissionSet(parent, protocolsByName, cache, typedefMap, blockTypedefMap, knownTypes, appleSdkTypes, enumNames, localProtocolNames, classProtocolClashNames);
            foreach (var s in parentSet.Signatures) sigs.Add(s);
            foreach (var m in parentSet.MemberNames) memberNames.Add(m);
            foreach (var p in parentSet.PropertyNames) propertyNames.Add(p);
        }

        // Pre-compute this protocol's own emittable properties — the single resolvability decision the
        // property replay below reuses — and seed their names into propertyNames BEFORE replaying methods,
        // mirroring EmitProtocol's emit order so a method colliding with an own-property name
        // renames here exactly as it does there. The accessor-selector set lets the method replay
        // drop B1 duplicate-export methods so they don't seed descendant protocols.
        var ownEmittableProps = proto.Properties
            .Where(p => knownTypes == null || IsPropertyTypeResolvable(p, knownTypes, appleSdkTypes, typedefMap, blockTypedefMap, localProtocolNames, classProtocolClashNames))
            .ToList();
        foreach (var prop in ownEmittableProps)
            propertyNames.Add(ToPascalCase(prop.Name));
        var ownAccessorSelectors = BuildPropertyAccessorSelectors(ownEmittableProps);

        // Mirror EmitProtocol's parameterless-init suppression so the cached emission set matches what
        // bgen actually sees. A protocol declaring a parameterless `init` requirement emits
        // [DisableDefaultCtor] and drops the `init` method (it would otherwise re-register the selector
        // and shadow NSObject.Init()), so the replay must NOT cache a phantom `Init` member — a stale
        // entry would wrongly dedup a descendant protocol's same-named member out of existence.
        var replayDeclaresParameterlessInit =
            proto.Methods.Any(m => m.Selector == "init" && m.Parameters.Count == 0)
            || proto.Methods.Any(m => m.Selector.StartsWith("initWith", StringComparison.Ordinal) && m.Parameters.Count == 0);

        // Replay this protocol's own methods against the seeded sets so any rename induced by an
        // ancestor (intra- or grandparent-level) OR an own property shows up in the cached result.
        // Method dedup only blocks on PROPERTY names — sibling method short names are valid overloads.
        foreach (var method in proto.Methods)
        {
            if (!WouldEmitMethod(method, knownTypes, appleSdkTypes, typedefMap, blockTypedefMap, enumNames, localProtocolNames, classProtocolClashNames))
                continue;
            if (replayDeclaresParameterlessInit && method.Selector == "init" && method.Parameters.Count == 0)
                continue;
            if (CollidesWithPropertyAccessor(method, ownAccessorSelectors))
                continue;
            var startName = proto.IsDelegateProtocol
                ? SelectorToDelegateMethodName(method.Selector, proto.RawObjCName ?? proto.Name)
                : SelectorToMethodName(method.Selector);
            var finalName = ResolveMethodNameWithDedup(startName, method, genericTypeParams: null, typedefMap: typedefMap, blockTypedefMap: blockTypedefMap, emittedMethodSignatures: sigs, emittedPropertyNames: propertyNames);
            memberNames.Add(finalName);
        }

        // Property names live in the member-name space (any prior emitted name blocks them) AND
        // in the property-only set used by descendant method dedup. Replays EmitProperty's
        // intra-protocol drop-on-Add behavior so the cached set matches what bgen actually sees.
        //
        // This walks `ownEmittableProps` — the SAME pre-filtered list the propertyNames pre-seed used —
        // rather than re-deciding resolvability here. Re-deciding is what let the two halves drift: the
        // open-coded gate this replaced dropped `localProtocolNames`/`classProtocolClashNames`, so a
        // property typed by a local protocol whose managed spelling is renamed (acronym convention or
        // the class/protocol-clash suffix) mapped to a name absent from knownTypes, failed the gate,
        // and stayed out of memberNames while the pre-seed had already put it in propertyNames. That
        // under-reports the ancestor's members to every descendant emission set, so a child protocol
        // re-emits a name the parent already owns. Sharing the list makes the divergence unexpressible.
        foreach (var prop in ownEmittableProps)
        {
            var propName = ToPascalCase(prop.Name);
            if (memberNames.Add(propName))
                propertyNames.Add(propName);
        }

        return result;
    }

    internal static string SelectorToFullMethodName(string selector)
    {
        // Use ALL selector parts, PascalCase each: "setObject:forKey:" → "SetObjectForKey"
        var parts = selector.Split(':', StringSplitOptions.RemoveEmptyEntries);
        return string.Concat(parts.Select(ToPascalCase));
    }

    /// <summary>
    /// For delegate protocol methods with multi-part selectors, concatenate all selector
    /// parts after the first (Xamarin convention). The first part is typically the delegate
    /// owner instance name (e.g., "messaging", "tableView", "URLSession"), while subsequent
    /// parts describe the action and context. Examples:
    ///   "messaging:didReceiveRegistrationToken:" → "DidReceiveRegistrationToken"
    ///   "URLSession:task:didCompleteWithError:"  → "TaskDidCompleteWithError"
    ///   "tableView:commitEditingStyle:forRowAtIndexPath:" → "CommitEditingStyleForRowAtIndexPath"
    ///   "didReceiveNotification:"                → "DidReceiveNotification"
    /// For single-part selectors, falls back to normal SelectorToMethodName behavior.
    /// <para>
    /// Dropping part[0] wholesale is only right when part[0] is nothing BUT the receiver name.
    /// The platform's own delegates routinely fold the first semantic word into it —
    /// <c>mapViewDidFailLoadingMap:withError:</c> — where the wholesale drop throws away
    /// <c>DidFailLoadingMap</c> and leaves a method called <c>WithError</c>. When
    /// <paramref name="delegatingProtocolName"/> identifies the delegating class, only the leading
    /// receiver token is peeled off and the rest of part[0] is kept:
    /// <c>DidFailLoadingMapWithError</c>. A part[0] that does not carry the receiver token keeps
    /// the historical behaviour.
    /// </para>
    /// </summary>
    /// <param name="selector">The Objective-C selector.</param>
    /// <param name="delegatingProtocolName">The delegate protocol's own ObjC name (e.g.
    /// <c>MLNMapViewDelegate</c>), or null when it isn't known — in which case no receiver token is
    /// peeled and the historical naming applies unchanged.</param>
    internal static string SelectorToDelegateMethodName(string selector, string? delegatingProtocolName = null)
    {
        var parts = selector.Split(':', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return string.Empty;

        var carriedByFirstPart = StripDelegateReceiverToken(parts[0], delegatingProtocolName);
        if (carriedByFirstPart != null)
            return ToPascalCase(carriedByFirstPart) + string.Concat(parts.Skip(1).Select(ToPascalCase));

        if (parts.Length >= 2)
            return string.Concat(parts.Skip(1).Select(ToPascalCase));
        return ToPascalCase(parts[0]);
    }

    /// <summary>The role suffixes a delegating class's protocol is named with.</summary>
    static readonly string[] DelegateRoleSuffixes = ["DataSource", "Delegate"];

    /// <summary>
    /// The part of <paramref name="firstSelectorPart"/> that remains after peeling the delegating
    /// class's receiver token, or <c>null</c> when it carries no receiver token (or the whole part
    /// IS the receiver, which is the case the wholesale drop already handles correctly).
    /// The remainder must start a new PascalCase word, so a receiver token can never bite into the
    /// middle of one.
    /// </summary>
    static string? StripDelegateReceiverToken(string firstSelectorPart, string? delegatingProtocolName)
    {
        if (string.IsNullOrEmpty(delegatingProtocolName))
            return null;

        foreach (var receiver in DelegateReceiverCandidates(delegatingProtocolName))
        {
            // Case-insensitive: the receiver token lower-cases the class name's first letter
            // (MLNMapView → mapView), and an acronym-leading class lower-cases the whole acronym.
            if (firstSelectorPart.Length > receiver.Length
                && firstSelectorPart.StartsWith(receiver, StringComparison.OrdinalIgnoreCase)
                && char.IsUpper(firstSelectorPart[receiver.Length]))
                return firstSelectorPart[receiver.Length..];
        }
        return null;
    }

    /// <summary>
    /// The receiver-token spellings a delegate selector may use for the class behind
    /// <paramref name="protocolName"/>, longest first so the match doesn't depend on candidate
    /// order. Two forms: the delegating class name itself (protocol name minus its role suffix)
    /// and that name with its framework acronym removed — ObjC selectors name the receiver by the
    /// unprefixed class (<c>MLNMapViewDelegate</c> → <c>MLNMapView</c> → <c>mapView</c>).
    /// </summary>
    static IEnumerable<string> DelegateReceiverCandidates(string protocolName)
    {
        var declaringClass = protocolName;
        foreach (var suffix in DelegateRoleSuffixes)
        {
            if (declaringClass.Length > suffix.Length
                && declaringClass.EndsWith(suffix, StringComparison.Ordinal))
            {
                declaringClass = declaringClass[..^suffix.Length];
                break;
            }
        }

        yield return declaringClass;
        var unprefixed = StripLeadingAcronym(declaringClass);
        if (!string.Equals(unprefixed, declaringClass, StringComparison.Ordinal))
            yield return unprefixed;
    }

    /// <summary>
    /// Removes a leading all-uppercase framework acronym from <paramref name="name"/>, keeping the
    /// acronym run's last letter when it starts the next PascalCase word (<c>MLNMapView</c> →
    /// <c>MapView</c>). Returns the input unchanged when there is no acronym of at least two
    /// letters to remove.
    /// </summary>
    static string StripLeadingAcronym(string name)
    {
        var run = 0;
        while (run < name.Length && char.IsUpper(name[run]))
            run++;
        if (run < name.Length && char.IsLower(name[run]))
            run--;
        return run >= 2 && run < name.Length ? name[run..] : name;
    }

    static string ToPascalCase(string name)
    {
        if (string.IsNullOrEmpty(name))
            return name;
        return char.ToUpperInvariant(name[0]) + name[1..];
    }

    /// <summary>
    /// Formats the ArgumentSemantic suffix for [Export] attributes on properties.
    /// Returns empty string when no semantic is specified, otherwise ", ArgumentSemantic.X".
    /// Retain maps to Strong (they are equivalent in ARC).
    /// </summary>
    internal static string FormatArgumentSemantic(ObjCMemorySemantic semantic) => semantic switch
    {
        ObjCMemorySemantic.Copy => ", ArgumentSemantic.Copy",
        ObjCMemorySemantic.Assign or ObjCMemorySemantic.UnsafeUnretained => ", ArgumentSemantic.Assign",
        ObjCMemorySemantic.Weak => ", ArgumentSemantic.Weak",
        ObjCMemorySemantic.Strong or ObjCMemorySemantic.Retain => ", ArgumentSemantic.Retain",
        _ => ""
    };
}
