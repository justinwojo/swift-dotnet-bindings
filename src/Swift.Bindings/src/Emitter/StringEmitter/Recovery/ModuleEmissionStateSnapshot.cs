// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text;

namespace BindingsGeneration;

/// <summary>
/// Captures the whole mutable state of one <see cref="ModuleEmissionContext"/> so a re-run of
/// emission can start from the exact pre-emission shape. Dedup sets, helper latches, registered
/// wrapper symbols, counters, and collected lines all influence later decisions; leaving any of
/// them dirty after a failed attempt makes the retry produce different output than a clean run.
/// </summary>
/// <remarks>
/// Collections are restored in place (<c>Clear</c> then re-add the captured entries in the
/// captured order). Callers hold <c>IReadOnly*</c> views onto the same collection instances, and
/// many fields are <c>readonly</c>, so replacing the collection object would leave those views
/// pointing at a dead instance.
/// </remarks>
internal sealed class ModuleEmissionStateSnapshot
{
    // Reflection is used here deliberately: ModuleEmissionContext state is private by design,
    // and a hand-written field-by-field mirror would silently rot as fields are added. Walking
    // every instance field reflectively keeps Capture/Restore exhaustive by construction; a unit
    // test further asserts completeness against the live type.

    /// <summary>
    /// Anchor type for the trimmer: keeps every field of <see cref="ModuleEmissionContext"/> so
    /// the reflective walk still sees them under AOT/trimming.
    /// </summary>
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)]
    private static readonly Type ContextType = typeof(ModuleEmissionContext);

    private static readonly FieldHandler[] Handlers = BuildHandlers();

    private readonly ModuleEmissionContext _context;
    private readonly object?[] _capturedValues;

    private ModuleEmissionStateSnapshot(ModuleEmissionContext context, object?[] capturedValues)
    {
        _context = context;
        _capturedValues = capturedValues;
    }

    /// <summary>
    /// Number of instance fields the reflective walk covers (one handler per field).
    /// </summary>
    internal static int CoveredFieldCount => Handlers.Length;

    /// <summary>
    /// How each field is classified, for the test that guards the one way this walk can decay.
    /// Every field gets a handler, so nothing is ever skipped outright — but a collection shape the
    /// factory does not recognise falls through to the scalar handler, which restores the reference
    /// and leaves the abandoned attempt's entries sitting inside it. That is silent and it changes
    /// the retry's output, so the test requires content restoration for anything holding entries.
    /// </summary>
    internal static IReadOnlyList<(string Field, Type FieldType, bool RestoresContents)> DescribeCoverage()
    {
        var described = new List<(string, Type, bool)>(Handlers.Length);
        foreach (FieldHandler handler in Handlers)
        {
            described.Add((handler.FieldName, handler.FieldType, handler is not ScalarHandler));
        }

        return described;
    }

    /// <summary>
    /// Snapshots every instance field of <paramref name="context"/> (collections by entry copy,
    /// scalars by value/reference).
    /// </summary>
    public static ModuleEmissionStateSnapshot Capture(ModuleEmissionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        object?[] values = new object?[Handlers.Length];
        for (int i = 0; i < Handlers.Length; i++)
        {
            values[i] = Handlers[i].Capture(context);
        }

        return new ModuleEmissionStateSnapshot(context, values);
    }

    /// <summary>
    /// Writes every captured value back onto the same <see cref="ModuleEmissionContext"/> instance
    /// that was captured. Safe to call repeatedly; each call re-applies the same pre-image.
    /// </summary>
    public void Restore()
    {
        for (int i = 0; i < Handlers.Length; i++)
        {
            Handlers[i].Restore(_context, _capturedValues[i]);
        }
    }

    private static FieldHandler[] BuildHandlers()
    {
        FieldInfo[] fields = ContextType.GetFields(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        FieldHandler[] handlers = new FieldHandler[fields.Length];
        for (int i = 0; i < fields.Length; i++)
        {
            handlers[i] = CreateHandler(fields[i]);
        }

        return handlers;
    }

    private static FieldHandler CreateHandler(FieldInfo field)
    {
        Type fieldType = field.FieldType;

        if (typeof(StringBuilder).IsAssignableFrom(fieldType))
        {
            return new StringBuilderHandler(field);
        }

        if (IsClosedGeneric(fieldType, typeof(Stack<>)))
        {
            return new StackHandler(field);
        }

        if (typeof(IDictionary).IsAssignableFrom(fieldType))
        {
            return new DictionaryHandler(field);
        }

        if (typeof(IList).IsAssignableFrom(fieldType))
        {
            return new ListHandler(field);
        }

        if (IsClosedGeneric(fieldType, typeof(HashSet<>))
            || IsClosedGeneric(fieldType, typeof(SortedSet<>)))
        {
            return new SetHandler(field);
        }

        return new ScalarHandler(field);
    }

    private static bool IsClosedGeneric(Type type, Type openGeneric)
    {
        return type.IsGenericType && type.GetGenericTypeDefinition() == openGeneric;
    }

    private abstract class FieldHandler
    {
        protected FieldHandler(FieldInfo field)
        {
            Field = field;
        }

        protected FieldInfo Field { get; }

        public string FieldName => Field.Name;

        public Type FieldType => Field.FieldType;

        public abstract object? Capture(ModuleEmissionContext context);

        public abstract void Restore(ModuleEmissionContext context, object? captured);
    }

    /// <summary>Non-collection field: store the value and assign it back.</summary>
    private sealed class ScalarHandler : FieldHandler
    {
        public ScalarHandler(FieldInfo field)
            : base(field)
        {
        }

        public override object? Capture(ModuleEmissionContext context) => Field.GetValue(context);

        public override void Restore(ModuleEmissionContext context, object? captured) =>
            Field.SetValue(context, captured);
    }

    /// <summary>
    /// Capture payload for a collection field: the live instance (so restore can re-point a
    /// reassigned non-readonly field) plus a shallow entry copy.
    /// </summary>
    private sealed class CollectionCapture
    {
        public CollectionCapture(object? instance, object? entries)
        {
            Instance = instance;
            Entries = entries;
        }

        public object? Instance { get; }
        public object? Entries { get; }
    }

    private sealed class ListHandler : FieldHandler
    {
        public ListHandler(FieldInfo field)
            : base(field)
        {
        }

        public override object? Capture(ModuleEmissionContext context)
        {
            object? current = Field.GetValue(context);
            if (current is null)
            {
                return new CollectionCapture(null, null);
            }

            IList list = (IList)current;
            object?[] copy = new object?[list.Count];
            for (int i = 0; i < list.Count; i++)
            {
                copy[i] = list[i];
            }

            return new CollectionCapture(current, copy);
        }

        public override void Restore(ModuleEmissionContext context, object? captured)
        {
            CollectionCapture capture = (CollectionCapture)captured!;
            if (capture.Instance is null)
            {
                Field.SetValue(context, null);
                return;
            }

            IList list = (IList)capture.Instance;
            list.Clear();
            object?[] items = (object?[])capture.Entries!;
            for (int i = 0; i < items.Length; i++)
            {
                list.Add(items[i]);
            }

            Field.SetValue(context, capture.Instance);
        }
    }

    private sealed class DictionaryHandler : FieldHandler
    {
        public DictionaryHandler(FieldInfo field)
            : base(field)
        {
        }

        public override object? Capture(ModuleEmissionContext context)
        {
            object? current = Field.GetValue(context);
            if (current is null)
            {
                return new CollectionCapture(null, null);
            }

            IDictionary dictionary = (IDictionary)current;
            DictionaryEntry[] copy = new DictionaryEntry[dictionary.Count];
            int index = 0;
            foreach (DictionaryEntry entry in dictionary)
            {
                copy[index++] = entry;
            }

            return new CollectionCapture(current, copy);
        }

        public override void Restore(ModuleEmissionContext context, object? captured)
        {
            CollectionCapture capture = (CollectionCapture)captured!;
            if (capture.Instance is null)
            {
                Field.SetValue(context, null);
                return;
            }

            IDictionary dictionary = (IDictionary)capture.Instance;
            dictionary.Clear();
            DictionaryEntry[] entries = (DictionaryEntry[])capture.Entries!;
            for (int i = 0; i < entries.Length; i++)
            {
                dictionary[entries[i].Key] = entries[i].Value;
            }

            Field.SetValue(context, capture.Instance);
        }
    }

    /// <summary>
    /// HashSet{T}/SortedSet{T}: not IList/IDictionary. Clear/Add MethodInfos are resolved once
    /// per field from the closed generic field type.
    /// </summary>
    private sealed class SetHandler : FieldHandler
    {
        private readonly MethodInfo _clear;
        private readonly MethodInfo _add;

        // FieldType is a closed HashSet{T}/SortedSet{T} declared on ModuleEmissionContext; those
        // BCL collection public methods are always present. The analyzer cannot see that through
        // FieldInfo.FieldType, so the DAM/AOT warnings are suppressed here only.
        [UnconditionalSuppressMessage(
            "Trimming",
            "IL2075",
            Justification = "FieldType is a closed HashSet/SortedSet on ModuleEmissionContext; Clear/Add are public BCL members.")]
        public SetHandler(FieldInfo field)
            : base(field)
        {
            Type fieldType = field.FieldType;
            Type elementType = fieldType.GetGenericArguments()[0];
            _clear = fieldType.GetMethod("Clear", Type.EmptyTypes)
                ?? throw new InvalidOperationException(
                    $"Clear not found on set field '{field.Name}' ({fieldType}).");
            _add = fieldType.GetMethod("Add", new[] { elementType })
                ?? throw new InvalidOperationException(
                    $"Add not found on set field '{field.Name}' ({fieldType}).");
        }

        public override object? Capture(ModuleEmissionContext context)
        {
            object? current = Field.GetValue(context);
            if (current is null)
            {
                return new CollectionCapture(null, null);
            }

            List<object?> items = new();
            foreach (object? item in (IEnumerable)current)
            {
                items.Add(item);
            }

            return new CollectionCapture(current, items.ToArray());
        }

        public override void Restore(ModuleEmissionContext context, object? captured)
        {
            CollectionCapture capture = (CollectionCapture)captured!;
            if (capture.Instance is null)
            {
                Field.SetValue(context, null);
                return;
            }

            object instance = capture.Instance;
            _clear.Invoke(instance, null);
            object?[] items = (object?[])capture.Entries!;
            object?[] args = new object?[1];
            for (int i = 0; i < items.Length; i++)
            {
                args[0] = items[i];
                _add.Invoke(instance, args);
            }

            Field.SetValue(context, instance);
        }
    }

    /// <summary>
    /// Stack{T}: enumeration is top-to-bottom; restore pushes bottom-first so the top is last.
    /// </summary>
    private sealed class StackHandler : FieldHandler
    {
        private readonly MethodInfo _clear;
        private readonly MethodInfo _push;

        [UnconditionalSuppressMessage(
            "Trimming",
            "IL2075",
            Justification = "FieldType is a closed Stack{T} on ModuleEmissionContext; Clear/Push are public BCL members.")]
        public StackHandler(FieldInfo field)
            : base(field)
        {
            Type fieldType = field.FieldType;
            Type elementType = fieldType.GetGenericArguments()[0];
            _clear = fieldType.GetMethod("Clear", Type.EmptyTypes)
                ?? throw new InvalidOperationException(
                    $"Clear not found on stack field '{field.Name}' ({fieldType}).");
            _push = fieldType.GetMethod("Push", new[] { elementType })
                ?? throw new InvalidOperationException(
                    $"Push not found on stack field '{field.Name}' ({fieldType}).");
        }

        public override object? Capture(ModuleEmissionContext context)
        {
            object? current = Field.GetValue(context);
            if (current is null)
            {
                return new CollectionCapture(null, null);
            }

            // Stack enumeration: index 0 is the top.
            List<object?> topToBottom = new();
            foreach (object? item in (IEnumerable)current)
            {
                topToBottom.Add(item);
            }

            return new CollectionCapture(current, topToBottom.ToArray());
        }

        public override void Restore(ModuleEmissionContext context, object? captured)
        {
            CollectionCapture capture = (CollectionCapture)captured!;
            if (capture.Instance is null)
            {
                Field.SetValue(context, null);
                return;
            }

            object instance = capture.Instance;
            _clear.Invoke(instance, null);
            object?[] topToBottom = (object?[])capture.Entries!;
            object?[] args = new object?[1];
            // Push bottom first so the last push restores the original top.
            for (int i = topToBottom.Length - 1; i >= 0; i--)
            {
                args[0] = topToBottom[i];
                _push.Invoke(instance, args);
            }

            Field.SetValue(context, instance);
        }
    }

    /// <summary>
    /// StringBuilder mutates in place: capture the character content, restore via Clear+Append
    /// on the same instance (not a field reassignment of a new builder).
    /// </summary>
    private sealed class StringBuilderHandler : FieldHandler
    {
        public StringBuilderHandler(FieldInfo field)
            : base(field)
        {
        }

        public override object? Capture(ModuleEmissionContext context)
        {
            object? current = Field.GetValue(context);
            if (current is null)
            {
                return new CollectionCapture(null, null);
            }

            StringBuilder builder = (StringBuilder)current;
            return new CollectionCapture(current, builder.ToString());
        }

        public override void Restore(ModuleEmissionContext context, object? captured)
        {
            CollectionCapture capture = (CollectionCapture)captured!;
            if (capture.Instance is null)
            {
                Field.SetValue(context, null);
                return;
            }

            StringBuilder builder = (StringBuilder)capture.Instance;
            builder.Clear();
            builder.Append((string)capture.Entries!);
            Field.SetValue(context, capture.Instance);
        }
    }
}
