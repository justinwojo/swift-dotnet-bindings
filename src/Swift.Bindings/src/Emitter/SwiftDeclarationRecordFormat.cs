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
    }
}
