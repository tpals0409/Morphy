import AppKit

/// A newer Mac release on the website (docs/release.md, DECISIONS.md D9).
struct AppUpdate {
    let version: String
    let url: URL
    let bytes: Int
    let sha256: String
}

/// Checks `/api/release` and replaces the running app in place. Same rules as the Windows app (windows/README.md):
/// only a higher version, only `/releases/Morphy-X.Y.Z.dmg` on our own site, the site's size and SHA-256, and an app
/// signed by the same team as the running one. Nothing inside the DMG runs before the swap.
enum Updater {
    /// `defaults write app.morphy.Morphy updateOrigin http://localhost:8787` (or `-updateOrigin <url>` on the command line)
    /// points it at `wrangler dev` for testing.
    static var origin: URL { URL(string: UserDefaults.standard.string(forKey: "updateOrigin") ?? "https://morphy.tpals0409dev.workers.dev")! }
    static var current: String { Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String ?? "0.0.0" }

    static func isNewer(_ a: String, than b: String) -> Bool {
        let parts = { (v: String) in v.split(separator: ".").compactMap { Int($0) } }
        return parts(b).lexicographicallyPrecedes(parts(a))
    }

    /// nil unless the JSON describes a well-formed release newer than `current`.
    static func parse(_ data: Data, current: String) -> AppUpdate? {
        guard let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let version = json["version"] as? String, version.range(of: #"^\d+\.\d+\.\d+$"#, options: .regularExpression) != nil,
              isNewer(version, than: current),
              let path = json["path"] as? String, path == "/releases/Morphy-\(version).dmg",
              let sha = json["sha256"] as? String, sha.range(of: #"^[0-9a-f]{64}$"#, options: .regularExpression) != nil,
              let bytes = json["bytes"] as? Int, bytes > 0, bytes < 1_000_000_000,
              let url = URL(string: path, relativeTo: origin)?.absoluteURL else { return nil }
        return AppUpdate(version: version, url: url, bytes: bytes, sha256: sha)
    }

    /// nil when this is the newest version.
    static func check() async throws -> AppUpdate? {
        let (data, response) = try await URLSession.shared.data(from: origin.appendingPathComponent("api/release"))
        let status = (response as? HTTPURLResponse)?.statusCode
        if status == 404 { return nil }
        guard status == 200 else { throw MorphyError("업데이트를 확인할 수 없어요. 잠시 후 다시 시도해 주세요.") }
        return parse(data, current: current)
    }

    /// Downloads and verifies the update, then swaps it in for the running app. Relaunch afterwards.
    static func install(_ update: AppUpdate) async throws {
        let (temp, response) = try await URLSession.shared.download(from: update.url)
        guard (response as? HTTPURLResponse)?.statusCode == 200 else { throw MorphyError("업데이트를 내려받을 수 없어요. 잠시 후 다시 시도해 주세요.") }
        let fm = FileManager.default
        try fm.createDirectory(at: EngineInstaller.cache, withIntermediateDirectories: true)
        let dmg = EngineInstaller.cache.appendingPathComponent("Morphy-update.dmg")
        try? fm.removeItem(at: dmg)
        try fm.moveItem(at: temp, to: dmg)
        defer { try? fm.removeItem(at: dmg) }
        try replace(Bundle.main.bundleURL, with: dmg, bytes: update.bytes, sha256: update.sha256)
    }

    /// Size + SHA-256 → mount read-only → same-team signature → copy beside `app` → check the copy → swap.
    static func replace(_ app: URL, with dmg: URL, bytes: Int, sha256: String) throws {
        let fm = FileManager.default
        let corrupt = MorphyError("받은 업데이트 파일이 올바르지 않아요. 다시 시도해 주세요.")
        guard (try? dmg.resourceValues(forKeys: [.fileSizeKey]))?.fileSize == bytes, try EngineInstaller.hash(dmg) == sha256 else { throw corrupt }

        let mount = EngineInstaller.cache.appendingPathComponent("mount-\(UUID().uuidString)")
        try fm.createDirectory(at: mount, withIntermediateDirectories: true)
        defer {
            EngineInstaller.shell("/usr/bin/hdiutil", ["detach", "-force", mount.path])
            try? fm.removeItem(at: mount)
        }
        guard EngineInstaller.shell("/usr/bin/hdiutil", ["attach", "-nobrowse", "-readonly", "-noautoopen", "-mountpoint", mount.path, dmg.path]).status == 0
        else { throw corrupt }
        let source = mount.appendingPathComponent("Morphy.app")
        guard fm.fileExists(atPath: source.path) else { throw corrupt }
        try checkSameSigner(source, as: app)

        // Same folder as the app, so the final swap is a rename on one volume.
        let staging = app.deletingLastPathComponent().appendingPathComponent(".Morphy-update-\(UUID().uuidString).app")
        defer { try? fm.removeItem(at: staging) }
        guard EngineInstaller.shell("/usr/bin/ditto", [source.path, staging.path]).status == 0 else {
            throw MorphyError("업데이트를 설치할 권한이 없어요.", "\(app.deletingLastPathComponent().path) 폴더에 쓸 수 있는지 확인해 주세요.")
        }
        try checkSameSigner(staging, as: app)
        _ = try fm.replaceItemAt(app, withItemAt: staging)
    }

    /// The update must carry a valid signature from the running app's team, so permissions granted to Morphy
    /// carry over and nobody else's build can replace it. A notarized install never accepts an un-notarized update.
    static func checkSameSigner(_ new: URL, as old: URL) throws {
        let team = { (app: URL) in
            EngineInstaller.shell("/usr/bin/codesign", ["-dv", app.path]).output.split(separator: "\n")
                .first { $0.hasPrefix("TeamIdentifier=") }.map { String($0.dropFirst("TeamIdentifier=".count)) }
        }
        let gatekeeper = { (app: URL) in EngineInstaller.shell("/usr/sbin/spctl", ["--assess", "--type", "execute", app.path]).status == 0 }
        guard EngineInstaller.shell("/usr/bin/codesign", ["--verify", "--deep", "--strict", new.path]).status == 0,
              let newTeam = team(new), newTeam != "not set", newTeam == team(old),
              !gatekeeper(old) || gatekeeper(new)
        else { throw MorphyError("업데이트 파일의 서명을 확인할 수 없어요. 공식 배포 파일이 아니에요.") }
    }

    /// Opens the new copy once this process has exited. The path goes in as $0, so it is never parsed as shell code.
    static func relaunch() {
        let p = Process()
        p.executableURL = URL(fileURLWithPath: "/bin/sh")
        p.arguments = ["-c", "while /bin/kill -0 \(getpid()) 2>/dev/null; do /bin/sleep 0.2; done; /usr/bin/open \"$0\"", Bundle.main.bundlePath]
        try? p.run()
        NSApp.terminate(nil)
    }
}
