// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace SwiftBindings.TransitionGate;

/// <summary>What one native-signature carrier is, for the purposes of the transition rules.</summary>
public enum CarrierKind
{
    /// <summary><c>void</c>, as a result.</summary>
    Void,

    /// <summary>An integer of 1/2/4/8 bytes, <c>nint</c>/<c>nuint</c>, <c>bool</c>, <c>char</c>,
    /// <c>float</c>, <c>double</c>, a pointer, a function pointer, a byref (passed as its address),
    /// an enum, or an allow-listed one-word runtime wrapper.</summary>
    Primitive,

    /// <summary><c>SwiftSelf</c> (untyped self, x20 / r13).</summary>
    SwiftSelf,

    /// <summary><c>SwiftSelf&lt;T&gt;</c> (typed self: lowered or by address).</summary>
    SwiftSelfGeneric,

    /// <summary><c>SwiftError*</c> or <c>ref SwiftError</c> (the error register, x21 / r12).</summary>
    SwiftError,

    /// <summary><c>SwiftIndirectResult</c> (the indirect-result register, x8 / rax).</summary>
    SwiftIndirectResult,

    /// <summary>A value type that is not primitive-equivalent: a multi-field struct, a generic
    /// instantiation, or a one-field struct that is not on the allow-list.</summary>
    Aggregate,

    /// <summary>A class, string, array or object: the runtime marshals it.</summary>
    ManagedReference,

    /// <summary>An unresolved generic parameter.</summary>
    GenericParameter,

    /// <summary>A value type whose definition could not be found, so primitive-equivalence cannot
    /// be shown. Fails closed.</summary>
    Unresolved,
}

/// <summary>
/// A classified carrier: its kind, its display spelling, when not primitive why, and when primitive
/// or void its canonical native spelling (see <see cref="SwiftTransitionScanner.CanonicalCarrier"/>),
/// with an enum spelled as its underlying integer and a one-word wrapper as the word it wraps.
/// </summary>
public sealed record CarrierVerdict(CarrierKind Kind, string Display, string? Detail = null, string? Canonical = null)
{
    /// <summary>For a <see cref="CarrierKind.SwiftSelfGeneric"/>, where CoreCLR places it (<see cref="LegacySwiftSelfPhysical"/>).</summary>
    public LegacySelfPhysical? SelfPhysical { get; init; }

    public bool IsPrimitiveOrVoid => Kind is CarrierKind.Primitive or CarrierKind.Void;

    public bool IsSwiftSpecial => Kind is CarrierKind.SwiftSelf or CarrierKind.SwiftSelfGeneric
        or CarrierKind.SwiftError or CarrierKind.SwiftIndirectResult;
}

/// <summary>The definition an allow-listed one-word wrapper must have.</summary>
public enum OneWordShape
{
    /// <summary>A struct with one pointer-sized instance field.</summary>
    Struct,

    /// <summary>An enum (its underlying integer).</summary>
    Enum,
}

/// <summary>One entry of <see cref="CarrierClassifier.OneWordWrappers"/>, by full metadata name
/// (<c>Namespace.Name</c>, with the arity suffix for a generic type).</summary>
public sealed record OneWordWrapper(string FullName, OneWordShape Shape);

/// <summary>
/// The shared primitive-carrier classifier (design invariant I1): a forward Swift-convention
/// transition may carry only integers of 1/2/4/8 bytes, <c>nint</c>/<c>nuint</c>, pointers,
/// <c>float</c>, <c>double</c> and the allow-listed one-word wrappers, and return void or one of
/// those. Every native runtime places these identically under swiftcc and the C convention, on
/// arm64 and x86_64, in the interpreter and precompiled; the measured interpreter defects all
/// involve a special register or an aggregate.
/// </summary>
public sealed class CarrierClassifier
{
    public const string SwiftInteropNamespace = "System.Runtime.InteropServices.Swift";

