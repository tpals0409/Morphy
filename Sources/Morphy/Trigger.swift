import AppKit
import ApplicationServices
import os

let log = Logger(subsystem: "app.morphy.Morphy", category: "trigger")

/// Fires `onTrigger` when Option (⌥, either side) alone is held still for `holdDelay` while Finder is frontmost
/// (PRD 5.2–5.4, DECISIONS.md D1/D2/D6). The menu opens while Option is still down; the menu handles the release (D5).
/// Option, not Shift: Shift, ⌘ and Control are held before a click to select files in Finder (D6).
/// Every press that doesn't open the menu logs why (`log stream --predicate 'subsystem == "app.morphy.Morphy"'`).
final class OptionTrigger {
    var onTrigger: () -> Void = {}
    /// Called on Option-down, so slow work (reading Finder's selection) overlaps the hold instead of following it.
    var onArm: () -> Void = {}
    var enabled: Bool {
        get { !UserDefaults.standard.bool(forKey: "triggerDisabled") }
        set { UserDefaults.standard.set(!newValue, forKey: "triggerDisabled") }
    }

    // ponytail: fixed hold time and mouse slop; expose in settings once beta users weigh in.
    private let holdDelay = 0.15  // logged real taps/combos released or interrupted the key within this (D2)
    private let mouseSlop: CGFloat = 24  // 6pt cancelled ~half of real holds: hands resting on a mouse/trackpad drift

    private var monitors: [Any] = []
    private var pending: DispatchWorkItem?
    private var armedMouse = NSPoint.zero

    /// Needs Accessibility trust; without it global monitors silently receive nothing.
    func start() {
        guard monitors.isEmpty else { return }
        log.info("start monitors, trusted=\(AXIsProcessTrusted())")
        if let m = NSEvent.addGlobalMonitorForEvents(matching: .flagsChanged, handler: { [weak self] in self?.flagsChanged($0) }) {
            monitors.append(m)
        }
        // Any key or mouse activity while Option is down means it's a modifier (⌥-drag copies, ⌥-typing), not our trigger.
        let interrupts: NSEvent.EventTypeMask = [.keyDown, .leftMouseDown, .rightMouseDown, .otherMouseDown, .leftMouseDragged, .scrollWheel]
        if let m = NSEvent.addGlobalMonitorForEvents(matching: interrupts, handler: { [weak self] in self?.disarm("event \($0.type.rawValue)") }) {
            monitors.append(m)
        }
        // Moving the pointer while holding Option usually means aiming an ⌥-click or ⌥-drag.
        if let m = NSEvent.addGlobalMonitorForEvents(matching: .mouseMoved, handler: { [weak self] _ in
            guard let self, pending != nil else { return }
            let p = NSEvent.mouseLocation
            if hypot(p.x - armedMouse.x, p.y - armedMouse.y) > mouseSlop { disarm("mouse moved") }
        }) {
            monitors.append(m)
        }
    }

    private func flagsChanged(_ event: NSEvent) {
        let flags = event.modifierFlags.intersection(.deviceIndependentFlagsMask).subtracting([.capsLock, .numericPad, .function])
        // Judge by the key in this event, not remembered state: while the menu is key, key-up goes to our panel
        // and never reaches this global monitor, so a remembered "key is down" made every other press after a menu fail.
        let optionKey = event.keyCode == 58 || event.keyCode == 61  // left, right Option
        if flags == .option && optionKey {
            guard pending == nil else { return }
            guard enabled else { return log.info("not opened: Option trigger disabled") }
            guard Finder.isFrontmost else {
                return log.info("not opened: frontmost is \(NSWorkspace.shared.frontmostApplication?.bundleIdentifier ?? "?", privacy: .public)")
            }
            armedMouse = NSEvent.mouseLocation
            let work = DispatchWorkItem { [weak self] in
                self?.pending = nil
                guard Finder.isFrontmost else { return log.info("not opened: Finder lost focus during hold") }
                guard !Finder.isEditingText else { return log.info("not opened: Finder focus is text (\(Finder.focusedRole, privacy: .public))") }
                log.info("hold fired")
                self?.onTrigger()
            }
            pending = work
            DispatchQueue.main.asyncAfter(deadline: .now() + holdDelay, execute: work)
            onArm()
        } else if flags != .option {
            disarm("flags \(flags.rawValue)")  // released early, or combined with another modifier
        }
    }

    private func disarm(_ reason: String) {
        if pending != nil { log.info("not opened: \(reason, privacy: .public)") }
        pending?.cancel()
        pending = nil
    }
}

enum Finder {
    static var isFrontmost: Bool { NSWorkspace.shared.frontmostApplication?.bundleIdentifier == "com.apple.finder" }

    /// True while renaming a file or typing in Finder's search field.
    static var isEditingText: Bool {
        [kAXTextFieldRole, kAXTextAreaRole, kAXComboBoxRole].contains(focusedRole.components(separatedBy: "/")[0])
    }

    /// "role/subrole" of Finder's focused element, e.g. "AXTextField/AXSearchField".
    static var focusedRole: String {
        guard let pid = NSWorkspace.shared.frontmostApplication?.processIdentifier else { return "" }
        var focused: CFTypeRef?
        guard AXUIElementCopyAttributeValue(AXUIElementCreateApplication(pid), kAXFocusedUIElementAttribute as CFString, &focused) == .success,
              let element = focused else { return "" }
        func attr(_ name: String) -> String {
            var value: CFTypeRef?
            AXUIElementCopyAttributeValue(element as! AXUIElement, name as CFString, &value)
            return value as? String ?? ""
        }
        return "\(attr(kAXRoleAttribute))/\(attr(kAXSubroleAttribute))"
    }

    private static let script = NSAppleScript(source: """
        tell application "Finder"
            set out to ""
            repeat with i in (get selection)
                set out to out & POSIX path of (i as alias) & linefeed
            end repeat
            return out
        end tell
        """)!

    /// Finder window or Desktop selection. Folders come back with a trailing "/".
    static func selection() throws -> [URL] {
        var error: NSDictionary?
        let result = script.executeAndReturnError(&error)
        if let error {
            if error[NSAppleScript.errorNumber] as? Int == -1743 {
                throw MorphyError("Finder 제어 권한이 필요해요.", "시스템 설정 > 개인정보 보호 및 보안 > 자동화에서 Morphy의 Finder 접근을 허용해 주세요.")
            }
            throw MorphyError("파일을 다시 선택해 주세요.")
        }
        return (result.stringValue ?? "").split(separator: "\n").map { URL(fileURLWithPath: String($0)) }
    }
}
