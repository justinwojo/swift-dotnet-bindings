# TestFramework Enhancement Plan: Real-World Library Patterns

> **Goal**: 95%+ coverage of all interop patterns found in third-party bindings, so the TestFramework can catch regressions without relying on real library bindings.
>
> **Sources**: 21 libraries across sim-validation (16) and swift-dotnet-packages (5), plus 11 NativeAOT stability fix commits.

## Current TestFramework Coverage

The TestFramework already covers these core patterns well:
- Simple/frozen enums (int-backed, string-backed, associated values, multi-associated-values)
- Nested string enums in **structs** (NetworkConfig.HttpMethod, OrderContainer.Status)
- Enum FromRawValue factory, enum computed properties
- Classes (base, inheritance, final), structs (frozen, non-frozen)
- Protocols (basic, composition, non-blittable, witness dispatch, existential callbacks)
- Generics (types, functions, constraints, associated types, existentials)
- Arrays as free-function params and returns
- Optionals (Int32?, Animal?, String?, struct with optional props)
- Tuples, closures (escaping, returns), async methods, error handling/throws
- Static properties (let, var, computed), operators (arithmetic, comparison on frozen structs)
- Inout params, real-world compositions (BatchConfig, Registry.Shared, EventHandler, Transformer)

## Gap Analysis: 25 Missing Patterns

### Legend
- **Status**: Missing (no coverage), Disabled (`.swift.disabled`), Partial (some coverage), Enhance (exists but needs expansion)
- **Evidence**: Which libraries demonstrated the need
- **Fix ref**: Which commit/fix this pattern would have caught

---

### GROUP A: Foundation Types (3 patterns)

| # | Pattern | Status | Evidence | Fix Ref |
|---|---------|--------|----------|---------|
| A1 | Foundation.Date params/returns/properties | Disabled | RxSwift HistoricalScheduler, KeychainAccess Optional\<Date\>, BlinkID | DateProjection (5ee25de7) |
| A2 | Foundation.URL params/returns | Disabled | Starscream, Nuke pipelines | Large Optional fix + ObjC bridging |
| A3 | Foundation.Data params | Disabled | Starscream WebSocketEvent.Binary | Needs DataProjection (known limitation) |

**A1: Foundation.Date** — ENABLE + ENHANCE

Existing `Date.swift.disabled` covers: Date param, Date return, two Date params, Date arithmetic, struct with Date property. Add:
```swift
// Optional<Date> (KeychainAccess pattern)
public func optionalDate(epochSeconds: Double?) -> Date? {
    guard let seconds = epochSeconds else { return nil }
    return Date(timeIntervalSince1970: seconds)
}

// Struct with optional Date properties
public struct EventConfig {
    public var label: String
    public var startDate: Date?
    public var endDate: Date?
    public init(label: String, startDate: Date?, endDate: Date?) { ... }
}

// Date as enum associated value (RxSwift pattern)
public enum SchedulerEvent {
    case scheduled(at: Date)
    case cancelled
}
```

**A2: Foundation.URL** — ENABLE when URL projection implemented. Keep disabled.

**A3: Foundation.Data** — DEFERRED. Needs DataProjection. Keep disabled.

---

### GROUP B: Protocol/Existential Patterns (2 patterns)

| # | Pattern | Status | Evidence | Fix Ref |
|---|---------|--------|----------|---------|
| B1 | Concrete type passed as protocol existential param | Missing | CryptoSwift `AES(key, ECB, Padding)` where ECB is concrete struct passed as `any BlockMode` | IExistentialBoxable (8c74b688) |
| B2 | Constructor combining collection + existential + enum params | Missing | CryptoSwift `AES(byte[], ECB, Padding)` | IExistentialBoxable + DangerousGetHandle |

