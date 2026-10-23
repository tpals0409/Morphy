import AppKit
import ApplicationServices
import ServiceManagement
import UserNotifications

struct Job {
    enum State { case waiting, running, done([URL]), failed(String), cancelled }  // several URLs: one CSV per sheet
    let id = UUID()
    let source: URL
    let format: Format
    let folder: URL?  // nil when the user declined to pick another folder for an unwritable one
    var state = State.waiting
}

/// Sequential batch queue (PRD 9.2). One failure never stops the rest.
final class JobQueue {
    private(set) var jobs: [Job] = []
    private(set) var isRunning = false
    private var token = CancelToken()
    var onChange: () -> Void = {}
    var onBatchFinished: ([Job]) -> Void = { _ in }

    func add(_ new: [Job]) {
        if isRunning { jobs += new } else { jobs = new }
        pump()
    }

    func cancelAll() {
        for i in jobs.indices where jobs[i].isWaiting { jobs[i].state = .cancelled }
        token.cancel()
        onChange()
    }

    /// Only failed items run again; finished results are never regenerated (F10).
    func retryFailed() {
        for i in jobs.indices { if case .failed = jobs[i].state { jobs[i].state = .waiting } }
        pump()
    }

    func clear() { if !isRunning { jobs = []; onChange() } }

    private func pump() {
        guard !isRunning else { return }
        guard let i = jobs.firstIndex(where: \.isWaiting) else { return }
        isRunning = true
        jobs[i].state = .running
        token = CancelToken()
        let job = jobs[i], token = token
        onChange()
        DispatchQueue.global(qos: .userInitiated).async {
            let result = Pipeline.run(job.source, to: job.format, saveIn: job.folder, token)
            DispatchQueue.main.async { [self] in
                if let j = jobs.firstIndex(where: { $0.id == job.id }) { jobs[j].state = result }
                isRunning = false
                if !jobs.contains(where: \.isWaiting) { onBatchFinished(jobs) }
                onChange()
                pump()
            }
        }
    }
}

extension Job {
    var isWaiting: Bool { if case .waiting = state { true } else { false } }
    var failed: Bool { if case .failed = state { true } else { false } }
}

final class AppDelegate: NSObject, NSApplicationDelegate, NSMenuDelegate {
    private let status = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
    private let trigger = OptionTrigger()
    private let queue = JobQueue()
    private let installer = EngineInstaller.shared
    private var radial: RadialMenu?
    private var snapshot: Result<[URL], Error>?  // Finder selection taken at Option-down (PRD 5.5)
    private var update: AppUpdate?   // a newer release found on the website (DECISIONS.md D9)
    private var updating = false

    func applicationDidFinishLaunching(_ notification: Notification) {
        status.button?.image = Self.menuBarIcon
        status.button?.imagePosition = .imageLeading
        let menu = NSMenu()
        menu.delegate = self
        status.menu = menu

        queue.onChange = { [weak self] in self?.refreshTitle() }
        queue.onBatchFinished = { Self.notify($0) }
        trigger.onTrigger = { [weak self] in self?.invoke() }
        installer.onChange = { [weak self] in self?.refreshTitle() }
        DispatchQueue.global(qos: .utility).async { EngineInstaller.cleanUpLeftovers() }
        trigger.onArm = { [weak self] in
            guard let self, radial == nil, !Finder.isEditingText else { return }
            snapshot = Result { try Finder.selection() }
        }
        UNUserNotificationCenter.current().requestAuthorization(options: [.alert]) { _, _ in }
        checkForUpdate()
        Timer.scheduledTimer(withTimeInterval: 6 * 3600, repeats: true) { [weak self] _ in self?.checkForUpdate() }

        let prompt = [kAXTrustedCheckOptionPrompt.takeUnretainedValue() as String: true] as CFDictionary
        let trusted = AXIsProcessTrustedWithOptions(prompt)
        log.info("launch trusted=\(trusted)")
        if trusted {
            trigger.start()
        } else {
            Timer.scheduledTimer(withTimeInterval: 2, repeats: true) { [weak self] t in
                guard AXIsProcessTrusted() else { return }
                t.invalidate()
                self?.trigger.start()
            }
        }
    }

    // MARK: Invocation

