// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CodeDom.Compiler;

namespace BindingsGeneration
{
    /// <summary>
    /// Represents an class for writing C# source code.
    /// </summary>
    public class CSharpWriter : IndentedTextWriter
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="CSharpWriter"/> class.
        /// </summary>
        /// <param name="writer">The writer.</param>
        public CSharpWriter(StringWriter writer) : base(writer)
        {
        }
    }

    /// <summary>
    /// Represents an class for writing Swift source code.
    /// </summary>
    public class SwiftWriter : IndentedTextWriter
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="SwiftWriter"/> class.
        /// </summary>
        /// <param name="writer">The writer.</param>
        public SwiftWriter(StringWriter writer) : base(writer)
        {
        }
    }
}
