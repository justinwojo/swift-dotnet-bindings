// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// Simple enums with no raw value the binding can know, crossing by case identity: each C# constant
// is its case's declaration-order index, and every crossing maps it to that case with a switch on
// the case, never by loading the enum's bytes. Every function reports the case Swift actually
// holds by name, so a test can tell a wrong case from a right one.

/// A non-frozen enum whose first case is `@_spi`: the binding cannot name it, but Swift can hand
/// it out. It must reach C# as a value that is no published member and come back as itself.
public enum CaseShift {
    @_spi(CaseIdentity) case hidden
    case first
    case second
}

public func caseShiftName(_ value: CaseShift) -> String {
    switch value {
    case .hidden: return "hidden"
    case .first: return "first"
    case .second: return "second"
    }
}

public func caseShiftNameOrNone(_ value: CaseShift?) -> String { value.map(caseShiftName) ?? "none" }
public func caseShiftHidden() -> CaseShift { .hidden }
public func caseShiftSecond() -> CaseShift { .second }
public func caseShiftEcho(_ value: CaseShift) -> CaseShift { value }
public func caseShiftOptional(_ hidden: Bool) -> CaseShift? { hidden ? .hidden : nil }
public func caseShiftOptionalThrowing(_ hidden: Bool) throws -> CaseShift? { hidden ? .hidden : nil }

extension CaseShift {
    /// The case's name, read on the enum itself.
    public var caseName: String { caseShiftName(self) }

    /// The other visible case; the hidden case stays itself.
    public func toggled() -> CaseShift {
        switch self {
        case .hidden: return .hidden
        case .first: return .second
        case .second: return .first
        }
    }
}

/// Every other crossing: a stored property, a subscript, an optional return, and closures in both
/// directions (an argument Swift hands out, a result Swift takes back).
public final class CaseShiftBox {
    public var mode: CaseShift
    public init(mode: CaseShift) { self.mode = mode }
    public var modeName: String { caseShiftName(mode) }
    public subscript(slot: Int) -> CaseShift { slot == 0 ? .hidden : .second }
    public func optionalMode(_ present: Bool) -> CaseShift? { present ? mode : nil }
    public func report(_ body: (CaseShift) -> Void) { body(mode) }
    public func reportOptional(_ present: Bool, _ body: (CaseShift?) -> Void) { body(present ? mode : nil) }
    public func transform(_ body: (CaseShift) -> CaseShift) { mode = body(mode) }
    public func pickOptional(_ body: () -> CaseShift?) -> String { body().map(caseShiftName) ?? "none" }
}

/// One case: Swift stores it in zero bytes.
public enum SoloPlain {
    case only
}

public func soloPlainMake() -> SoloPlain { .only }
public func soloPlainName(_ value: SoloPlain) -> String { value == .only ? "only" : "?" }

/// More cases than a byte holds: Swift stores the tag in two bytes.
public enum WideCases: CaseIterable {
    case c0, c1, c2, c3, c4, c5, c6, c7, c8, c9, c10, c11, c12, c13, c14
    case c15, c16, c17, c18, c19, c20, c21, c22, c23, c24, c25, c26, c27, c28, c29
    case c30, c31, c32, c33, c34, c35, c36, c37, c38, c39, c40, c41, c42, c43, c44
    case c45, c46, c47, c48, c49, c50, c51, c52, c53, c54, c55, c56, c57, c58, c59
    case c60, c61, c62, c63, c64, c65, c66, c67, c68, c69, c70, c71, c72, c73, c74
    case c75, c76, c77, c78, c79, c80, c81, c82, c83, c84, c85, c86, c87, c88, c89
    case c90, c91, c92, c93, c94, c95, c96, c97, c98, c99, c100, c101, c102, c103, c104
    case c105, c106, c107, c108, c109, c110, c111, c112, c113, c114, c115, c116, c117, c118, c119
    case c120, c121, c122, c123, c124, c125, c126, c127, c128, c129, c130, c131, c132, c133, c134
    case c135, c136, c137, c138, c139, c140, c141, c142, c143, c144, c145, c146, c147, c148, c149
    case c150, c151, c152, c153, c154, c155, c156, c157, c158, c159, c160, c161, c162, c163, c164
    case c165, c166, c167, c168, c169, c170, c171, c172, c173, c174, c175, c176, c177, c178, c179
    case c180, c181, c182, c183, c184, c185, c186, c187, c188, c189, c190, c191, c192, c193, c194
    case c195, c196, c197, c198, c199, c200, c201, c202, c203, c204, c205, c206, c207, c208, c209
    case c210, c211, c212, c213, c214, c215, c216, c217, c218, c219, c220, c221, c222, c223, c224
    case c225, c226, c227, c228, c229, c230, c231, c232, c233, c234, c235, c236, c237, c238, c239
    case c240, c241, c242, c243, c244, c245, c246, c247, c248, c249, c250, c251, c252, c253, c254
    case c255, c256, c257, c258, c259, c260, c261, c262, c263, c264, c265, c266, c267, c268, c269
    case c270, c271, c272, c273, c274, c275, c276, c277, c278, c279, c280, c281, c282, c283, c284
    case c285, c286, c287, c288, c289, c290, c291, c292, c293, c294, c295, c296, c297, c298, c299
}

