import AppKit
import CryptoKit

/// LibreOffice for document conversion, downloaded on first use (docs/engine-download.md, DECISIONS.md D4).
/// Version and hash are pinned here; a download installs only if its SHA-256 and TDF's Apple signature both check out.
enum LibreOffice {
    static let version = "26.8.1"
    static let minimum = [26, 8]       // an installed LibreOffice at least this new is used instead of downloading
    static let teamID = "7P5S3ZLCN7"   // The Document Foundation
    static let bytes = 298_845_477
    static let sha256 = "ca074e0b13571efb30a31fc531c5f95e997030637325e5d5de92d67aefdf0dfc"
    /// Stable mirrors first; once a release leaves "stable" it lives on the archive server under its full version.
    static let sources = [
        "https://download.documentfoundation.org/libreoffice/stable/26.8.1/mac/aarch64/LibreOffice_26.8.1_MacOS_aarch64.dmg",
        "https://downloadarchive.documentfoundation.org/libreoffice/old/26.8.1.1/mac/aarch64/LibreOffice_26.8.1.1_MacOS_aarch64.dmg",
    ].map { URL(string: $0)! }

    /// ".noindex" keeps Spotlight out, so the engine never shows up as an app of its own.
    static let root = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
        .appendingPathComponent("Morphy/Engines.noindex/LibreOffice")
    static var managed: URL { root.appendingPathComponent("\(version)/LibreOffice.app") }
    static let system = URL(fileURLWithPath: "/Applications/LibreOffice.app")

    /// The user's own install when new enough, else Morphy's copy (D4). `ignoreInstalledLibreOffice` lets the
    /// download flow be tried on a Mac that already has LibreOffice.
    static var app: URL? {
        if !UserDefaults.standard.bool(forKey: "ignoreInstalledLibreOffice"),
           let v = Bundle(url: system)?.infoDictionary?["CFBundleShortVersionString"] as? String, isNewEnough(v) { return system }
        return FileManager.default.fileExists(atPath: managed.path) ? managed : nil
    }

    static func isNewEnough(_ version: String) -> Bool {
        !version.split(separator: ".").compactMap { Int($0) }.lexicographicallyPrecedes(minimum)
    }
}

final class EngineInstaller: NSObject, URLSessionDownloadDelegate {
    static let shared = EngineInstaller()
    static let cancelled = MorphyError("내려받기를 취소했어요.")

    enum State: Equatable { case idle, downloading(received: Int64), installing }
    private(set) var state = State.idle { didSet { onChange() } }
    var onChange: () -> Void = {}

    private var waiters: [(MorphyError?) -> Void] = []
    private var task: URLSessionDownloadTask?
    private var source = 0
    private lazy var session = URLSession(configuration: .default, delegate: self, delegateQueue: .main)

    static let cache = FileManager.default.urls(for: .cachesDirectory, in: .userDomainMask)[0].appendingPathComponent("Morphy")
    private static var dmg: URL { cache.appendingPathComponent("LibreOffice.dmg") }
    private static var resumeFile: URL { cache.appendingPathComponent("LibreOffice.dmg.resume") }

    /// Calls `done` with nil once the engine is installed, or with why it isn't. Joins a download already running.
    func install(_ done: @escaping (MorphyError?) -> Void) {
        waiters.append(done)
        guard state == .idle else { return }
        let home = FileManager.default.homeDirectoryForCurrentUser
        let free = (try? home.resourceValues(forKeys: [.volumeAvailableCapacityForImportantUsageKey]))?.volumeAvailableCapacityForImportantUsage ?? 0
        guard free > 1_200_000_000 else { return finish(MorphyError("저장 공간이 1.2GB 필요해요. 공간을 확보한 뒤 다시 시도해 주세요.")) }
        source = 0
        download()
    }

    func cancel() {
        guard case .downloading = state else { return }
        task?.cancel { data in try? data?.write(to: Self.resumeFile) }  // kept so the next try resumes
        finish(Self.cancelled)
    }

    private func download() {
        try? FileManager.default.createDirectory(at: Self.cache, withIntermediateDirectories: true)
        if let data = try? Data(contentsOf: Self.resumeFile) {
            task = session.downloadTask(withResumeData: data)
        } else {
            task = session.downloadTask(with: LibreOffice.sources[source])
        }
        try? FileManager.default.removeItem(at: Self.resumeFile)
        state = .downloading(received: 0)
        task?.resume()
    }

