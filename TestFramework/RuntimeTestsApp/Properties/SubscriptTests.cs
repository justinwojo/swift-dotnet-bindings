// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Properties;

/// <summary>
/// Subscript tests — verifies that Swift subscript declarations are projected
/// as C# indexer properties (this[T index0] { get; set; }).
///
/// IndexedStore: blittable int subscript (get/set by int index).
/// KeyValueStore: string subscript (get/set by string key), returns nullable.
///
/// Tier structure:
/// - Tier 1: IndexedStore construction + blittable subscript get/set
/// - Tier 2: KeyValueStore string subscript CRUD, count, removeAll, allKeys, allValues
/// </summary>
[Skip("NativeAOT: SIGSEGV — class initialization for subscript types")]
public class SubscriptTests : TestBase
{
    public SubscriptTests(TestResults results) : base(results) { }

    #region IndexedStore — Blittable Subscript (Tier 1)

    public void TestIndexedStoreConstruction()
    {
        var store = new IndexedStore(capacity: 5);
        AssertNotNull(store, "IndexedStore constructed");
        AssertEqual(5, store.GetCount(), "IndexedStore capacity");
        TestLogger.Info("IndexedStore(5) construction passed");
    }

    public void TestIndexedStoreGetDefault()
    {
        var store = new IndexedStore(capacity: 3);
        var value = store[0];
        AssertEqual(0, value, "IndexedStore[0] default is 0");
        TestLogger.Info($"IndexedStore[0] = {value}");
    }

    public void TestIndexedStoreSetAndGet()
    {
        var store = new IndexedStore(capacity: 3);
        store[0] = 42;
        store[1] = 99;
        store[2] = -7;
        AssertEqual(42, store[0], "IndexedStore[0] after set");
        AssertEqual(99, store[1], "IndexedStore[1] after set");
        AssertEqual(-7, store[2], "IndexedStore[2] after set");
        TestLogger.Info("IndexedStore set/get round-trip passed");
    }

    public void TestIndexedStoreOverwrite()
    {
        var store = new IndexedStore(capacity: 2);
        store[0] = 10;
        AssertEqual(10, store[0], "IndexedStore[0] = 10");
        store[0] = 20;
        AssertEqual(20, store[0], "IndexedStore[0] overwritten to 20");
        TestLogger.Info("IndexedStore overwrite passed");
    }

    #endregion

    #region KeyValueStore — String Subscript (Tier 2)

    public void TestKeyValueStoreConstruction()
    {
        var store = new KeyValueStore();
        AssertNotNull(store, "KeyValueStore constructed");
        AssertEqual(0, store.GetCount(), "KeyValueStore initial count is 0");
        TestLogger.Info("KeyValueStore() construction passed");
    }

    public void TestKeyValueStoreSetAndGet()
    {
        var store = new KeyValueStore();
        store["name"] = "Alice";
        var value = store["name"];
        AssertNotNull(value, "KeyValueStore[\"name\"] not null after set");
        AssertEqual("Alice", value!, "KeyValueStore[\"name\"] value");
        TestLogger.Info($"KeyValueStore[\"name\"] = \"{value}\"");
    }

    public void TestKeyValueStoreGetMissing()
    {
        var store = new KeyValueStore();
        var value = store["nonexistent"];
        AssertNull(value, "KeyValueStore[\"nonexistent\"] should be null");
        TestLogger.Info("KeyValueStore missing key returns null");
    }

    public void TestKeyValueStoreCount()
    {
        var store = new KeyValueStore();
        AssertEqual(0, store.GetCount(), "Initial count 0");
        store["a"] = "1";
        AssertEqual(1, store.GetCount(), "Count after 1 insert");
        store["b"] = "2";
        AssertEqual(2, store.GetCount(), "Count after 2 inserts");
        store["c"] = "3";
        AssertEqual(3, store.GetCount(), "Count after 3 inserts");
        TestLogger.Info($"KeyValueStore count after 3 inserts = {store.GetCount()}");
    }

    public void TestKeyValueStoreOverwrite()
    {
        var store = new KeyValueStore();
        store["key"] = "first";
        AssertEqual("first", store["key"]!, "Initial value");
        store["key"] = "second";
        AssertEqual("second", store["key"]!, "Overwritten value");
        AssertEqual(1, store.GetCount(), "Count unchanged after overwrite");
        TestLogger.Info("KeyValueStore overwrite passed");
    }

    public void TestKeyValueStoreRemoveAll()
    {
        var store = new KeyValueStore();
        store["x"] = "1";
        store["y"] = "2";
        AssertEqual(2, store.GetCount(), "Count before removeAll");
        store.RemoveAll();
        AssertEqual(0, store.GetCount(), "Count after removeAll");
        AssertNull(store["x"], "Key 'x' removed");
        AssertNull(store["y"], "Key 'y' removed");
        TestLogger.Info("KeyValueStore.RemoveAll() passed");
    }

    public void TestKeyValueStoreSetNilToDelete()
    {
        var store = new KeyValueStore();
        store["key"] = "value";
        AssertEqual(1, store.GetCount(), "Count after insert");
        store["key"] = null;
        AssertEqual(0, store.GetCount(), "Count after set nil");
        AssertNull(store["key"], "Key deleted by setting nil");
        TestLogger.Info("KeyValueStore set nil to delete passed");
    }

    public void TestKeyValueStoreGetAllKeys()
    {
        var store = new KeyValueStore();
        store["alpha"] = "1";
        store["beta"] = "2";
        store["gamma"] = "3";
        var keys = store.GetAllKeys();
        AssertEqual(3, keys.Count, "AllKeys count");
        // Keys may not be in insertion order; verify all are present
        var keySet = new HashSet<string>();
        for (int i = 0; i < keys.Count; i++)
            keySet.Add(keys[i].ToString());
        AssertTrue(keySet.Contains("alpha"), "Keys contains 'alpha'");
        AssertTrue(keySet.Contains("beta"), "Keys contains 'beta'");
        AssertTrue(keySet.Contains("gamma"), "Keys contains 'gamma'");
        TestLogger.Info($"KeyValueStore.GetAllKeys() returned {keys.Count} keys");
    }

    public void TestKeyValueStoreGetAllValues()
    {
        var store = new KeyValueStore();
        store["a"] = "hello";
        store["b"] = "world";
        var values = store.GetAllValues();
        AssertEqual(2, values.Count, "AllValues count");
        var valueSet = new HashSet<string>();
        for (int i = 0; i < values.Count; i++)
            valueSet.Add(values[i].ToString());
        AssertTrue(valueSet.Contains("hello"), "Values contains 'hello'");
        AssertTrue(valueSet.Contains("world"), "Values contains 'world'");
        TestLogger.Info($"KeyValueStore.GetAllValues() returned {values.Count} values");
    }

    public void TestKeyValueStoreCrudLifecycle()
    {
        var store = new KeyValueStore();

        // Create
        store["user"] = "Alice";
        AssertEqual("Alice", store["user"]!, "Create");

        // Read
        var value = store["user"];
        AssertEqual("Alice", value!, "Read");

        // Update
        store["user"] = "Bob";
        AssertEqual("Bob", store["user"]!, "Update");
        AssertEqual(1, store.GetCount(), "Count after update");

        // Delete
        store["user"] = null;
        AssertNull(store["user"], "Delete");
        AssertEqual(0, store.GetCount(), "Count after delete");

        TestLogger.Info("KeyValueStore CRUD lifecycle passed");
    }

    #endregion
}
