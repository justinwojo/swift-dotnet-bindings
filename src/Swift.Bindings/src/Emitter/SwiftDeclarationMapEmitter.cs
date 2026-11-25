// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace BindingsGeneration
{
    /// <summary>
    /// Emits <c>{namespace}.swift-declarations.json</c>: which Swift declaration each emitted public C#
    /// member and type stands for. A C# name is not a Swift declaration's identity: a nested type named
    /// like its container, two members whose names differ only by case, and overloads that project to one
    /// C# signature are all renamed, and the rename depends on the declaration's siblings. A newer SDK
    /// that adds a sibling can therefore give an existing C# name to a different Swift declaration. One
    /// package carries a copy per toolchain, and a library compiled against one copy runs against
    /// whichever copy the app selects, so the package merge compares these records across copies and
    /// refuses a C# name that stands for different Swift declarations
    /// (<c>SwiftBindingPackageMerge</c>, SWIFTBIND138). The SDK packs the file beside the copy's
    /// declaration under <c>buildTransitive/&lt;tfm&gt;/</c>; nothing reads it at consumer build time.
    /// </summary>
    public static class SwiftDeclarationMapEmitter
    {
        /// <summary>
        /// A Swift declaration as the record names it: its kind, whether it is static, its module-qualified
        /// path and, for a function or subscript, its argument labels
        /// (<c>func Kit.Outer.outer.ping(times:)</c>, <c>static var Kit.Outer.shared</c>,
        /// <c>init Kit.Outer.outer(_:)</c>). Parameter and result types are left out on purpose: the C#
        /// signature the record is keyed by already carries their projection, and a Swift type that is
        /// spelled differently by another SDK (a closure that became <c>@Sendable</c>) is the same
        /// declaration, not a different one. Returns null for a declaration kind the record does not name.
        /// </summary>
        public static string? Describe(BaseDecl declaration)
        {
            switch (declaration)
            {
                case MethodDecl method:
                    {
                        var id = DeclIdFactory.ForMethod(method);
                        var kind = method.IsConstructor ? "init" : method.MethodType == MethodType.Static ? "static func" : "func";
                        // Index 0 of a method's signature is its result.
                        return $"{kind} {id.QualifiedPath}({Labels(id.ParameterLabels.Skip(1))})";
                    }
                case PropertyDecl property:
                    return $"{(property.IsStatic ? "static var" : "var")} {DeclIdFactory.ForProperty(property).QualifiedPath}";
                case SubscriptDecl subscript:
                    {
                        var id = DeclIdFactory.ForSubscript(subscript);
                        return $"{(subscript.IsStatic ? "static subscript" : "subscript")} {id.QualifiedPath}({Labels(id.ParameterLabels)})";
                    }
                case TypeDecl type:
                    return $"type {type.SwiftTypeName.ModuleQualifiedName}";
                default:
                    return null;
            }
        }

        /// <summary>
        /// Appended to the declaration of a native-width convenience overload (an <c>int</c> forwarder
        /// to the <c>nint</c> member a Swift <c>Int</c> projects to), which is the same Swift declaration
        /// reached another way: a newer SDK's overload taking <c>Int32</c> claims that C# signature
        /// with the same labels, and only this marker tells the two apart.
        /// </summary>
        public const string NarrowedForwarderMarker = " (native-width forwarder)";

        /// <summary>
        /// Records every type of <paramref name="module"/> the binding emitted, members or not: a type
        /// with no recorded member (an empty nested enum) still has a C# name a newer SDK can move.
        /// </summary>
        public static void RecordTypes(ModuleDecl module, ModuleEmissionContext emissionCtx, ITypeDatabase typeDatabase)
        {
            var pending = new Stack<TypeDecl>(module.Types);
            while (pending.Count > 0)
            {
                var type = pending.Pop();
                foreach (var nested in type.Types)
                    pending.Push(nested);
                if (type.IsModuleInternal || type.IsSpiProtected
                    || !typeDatabase.TryGetTypeRecord(type.SwiftTypeName, out _)
                    || ReportCollector.IsTypeEmitted(type.SwiftTypeName.ModuleQualifiedName) == false)
                    continue;
                emissionCtx.RecordSwiftTypeDeclaration(type, typeDatabase);
            }
        }

        private static string Labels(IEnumerable<string> labels)
        {
            var builder = new StringBuilder();
            foreach (var label in labels)
                builder.Append(label.Length == 0 ? "_" : label).Append(':');
            return builder.ToString();
        }

        /// <summary>The record as written to disk.</summary>
        public static string Serialize(SwiftDeclarationMapDocument document)
            => JsonSerializer.Serialize(document, SwiftDeclarationMapJsonContext.Default.SwiftDeclarationMapDocument);

        /// <summary>
        /// Writes the module's record next to the generated <c>.cs</c> and returns its path, or null with
        /// no emission context. Written even when empty, so a stale record of an earlier run never remains.
        /// </summary>
        public static string? Emit(string moduleName, string @namespace, ModuleEmissionContext? emissionCtx,
            string outputDirectory, ILogger logger)
        {
            if (emissionCtx is null) return null;
            var document = new SwiftDeclarationMapDocument
            {
                SchemaVersion = SwiftDeclarationRecordFormat.SchemaVersion,
                Module = moduleName,
                Types = new SortedDictionary<string, string>(emissionCtx.SwiftTypeDeclarations.ToDictionary(kv => kv.Key, kv => kv.Value), StringComparer.Ordinal),
                Members = new SortedDictionary<string, string>(emissionCtx.SwiftMemberDeclarations.ToDictionary(kv => kv.Key, kv => kv.Value), StringComparer.Ordinal),
            };
            var path = Path.Combine(outputDirectory, SwiftDeclarationRecordFormat.FileName(@namespace));
            File.WriteAllText(path, Serialize(document));
            logger.LogInformation($"Wrote Swift declaration record ({document.Types.Count} types, {document.Members.Count} members) to {path}");
            return path;
        }
    }

    /// <summary>
    /// On-disk shape of <c>{namespace}.swift-declarations.json</c>. Keys are the module-relative C# names
    /// the API manifest uses: a type's nested C# path, a member's API-manifest signature.
    /// </summary>
    public sealed class SwiftDeclarationMapDocument
    {
        [JsonPropertyName("schema_version")] public int SchemaVersion { get; set; }
        [JsonPropertyName("module")] public string Module { get; set; } = "";
        [JsonPropertyName("types")] public SortedDictionary<string, string> Types { get; set; } = new(StringComparer.Ordinal);
        [JsonPropertyName("members")] public SortedDictionary<string, string> Members { get; set; } = new(StringComparer.Ordinal);
    }

    [JsonSourceGenerationOptions(WriteIndented = true)]
    [JsonSerializable(typeof(SwiftDeclarationMapDocument))]
    internal partial class SwiftDeclarationMapJsonContext : JsonSerializerContext
    {
    }
}