**B1: Concrete Type as Protocol Existential** — NEW `Protocols/ExistentialBoxing.swift`
```swift
public protocol ProcessingMode {
    var modeName: String { get }
    func validate(input: Int32) -> Bool
}

public struct SimpleMode: ProcessingMode {
    public var modeName: String { "simple" }
    public init() {}
    public func validate(input: Int32) -> Bool { input >= 0 }
}

public struct StrictMode: ProcessingMode {
    public var modeName: String { "strict" }
    public init() {}
    public func validate(input: Int32) -> Bool { input > 0 && input < 1000 }
}

/// Class taking protocol existential param — the CryptoSwift AES(key, ECB) pattern
public class Processor {
    private let mode: any ProcessingMode
    public init(mode: any ProcessingMode) { self.mode = mode }
    public func process(value: Int32) -> Bool { mode.validate(input: value) }
    public func getModeName() -> String { mode.modeName }
}

/// Free function with existential param
public func runWithMode(_ mode: any ProcessingMode, value: Int32) -> Bool {
    return mode.validate(input: value)
}

/// Two existential params
public func compareResults(_ a: any ProcessingMode, _ b: any ProcessingMode, value: Int32) -> Bool {
    return a.validate(input: value) == b.validate(input: value)
}
```

**B2: Multi-param constructor with existential** — extend B1:
```swift
/// Constructor combining collection + protocol existential + enum (CryptoSwift AES pattern)
public class Pipeline {
    private let steps: [Int32]
    private let mode: any ProcessingMode

    public init(steps: [Int32], mode: any ProcessingMode) {
        self.steps = steps
        self.mode = mode
    }

    public func stepCount() -> Int32 { Int32(steps.count) }
    public func getModeName() -> String { mode.modeName }
}
```

---

### GROUP C: Subscripts (1 pattern)

| # | Pattern | Status | Evidence | Fix Ref |
|---|---------|--------|----------|---------|
| C1 | Subscript (string key → optional string, int key → int) | Missing | KeychainAccess `kc["key"] = "value"` | @_cdecl string param fix (103e8fed) |

**C1: Subscripts** — NEW `Properties/Subscripts.swift`
```swift
/// String-keyed subscript (KeychainAccess pattern)
public class KeyValueStore {
    private var storage: [String: String] = [:]
    public init() {}

    public subscript(key: String) -> String? {
        get { storage[key] }
        set { storage[key] = newValue }
    }

    public func count() -> Int32 { Int32(storage.count) }
    public func removeAll() { storage.removeAll() }
}

/// Int-keyed subscript (blittable comparison)
public class IndexedStore {
    private var items: [Int32]

    public init(capacity: Int32) {
        items = Array(repeating: 0, count: Int(capacity))
    }

    public subscript(index: Int32) -> Int32 {
        get { items[Int(index)] }
        set { items[Int(index)] = newValue }
    }

    public func count() -> Int32 { Int32(items.count) }
}
```

---

### GROUP D: Static Properties & Singletons (3 patterns)

| # | Pattern | Status | Evidence | Fix Ref |
|---|---------|--------|----------|---------|
| D1 | Static struct singleton (`static let` returning Self) | Missing | Alamofire `URLEncoding.Default`, Kingfisher `DefaultImageProcessor.Default`, BlinkID `RequestTimeout.Default` | Struct copy/destroy crash |
| D2 | Multiple static class singletons on same type | Missing | Swinject `ObjectScope.Transient`, `.Graph`, `.Container`, `.Weak` | Static property emission |
| D3 | Struct-backed "enum" (struct with static let props + rawValue) | Missing | Alamofire `HTTPMethod.Get/.Post/.Put`, BonMot `Emphasis.Italic/.Bold` | Static struct property getters |

**D1: Static Struct Singleton** — NEW `Properties/StaticStructSingleton.swift`
```swift
public struct EncodingConfig {
    public var formatName: String
    public var maxLength: Int32

    public init(formatName: String, maxLength: Int32) {
        self.formatName = formatName
        self.maxLength = maxLength
    }

    public static let standard = EncodingConfig(formatName: "standard", maxLength: 1024)
    public static let compact = EncodingConfig(formatName: "compact", maxLength: 256)
    public static let minimal = EncodingConfig(formatName: "minimal", maxLength: 64)

    public func isWithinLimit(_ length: Int32) -> Bool { length <= maxLength }
}
```

**D2: Multiple Class Singletons** — NEW (extend `Types/Classes.swift` or `Patterns/`)
```swift
public class Scope {
    public let name: String
    private init(name: String) { self.name = name }

    public static let transient = Scope(name: "transient")
    public static let graph = Scope(name: "graph")
    public static let container = Scope(name: "container")
    public static let weak = Scope(name: "weak")

    public func describe() -> String { "Scope: \(name)" }
}
```

