# Value Witness Table

Every value type in Swift has a compiler-defined value witness table. This is a common set of methods that are used by the compiler to work with.

The layout is:

- initializeBufferWithCopyOfBuffer `T *(*initializeBufferWithCopyOfBuffer)(T *dest, T* src, SwiftMetadata *metadata)` - initializes a pointer to blank buffer memory (dest) with a copy of a pointer to a buffered struct (src) with the provided type metadata.
- destroy `void (*destroy)(T *src, SwiftMetadata *metadata)` - destroys the contents of the struct (src) with the provided type metadata
- initializeWithCopy `T *(*initializeWithCopy)(T *dest, T* src, SwiftMetadata *metadata)`- initializes a pointer to memory (dest) with a copy of a struct pointer to by src using the provided type metadata
- assignWithCopy `T *(*assignWithCopy)(T *dest, T *src, SwiftMetadata *metadata)` - destroys the contents of dest before copying the contents of src on top of it using the provided type metadata
- initializeWithTake `T *(*initializeWithTake)(T *dest, T *src, SwiftMetadata *metadata)` - initializes the contents of fest with the contents of src, destroying src using the provided type metadata.
- assignWithTake `T *(*assignWithTake)(T *dest, T *src, SwiftMetadata *metadata)` - destroys the contents of dest before copying the contents of src on top of it, destroying src using the provided type metadata.
- getEnumTagSinglePayload - `unsigned int (*getEnumTagSinglePayload)(T* enumInst, unsigned int numEmptyCases, SwiftMetadata *metadata)` - gets the current discriminator for an enum with a single payload using the supplied type metadata.
- storeEnumTagSinglePayload - `void (*storeEnumTagSinglePayload)(T *enumInst, unsigned int whichCase, int numEmptyCases, SwiftMetadata *metadata)` - sets the current discriminator for an enum with a single payload using the supplied type metadata.
- Size - machine word representing the size of the type in bytes
- Stride - machine word representing the stride of the type in bytes
- Flags - 32 bit unsigned int - contains flags that describe how to work with the type including the memory alignment (including `HasEnumWitnesses`)
- ExtraInhabitantCount - 32 bit unsigned int - the number of extra inhabitants (free bits) in the type
- getEnumTag - `unsigned int (*getEnumTag)(T *enumInst, SwiftMetadata *metadata)` - tag for a multi-payload enum (payload cases first, then no-payload cases)
- destructiveProjectEnumData - `void (*destructiveProjectEnumData)(T *enumInst, SwiftMetadata *metadata)` - strip tag bits, leaving the payload in place
- destructiveInjectEnumTag - `void (*destructiveInjectEnumTag)(T *enumInst, unsigned int tag, SwiftMetadata *metadata)` - inject a tag into an enum instance

The enum-witness triple is always present in the C# layout mirror; it is meaningful when `HasEnumWitnesses` is set on `Flags` (used heavily by `SwiftOptional`, `SwiftResult`, `SwiftDictionary`, etc.).

## Getting the value witness table

There are two ways to get the value witness table for a type. The first is to get the address of memory using `dlsym` with the entry point for the value witness table. The second is get it relative to the type metadata. In Swift there is extended metadata information that always precedes the type metadata. One machine word back from the type metadata is a pointer to the ValueWitnessTable (see Apple documentation [here](https://github.com/swiftlang/swift/blob/main/docs/ABI/TypeMetadata.rst#common-metadata-layout))

Probably the most efficient way to get the value witness table is to always go from the Swift Type Metadata. This repository only uses that path: VWT at `metadata - 1` word (`TypeMetadata.ValueWitnessTable` / `SwiftMetadata.ValueWitnessTable`). It does **not** read a deinit slot at `metadata - 2`; class teardown, when needed, uses other class-metadata fields rather than a value-type VWT layout assumption.

## As built in this repo

- C# mirror: `Swift.Runtime.ValueWitnessTable` in `src/Swift.Runtime/src/Swift/Runtime/ValueWitnessTable.cs` (`LayoutKind.Sequential` `ref struct`)
- Access: `TypeMetadata.ValueWitnessTable` (normal paths; not `dlsym`)
- Function pointers are PascalCase (`InitializeWithCopy`, `Destroy`, `GetEnumTag`, …) and take `TypeMetadata`, not a C `SwiftMetadata *`
- Related consumers and non-frozen / async buffer patterns are cross-linked from `async-non-frozen-types.md`