    private func finish(_ error: MorphyError?) {
        if let error { log.error("engine install: \(error.message, privacy: .public)") }
        state = .idle
        let done = waiters
        waiters = []
        done.forEach { $0(error) }
    }

    func urlSession(_ session: URLSession, downloadTask: URLSessionDownloadTask, didWriteData _: Int64,
                    totalBytesWritten: Int64, totalBytesExpectedToWrite _: Int64) {
        if case .downloading = state { state = .downloading(received: totalBytesWritten) }
    }

    func urlSession(_ session: URLSession, downloadTask: URLSessionDownloadTask, didFinishDownloadingTo location: URL) {
        guard case .downloading = state else { return }
        // A release that left "stable" answers 404 there: move on to the archive server.
        if (downloadTask.response as? HTTPURLResponse)?.statusCode != 200 {
            source += 1
            return source < LibreOffice.sources.count ? download() : finish(MorphyError("문서 변환 기능을 내려받을 수 없어요. 잠시 후 다시 시도해 주세요."))
        }
        try? FileManager.default.removeItem(at: Self.dmg)
        do { try FileManager.default.moveItem(at: location, to: Self.dmg) } catch {  // must happen before returning
            return finish(MorphyError("문서 변환 기능을 저장하지 못했어요. 저장 공간을 확인해 주세요."))
        }
        state = .installing
        DispatchQueue.global(qos: .userInitiated).async {
            var failure: MorphyError?
            do {
                try Self.verifyAndInstall(dmg: Self.dmg, bytes: LibreOffice.bytes, sha256: LibreOffice.sha256,
                                          teamID: LibreOffice.teamID, as: LibreOffice.managed)
            } catch {
                failure = error as? MorphyError ?? MorphyError("문서 변환 기능을 추가하지 못했어요. 다시 시도해 주세요.")
            }
            try? FileManager.default.removeItem(at: Self.dmg)
            DispatchQueue.main.async { self.finish(failure) }
        }
    }

    func urlSession(_ session: URLSession, task: URLSessionTask, didCompleteWithError error: Error?) {
        guard let error = error as NSError?, case .downloading = state, error.code != NSURLErrorCancelled else { return }
        if let data = error.userInfo[NSURLSessionDownloadTaskResumeData] as? Data { try? data.write(to: Self.resumeFile) }
        finish(MorphyError("내려받기가 중단됐어요. 다시 시도하면 이어서 받아요."))
    }

    // MARK: Verify and install (static so the self-test can feed it fake DMGs)

    /// Size + SHA-256 → mount read-only → Apple signature of the expected team + Gatekeeper → copy → check the copy
    /// → atomic rename to `app`'s folder. Nothing inside the DMG is ever run.
    static func verifyAndInstall(dmg: URL, bytes: Int, sha256: String, teamID: String, as app: URL) throws {
        let corrupt = MorphyError("받은 파일이 올바르지 않아요. 다시 내려받아 주세요.")
        guard (try? dmg.resourceValues(forKeys: [.fileSizeKey]))?.fileSize == bytes, try hash(dmg) == sha256 else { throw corrupt }

        let fm = FileManager.default
        let mount = cache.appendingPathComponent("mount-\(UUID().uuidString)")
        try fm.createDirectory(at: mount, withIntermediateDirectories: true)
        defer {
            _ = shell("/usr/bin/hdiutil", ["detach", "-force", mount.path])
            try? fm.removeItem(at: mount)
        }
        guard shell("/usr/bin/hdiutil", ["attach", "-nobrowse", "-readonly", "-noautoopen", "-mountpoint", mount.path, dmg.path]).status == 0
        else { throw MorphyError("내려받은 파일을 열 수 없어요. 다시 내려받아 주세요.") }
        let source = mount.appendingPathComponent(app.lastPathComponent)
        guard fm.fileExists(atPath: source.path) else { throw corrupt }
        try checkSignature(source, teamID: teamID)

        let versionDir = app.deletingLastPathComponent()
        let staging = versionDir.deletingLastPathComponent().appendingPathComponent(".installing-\(UUID().uuidString)")
        try fm.createDirectory(at: staging, withIntermediateDirectories: true)
        defer { try? fm.removeItem(at: staging) }
        let copy = staging.appendingPathComponent(app.lastPathComponent)
        guard shell("/usr/bin/ditto", [source.path, copy.path]).status == 0 else {
            throw MorphyError("문서 변환 기능을 추가하지 못했어요. 저장 공간을 확인한 뒤 다시 시도해 주세요.")
        }
        try checkSignature(copy, teamID: teamID)

        // Replace any older version in one rename; the staging folder holds exactly what will be used.
        for old in (try? fm.contentsOfDirectory(at: versionDir.deletingLastPathComponent(), includingPropertiesForKeys: nil)) ?? []
        where !old.lastPathComponent.hasPrefix(".") { try? fm.removeItem(at: old) }
        try fm.moveItem(at: staging, to: versionDir)
        hide(app)
    }