**D3: Struct-Backed Enum** — NEW `Patterns/StructBackedEnum.swift`
```swift
public struct HttpVerb: Equatable {
    public var rawValue: String
    public init(rawValue: String) { self.rawValue = rawValue }

    public static let get = HttpVerb(rawValue: "GET")
    public static let post = HttpVerb(rawValue: "POST")
    public static let put = HttpVerb(rawValue: "PUT")
    public static let delete = HttpVerb(rawValue: "DELETE")
    public static let patch = HttpVerb(rawValue: "PATCH")
}
```

---

### GROUP E: Enum Patterns (6 patterns)

| # | Pattern | Status | Evidence | Fix Ref |
|---|---------|--------|----------|---------|
| E1 | Nested enums inside **class** (vs struct) | Missing | CryptoSwift `AES.Variant`, `SHA2.Variant`; Alamofire `URLEncoding.Destination` | Enum scoping |
| E2 | Enum with enum-typed associated value | Missing | CryptoSwift `HMAC.Variant.Sha2(SHA2.Variant.Sha256)` | Nested enum case marshalling |
| E3 | Enum extension methods (not defined inline) | Partial | KeychainAccess `ItemClass.GetDescription()`, DeviceKit `Device.CPU.GetDescription()` | Enum method dispatch |
| E4 | Large enum (100+ cases) | Missing | DeviceKit `Device` (100+ case enum with DestructiveInjectEnumTag) | Enum scalability |
| E5 | OptionSet struct | Missing | BonMot `Emphasis`, `XMLParsingOptions`; Nuke `ImageRequest.Options` | OptionSet emission |
| E6 | Non-Int32 backed enums (UInt16, Int64) | Missing | Starscream `SecurityErrorCode: UInt16`, BonMot `Ligatures: Int64` | Backing type handling |

**E1: Nested Enums in Class** — NEW `Types/NestedEnums.swift`
```swift
public class Codec {
    public enum Format: Int32 {
        case json = 0
        case xml = 1
        case binary = 2
    }

    public enum Encoding: String {
        case utf8 = "utf-8"
        case ascii = "ascii"
        case latin1 = "latin-1"
    }

    /// Nested enum with associated values
    public enum CompressionLevel {
        case none
        case fast
        case best
        case custom(level: Int32)
    }

    public var format: Format
    public var encoding: Encoding

    public init(format: Format, encoding: Encoding) {
        self.format = format
        self.encoding = encoding
    }

    public func describe() -> String { "\(format) / \(encoding.rawValue)" }
}
```

**E2: Enum with Enum-Typed Associated Value** — in `Types/NestedEnums.swift`
```swift
public enum SHA2Variant: Int32 {
    case sha224 = 0
    case sha256 = 1
    case sha384 = 2
    case sha512 = 3
}

public enum HashAlgorithm {
    case md5
    case sha1
    case sha2(variant: SHA2Variant)
    case custom(rounds: Int32)
}

public func createHashAlgorithm(sha2Variant: SHA2Variant) -> HashAlgorithm { .sha2(variant: sha2Variant) }
public func describeAlgorithm(_ algo: HashAlgorithm) -> String {
    switch algo {
    case .md5: return "MD5"
    case .sha1: return "SHA1"
    case .sha2(let v): return "SHA2-\(v.rawValue)"
    case .custom(let r): return "Custom-\(r)"
    }
}
```

**E3: Enum Extension Methods** — EXTEND `Types/Enums.swift` (Direction already has `opposite()` inline; add `getDescription()` as **extension**):
```swift
extension Color {
    public func complementary() -> Int32 { (self.rawValue + 3) % 6 }
    public func getHexDescription() -> String {
        switch self {
        case .red: return "#FF0000"
        case .green: return "#00FF00"
        case .blue: return "#0000FF"
        case .alpha: return "#000000FF"
        }
    }
}
```
Note: `Direction.opposite()` is already defined **inline** in the enum body, not as an extension. We need to also test an extension-defined method since that's a different emission path (extension vs member). Add a `getDescription()` extension on Direction.