public func wideCase(at index: Int) -> WideCases { WideCases.allCases[index] }
public func wideIndex(_ value: WideCases) -> Int { WideCases.allCases.firstIndex(of: value) ?? -1 }

/// A String-backed enum that declares its own `toRawValue()` and `fromRawValue(_:)`: the binding's
/// raw-value helpers must take other names rather than collide with them.
public enum RawNamedMode: String {
    case alpha = "A"
    case beta = "B"

    public func toRawValue() -> String { "own:" + rawValue }
    public static func fromRawValue(_ text: String) -> RawNamedMode? { text == "own" ? .beta : nil }
}

/// The same pair on a frozen String enum, which projects to a C# enum whose raw-value helpers are
/// extension methods beside the enum's own. (The static members return the enum itself: the C#
/// enum's extensions skip a static method returning an Optional of it.) The enum's own members
/// also take String parameters, which cross its wrappers as UTF-8 bytes: one of each shape, an
/// instance or static member with a String or a non-String result.
@frozen
public enum RawNamedFrozen: String {
    case alpha = "A"
    case beta = "B"

    public func toRawValue() -> String { "own:" + rawValue }
    public static func fromRawValue(_ text: String) -> RawNamedFrozen { text == "own" ? .beta : .alpha }

    public func labeled(_ prefix: String, _ suffix: String) -> String { prefix + rawValue + suffix }
    public func matches(_ text: String) -> Bool { text == rawValue }
    public static func describe(_ text: String, count: Int) -> String { "\(text.count)x\(count):\(text)" }
}

/// And on an integral raw-value enum.
public enum RawNamedLevel: Int {
    case low = 10
    case high = 20

    public func toRawValue() -> Int { rawValue + 1 }
    public static func fromRawValue(_ value: Int) -> RawNamedLevel { value == 0 ? .high : .low }
}

/// An integral enum with a hidden case: its raw-value helpers ask Swift, which knows the hidden
/// case's raw value and finds the hidden case for it.
public enum CaseShiftLevel: Int {
    @_spi(CaseIdentity) case hidden = 5
    case low = 10
    case high = 20
}

public func caseShiftLevelHidden() -> CaseShiftLevel { .hidden }
public func caseShiftLevelName(_ value: CaseShiftLevel) -> String {
    switch value {
    case .hidden: return "hidden"
    case .low: return "low"
    case .high: return "high"
    }
}

/// A one-byte C# enum whose 128 published constants fill the lower half of its range, followed by
/// a hidden case: the hidden case's unnamed value must still find a free pattern above them.
public enum NarrowCases: UInt8 {

    case n0, n1, n2, n3, n4, n5, n6, n7, n8, n9, n10, n11, n12, n13, n14, n15
    case n16, n17, n18, n19, n20, n21, n22, n23, n24, n25, n26, n27, n28, n29, n30, n31
    case n32, n33, n34, n35, n36, n37, n38, n39, n40, n41, n42, n43, n44, n45, n46, n47
    case n48, n49, n50, n51, n52, n53, n54, n55, n56, n57, n58, n59, n60, n61, n62, n63
    case n64, n65, n66, n67, n68, n69, n70, n71, n72, n73, n74, n75, n76, n77, n78, n79
    case n80, n81, n82, n83, n84, n85, n86, n87, n88, n89, n90, n91, n92, n93, n94, n95
    case n96, n97, n98, n99, n100, n101, n102, n103, n104, n105, n106, n107, n108, n109, n110, n111
    case n112, n113, n114, n115, n116, n117, n118, n119, n120, n121, n122, n123, n124, n125, n126, n127
    @_spi(CaseIdentity) case hidden
}

public func narrowCasesHidden() -> NarrowCases { .hidden }
public func narrowCase(at index: Int) -> NarrowCases { NarrowCases(rawValue: UInt8(index))! }
public func narrowCasesIsHidden(_ value: NarrowCases) -> Bool { value == .hidden }
public func narrowCasesRawValue(_ value: NarrowCases) -> Int { Int(value.rawValue) }