    /// <summary>
    /// The single-word wrappers a primitive-only signature may carry, explicitly, by full metadata
    /// name: the runtime's metadata, witness-table and descriptor pointers and the Swift pointer
    /// types, all of which Swift passes as one ordinary pointer-sized argument. Each entry is checked
    /// against its definition wherever it is used (<see cref="CheckWrapperDefinition"/>): it must be
    /// defined in <c>Swift.Runtime</c> and, for a struct, have exactly one instance field that is a
    /// pointer-sized word under sequential layout; anything else of the same name fails closed. A
    /// generic entry's type arguments do not matter, since its field is a pointer either way. Nothing
    /// else is a wrapper, however small: <c>Foundation.Data</c> and every other value payload is an
    /// aggregate, whose Swift lowering is the type's own business.
    /// </summary>
    public static readonly IReadOnlyList<OneWordWrapper> OneWordWrappers = new OneWordWrapper[]
    {
        new("Swift.Runtime.TypeMetadata", OneWordShape.Struct),
        new("Swift.Runtime.TypeMetadataRequest", OneWordShape.Enum),
        new("Swift.Runtime.ProtocolWitnessTable", OneWordShape.Struct),
        new("Swift.Runtime.ProtocolDescriptor", OneWordShape.Struct),
        new("Swift.Runtime.ProtocolConformanceDescriptor", OneWordShape.Struct),
        new("Swift.UnsafeRawPointer", OneWordShape.Struct),
        new("Swift.UnsafeMutableRawPointer", OneWordShape.Struct),
        new("Swift.UnsafePointer`1", OneWordShape.Struct),
        new("Swift.UnsafeMutablePointer`1", OneWordShape.Struct),
    };

    /// <summary>The allow-list entry named <paramref name="fullName"/>, if any.</summary>
    public static OneWordWrapper? FindWrapper(string fullName) =>
        OneWordWrappers.FirstOrDefault(w => w.FullName == fullName);

    /// <summary>
    /// Checks one allow-list entry against its definition: the assembly that defines it, its kind
    /// and its layout. Returns null when it holds, else why not.
    /// </summary>
    public static string? CheckWrapperDefinition(MetadataReader reader, TypeDefinitionHandle handle, OneWordWrapper entry)
    {
        var assemblyName = reader.IsAssembly ? reader.GetString(reader.GetAssemblyDefinition().Name) : "";
        if (assemblyName != RuntimeAssemblyName)
            return $"{entry.FullName} is allow-listed only from {RuntimeAssemblyName}, but is defined in '{assemblyName}'";
        var def = reader.GetTypeDefinition(handle);
        if (entry.Shape == OneWordShape.Enum)
            return IsEnum(reader, def) ? null : $"allow-listed {entry.FullName} is not an enum";
        if (IsEnum(reader, def))
            return $"allow-listed {entry.FullName} is an enum, not a one-word struct";
        var fields = InstanceFieldTypes(reader, def).ToList();
        if ((def.Attributes & TypeAttributes.LayoutMask) == TypeAttributes.ExplicitLayout || fields.Count != 1)
            return $"allow-listed {entry.FullName} is not a one-field sequential struct ({fields.Count} instance field(s))";
        return SwiftTransitionScanner.CanonicalCarrier(fields[0]) is "isize" or "usize" or "ptr"
            ? null
            : $"allow-listed {entry.FullName} wraps {TypeNames.Display(fields[0])}, which is not a pointer-sized word";
    }

    /// <summary>
    /// Checks the whole allow-list against <paramref name="runtime"/>, the <c>Swift.Runtime</c>
    /// assembly: every entry must exist there and hold. Returns one line per problem.
    /// </summary>
    public static IReadOnlyList<string> VerifyAllowList(MetadataReader runtime)
    {
        var problems = new List<string>();
        foreach (var entry in OneWordWrappers)
        {
            var handle = runtime.TypeDefinitions.FirstOrDefault(h =>
            {
                var d = runtime.GetTypeDefinition(h);
                if (!d.GetDeclaringType().IsNil)
                    return false;
                var ns = runtime.GetString(d.Namespace);
                return (ns.Length == 0 ? "" : ns + ".") + runtime.GetString(d.Name) == entry.FullName;
            });
            if (handle.IsNil)
                problems.Add($"allow-listed {entry.FullName} is not defined in {RuntimeAssemblyName}");
            else if (CheckWrapperDefinition(runtime, handle, entry) is { } problem)
                problems.Add(problem);
        }
        return problems;
    }