**E4: Large Enum** — NEW `Types/LargeEnum.swift`
```swift
/// Large enum (50+ cases) testing DestructiveInjectEnumTag scalability (DeviceKit Device pattern)
public enum DeviceModel {
    // 50 no-payload cases
    case phone1, phone2, phone3, phone4, phone5
    case phone6, phone7, phone8, phone9, phone10
    case tablet1, tablet2, tablet3, tablet4, tablet5
    case tablet6, tablet7, tablet8, tablet9, tablet10
    case watch1, watch2, watch3, watch4, watch5
    case laptop1, laptop2, laptop3, laptop4, laptop5
    case desktop1, desktop2, desktop3, desktop4, desktop5
    case tv1, tv2, tv3, tv4, tv5
    case speaker1, speaker2, speaker3, speaker4, speaker5
    case accessory1, accessory2, accessory3, accessory4, accessory5
    // Payload cases
    case unknown(identifier: String)
    case custom(name: String, year: Int32)
}

public func deviceDescription(_ model: DeviceModel) -> String {
    switch model {
    case .phone1: return "Phone 1"
    case .unknown(let id): return "Unknown: \(id)"
    case .custom(let name, let year): return "\(name) (\(year))"
    default: return "Device"
    }
}
```

**E5: OptionSet** — NEW `Types/OptionSets.swift`
```swift
/// OptionSet struct (BonMot Emphasis, Nuke ImageRequest.Options pattern)
public struct TextStyle: OptionSet {
    public let rawValue: Int32
    public init(rawValue: Int32) { self.rawValue = rawValue }

    public static let bold = TextStyle(rawValue: 1 << 0)
    public static let italic = TextStyle(rawValue: 1 << 1)
    public static let underline = TextStyle(rawValue: 1 << 2)
    public static let strikethrough = TextStyle(rawValue: 1 << 3)
}

/// OptionSet on a class (Nuke ImageRequest.Options pattern — nested OptionSet)
public class ImageRequest {
    public struct Options: OptionSet {
        public let rawValue: Int32
        public init(rawValue: Int32) { self.rawValue = rawValue }

        public static let disableCache = Options(rawValue: 1 << 0)
        public static let returnCached = Options(rawValue: 1 << 1)
        public static let lowPriority = Options(rawValue: 1 << 2)
    }

    public var options: Options
    public init(options: Options) { self.options = options }
}

public func describeTextStyle(_ style: TextStyle) -> String {
    var parts: [String] = []
    if style.contains(.bold) { parts.append("bold") }
    if style.contains(.italic) { parts.append("italic") }
    if style.contains(.underline) { parts.append("underline") }
    if style.contains(.strikethrough) { parts.append("strikethrough") }
    return parts.joined(separator: ", ")
}
```

**E6: Non-Int32 Enums** — NEW `Types/NonStandardEnums.swift`
```swift
/// UInt16-backed enum (Starscream SecurityErrorCode pattern)
public enum SecurityError: UInt16 {
    case none = 0
    case badCertificate = 1
    case pinningFailed = 2
    case invalidChain = 3
}

/// Int64-backed enum (BonMot Ligatures pattern)
public enum FeatureFlag: Int64 {
    case disabled = 0
    case enabled = 1
    case experimental = 2
}

/// UInt32-backed enum
public enum Permission: UInt32 {
    case none = 0
    case read = 1
    case write = 2
    case execute = 4
}
```

---

### GROUP F: Constructor Patterns (4 patterns)

| # | Pattern | Status | Evidence | Fix Ref |
|---|---------|--------|----------|---------|
| F1 | Constructor with array/collection param | Missing | CryptoSwift `HMAC(byte[])`, Lottie `AnimationKeypath(List<string>)` | DangerousGetHandle (103e8fed) |
| F2 | Constructor with Dictionary param | Missing | Alamofire `HTTPHeaders(IDictionary)`, Lottie `DictionaryTextProvider(dict)` | Dictionary passing |
| F3 | Constructor with optional class param | Missing | Swinject `Container(parent: nil)` | Optional class constructor |
| F4 | Constructor with CGSize/CGRect struct param | Missing | Kingfisher `ResizingImageProcessor(CGSize, ContentMode)`, Lottie `LottieAnimationView(CGRect)` | Frozen struct params |

