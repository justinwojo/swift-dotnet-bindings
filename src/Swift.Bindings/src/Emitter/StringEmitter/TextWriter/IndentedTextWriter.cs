// Copyright (c) Microsoft Corporation.
// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.CodeDom.Compiler;

namespace BindingsGeneration
{
    /// <summary>
    /// Represents an class for writing C# source code.
    /// </summary>
    public class CSharpWriter : IndentedTextWriter
    {
        private readonly StringWriter _innerWriter;

        /// <summary>
        /// Initializes a new instance of the <see cref="CSharpWriter"/> class.
        /// </summary>
        /// <param name="writer">The writer.</param>
        public CSharpWriter(StringWriter writer) : base(writer)
        {
            _innerWriter = writer;
        }

        /// <summary>
        /// A position in the C# output buffer that <see cref="RollbackTo"/> can return to.
        /// Captures both the buffer length and the indent depth so a rolled-back writer
        /// resumes exactly where it was when the checkpoint was taken.
        /// </summary>
        public readonly struct WriterCheckpoint
        {
            internal WriterCheckpoint(int length, int indent)
            {
                Length = length;
                Indent = indent;
            }

            internal int Length { get; }
            internal int Indent { get; }
        }

        /// <summary>
        /// Captures the current end-of-buffer position so a later <see cref="RollbackTo"/>
        /// can discard everything written in between. The wrapper-symbol contract path uses
        /// this to emit a member into the live buffer and then erase it byte-for-byte when
        /// the contract trips mid-emission — an in-emission transactional rollback that
        /// replaces the old generate-then-regex-strip recovery. Required for the method
        /// site because async <c>@_cdecl</c> wrappers register their symbol *inside*
        /// <c>EmitMethod</c> (after the public signature is already written), so a
        /// predict-before-emit gate cannot tell a valid async method from a silent bail.
        /// </summary>
        public WriterCheckpoint Checkpoint()
        {
            Flush();
            return new WriterCheckpoint(_innerWriter.GetStringBuilder().Length, Indent);
        }

        /// <summary>
        /// Truncates the C# output buffer back to <paramref name="checkpoint"/> and restores
        /// the indent depth, discarding everything written since the checkpoint was taken.
        /// </summary>
        public void RollbackTo(WriterCheckpoint checkpoint)
        {
            Flush();
            var builder = _innerWriter.GetStringBuilder();
            if (builder.Length > checkpoint.Length)
            {
                builder.Length = checkpoint.Length;
            }
            Indent = checkpoint.Indent;
        }

        /// <summary>
        /// Truncates the buffer back to <paramref name="checkpoint"/> exactly like
        /// <see cref="RollbackTo"/>, but first returns everything written since. A caller uses this
        /// to inject a prefix ahead of an already-emitted member — e.g. a compile-time-visible
        /// <c>[Obsolete(error: true)]</c> attribute in front of a member whose signature (and, for
        /// async, whose faulting body) is already written and cannot be re-emitted without
        /// duplicating its Swift side. Re-append the returned text verbatim with
        /// <see cref="AppendCaptured"/> after emitting the prefix.
        /// </summary>
        public string RollbackToAndCapture(WriterCheckpoint checkpoint)
        {
            Flush();
            var builder = _innerWriter.GetStringBuilder();
            string captured = string.Empty;
            if (builder.Length > checkpoint.Length)
            {
                captured = builder.ToString(checkpoint.Length, builder.Length - checkpoint.Length);
                builder.Length = checkpoint.Length;
            }
            Indent = checkpoint.Indent;
            return captured;
        }

        /// <summary>
        /// Appends text captured by <see cref="RollbackToAndCapture"/> verbatim, bypassing indent
        /// processing — the captured text already carries its own leading indentation, so re-running
        /// it through the indenting writer would double-indent every line.
        /// </summary>
        public void AppendCaptured(string captured)
        {
            Flush();
            _innerWriter.GetStringBuilder().Append(captured);
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