    public const string RuntimeAssemblyName = "Swift.Runtime";

    private readonly AssemblyResolver _resolver;
    private readonly Dictionary<(string Scope, string FullName), CarrierVerdict> _namedCache = new();

    public CarrierClassifier(AssemblyResolver resolver) => _resolver = resolver;

    /// <summary>Classifies one parameter or result carrier of a native signature.</summary>
    public CarrierVerdict Classify(SigType type)
    {
        var display = TypeNames.Display(type);
        switch (type.Unwrapped())
        {
            case SigType.Primitive { Code: PrimitiveTypeCode.Void }:
                return new(CarrierKind.Void, display, Canonical: "void");
            case SigType.Primitive { Code: PrimitiveTypeCode.String or PrimitiveTypeCode.Object or PrimitiveTypeCode.TypedReference }:
                return new(CarrierKind.ManagedReference, display, "a managed reference the runtime marshals");
            case SigType.Primitive:
                return new(CarrierKind.Primitive, display, Canonical: SwiftTransitionScanner.CanonicalCarrier(type));
            case SigType.Pointer { Element: var element } when IsSwiftError(element):
                return new(CarrierKind.SwiftError, display, "SwiftError* (error register)");
            case SigType.ByRef { Element: var element } when IsSwiftError(element):
                return new(CarrierKind.SwiftError, display, "ref SwiftError (error register)");
            case SigType.Pointer or SigType.ByRef or SigType.FunctionPointer:
                return new(CarrierKind.Primitive, display, Canonical: "ptr");
            case SigType.SZArray or SigType.Array:
                return new(CarrierKind.ManagedReference, display, "a managed array the runtime marshals");
            case SigType.GenericParameter:
                return new(CarrierKind.GenericParameter, display, "an unresolved generic parameter");
            case SigType.Named named:
                return ClassifyNamed(named, display);
            default:
                return new(CarrierKind.Unresolved, display, "an unrecognised signature element");
        }
    }

    /// <summary>The Swift special kind of a type, if it is one (whatever the signature's convention).</summary>
    public static CarrierKind? SpecialKind(SigType type) => type.Unwrapped() switch
    {
        SigType.Named n when n.Is(SwiftInteropNamespace, "SwiftSelf") => CarrierKind.SwiftSelf,
        SigType.Named n when n.Is(SwiftInteropNamespace, "SwiftSelf`1") => CarrierKind.SwiftSelfGeneric,
        SigType.Named n when n.Is(SwiftInteropNamespace, "SwiftIndirectResult") => CarrierKind.SwiftIndirectResult,
        SigType.Named n when n.Is(SwiftInteropNamespace, "SwiftError") => CarrierKind.SwiftError,
        _ => null,
    };

    private static bool IsSwiftError(SigType type) => SpecialKind(type) == CarrierKind.SwiftError;

