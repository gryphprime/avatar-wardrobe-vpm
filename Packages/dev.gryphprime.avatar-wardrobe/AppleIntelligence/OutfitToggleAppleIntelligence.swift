import Darwin
import Foundation
import FoundationModels

public typealias Completion = @convention(c) (UnsafePointer<CChar>?, UnsafePointer<CChar>?) -> Void

private struct ToggleInput: Codable {
    let id: Int
    let path: String
    let name: String
}

private struct CleanRequest: Codable {
    let projectType: String
    let avatarName: String
    let outfitName: String
    let outfitPrefabPath: String
    let toggles: [ToggleInput]
}

private struct ClusterInput: Codable {
    let id: Int
    let label: String
    let path: String
}

private struct ClusterRequest: Codable {
    let projectType: String
    let avatarName: String
    let outfitName: String
    let outfitPrefabPath: String
    let toggles: [ClusterInput]
}

@available(macOS 26.0, *)
@Generable
private struct CleanedToggle {
    @Guide(description: "The input toggle ID, copied exactly.")
    var id: Int

    @Guide(description: "A concise user-facing toggle label of at most 32 characters.")
    var label: String
}

@available(macOS 26.0, *)
@Generable
private struct CleanResponse {
    var labels: [CleanedToggle]
}

@available(macOS 26.0, *)
@Generable
private struct ToggleCluster {
    @Guide(description: "IDs of two or more input toggles that should always turn on and off together.")
    var ids: [Int]

    @Guide(description: "A concise user-facing label for the combined toggle, at most 32 characters.")
    var label: String
}

@available(macOS 26.0, *)
@Generable
private struct ClusterResponse {
    var groups: [ToggleCluster]
}

private struct BridgeLabel: Encodable {
    let id: Int
    let label: String
}

private struct BridgeResponse: Encodable {
    let labels: [BridgeLabel]
}

private struct BridgeCluster: Encodable {
    let ids: [Int]
    let label: String
}

private struct BridgeClusterResponse: Encodable {
    let groups: [BridgeCluster]
}

private func copiedString(_ value: String) -> UnsafePointer<CChar>? {
    UnsafePointer(strdup(value))
}

private func finish(_ completion: @escaping Completion, response: String? = nil, error: String? = nil) {
    completion(response.flatMap(copiedString), error.flatMap(copiedString))
}

private let accessorySuffixes = ["accessories", "accessory", "acces", "access", "accs", "ascs", "acc", "asc", "acs"]
private let boothBaseNames = ["Kaguya", "Manuka", "Shinano", "Miltina", "Selestia", "Moe", "Chiffon", "Airi", "Kikyo", "Shinra", "Sio", "Mame Friends", "Milphy", "Eku", "Lumina", "Maya", "Karin", "Lapwing", "Lashu", "Ichigo", "Mafuyu"]

private func humanizeLabel(_ label: String) -> String {
    label
        .replacingOccurrences(of: "([a-z])([A-Z])", with: "$1 $2", options: .regularExpression)
        .trimmingCharacters(in: .whitespacesAndNewlines)
}

private func accessorySuffix(in value: String) -> String? {
    let lowercased = value.trimmingCharacters(in: .whitespacesAndNewlines).lowercased()
    return accessorySuffixes.first { lowercased.hasSuffix($0) }
}

private func replacingAccessorySuffix(_ label: String, suffix: String) -> String {
    let trimmed = label.trimmingCharacters(in: .whitespacesAndNewlines)
    let prefix = String(trimmed.dropLast(suffix.count)).trimmingCharacters(in: .whitespacesAndNewlines)
    return prefix.isEmpty ? "Accessory" : prefix + " Accessory"
}

private func removingOutfitPrefix(_ label: String, outfitName: String) -> String {
    let trimmed = label.trimmingCharacters(in: .whitespacesAndNewlines)
    guard let prefix = trimmed.range(of: outfitName, options: [.anchored, .caseInsensitive]) else { return trimmed }
    let remainder = String(trimmed[prefix.upperBound...]).trimmingCharacters(in: .whitespacesAndNewlines.union(CharacterSet(charactersIn: "_-")))
    return remainder.isEmpty ? trimmed : remainder
}

