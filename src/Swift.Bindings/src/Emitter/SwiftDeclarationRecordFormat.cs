// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration
{
    /// <summary>
    /// The shape of a module's Swift declaration record (<c>{namespace}.swift-declarations.json</c>),
    /// shared by the generator that writes it (<c>SwiftDeclarationMapEmitter</c>) and the package merge
    /// that compares it across copies (<c>SwiftBindingPackageMerge</c>).
    /// </summary>
    public static class SwiftDeclarationRecordFormat
    {
        /// <summary>Bumped only when the on-disk shape changes; the merge refuses a schema it does not know.</summary>
        public const int SchemaVersion = 1;

        /// <summary>The suffix of every record's file name.</summary>
        public const string FileSuffix = ".swift-declarations.json";

        /// <summary>The file name of a module's record.</summary>
        public static string FileName(string @namespace) => @namespace + FileSuffix;

        /// <summary>
        /// The <c>AssemblyMetadataAttribute</c> key of a record's witness: the assembly a module is
        /// generated into names the module's record and the SHA-256 of its bytes, so a copy whose record
        /// was not packed, or was packed from another generation, is refused by the merge instead of
        /// leaving the module's names unchecked when no copy carries one.
        /// </summary>
        public const string AttributeKey = "SwiftBindings.SwiftDeclarations";

        /// <summary>A record's hash: <c>sha256:</c> and 64 lowercase hex digits.</summary>
        public static string Hash(byte[] record) =>
            "sha256:" + System.Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(record)).ToLowerInvariant();

        /// <summary>The witness of <paramref name="module"/>'s record <paramref name="fileName"/> with <paramref name="hash"/>.</summary>
        public static string Witness(string module, string fileName, string hash) =>
            $"module={module};record={fileName};hash={hash}";

        /// <summary>Reads a witness <see cref="Witness"/> wrote; false for anything else.</summary>
        public static bool TryParseWitness(string value, out string module, out string record, out string hash)
        {
            module = record = hash = "";
            var parts = (value ?? "").Split(';');
            if (parts.Length != 3 || !parts[0].StartsWith("module=", System.StringComparison.Ordinal)
                || !parts[1].StartsWith("record=", System.StringComparison.Ordinal) || !parts[2].StartsWith("hash=sha256:", System.StringComparison.Ordinal))
                return false;
            module = parts[0]["module=".Length..];
            record = parts[1]["record=".Length..];
            hash = parts[2]["hash=".Length..];
            return module.Length > 0 && record.EndsWith(FileSuffix, System.StringComparison.Ordinal)
                   && record.IndexOfAny(new[] { '/', '\\' }) < 0 && hash.Length == "sha256:".Length + 64;
        }

        /// <summary>The C# attribute declaring <paramref name="witness"/> on the assembly.</summary>
        public static string AttributeSource(string witness) =>
            "// The Swift declaration record of this module and its hash, which the package merge checks the\n" +
            "// packed record against (SWIFTBIND138).\n" +
            $"[assembly: global::System.Reflection.AssemblyMetadataAttribute(\"{AttributeKey}\", \"{witness}\")]\n";
    }
}
