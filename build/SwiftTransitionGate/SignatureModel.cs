// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;

namespace SwiftBindings.TransitionGate;

/// <summary>
/// A type as it appears in a metadata signature, decoded structurally (never by matching the text of
/// a signature). Named types keep the reader and handle they came from so they can be resolved to
/// their definition later.
/// </summary>
public abstract record SigType
{
    public sealed record Primitive(PrimitiveTypeCode Code) : SigType;

    public sealed record Pointer(SigType Element) : SigType;

    public sealed record ByRef(SigType Element) : SigType;

    public sealed record FunctionPointer(MethodSignature<SigType> Signature) : SigType
    {
        public TransitionConvention Convention => Conventions.OfFunctionPointer(Signature);
    }

    public sealed record SZArray(SigType Element) : SigType;

    public sealed record Array(SigType Element, int Rank) : SigType;

    public sealed record GenericParameter(bool IsMethodParameter, int Index) : SigType;

    public sealed record Modified(SigType Modifier, SigType Unmodified, bool IsRequired) : SigType;

    public sealed record Pinned(SigType Element) : SigType;

    /// <summary>
    /// A type definition, reference or generic instantiation. <see cref="Namespace"/> and
    /// <see cref="Name"/> are the outermost declaring type's namespace and the nested path
    /// (<c>Outer/Inner</c>); <see cref="Scope"/> is the defining assembly's simple name, or empty for
    /// a type defined in the module being read.
    /// </summary>
    public sealed record Named(
        string Namespace,
        string Name,
        string Scope,
        bool IsValueType,
        ImmutableArray<SigType> GenericArguments,
        MetadataReader Reader,
        EntityHandle Handle) : SigType
    {
        public string FullName => Namespace.Length == 0 ? Name : Namespace + "." + Name;

        public bool Is(string ns, string name) => Namespace == ns && Name == name;

        // The reader is identity, not value: two Named records are equal when they spell the same
        // type from the same scope, whichever reader decoded them.
        public bool Equals(Named? other) =>
            other is not null && Namespace == other.Namespace && Name == other.Name && Scope == other.Scope &&
            IsValueType == other.IsValueType && GenericArguments.SequenceEqual(other.GenericArguments);

        public override int GetHashCode() => HashCode.Combine(Namespace, Name, Scope);
    }

    /// <summary>Strips modifiers and <c>pinned</c> to the type the native signature carries.</summary>
    public SigType Unwrapped()
    {
        var type = this;
        while (true)
        {
            switch (type)
            {
                case Modified m:
                    type = m.Unmodified;
                    continue;
                case Pinned p:
                    type = p.Element;
                    continue;
                default:
                    return type;
            }
        }
    }

    /// <summary>Every type reachable from this one, including itself, element types, generic
    /// arguments and the parameters and return of nested function-pointer types.</summary>
    public IEnumerable<SigType> SelfAndDescendants()
    {
        yield return this;
        IEnumerable<SigType> children = this switch
        {
            Pointer p => new[] { p.Element },
            ByRef r => new[] { r.Element },
            SZArray a => new[] { a.Element },
            Array a => new[] { a.Element },
            Modified m => new[] { m.Unmodified },
            Pinned p => new[] { p.Element },
            Named n => n.GenericArguments,
            FunctionPointer f => f.Signature.ParameterTypes.Prepend(f.Signature.ReturnType),
            _ => System.Array.Empty<SigType>(),
        };
        foreach (var child in children)
            foreach (var descendant in child.SelfAndDescendants())
                yield return descendant;
    }
}

/// <summary>The calling convention of one managed-to-native or native-to-managed transition.</summary>
public enum TransitionConvention
{
    /// <summary>A managed signature: not a transition (<c>delegate*&lt;…&gt;</c>).</summary>
    Managed,

    /// <summary>The platform default unmanaged convention (C on every Apple target).</summary>
    PlatformDefault,
    Cdecl,
    Stdcall,
    Thiscall,
    Fastcall,
    Swift,
}

public static class Conventions
{
    public const string CompilerServicesNamespace = "System.Runtime.CompilerServices";

