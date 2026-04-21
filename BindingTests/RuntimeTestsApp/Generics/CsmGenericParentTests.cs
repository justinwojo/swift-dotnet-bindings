// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Generics;

/// <summary>
/// CSM generic-parent coverage. <c>GenericContainer&lt;T: SearchableItem&gt;.append&lt;D: DataProtocol&gt;</c>
/// is specialized by both parent T (3 conformers: SongItem/AlbumItem/ArtistItem) and method D
/// (2 conformers: Data/byte[]). Each parent-conformer tuple lives in its own
/// <c>GenericContainer{ParentConformer}CsmExtensions</c> static class with one overload per
/// method-conformer — the extension-class pattern is required because the receiver must close
/// over the parent generic (e.g. <c>this GenericContainer&lt;SongItem&gt; self</c>).
/// </summary>
public class CsmGenericParentTests : TestBase
{
    public CsmGenericParentTests(TestResults results) : base(results) { }

    // NOTE: GenericContainer.count()/tagBytes() have no @_cdecl wrapper (non-generic methods on
    // a generic struct → direct CallConvSwift with metadata). Those getters crash under Mono
    // JIT on simulator, so we can't use them to witness state. Tests only verify CSM extension
    // dispatch without crashing — proving the parent-generic specialization pipeline (wrapper
    // symbol, extension-class emission, mutating-self handle round-trip) is wired.
    //
    // All calls use instance syntax (container.Append(...)) — this is the regression test for
    // the shadow fix. Previously the open-generic Append<D>(T,D) where D:ISwiftObject was also
    // emitted on GenericContainer<T> and won C# overload resolution over the CSM extensions
    // when D=Data, routing callers into a broken no-wrapper path. The fix suppresses that open
    // generic for CSM-eligible methods on generic parents, so these instance-syntax calls now
    // bind directly to the extension methods.

    public void TestGenericContainerSongItem_Append_Data()
    {
        var container = new GenericContainer<SongItem>();
        var tag = global::Swift.Foundation.Data.FromByteArray(new byte[] { 1, 2, 3, 4 });
        container.Append(new SongItem(), tag);
    }

    public void TestGenericContainerSongItem_Append_ByteArray()
    {
        var container = new GenericContainer<SongItem>();
        byte[] tag = { 9, 9, 9, 9, 9 };
        container.Append(new SongItem(), tag);
    }

    public void TestGenericContainerAlbumItem_Append_Data()
    {
        var container = new GenericContainer<AlbumItem>();
        var tag = global::Swift.Foundation.Data.FromByteArray(new byte[] { 7, 7 });
        container.Append(new AlbumItem(), tag);
    }

    // Missing pairing completions — without these, AlbumItem/byte[] and ArtistItem/Data
    // would be unexercised at runtime and a regression in their emission could pass
    // silently. The CSM cartesian emits 3 (parent conformers) × 2 (method conformers) = 6
    // overloads, and every one must be hit to guarantee full surface coverage.
    public void TestGenericContainerAlbumItem_Append_ByteArray()
    {
        var container = new GenericContainer<AlbumItem>();
        byte[] tag = { 4, 5, 6 };
        container.Append(new AlbumItem(), tag);
    }

    public void TestGenericContainerArtistItem_Append_Data()
    {
        var container = new GenericContainer<ArtistItem>();
        var tag = global::Swift.Foundation.Data.FromByteArray(new byte[] { 8, 8, 8, 8 });
        container.Append(new ArtistItem(), tag);
    }

    public void TestGenericContainerArtistItem_Append_ByteArray()
    {
        var container = new GenericContainer<ArtistItem>();
        byte[] tag = { 0, 0, 0 };
        container.Append(new ArtistItem(), tag);
    }

    public void TestGenericContainerSongItem_Append_Mixed()
    {
        // Hammers both CSM overloads (Data + byte[]) against the same receiver so any lurking
        // mutating-self or handle-aliasing bug would surface as a crash across 3 appends.
        var container = new GenericContainer<SongItem>();
        container.Append(new SongItem(), global::Swift.Foundation.Data.FromByteArray(new byte[] { 1, 2 }));
        container.Append(new SongItem(), new byte[] { 3, 4, 5 });
        container.Append(new SongItem(), global::Swift.Foundation.Data.FromByteArray(new byte[] { 6 }));
    }

    // --- Mutating-self witness via a second CSM-eligible method on the same parent.
    // `count()`/`tagBytes()` are non-generic methods on a generic struct (CallConvSwift +
    // metadata) and crash Mono JIT, so they can't witness `append`'s mutation. `countSeen`
    // is method-generic (D: DataProtocol) → it routes through the CSM extension pipeline
    // just like `append`, giving us a crash-free read path. If the mutating-self write-back
    // on `append` regresses, `countSeen` will return 0 even after multiple appends.

    public void TestGenericContainerSongItem_CountSeenAfterAppend_Data()
    {
        var container = new GenericContainer<SongItem>();
        container.Append(new SongItem(), global::Swift.Foundation.Data.FromByteArray(new byte[] { 1 }));
        container.Append(new SongItem(), global::Swift.Foundation.Data.FromByteArray(new byte[] { 2, 3 }));
        var probe = global::Swift.Foundation.Data.FromByteArray(new byte[] { 0 });
        AssertEqual(2, (int)container.CountSeen(probe),
            "countSeen(Data) after 2 appends must witness the mutating-self write-back");
    }

    public void TestGenericContainerAlbumItem_CountSeenAfterAppend_ByteArray()
    {
        var container = new GenericContainer<AlbumItem>();
        container.Append(new AlbumItem(), new byte[] { 1, 2, 3 });
        container.Append(new AlbumItem(), new byte[] { 4 });
        container.Append(new AlbumItem(), new byte[] { 5, 6 });
        byte[] probe = { 0 };
        AssertEqual(3, (int)container.CountSeen(probe),
            "countSeen(byte[]) after 3 appends must witness the mutating-self write-back");
    }
}