**F1: Constructor with Collection** — NEW `Collections/ConstructorCollections.swift`
```swift
public class DataBuffer {
    private let data: [Int32]
    public init(data: [Int32]) { self.data = data }
    public func count() -> Int32 { Int32(data.count) }
    public func sum() -> Int32 { Int32(data.reduce(0, +)) }
    public func first() -> Int32? { data.first.map { Int32($0) } }
}

/// Constructor with string array param (Lottie AnimationKeypath pattern)
public class PathResolver {
    private let components: [String]
    public init(components: [String]) { self.components = components }
    public func fullPath() -> String { components.joined(separator: ".") }
    public func depth() -> Int32 { Int32(components.count) }
}

/// Constructor with array + other params
public class LabeledBuffer {
    private let label: String
    private let data: [Int32]
    public init(label: String, data: [Int32]) {
        self.label = label
        self.data = data
    }
    public func describe() -> String { "\(label): \(data.count) items" }
}
```

**F2: Constructor with Dictionary** — NEW `Collections/DictionaryConstructor.swift`
```swift
/// Constructor taking Dictionary param (Alamofire HTTPHeaders pattern)
public class HeaderMap {
    private var headers: [String: String]
    public init(headers: [String: String]) { self.headers = headers }
    public func count() -> Int32 { Int32(headers.count) }
    public func get(_ key: String) -> String? { headers[key] }
    public func set(_ key: String, _ value: String) { headers[key] = value }
}
```

**F3: Constructor with Optional Class Param** — NEW (extend `Types/Classes.swift`)
```swift
/// Class with optional parent (Swinject Container pattern)
public class TreeNode {
    public let label: String
    public let parent: TreeNode?
    public init(label: String, parent: TreeNode?) {
        self.label = label
        self.parent = parent
    }
    public func depth() -> Int32 {
        if let p = parent { return p.depth() + 1 }
        return 0
    }
    public func rootLabel() -> String {
        if let p = parent { return p.rootLabel() }
        return label
    }
}
```

**F4: Constructor with CGSize param** — If CGSize is already available via frozen structs, this is covered by FrozenPoint. The specific pattern is a constructor taking a **Foundation/CoreGraphics frozen struct**. The existing `FrozenPoint` covers the struct-param-in-constructor ABI pattern. **No separate action needed** — covered by existing frozen struct params.

---

### GROUP G: Method/Property Patterns (5 patterns)

| # | Pattern | Status | Evidence | Fix Ref |
|---|---------|--------|----------|---------|
| G1 | Builder pattern (methods returning Self) | Missing | KeychainAccess `.withAccessibility().withLabel()` | Chained class method returns |
| G2 | Static factory method returning optional Self | Missing | Lottie `LottieAnimation.Filepath() -> LottieAnimation?`, ObjectMapper `DateTransform.Unit.FromRawValue(999) -> nil` | Optional factory |
| G3 | Instance property returning another class | Partial | Nuke `KingfisherManager.Cache -> ImageCache`, Swinject `Assembler.Resolver` | Class-returning properties |
| G4 | Method returning collection | Partial | KeychainAccess `GetAllKeys() -> [String]`, PhoneNumberKit `GetAllCountries()` | Collection return from method |
| G5 | Method with completion closure param | Partial | Lottie `view.Play(completion: (Bool) -> Void)` | Closure param on instance method |

**G1: Builder Pattern** — NEW `Patterns/BuilderPattern.swift`
```swift
public class RequestBuilder {
    public var url: String
    public var method: String
    public var timeout: Int32
    public var retryCount: Int32

    public init(url: String) {
        self.url = url
        self.method = "GET"
        self.timeout = 30
        self.retryCount = 0
    }

    public func withMethod(_ method: String) -> RequestBuilder {
        self.method = method
        return self
    }

    public func withTimeout(_ timeout: Int32) -> RequestBuilder {
        self.timeout = timeout
        return self
    }

    public func withRetryCount(_ count: Int32) -> RequestBuilder {
        self.retryCount = count
        return self
    }

    public func describe() -> String { "\(method) \(url) timeout=\(timeout) retries=\(retryCount)" }
}
```

**G2: Static Factory Returning Optional** — NEW `Patterns/StaticFactory.swift`
```swift
/// Class with static factory methods returning optional (Lottie LottieAnimation.Filepath pattern)
public class ConfigLoader {
    public let name: String
    public let version: Int32

    private init(name: String, version: Int32) {
        self.name = name
        self.version = version
    }

    /// Factory returns nil for empty name (failable pattern)
    public static func create(name: String) -> ConfigLoader? {
        guard !name.isEmpty else { return nil }
        return ConfigLoader(name: name, version: 1)
    }

    /// Factory returns nil for invalid version
    public static func create(name: String, version: Int32) -> ConfigLoader? {
        guard version > 0 else { return nil }
        return ConfigLoader(name: name, version: version)
    }

    public func describe() -> String { "\(name) v\(version)" }
}
```

