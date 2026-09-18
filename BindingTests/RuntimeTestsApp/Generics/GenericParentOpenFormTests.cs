// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Generics;

/// <summary>
/// Instance methods of a generic parent that also get closed per-conformer extension overloads.
/// The open-generic form is kept whenever it has a <c>@_cdecl</c> wrapper, so these call the
/// member under its own name through that wrapper and check it against the closed overload and
/// against state only Swift could have produced.
/// </summary>
public class GenericParentOpenFormTests : TestBase
{
    public GenericParentOpenFormTests(TestResults results) : base(results) { }

    public void TestGenericContainer_OpenFormObserversWitnessAppends()
    {
        using var container = new GenericContainer<SongItem>();
        container.Append(new SongItem(), new byte[] { 1, 2, 3 });
        container.Append(new SongItem(), global::Swift.Foundation.Data.FromByteArray(new byte[] { 4, 5, 6, 7 }));

        AssertEqual((nint)2, container.GetCount(), "count() through the open-generic wrapper");
        AssertEqual((nint)7, container.GetTagBytes(), "tagBytes() through the open-generic wrapper");
        AssertEqual(container.GetCount(), GenericContainerSongItemCsmExtensions.Count(container),
            "open form and closed overload read the same count");
        AssertEqual(container.GetTagBytes(), GenericContainerSongItemCsmExtensions.TagBytes(container),
            "open form and closed overload read the same tag bytes");
    }

    public void TestGenericContainer_OpenFormOnASecondConformer_StartsEmpty()
    {
        using var container = new GenericContainer<ArtistItem>();
        AssertEqual((nint)0, container.GetCount(), "fresh ArtistItem container count");
        AssertEqual((nint)0, container.GetTagBytes(), "fresh ArtistItem container tag bytes");

        container.Append(new ArtistItem(), new byte[] { 9 });
        AssertEqual((nint)1, container.GetCount(), "ArtistItem container count after one append");
        AssertEqual((nint)1, container.GetTagBytes(), "ArtistItem container tag bytes after one append");
    }

    public void TestDescribableHolder_OpenForm_StructConformer()
    {
        using var item = new SimpleItem("h1", "held-struct");
        using var holder = new DescribableHolder<SimpleItem>(item);

        AssertEqual("[h1] held-struct", holder.GetHeld(), "held() through the open-generic wrapper");
        AssertEqual(holder.GetHeld(), DescribableHolderSwiftBindingsTestLib_SimpleItemCsmExtensions.Held(holder),
            "open form and closed overload agree");
    }

    public void TestDescribableHolder_OpenForm_ClassConformer_OutlivesTheCallerHandle()
    {
        DescribableHolder<MultiProtocolEntity> holder;
        using (var entity = new MultiProtocolEntity("h2", "held-class"))
        {
            holder = new DescribableHolder<MultiProtocolEntity>(entity);
        }

        using (holder)
        {
            AssertEqual("[h2] held-class", holder.GetHeld(), "held() reads a class conformer the holder retains");
        }
    }

    public void TestRefWeighedBox_OpenForm_ReadsEachConformer()
    {
        using var light = new LightSlot(3, "light");
        using var heavy = new HeavySlot(40, "heavy");
        using var lightBox = new RefWeighedBox<LightSlot>(light, "l");
        using var heavyBox = new RefWeighedBox<HeavySlot>(heavy, "h");

        AssertEqual(3, lightBox.GetPlainUnits(), "plainUnits() on LightSlot through the open-generic wrapper");
        AssertEqual(40, heavyBox.GetPlainUnits(), "plainUnits() on HeavySlot through the open-generic wrapper");
        AssertEqual(lightBox.GetPlainUnits(), RefWeighedBoxLightSlotCsmExtensions.PlainUnits(lightBox),
            "open form and closed overload agree");
    }

    public void TestTypedBag_OpenForm_CountsAProjectedBag()
    {
        using var response = Functions.MakeAlbumLibraryResponse();
        using var bag = response.Items();

        AssertEqual((nint)3, bag.GetCount(), "count() through the open-generic wrapper");
        AssertEqual(bag.GetCount(), TypedBagSwiftBindingsTestLib_TcpAlbumCsmExtensions.Count(bag),
            "open form and closed overload agree");
    }
}