    private CarrierVerdict ClassifyNamed(SigType.Named named, string display)
    {
        switch (SpecialKind(named))
        {
            case CarrierKind.SwiftSelf:
                return new(CarrierKind.SwiftSelf, display, "SwiftSelf (self register)");
            case CarrierKind.SwiftSelfGeneric:
            {
                var physical = LegacySwiftSelfPhysical.Classify(named, _resolver);
                return new(CarrierKind.SwiftSelfGeneric, display, $"SwiftSelf<T> (typed self, {physical.Describe()})")
                {
                    SelfPhysical = physical,
                };
            }
            case CarrierKind.SwiftIndirectResult:
                return new(CarrierKind.SwiftIndirectResult, display, "SwiftIndirectResult (indirect-result register)");
            case CarrierKind.SwiftError:
                // SwiftError by value is not a valid carrier in either convention.
                return new(CarrierKind.SwiftError, display, "SwiftError by value");
        }

        if (!named.IsValueType)
            return new(CarrierKind.ManagedReference, display, "a managed reference the runtime marshals");
        if (!named.GenericArguments.IsDefaultOrEmpty && FindWrapper(named.FullName) is null)
            return new(CarrierKind.Aggregate, display, $"generic value-type instantiation {named.FullName}");

        var key = (named.Scope, named.FullName);
        if (_namedCache.TryGetValue(key, out var cached))
            return cached with { Display = display };
        var verdict = ClassifyValueType(named, display);
        _namedCache[key] = verdict;
        return verdict;
    }

    private CarrierVerdict ClassifyValueType(SigType.Named named, string display)
    {
        var resolved = _resolver.Resolve(named);
        if (resolved is null)
            return new(CarrierKind.Unresolved, display,
                $"value type {named.FullName} from '{(named.Scope.Length == 0 ? "this assembly" : named.Scope)}' could not be resolved");

        var (reader, handle) = resolved.Value;
        var def = reader.GetTypeDefinition(handle);
        if (IsEnum(reader, def))
        {
            // An enum's one instance field (value__) is its underlying integer.
            var underlying = InstanceFieldTypes(reader, def).ToList();
            return underlying.Count == 1 && SwiftTransitionScanner.CanonicalCarrier(underlying[0]) is { } integer
                ? new(CarrierKind.Primitive, display, Canonical: integer)
                : new(CarrierKind.Unresolved, display, $"enum {named.FullName} has no readable underlying integer");
        }

        if (FindWrapper(named.FullName) is not { } entry)
            return new(CarrierKind.Aggregate, display, $"aggregate {named.FullName}");
        if (CheckWrapperDefinition(reader, handle, entry) is { } problem)
            return new(CarrierKind.Aggregate, display, problem);
        var word = SwiftTransitionScanner.CanonicalCarrier(InstanceFieldTypes(reader, def).Single());
        return new(CarrierKind.Primitive, display, Canonical: word);
    }

    private static bool IsEnum(MetadataReader reader, TypeDefinition def)
    {
        var baseType = def.BaseType;
        if (baseType.IsNil)
            return false;
        string ns, name;
        switch (baseType.Kind)
        {
            case HandleKind.TypeReference:
                (ns, name, _) = TypeNames.OfReference(reader, (TypeReferenceHandle)baseType);
                break;
            case HandleKind.TypeDefinition:
                (ns, name) = TypeNames.OfDefinition(reader, (TypeDefinitionHandle)baseType);
                break;
            default:
                return false;
        }
        return ns == "System" && name == "Enum";
    }

    private static IEnumerable<SigType> InstanceFieldTypes(MetadataReader reader, TypeDefinition def)
    {
        var provider = new SigTypeProvider(reader);
        foreach (var fieldHandle in def.GetFields())
        {
            var field = reader.GetFieldDefinition(fieldHandle);
            if ((field.Attributes & FieldAttributes.Static) != 0)
                continue;
            yield return provider.DecodeField(field.Signature);
        }
    }
}

/// <summary>
/// Resolves type references to their definitions across a set of assemblies: the one being scanned
/// plus every <c>.dll</c> in the reference directories. Type forwarders are followed. Assemblies are
/// opened lazily and kept open until the resolver is disposed.
/// </summary>
public sealed class AssemblyResolver : IDisposable
{
    private readonly List<string> _directories;
    private readonly Dictionary<string, MetadataReader?> _byName = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<IDisposable> _owned = new();

    public AssemblyResolver(IEnumerable<string> referenceDirectories) =>
        _directories = referenceDirectories.Where(Directory.Exists).ToList();