**G3: Property Returning Another Class** — EXTEND `Patterns/RealWorldCompositions.swift`
```swift
/// Class with properties returning other class instances (Nuke KingfisherManager pattern)
public class ServiceContainer {
    public let cache: Registry   // reuse existing Registry
    public let name: String

    public init(name: String) {
        self.name = name
        self.cache = Registry.shared
    }
}
```

**G4: Method Returning Collection** — EXTEND existing types
```swift
// Add to KeyValueStore (Pattern C1):
public func allKeys() -> [String] { Array(storage.keys) }
public func allValues() -> [String] { Array(storage.values) }
```
Also already partially covered by `describeAnimals()` and `createStringArray()`. The gap is specifically an **instance method** returning a collection (vs free function). The KeyValueStore additions cover this.

**G5: Method with Completion Closure** — Already covered by `ClosureConsumer.applyToValue()` and the various `callWith*` free functions. The Lottie pattern is specifically a **void method with optional completion closure**, which is a variation. Add to the builder or a new class:
```swift
// Add to RequestBuilder or new type:
public func execute(completion: ((Bool) -> Void)?) {
    completion?(true)
}
```

---

### GROUP H: Equality & Operators (1 pattern)

| # | Pattern | Status | Evidence | Fix Ref |
|---|---------|--------|----------|---------|
| H1 | Non-frozen struct equality (Equatable via @_cdecl wrapper) | Partial | Alamofire HTTPHeader ==, KeychainAccess AuthenticationPolicy ==, SnapKit ConstraintPriority == | @_cdecl equality wrappers (dc06e216) |

**H1: Non-Frozen Struct Equality** — EXTEND `Operators/Comparison.swift`
```swift
/// Non-frozen struct with Equatable (Alamofire HTTPHeader pattern — @_cdecl equality wrapper)
public struct Tag: Equatable {
    public var key: String
    public var value: String
    public init(key: String, value: String) { self.key = key; self.value = value }
}
```
The existing `ComparableValue` and `ApproximatelyEqual` are both **frozen**. The gap is testing equality on **non-frozen** structs, which takes the @_cdecl wrapper path instead of the CallConvSwift path.

---

### GROUP I: Failable Initializers (1 pattern)

| # | Pattern | Status | Evidence | Fix Ref |
|---|---------|--------|----------|---------|
| I1 | Failable initializer (init?) | Disabled | ObjectMapper `DateTransform.Unit.FromRawValue(999) -> nil`, XMLCoder `BoolBox.TryCreate` | Failable init emission |

**I1**: Files exist in `Initializers.disabled/Failable.swift`. The TestFramework has `SafeDiv`, `NonEmptyString`, `RangedInt` — all with `init?`. Enable if the generator supports failable initializers. **Check generator status before enabling.**

---

### GROUP J: Inheritance Patterns (1 pattern)

| # | Pattern | Status | Evidence | Fix Ref |
|---|---------|--------|----------|---------|
| J1 | Subclass accessing base class properties via @_cdecl | Partial | SwiftyBeaver `ConsoleDestination` accessing `BaseDestination.Format`, `.Asynchronously` | Inherited property emission |

**J1**: The TestFramework has `Dog : Animal` with method override, but doesn't test **property access** on the subclass that's defined on the base class. The Alamofire/SwiftyBeaver pattern is: construct subclass → access property defined on base class.

```swift
// Dog already inherits from Animal. Add property access tests in C# runtime tests:
// var dog = new Dog("Rex", "Lab");
// var name = dog.Name;  // inherited from Animal
// var sound = dog.Sound; // inherited from Animal
```
This is primarily a **C# runtime test** addition, not a Swift source change.

---

### GROUP K: Cache/CRUD Pattern (1 pattern)

| # | Pattern | Status | Evidence | Fix Ref |
|---|---------|--------|----------|---------|
| K1 | Full CRUD lifecycle (store → contains → retrieve → remove → verify removed) | Missing | Nuke DataCache, Kingfisher ImageCache, KeychainAccess Keychain, Lottie DefaultAnimationCache | State transition testing |