private func removingRedundantBaseNames(_ label: String, avatarName: String) -> String {
    var result = label
    for baseName in boothBaseNames + [avatarName] where !baseName.isEmpty {
        let pattern = "\\b" + NSRegularExpression.escapedPattern(for: baseName) + "\\b"
        result = result.replacingOccurrences(of: pattern, with: "", options: .regularExpression)
    }
    return result.replacingOccurrences(of: "\\s+", with: " ", options: .regularExpression)
        .trimmingCharacters(in: .whitespacesAndNewlines.union(CharacterSet(charactersIn: "_-")))
}

private func expandKnownAbbreviation(_ label: String, sourceName: String) -> String {
    let trimmed = humanizeLabel(label)
    guard !trimmed.isEmpty else { return trimmed }

    if accessorySuffix(in: sourceName) != nil {
        if let suffix = accessorySuffix(in: trimmed) {
            return replacingAccessorySuffix(trimmed, suffix: suffix)
        }
        if trimmed.lowercased().hasSuffix("accent") {
            return replacingAccessorySuffix(trimmed, suffix: "accent")
        }
        return trimmed + " Accessory"
    }
    return accessorySuffix(in: trimmed).map { replacingAccessorySuffix(trimmed, suffix: $0) } ?? trimmed
}

@available(macOS 26.0, *)
private func cleanedLabels(_ request: CleanRequest) async throws -> [BridgeLabel] {
    let source = String(data: try JSONEncoder().encode(request), encoding: .utf8)!
    let session = LanguageModelSession(instructions: """
        You are renaming components of a VRChat avatar outfit toggle menu. The request includes the VRChat
        project type, avatar name, outfit names, and outfit prefab asset paths as context. The names and paths
        can each describe multiple selected outfits. Use the avatar name
        to remove the base avatar identifier from outfit and component labels when it is redundant; never
        copy technical folder, creator, version, or file-path text into a label. Preserve the outfit item's
        meaning and distinct variants.

        Common Booth avatar base names that are usually redundant in outfit labels include Kaguya, Manuka,
        Shinano, Miltina, Selestia, Moe, Chiffon, Airi, Kikyo, Shinra, Sio, Mame Friends, Milphy, Eku,
        Lumina, Maya, Karin, Lapwing, Lashu, Ichigo, and Mafuyu. Strip these only when they identify the
        base avatar rather than the outfit itself; never leave an empty or ambiguous label.

        Never abbreviate or shorten words. Expand an abbreviation only when its meaning is clear; otherwise
        remove the technical fragment rather than guessing. Do not emit partial abbreviations such as Leg
        Asc: for outfit components, treat Acc, Accs, Asc, Ascs, Acs, Access, and similar suffix variations
        as Accessory. LegAsc, Leg Acc, and LegAccs must become Leg Accessory, while Hat_Pins must become Hat
        Pins. Do not invent features. Return one concise label for every input ID. Input ID 0 is the outfit
        menu title: use all selected outfit names and paths to produce one concise combined title, rather than
        a generic hierarchy name such as Color 1 or Outfit Menu. When several outfits are supplied, retain an
        identifying non-color word from each; never reduce a combined title to colors or variants alone. For example,
        Reverie_Shinano_Black on avatar Shinano should become Reverie Black.
        """)
    let response = try await session.respond(to: "Clean these names: \(source)", generating: CleanResponse.self)
    let knownIDs = Set(request.toggles.map(\.id))
    let sourceNames = request.toggles.reduce(into: [Int: String]()) { $0[$1.id] = $1.name }
    return response.content.labels
        .filter { knownIDs.contains($0.id) && !$0.label.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty }
        .map {
            let modelLabel = humanizeLabel($0.label)
            let label = $0.id == 0 ? modelLabel : removingOutfitPrefix(modelLabel, outfitName: request.outfitName)
            let withoutBaseName = removingRedundantBaseNames(label, avatarName: request.avatarName)
            return BridgeLabel(id: $0.id, label: expandKnownAbbreviation(withoutBaseName, sourceName: sourceNames[$0.id] ?? ""))
        }
}

@available(macOS 26.0, *)
private func cleanedLabelsPartially(_ request: CleanRequest, toggles: [ToggleInput]) async -> [BridgeLabel] {
    let chunk = CleanRequest(
        projectType: request.projectType,
        avatarName: request.avatarName,
        outfitName: request.outfitName,
        outfitPrefabPath: request.outfitPrefabPath,
        toggles: toggles)
    do {
        return try await cleanedLabels(chunk)
    } catch {
        guard toggles.count > 1 else { return [] }
        let midpoint = toggles.count / 2
        let first = await cleanedLabelsPartially(request, toggles: Array(toggles[..<midpoint]))
        let second = await cleanedLabelsPartially(request, toggles: Array(toggles[midpoint...]))
        return first + second
    }
}