    private func invoke() {
        guard radial == nil else { return log.info("invoke: menu already open") }
        let urls: [URL]
        defer { snapshot = nil }
        do { urls = try (snapshot ?? Result { try Finder.selection() }).get() } catch {
            log.error("selection failed: \((error as? MorphyError)?.message ?? "\(error)", privacy: .public)")
            let e = error as? MorphyError
            return alert(e?.message ?? "파일을 다시 선택해 주세요.", e?.detail ?? "")
        }
        log.info("invoke: selection count=\(urls.count)")
        // One file at a time (DECISIONS.md D3): several selected → no menu, no alert.
        guard urls.count == 1, let url = urls.first else { return log.info("not opened: \(urls.count) items selected") }
        let input: Input
        do { input = try Input.check(url) } catch {
            let e = error as? MorphyError ?? MorphyError("파일을 확인할 수 없어요.")
            // Folders and formats without a route: no menu and no alert, as if Option did nothing.
            if e.isUnsupported { return log.info("not opened: unsupported \(url.pathExtension, privacy: .public)") }
            // Supported formats with a real problem (encrypted, damaged, too big, not downloaded) still explain themselves.
            return alert(e.message, e.detail)
        }
        let outputs = Format.allCases.filter(input.outputs.contains).sorted { $0.order < $1.order }
        radial = RadialMenu(input: input, outputs: outputs,
                            onPick: { [weak self] in self?.start(input, $0) },
                            onClose: { [weak self] in self?.radial = nil })
    }

    private func start(_ input: Input, _ format: Format) {
        let parent = input.url.deletingLastPathComponent()
        // nil folder = user declined another folder; the job then fails with a clear reason.
        let folder = FileManager.default.isWritableFile(atPath: parent.path) ? parent : askFolder(for: parent)
        let job = Job(source: input.url, format: format, folder: folder)
        input.needsEngine ? installEngine(then: job) : queue.add([job])
    }

    /// Asks before downloading (PRD 13.3, docs/engine-download.md), then runs the conversion that needed it.
    private func installEngine(then job: Job? = nil) {
        if installer.state == .idle {
            NSApp.activate()
            let a = NSAlert()
            // Worded as a Morphy feature, not a second app; the engine is still named for transparency and its licence.
            a.messageText = "문서 변환 기능을 추가할까요?"
            a.informativeText = """
                Word·Excel·PowerPoint 문서를 변환하려면 이 기능을 한 번 내려받아야 해요. 약 300MB를 받고, 추가하면 805MB를 차지해요.
                파일 내용은 외부로 보내지 않아요.

                오픈소스 LibreOffice(The Document Foundation, MPL-2.0)를 공식 서버에서 받아 사용해요.
                """
            a.addButton(withTitle: "추가하기")
            a.addButton(withTitle: "나중에")
            guard a.runModal() == .alertFirstButtonReturn else { return }
        }
        // Joining a download already running needs no second question.
        installer.install { [weak self] error in
            if let error {  // a cancel the user just clicked needs no alert
                if error.message != EngineInstaller.cancelled.message { self?.alert(error.message, error.detail) }
                return
            }
            if let job { self?.queue.add([job]) }
            Self.notify(title: "문서 변환 기능을 추가했어요", body: job.map { "\($0.source.lastPathComponent)을(를) \($0.format.rawValue)로 변환하는 중이에요." } ?? "")
        }
    }

    /// Never silently redirects output: the user picks the folder or the job fails (PRD 10.5).
    private func askFolder(for parent: URL) -> URL? {
        NSApp.activate()
        let panel = NSOpenPanel()
        panel.canChooseFiles = false
        panel.canChooseDirectories = true
        panel.canCreateDirectories = true
        panel.message = "‘\(parent.lastPathComponent)’ 폴더에 저장할 권한이 없어요. 결과를 저장할 폴더를 선택해 주세요."
        panel.prompt = "여기에 저장"
        return panel.runModal() == .OK ? panel.url : nil
    }

    private func alert(_ title: String, _ detail: String = "") {
        NSApp.activate()
        let a = NSAlert()
        a.messageText = title
        a.informativeText = detail
        a.runModal()
    }

    // MARK: Updates

    /// Quiet unless `manual`: a found update only shows up as the menu's update item.
    private func checkForUpdate(manual: Bool = false) {
        Task { @MainActor in
            do {
                update = try await Updater.check()
                guard manual else { return }
                if let update { installUpdate(update) } else { alert("최신 버전이에요", "Morphy \(Updater.current)을(를) 쓰고 있어요.") }
            } catch {
                log.error("update check: \(error.localizedDescription, privacy: .public)")
                if manual { alert((error as? MorphyError)?.message ?? "업데이트를 확인할 수 없어요. 인터넷 연결을 확인해 주세요.") }
            }
        }
    }

    private func installUpdate(_ update: AppUpdate) {
        guard !queue.isRunning, installer.state == .idle else { return alert("변환이 끝난 뒤에 업데이트할 수 있어요.") }
        NSApp.activate()
        let a = NSAlert()
        a.messageText = "Morphy \(update.version)(으)로 업데이트할까요?"
        a.informativeText = "지금 버전은 \(Updater.current)이에요. 내려받아 확인한 뒤 Morphy를 다시 시작해요."
        a.addButton(withTitle: "업데이트")
        a.addButton(withTitle: "나중에")
        guard a.runModal() == .alertFirstButtonReturn else { return }
        updating = true
        refreshTitle()
        Task { @MainActor in
            do {
                try await Updater.install(update)
                Updater.relaunch()
            } catch {
                updating = false
                refreshTitle()
                let e = error as? MorphyError
                alert(e?.message ?? "업데이트를 내려받지 못했어요. 인터넷 연결을 확인해 주세요.", e?.detail ?? "")
            }
        }
    }