**K1**: This is covered by the `KeyValueStore` (Pattern C1) + `allKeys()` method (Pattern G4). The full lifecycle is:
1. `store["key"] = "value"` (create)
2. `store["key"]` returns `"value"` (read)
3. `store["key"] = "updated"` (update)
4. `store["key"] = nil` (delete)
5. `store["key"]` returns `nil` (verify)
6. `store.count()` returns 0

This is a **C# runtime test** pattern using the types from C1, not a new Swift type.

---

## Coverage Matrix: Real-World Library → TestFramework

| Library | Key Patterns | TestFramework Coverage (current → after) |
|---------|-------------|------------------------------------------|
| **Alamofire** | Static struct singleton, struct-backed enum (HTTPMethod), non-frozen struct equality, builder pattern, nested enums in struct, dictionary constructor, class singleton chain | 30% → 95% |
| **CryptoSwift** | Concrete type as existential param, enum-in-enum associated value, constructor with byte[], nested enum in class, protocol existential constructor | 10% → 90% |
| **KeychainAccess** | String subscript, builder pattern, enum extension methods, Optional\<Date\>, struct equality, CRUD lifecycle | 10% → 95% |
| **RxSwift** | DateTimeOffset (Date projection), class singleton, static bool setter, frozen enum with Tag | 50% → 90% |
| **DeviceKit** | Large enum (100+ cases), static collection property, enum extension method, optional string properties, enum payload extraction | 30% → 85% |
| **PhoneNumberKit** | Constructor with class+string, method returning collection, struct property chain, UInt64 properties | 60% → 90% |
| **Starscream** | UInt16 enum, struct with enum+string+int constructor, optional Data (deferred), WSError multi-type constructor | 40% → 80% |
| **Swinject** | Multiple class singletons, constructor with optional class, protocol existential return (known limitation) | 30% → 85% |
| **ObjectMapper** | Failable initializer (FromRawValue invalid), nested enum constructor, class inheritance chain | 50% → 80% |
| **SwiftyBeaver** | Subclass base-class property access, namespace collision, combined static methods workflow | 60% → 90% |
| **BonMot** | OptionSet, Int64-backed enum, struct equality, static struct properties | 20% → 90% |
| **SnapKit** | Struct with float properties, struct method returning same type, ObjC-bridged class | 50% → 85% |
| **Kingfisher** | Class singleton, CGSize param, enum with double payload, cache lifecycle, enum extension method | 50% → 90% |
| **Nuke** | Async method, optional double property, OptionSet, class property returning class, static factory optional | 40% → 85% |
| **Lottie** | Static factory returning optional, constructor with CGRect/CGSize, method with completion closure, collection property | 40% → 85% |
| **Stripe** | Multi-module, doubly-nested enum, @_spi suppression | 30% → 70% (multi-module untestable in single lib) |
| **BlinkID/UX** | Frozen enum RawValue access, cross-framework dependency, static property returning Self | 50% → 85% |
| **XMLCoder** | Optional double/string getters, struct TryCreate (failable), nested enums in class | 40% → 85% |
| **Reachability** | Enum multi-param payload, namespace collision, TryGet extraction | 60% → 90% |

**Estimated overall: 40% → 88% (without deferred items), 88% → 95%+ (after URL/Data projections + failable inits)**

---

## Implementation Plan

### Phase 1: Critical Fixes Without Test Coverage (validates already-shipped fixes)
| Pattern | New Swift File | Priority |
|---------|---------------|----------|
| A1: Foundation.Date | Enable `Foundation/Date.swift` + add Optional\<Date\> | **P0** — DateProjection shipped with zero test coverage |
| B1+B2: Existential boxing | `Protocols/ExistentialBoxing.swift` | **P0** — IExistentialBoxable shipped with zero test coverage |
| C1: Subscripts | `Properties/Subscripts.swift` | **P0** — @_cdecl string fix shipped, zero subscript tests |
| E3: Enum extension methods | Extend `Types/Enums.swift` | **P0** — trivial, common pattern |