    /// The engine is a part of Morphy, not a second app: take it out of "Open With" and Launch Services,
    /// and drop its Quick Look extensions so Finder previews stay macOS's own.
    static func hide(_ app: URL) {
        let lsregister = "/System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister"
        for ext in (try? FileManager.default.contentsOfDirectory(at: app.appendingPathComponent("Contents/PlugIns"),
                                                                 includingPropertiesForKeys: nil)) ?? [] {
            shell("/usr/bin/pluginkit", ["-r", ext.path])
        }
        shell(lsregister, ["-u", app.path])
    }

    private static func checkSignature(_ app: URL, teamID: String) throws {
        let bad = MorphyError("내려받은 파일의 서명을 확인할 수 없어요. 공식 배포 파일이 아니에요.")
        guard shell("/usr/bin/codesign", ["--verify", "--deep", "--strict", app.path]).status == 0,
              shell("/usr/bin/codesign", ["-dv", app.path]).output.contains("TeamIdentifier=\(teamID)\n"),
              shell("/usr/sbin/spctl", ["--assess", "--type", "execute", app.path]).status == 0  // notarized (online check)
        else { throw bad }
    }

    private static func hash(_ url: URL) throws -> String {
        let file = try FileHandle(forReadingFrom: url)
        defer { try? file.close() }
        var sha = SHA256()
        while let chunk = try file.read(upToCount: 8 << 20), !chunk.isEmpty { sha.update(data: chunk) }
        return sha.finalize().map { String(format: "%02x", $0) }.joined()
    }

    @discardableResult
    static func shell(_ path: String, _ args: [String]) -> (status: Int32, output: String) {
        let p = Process(), pipe = Pipe()
        p.executableURL = URL(fileURLWithPath: path)
        p.arguments = args
        p.standardOutput = pipe
        p.standardError = pipe
        p.standardInput = FileHandle.nullDevice
        guard (try? p.run()) != nil else { return (-1, "") }
        let data = pipe.fileHandleForReading.readDataToEndOfFile()
        p.waitUntilExit()
        return (p.terminationStatus, String(decoding: data, as: UTF8.self))
    }

    /// Leftovers from a crash or force quit (PRD 9.6): half-copied engines, mounts, stale partial downloads.
    /// Also moves an engine from the pre-".noindex" folder and re-hides it (Spotlight may have registered it).
    static func cleanUpLeftovers() {
        let fm = FileManager.default
        let old = LibreOffice.root.deletingLastPathComponent().deletingLastPathComponent().appendingPathComponent("Engines")
        if fm.fileExists(atPath: old.path), !fm.fileExists(atPath: LibreOffice.root.deletingLastPathComponent().path) {
            try? fm.moveItem(at: old, to: LibreOffice.root.deletingLastPathComponent())
        }
        if fm.fileExists(atPath: LibreOffice.managed.path) { hide(LibreOffice.managed) }
        for dir in (try? fm.contentsOfDirectory(at: LibreOffice.root, includingPropertiesForKeys: nil)) ?? []
        where dir.lastPathComponent.hasPrefix(".installing-") { try? fm.removeItem(at: dir) }
        for item in (try? fm.contentsOfDirectory(at: cache, includingPropertiesForKeys: [.contentModificationDateKey])) ?? [] {
            let name = item.lastPathComponent
            if name.hasPrefix("mount-") {
                shell("/usr/bin/hdiutil", ["detach", "-force", item.path])
                try? fm.removeItem(at: item)
            } else if name == dmg.lastPathComponent {
                try? fm.removeItem(at: item)
            } else if name == resumeFile.lastPathComponent,
                      let date = (try? item.resourceValues(forKeys: [.contentModificationDateKey]))?.contentModificationDate,
                      date < Date().addingTimeInterval(-7 * 86_400) {
                try? fm.removeItem(at: item)
            }
        }
    }
}
