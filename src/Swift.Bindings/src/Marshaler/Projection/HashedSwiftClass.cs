// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// A Swift class (NSObject-rooted or not) that a collection of the module holds as a dictionary key
/// or set element. The collection is one over the class's wrapper, as an array of it is, and is
/// hashed and compared through the class's own <c>Hashable</c> conformance. The wrapper of the module
/// exports the metadata of <c>Set&lt;Class&gt;</c>, which holds that conformance's witness table as
/// the compiler resolves it (declared by the class, inherited from a superclass, or NSObject's), and
/// the module initializer registers the table for the wrapper type
/// (<c>SwiftObjectKeys.RegisterHashableClass</c>).
/// </summary>
/// <remarks>
/// A bound generic class (<c>Box&lt;Swift.String&gt;</c>) is one such class per instantiation: its
/// identity is spelled with its arguments, its wrapper is closed over them, and its witness table is
/// the instantiation's (a conditional conformance included, which the compiler instantiates for the
/// <c>Set</c>).
/// </remarks>
/// <param name="Identity">The class's Swift type, which names the exported <c>Set</c> accessor.</param>
/// <param name="WrapperType">The C# wrapper type, fully qualified with <c>global::</c>.</param>
/// <param name="ReferencedTypeNames">The module-qualified Swift names of the class and, for a bound
/// generic class, of every type its arguments name: each must have a wrapper for the registration to
/// compile.</param>
public sealed record HashedSwiftClass(ObjectKeyIdentity Identity, string WrapperType, IReadOnlyList<string> ReferencedTypeNames)
{
    /// <summary>A non-generic class, which names no type but itself.</summary>
    public HashedSwiftClass(ObjectKeyIdentity identity, string wrapperType)
        : this(identity, wrapperType, new[] { identity.SwiftTypeName })
    {
    }

    /// <summary>
    /// The C# import of the exported <c>Set</c> accessor: its symbol, which hashes the Swift name, so
    /// two classes whose names flatten alike (<c>Outer.Key</c>, <c>Outer_Key</c>) keep two imports.
    /// </summary>
    public string AccessorMethodName(string moduleName) => Identity.AccessorSymbol(moduleName);

    /// <summary>
    /// The hashed class <paramref name="typeSpec"/> names, carried as <paramref name="wrapperType"/>,
    /// or <c>null</c> when some part of its Swift name is not module-qualified (the wrapper cannot
    /// spell it; the wrapper type's own conformance lookup then applies).
    /// </summary>
    public static HashedSwiftClass? For(NamedTypeSpec typeSpec, string wrapperType)
        => ObjectKeyIdentity.For(typeSpec) is { } identity
            ? new HashedSwiftClass(identity, wrapperType.StartsWith("global::", StringComparison.Ordinal) ? wrapperType : "global::" + wrapperType,
                ReferencedNames(typeSpec))
            : null;

    private static IReadOnlyList<string> ReferencedNames(NamedTypeSpec typeSpec)
    {
        var names = new List<string>();
        Collect(typeSpec);
        return names;

        void Collect(TypeSpec spec)
        {
            if (spec is not NamedTypeSpec named)
                return;
            if (!names.Contains(named.Name))
                names.Add(named.Name);
            foreach (var argument in named.GenericParameters)
                Collect(argument);
        }
    }
}