### Phase 2: High-Value New Patterns
| Pattern | New Swift File | Priority |
|---------|---------------|----------|
| D1: Static struct singleton | `Properties/StaticStructSingleton.swift` | P1 |
| D3: Struct-backed enum | `Patterns/StructBackedEnum.swift` | P1 |
| E1+E2: Nested enums + enum-in-enum | `Types/NestedEnums.swift` | P1 |
| F1: Constructor with collection | `Collections/ConstructorCollections.swift` | P1 |
| H1: Non-frozen struct equality | Extend `Operators/Comparison.swift` | P1 |
| E5: OptionSet | `Types/OptionSets.swift` | P1 |

### Phase 3: Coverage Depth
| Pattern | New Swift File | Priority |
|---------|---------------|----------|
| E4: Large enum | `Types/LargeEnum.swift` | P2 |
| E6: Non-Int32 enums | `Types/NonStandardEnums.swift` | P2 |
| D2: Multiple class singletons | Extend patterns | P2 |
| F2: Dictionary constructor | `Collections/DictionaryConstructor.swift` | P2 |
| F3: Optional class constructor | Extend `Types/Classes.swift` | P2 |
| G1: Builder pattern | `Patterns/BuilderPattern.swift` | P2 |
| G2: Static factory optional | `Patterns/StaticFactory.swift` | P2 |
| J1: Subclass base-class props | C# tests only | P2 |

### Deferred
| Pattern | Reason |
|---------|--------|
| A2: Foundation.URL | Needs URL projection |
| A3: Foundation.Data | Needs DataProjection |
| I1: Failable initializer | Check generator support first |

---

## File Layout Summary

### New Swift files (12)
```
TestFramework/Sources/SwiftBindingsTestLib/
  Foundation/Date.swift                    ← RENAME from .disabled + extend
  Protocols/ExistentialBoxing.swift         ← NEW
  Properties/Subscripts.swift              ← NEW
  Properties/StaticStructSingleton.swift   ← NEW
  Types/NestedEnums.swift                  ← NEW
  Types/LargeEnum.swift                    ← NEW
  Types/OptionSets.swift                   ← NEW
  Types/NonStandardEnums.swift             ← NEW
  Collections/ConstructorCollections.swift ← NEW
  Collections/DictionaryConstructor.swift  ← NEW
  Patterns/BuilderPattern.swift            ← NEW
  Patterns/StructBackedEnum.swift          ← NEW
  Patterns/StaticFactory.swift             ← NEW
```

### Extended Swift files (3)
```
  Types/Enums.swift                        ← ADD Color extension methods
  Types/Classes.swift                      ← ADD TreeNode (optional class constructor)
  Operators/Comparison.swift               ← ADD non-frozen Tag struct
```

### New C# runtime test files (~14)
```
TestFramework/RuntimeTestsApp/
  Marshalling/DateMarshallingTests.cs
  Marshalling/NestedEnumTests.cs
  Marshalling/LargeEnumTests.cs
  Marshalling/OptionSetTests.cs
  Marshalling/NonStandardEnumTests.cs
  Protocols/ExistentialBoxingTests.cs
  Properties/SubscriptTests.cs
  Properties/StaticStructSingletonTests.cs
  Collections/ConstructorCollectionTests.cs
  Collections/DictionaryConstructorTests.cs
  Patterns/BuilderPatternTests.cs
  Patterns/StructBackedEnumTests.cs
  Patterns/StaticFactoryTests.cs
  Operators/StructEqualityTests.cs
```

### Estimated Size
- **Swift source**: ~500 lines new, ~60 lines extensions, 1 rename
- **C# tests**: ~1200-1500 lines across 14 test files
- **Total**: ~1800 lines of new code

---

## Patterns NOT Worth Adding (and why)

| Pattern | Why Skip |
|---------|----------|
| Namespace collision (class name = module name) | C# codegen issue, not ABI pattern; would require `global::` qualifiers that the generator already handles |
| Multi-module vendor (Stripe 9 modules) | Can't test inter-module deps in single test library; the generator handles this at the xcframework level |
| @_spi suppression | Generator-level filter, not an ABI pattern |
| NSObject subclass singleton | ObjC interop, not Swift ABI |
| ArraySlice bridge | Generator emits extension methods to adapt Array→ArraySlice; not a user-facing pattern |
| Doubly-nested enum | Same ABI as singly-nested; just scoping depth |
| Async with CancellationToken | .NET async infrastructure, not Swift ABI |
| Resource bundle crash (BlinkIDUX) | App packaging issue, not binding pattern |