@_cdecl("OutfitToggleAppleIntelligence_IsAvailable")
public func isAvailable() -> Int32 {
    guard #available(macOS 26.0, *), case .available = SystemLanguageModel.default.availability else {
        return 0
    }
    return 1
}

@_cdecl("OutfitToggleAppleIntelligence_CleanNames")
public func cleanNames(_ requestJSON: UnsafePointer<CChar>?, _ completion: @escaping Completion) {
    guard let requestJSON else {
        finish(completion, error: "Missing cleanup request.")
        return
    }
    let input = String(cString: requestJSON)

    Task {
        do {
            guard #available(macOS 26.0, *) else {
                finish(completion, error: "Apple Intelligence requires macOS 26 or later.")
                return
            }
            guard case .available = SystemLanguageModel.default.availability else {
                finish(completion, error: "Apple Intelligence is not available on this Mac.")
                return
            }

            let request = try JSONDecoder().decode(CleanRequest.self, from: Data(input.utf8))
            let labels = await cleanedLabelsPartially(request, toggles: request.toggles)
            if labels.isEmpty {
                finish(completion, error: "Apple Intelligence could not clean any of these names.")
                return
            }
            let output = try JSONEncoder().encode(BridgeResponse(labels: labels))
            finish(completion, response: String(data: output, encoding: .utf8)!)
        } catch {
            finish(completion, error: error.localizedDescription)
        }
    }
}

@_cdecl("OutfitToggleAppleIntelligence_ClusterToggles")
public func clusterToggles(_ requestJSON: UnsafePointer<CChar>?, _ completion: @escaping Completion) {
    guard let requestJSON else {
        finish(completion, error: "Missing clustering request.")
        return
    }
    let input = String(cString: requestJSON)

    Task {
        do {
            guard #available(macOS 26.0, *) else {
                finish(completion, error: "Apple Intelligence requires macOS 26 or later.")
                return
            }
            guard case .available = SystemLanguageModel.default.availability else {
                finish(completion, error: "Apple Intelligence is not available on this Mac.")
                return
            }

            let request = try JSONDecoder().decode(ClusterRequest.self, from: Data(input.utf8))
            let source = String(data: try JSONEncoder().encode(request), encoding: .utf8)!
            let session = LanguageModelSession(instructions: """
                You group VRChat avatar outfit toggles. Return only groups whose objects should always be turned
                on and off together as one optional item; every returned group must have at least two IDs, and no
                ID may appear in more than one group. Leave unrelated items out of groups.

                Group dependent parts of one named item: for example, a bag with its charm, hand strap, perfume,
                sunglasses case, or sunscreen belongs in one Bag toggle; Necklace 1, Necklace 2, and Necklace 3
                can form one Necklace toggle; Straw Hat and Straw Hat Lace can form one Straw Hat toggle.
                Similar words alone do not make a group: Bikini Top and Bikini Bottom stay separate, and Chain
                Ankle, Chain Leg, and Chain Waist stay separate because they occupy different locations. Preserve
                the supplied meaning, use hierarchy paths as context, and do not invent groups.
                """)
            let response = try await session.respond(to: "Group these toggles: \(source)", generating: ClusterResponse.self)
            let knownIDs = Set(request.toggles.map(\.id))
            var claimed = Set<Int>()
            var groups = [BridgeCluster]()
            for group in response.content.groups {
                var seen = Set<Int>()
                let ids = group.ids.filter { knownIDs.contains($0) && seen.insert($0).inserted && !claimed.contains($0) }
                guard ids.count > 1, !group.label.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { continue }
                claimed.formUnion(ids)
                groups.append(BridgeCluster(ids: ids, label: group.label.trimmingCharacters(in: .whitespacesAndNewlines)))
            }
            let output = try JSONEncoder().encode(BridgeClusterResponse(groups: groups))
            finish(completion, response: String(data: output, encoding: .utf8)!)
        } catch {
            finish(completion, error: error.localizedDescription)
        }
    }
}

@_cdecl("OutfitToggleAppleIntelligence_FreeString")
public func freeString(_ value: UnsafeMutablePointer<CChar>?) {
    free(value)
}