    /// <summary>
    /// Maps the <c>CallConv*</c> type names named by an <c>UnmanagedCallConv</c> or
    /// <c>UnmanagedCallersOnly</c> attribute, or by the modopts of an unmanaged function pointer,
    /// to a base convention. Modifier conventions (<c>CallConvSuppressGCTransition</c>,
    /// <c>CallConvMemberFunction</c>) do not change it. Returns null when none is named.
    /// </summary>
    public static TransitionConvention? FromCallConvNames(IEnumerable<string> fullNames)
    {
        TransitionConvention? result = null;
        foreach (var name in fullNames)
        {
            TransitionConvention? named = name switch
            {
                CompilerServicesNamespace + ".CallConvSwift" => TransitionConvention.Swift,
                CompilerServicesNamespace + ".CallConvCdecl" => TransitionConvention.Cdecl,
                CompilerServicesNamespace + ".CallConvStdcall" => TransitionConvention.Stdcall,
                CompilerServicesNamespace + ".CallConvThiscall" => TransitionConvention.Thiscall,
                CompilerServicesNamespace + ".CallConvFastcall" => TransitionConvention.Fastcall,
                _ => null,
            };
            // Swift dominates: any Swift-convention transition is one this gate must judge.
            if (named is { } n && (result is null || n == TransitionConvention.Swift))
                result = n;
        }
        return result;
    }

    /// <summary>The convention of a function-pointer type, from its header and return modopts.</summary>
    public static TransitionConvention OfFunctionPointer(MethodSignature<SigType> signature) =>
        signature.Header.CallingConvention switch
        {
            SignatureCallingConvention.Default or SignatureCallingConvention.VarArgs => TransitionConvention.Managed,
            SignatureCallingConvention.CDecl => TransitionConvention.Cdecl,
            SignatureCallingConvention.StdCall => TransitionConvention.Stdcall,
            SignatureCallingConvention.ThisCall => TransitionConvention.Thiscall,
            SignatureCallingConvention.FastCall => TransitionConvention.Fastcall,
            SignatureCallingConvention.Unmanaged =>
                FromCallConvNames(ReturnModifiers(signature.ReturnType)) ?? TransitionConvention.PlatformDefault,
            _ => TransitionConvention.PlatformDefault,
        };

    private static IEnumerable<string> ReturnModifiers(SigType returnType)
    {
        var type = returnType;
        while (type is SigType.Modified m)
        {
            if (m.Modifier is SigType.Named named)
                yield return named.FullName;
            type = m.Unmodified;
        }
    }
}

/// <summary>Builds <see cref="SigType"/> values from metadata signatures for one reader.</summary>
public sealed class SigTypeProvider : ISignatureTypeProvider<SigType, object?>
{
    private readonly MetadataReader _reader;

    public SigTypeProvider(MetadataReader reader) => _reader = reader;

    public SigType GetPrimitiveType(PrimitiveTypeCode typeCode) => new SigType.Primitive(typeCode);

    public SigType GetPointerType(SigType elementType) => new SigType.Pointer(elementType);

    public SigType GetByReferenceType(SigType elementType) => new SigType.ByRef(elementType);

    public SigType GetFunctionPointerType(MethodSignature<SigType> signature) => new SigType.FunctionPointer(signature);

    public SigType GetSZArrayType(SigType elementType) => new SigType.SZArray(elementType);

    public SigType GetArrayType(SigType elementType, ArrayShape shape) => new SigType.Array(elementType, shape.Rank);

    public SigType GetGenericMethodParameter(object? genericContext, int index) => new SigType.GenericParameter(true, index);

    public SigType GetGenericTypeParameter(object? genericContext, int index) => new SigType.GenericParameter(false, index);

    public SigType GetModifiedType(SigType modifier, SigType unmodifiedType, bool isRequired) =>
        new SigType.Modified(modifier, unmodifiedType, isRequired);

    public SigType GetPinnedType(SigType elementType) => new SigType.Pinned(elementType);

    public SigType GetGenericInstantiation(SigType genericType, ImmutableArray<SigType> typeArguments) =>
        genericType is SigType.Named named ? named with { GenericArguments = typeArguments } : genericType;

    public SigType GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
    {
        var (ns, name) = TypeNames.OfDefinition(reader, handle);
        return new SigType.Named(ns, name, "", rawTypeKind == (byte)SignatureTypeKind.ValueType,
            ImmutableArray<SigType>.Empty, reader, handle);
    }

    public SigType GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
    {
        var (ns, name, scope) = TypeNames.OfReference(reader, handle);
        return new SigType.Named(ns, name, scope, rawTypeKind == (byte)SignatureTypeKind.ValueType,
            ImmutableArray<SigType>.Empty, reader, handle);
    }

    public SigType GetTypeFromSpecification(MetadataReader reader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind) =>
        reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);

    public MethodSignature<SigType> DecodeMethod(BlobHandle blob)
    {
        var reader = _reader.GetBlobReader(blob);
        return new SignatureDecoder<SigType, object?>(this, _reader, null).DecodeMethodSignature(ref reader);
    }

    public SigType DecodeField(BlobHandle blob)
    {
        var reader = _reader.GetBlobReader(blob);
        return new SignatureDecoder<SigType, object?>(this, _reader, null).DecodeFieldSignature(ref reader);
    }

    public ImmutableArray<SigType> DecodeLocals(StandaloneSignatureHandle handle)
    {
        var reader = _reader.GetBlobReader(_reader.GetStandaloneSignature(handle).Signature);
        return new SignatureDecoder<SigType, object?>(this, _reader, null).DecodeLocalSignature(ref reader);
    }
}