    // MARK: Status menu

    private func refreshTitle() {
        let done = queue.jobs.filter { if case .waiting = $0.state { false } else if case .running = $0.state { false } else { true } }.count
        let failed = queue.jobs.filter(\.failed).count
        if updating { status.button?.title = " ⬆︎업데이트 중"; return }
        switch installer.state {
        case .downloading(let received): status.button?.title = " ⬇︎\(received * 100 / Int64(LibreOffice.bytes))%"
        case .installing: status.button?.title = " ⬇︎설치 중"
        case .idle: status.button?.title = queue.isRunning ? " \(done + 1)/\(queue.jobs.count)" : failed > 0 ? " ✕\(failed)" : ""
        }
    }

    func menuNeedsUpdate(_ menu: NSMenu) {
        menu.removeAllItems()
        if !AXIsProcessTrusted() {
            menu.addItem(ActionItem("손쉬운 사용 권한이 필요해요 — 설정 열기") {
                NSWorkspace.shared.open(URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_Accessibility")!)
            })
            menu.addItem(.separator())
        }
        for job in queue.jobs { menu.addItem(Self.item(for: job)) }
        if queue.isRunning { menu.addItem(ActionItem("모두 취소") { [queue] in queue.cancelAll() }) }
        if queue.jobs.contains(where: \.failed) { menu.addItem(ActionItem("실패한 항목 다시 시도") { [queue] in queue.retryFailed() }) }
        if !queue.jobs.isEmpty && !queue.isRunning { menu.addItem(ActionItem("목록 지우기") { [queue] in queue.clear() }) }
        if !queue.jobs.isEmpty { menu.addItem(.separator()) }

        switch installer.state {
        case .downloading(let received):
            let mb = { (b: Int64) in b / 1_000_000 }
            menu.addItem(NSMenuItem(title: "문서 변환 기능 내려받는 중 · \(mb(received))/\(mb(Int64(LibreOffice.bytes)))MB", action: nil, keyEquivalent: ""))
            menu.addItem(ActionItem("내려받기 취소") { [installer] in installer.cancel() })
        case .installing:
            menu.addItem(NSMenuItem(title: "문서 변환 기능을 확인하고 추가하는 중…", action: nil, keyEquivalent: ""))
        case .idle:
            if let app = LibreOffice.app {
                let own = app == LibreOffice.managed
                let v = Bundle(url: app)?.infoDictionary?["CFBundleShortVersionString"] as? String ?? ""
                menu.addItem(NSMenuItem(title: own ? "문서 변환 기능 켜짐" : "문서 변환: 설치된 LibreOffice \(v) 사용 중", action: nil, keyEquivalent: ""))
            } else {
                menu.addItem(ActionItem("문서 변환 기능 추가…") { [weak self] in self?.installEngine() })
            }
            // Offered whenever Morphy's own copy exists, even unused (e.g. LibreOffice was installed in /Applications later).
            if FileManager.default.fileExists(atPath: LibreOffice.managed.path) {
                let unused = LibreOffice.app != LibreOffice.managed
                menu.addItem(ActionItem("문서 변환 기능 삭제…\(unused ? " (사용 안 함)" : "")") { [weak self] in self?.removeEngine() })
            }
        }
        menu.addItem(.separator())

        let hold = ActionItem("Option(⌥)을 꾹 눌러 메뉴 열기") { [trigger] in trigger.enabled.toggle() }
        hold.state = trigger.enabled ? .on : .off
        menu.addItem(hold)
        let login = ActionItem("로그인 시 실행") {
            let s = SMAppService.mainApp
            try? s.status == .enabled ? s.unregister() : s.register()
        }
        login.state = SMAppService.mainApp.status == .enabled ? .on : .off
        menu.addItem(login)
        menu.addItem(.separator())
        if updating {
            menu.addItem(NSMenuItem(title: "업데이트 설치 중…", action: nil, keyEquivalent: ""))
        } else if let update {
            menu.addItem(ActionItem("업데이트 설치 (\(Updater.current) → \(update.version))…") { [weak self] in self?.installUpdate(update) })
        } else {
            menu.addItem(ActionItem("업데이트 확인 · 현재 \(Updater.current)") { [weak self] in self?.checkForUpdate(manual: true) })
        }
        menu.addItem(ActionItem("Morphy 종료") { NSApp.terminate(nil) })
    }

    private static func item(for job: Job) -> NSMenuItem {
        let name = "\(job.source.lastPathComponent) → \(job.format.rawValue)"
        switch job.state {
        case .waiting: return NSMenuItem(title: "대기 · \(name)", action: nil, keyEquivalent: "")
        case .running: return NSMenuItem(title: "변환 중 · \(name)", action: nil, keyEquivalent: "")
        case .cancelled: return NSMenuItem(title: "취소됨 · \(name)", action: nil, keyEquivalent: "")
        case .failed(let why): return NSMenuItem(title: "✕ \(name) — \(why)", action: nil, keyEquivalent: "")
        case .done(let urls):
            let more = urls.count > 1 ? " 외 \(urls.count - 1)개" : ""
            let item = NSMenuItem(title: "✓ \(urls[0].lastPathComponent)\(more)", action: nil, keyEquivalent: "")
            item.submenu = NSMenu()
            item.submenu?.addItem(ActionItem("열기") { urls.forEach { NSWorkspace.shared.open($0) } })
            item.submenu?.addItem(ActionItem("Finder에서 보기") { NSWorkspace.shared.activateFileViewerSelecting(urls) })
            return item
        }
    }

    /// Moves Morphy's copy to the Trash (recoverable), after asking.
    private func removeEngine() {
        NSApp.activate()
        let a = NSAlert()
        a.messageText = "문서 변환 기능을 삭제할까요?"
        a.informativeText = "805MB를 휴지통으로 옮겨요. Word·Excel·PowerPoint 문서를 다시 변환하려면 기능을 다시 추가해야 해요."
        a.addButton(withTitle: "휴지통으로 이동")
        a.addButton(withTitle: "취소")
        guard a.runModal() == .alertFirstButtonReturn else { return }
        do { try FileManager.default.trashItem(at: LibreOffice.managed.deletingLastPathComponent(), resultingItemURL: nil) } catch {
            alert("문서 변환 기능을 삭제하지 못했어요.", "Finder에서 ~/Library/Application Support/Morphy를 확인해 주세요.")
        }
    }

    /// The app icon's donut in one colour: a ring with its upper-right slice lifted out. A template image, so macOS
    /// tints it for light/dark menu bars and highlights. Drawn as vectors so it stays sharp at any scale.
    static let menuBarIcon: NSImage = {
        let image = NSImage(size: NSSize(width: 18, height: 18), flipped: false) { rect in
            let c = NSPoint(x: rect.midX, y: rect.midY - 0.5)
            let outer: CGFloat = 7.2, inner: CGFloat = 3.6
            // Clockwise-from-top degrees → AppKit's counter-clockwise-from-x angles.
            func band(from: CGFloat, to: CGFloat, center: NSPoint) -> NSBezierPath {
                let p = NSBezierPath()
                p.appendArc(withCenter: center, radius: outer, startAngle: 90 - from, endAngle: 90 - to, clockwise: true)
                p.appendArc(withCenter: center, radius: inner, startAngle: 90 - to, endAngle: 90 - from, clockwise: false)
                p.close()
                return p
            }
            NSColor.black.setFill()
            band(from: 76, to: 374, center: c).fill()  // the ring, open between 14° and 76°
            let lift: CGFloat = 1.4, mid = 45 * CGFloat.pi / 180
            band(from: 22, to: 68, center: NSPoint(x: c.x + sin(mid) * lift, y: c.y + cos(mid) * lift)).fill()  // the slice
            return true
        }
        image.isTemplate = true
        image.accessibilityDescription = "Morphy"
        return image
    }()

    private static func notify(title: String, body: String) {
        let content = UNMutableNotificationContent()
        content.title = title
        content.body = body
        UNUserNotificationCenter.current().add(UNNotificationRequest(identifier: UUID().uuidString, content: content, trigger: nil))
    }

    private static func notify(_ jobs: [Job]) {
        let ok = jobs.filter { if case .done = $0.state { true } else { false } }.count
        let failed = jobs.filter(\.failed).count
        let cancelled = jobs.count - ok - failed
        notify(title: failed > 0 ? "일부 파일을 변환하지 못했어요" : "변환 완료",
               body: ["\(ok)개 완료", failed > 0 ? "\(failed)개 실패" : nil, cancelled > 0 ? "\(cancelled)개 취소" : nil]
                   .compactMap { $0 }.joined(separator: " · "))
    }
}

final class ActionItem: NSMenuItem {
    private let handler: () -> Void
    init(_ title: String, _ handler: @escaping () -> Void) {
        self.handler = handler
        super.init(title: title, action: #selector(fire), keyEquivalent: "")
        target = self
    }
    required init(coder: NSCoder) { fatalError() }
    @objc private func fire() { handler() }
}