    /// <summary>Registers an already-open assembly (the one being scanned, or an in-memory image).</summary>
    public void Add(MetadataReader reader)
    {
        if (reader.IsAssembly)
            _byName[reader.GetString(reader.GetAssemblyDefinition().Name)] = reader;
    }

    /// <summary>Opens an assembly from bytes and registers it; the resolver owns the image.</summary>
    public MetadataReader AddImage(byte[] image)
    {
        var pe = new PEReader(new MemoryStream(image, writable: false));
        _owned.Add(pe);
        var reader = pe.GetMetadataReader();
        Add(reader);
        return reader;
    }

    public (MetadataReader Reader, TypeDefinitionHandle Handle)? Resolve(SigType.Named named)
    {
        if (named.Handle.Kind == HandleKind.TypeDefinition)
            return (named.Reader, (TypeDefinitionHandle)named.Handle);
        if (named.Handle.Kind != HandleKind.TypeReference)
            return null;
        return ResolveReference(named.Reader, (TypeReferenceHandle)named.Handle, depth: 0);
    }

    private (MetadataReader, TypeDefinitionHandle)? ResolveReference(MetadataReader reader, TypeReferenceHandle handle, int depth)
    {
        if (depth > 16)
            return null;
        var reference = reader.GetTypeReference(handle);
        var name = reader.GetString(reference.Name);
        var scope = reference.ResolutionScope;
        switch (scope.Kind)
        {
            case HandleKind.TypeReference:
            {
                var outer = ResolveReference(reader, (TypeReferenceHandle)scope, depth + 1);
                if (outer is not { } o)
                    return null;
                foreach (var nested in o.Item1.GetTypeDefinition(o.Item2).GetNestedTypes())
                    if (o.Item1.GetString(o.Item1.GetTypeDefinition(nested).Name) == name)
                        return (o.Item1, nested);
                return null;
            }
            case HandleKind.AssemblyReference:
            {
                var assembly = reader.GetString(reader.GetAssemblyReference((AssemblyReferenceHandle)scope).Name);
                var target = Open(assembly);
                return target is null ? null : FindTopLevel(target, reader.GetString(reference.Namespace), name, depth);
            }
            default:
                return FindTopLevel(reader, reader.GetString(reference.Namespace), name, depth);
        }
    }

    private (MetadataReader, TypeDefinitionHandle)? FindTopLevel(MetadataReader reader, string ns, string name, int depth)
    {
        foreach (var handle in reader.TypeDefinitions)
        {
            var def = reader.GetTypeDefinition(handle);
            if (def.GetDeclaringType().IsNil && reader.GetString(def.Name) == name && reader.GetString(def.Namespace) == ns)
                return (reader, handle);
        }
        foreach (var exportedHandle in reader.ExportedTypes)
        {
            var exported = reader.GetExportedType(exportedHandle);
            if (!exported.IsForwarder || reader.GetString(exported.Name) != name || reader.GetString(exported.Namespace) != ns)
                continue;
            if (exported.Implementation.Kind != HandleKind.AssemblyReference)
                return null;
            var assembly = reader.GetString(reader.GetAssemblyReference((AssemblyReferenceHandle)exported.Implementation).Name);
            var target = Open(assembly);
            return target is null || depth > 16 ? null : FindTopLevel(target, ns, name, depth + 1);
        }
        return null;
    }

    private MetadataReader? Open(string assemblyName)
    {
        if (_byName.TryGetValue(assemblyName, out var known))
            return known;
        MetadataReader? reader = null;
        foreach (var directory in _directories)
        {
            var path = Path.Combine(directory, assemblyName + ".dll");
            if (!File.Exists(path))
                continue;
            var pe = new PEReader(File.OpenRead(path));
            _owned.Add(pe);
            if (pe.HasMetadata)
            {
                reader = pe.GetMetadataReader();
                break;
            }
        }
        _byName[assemblyName] = reader;
        return reader;
    }

    public void Dispose()
    {
        foreach (var owned in _owned)
            owned.Dispose();
        _owned.Clear();
    }
}
