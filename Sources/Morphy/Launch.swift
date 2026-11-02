import AppKit

/// One Morphy per user, run from a real install location (DECISIONS.md D12).
/// Two copies running — one opened inside the DMG, one from /Applications — showed two menu bar icons and could
/// open the ring twice. A copy run from the DMG or from App Translocation also can't update itself.
enum Launch {
    /// Another Morphy (any copy, same bundle ID) already running for this user.
    static func otherInstance(_ apps: [NSRunningApplication] = NSRunningApplication.runningApplications(withBundleIdentifier: "app.morphy.Morphy"),
                              me: pid_t = ProcessInfo.processInfo.processIdentifier) -> NSRunningApplication? {
        apps.first { $0.processIdentifier != me && !$0.isTerminated }
    }

    /// Inside a mounted disk image, or copied to a random read-only spot by macOS because the app was opened
    /// straight from Downloads without being moved (App Translocation, applies to apps without notarization).
    static func isMisplaced(_ path: String) -> Bool {
        path.hasPrefix("/Volumes/") || path.contains("/AppTranslocation/")
    }

    /// Runs before anything else starts. Returns false when this copy should quit.
    static func shouldContinue() -> Bool {
        if let other = otherInstance() {
            NSApp.activate()
            let a = NSAlert()
            a.messageText = "Morphy가 이미 실행 중이에요"
            a.informativeText = "메뉴 막대의 Morphy 아이콘을 눌러 보세요."
                + (other.bundleURL.map { $0.path != Bundle.main.bundlePath ? "\n\n실행 중인 Morphy: \($0.path)" : "" } ?? "")
            a.runModal()
            return false
        }
        if isMisplaced(Bundle.main.bundlePath) {
            NSApp.activate()
            let a = NSAlert()
            a.messageText = "Morphy를 응용 프로그램 폴더로 옮겨 주세요"
            a.informativeText = "디스크 이미지(DMG) 안에서 바로 열었거나, 다운로드 폴더에서 옮기지 않고 열었어요. 이대로 쓰면 업데이트를 설치할 수 없고, 다음에 열 때 Morphy가 하나 더 생길 수 있어요.\n\nDMG 창에서 Morphy를 응용 프로그램 폴더로 끌어다 놓은 뒤, 응용 프로그램 폴더에서 열어 주세요."
            a.addButton(withTitle: "종료")
            a.addButton(withTitle: "이대로 실행")
            return a.runModal() == .alertSecondButtonReturn
        }
        return true
    }
}