/// <summary>Namespace/name/scope spelling of type definitions and references.</summary>
public static class TypeNames
{
    public static (string Namespace, string Name) OfDefinition(MetadataReader reader, TypeDefinitionHandle handle)
    {
        var def = reader.GetTypeDefinition(handle);
        var name = reader.GetString(def.Name);
        var declaring = def.GetDeclaringType();
        if (declaring.IsNil)
            return (reader.GetString(def.Namespace), name);
        var (ns, outer) = OfDefinition(reader, declaring);
        return (ns, outer + "/" + name);
    }

    public static (string Namespace, string Name, string Scope) OfReference(MetadataReader reader, TypeReferenceHandle handle)
    {
        var reference = reader.GetTypeReference(handle);
        var name = reader.GetString(reference.Name);
        var scope = reference.ResolutionScope;
        switch (scope.Kind)
        {
            case HandleKind.TypeReference:
            {
                var (ns, outer, assembly) = OfReference(reader, (TypeReferenceHandle)scope);
                return (ns, outer + "/" + name, assembly);
            }
            case HandleKind.AssemblyReference:
                return (reader.GetString(reference.Namespace), name,
                    reader.GetString(reader.GetAssemblyReference((AssemblyReferenceHandle)scope).Name));
            default:
                // ModuleDefinition / ModuleReference / nil: the type lives in this assembly.
                return (reader.GetString(reference.Namespace), name, "");
        }
    }

    /// <summary>A short, C#-flavoured spelling of a signature type for reports.</summary>
    public static string Display(SigType type) => type switch
    {
        SigType.Primitive p => p.Code switch
        {
            PrimitiveTypeCode.Void => "void",
            PrimitiveTypeCode.Boolean => "bool",
            PrimitiveTypeCode.Char => "char",
            PrimitiveTypeCode.SByte => "sbyte",
            PrimitiveTypeCode.Byte => "byte",
            PrimitiveTypeCode.Int16 => "short",
            PrimitiveTypeCode.UInt16 => "ushort",
            PrimitiveTypeCode.Int32 => "int",
            PrimitiveTypeCode.UInt32 => "uint",
            PrimitiveTypeCode.Int64 => "long",
            PrimitiveTypeCode.UInt64 => "ulong",
            PrimitiveTypeCode.Single => "float",
            PrimitiveTypeCode.Double => "double",
            PrimitiveTypeCode.IntPtr => "nint",
            PrimitiveTypeCode.UIntPtr => "nuint",
            PrimitiveTypeCode.String => "string",
            PrimitiveTypeCode.Object => "object",
            PrimitiveTypeCode.TypedReference => "TypedReference",
            _ => p.Code.ToString(),
        },
        SigType.Pointer p => Display(p.Element) + "*",
        SigType.ByRef r => "ref " + Display(r.Element),
        SigType.SZArray a => Display(a.Element) + "[]",
        SigType.Array a => Display(a.Element) + "[" + new string(',', Math.Max(0, a.Rank - 1)) + "]",
        SigType.GenericParameter g => (g.IsMethodParameter ? "!!" : "!") + g.Index,
        SigType.Modified m => Display(m.Unmodified),
        SigType.Pinned p => Display(p.Element),
        SigType.Named n => ShortName(n) + (n.GenericArguments.IsDefaultOrEmpty
            ? ""
            : "<" + string.Join(", ", n.GenericArguments.Select(Display)) + ">"),
        SigType.FunctionPointer f => "delegate* " + ConventionKeyword(f.Convention) +
            "<" + string.Join(", ", f.Signature.ParameterTypes.Select(Display).Append(Display(f.Signature.ReturnType))) + ">",
        _ => type.ToString(),
    };

    /// <summary>A method signature spelled as <c>[Convention] Return (Params…)</c>.</summary>
    public static string Display(TransitionConvention convention, MethodSignature<SigType> signature) =>
        $"[{convention}] {Display(signature.ReturnType)} ({string.Join(", ", signature.ParameterTypes.Select(Display))})";

    private static string ConventionKeyword(TransitionConvention convention) => convention switch
    {
        TransitionConvention.Managed => "managed",
        TransitionConvention.PlatformDefault => "unmanaged",
        _ => $"unmanaged[{convention}]",
    };

    private static string ShortName(SigType.Named n)
    {
        var name = n.Name;
        var slash = name.LastIndexOf('/');
        if (slash >= 0)
            name = name.Replace('/', '.');
        var tick = name.IndexOf('`');
        return tick >= 0 ? name.Substring(0, tick) : name;
    }
}
