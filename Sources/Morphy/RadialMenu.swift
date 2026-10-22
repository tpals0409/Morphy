import AppKit
import SwiftUI

private final class KeyPanel: NSPanel {
    override var canBecomeKey: Bool { true }
}

/// Fills a whole display behind the donut. A click there cancels the menu and goes no further: Finder never sees it,
/// so it can't select or deselect files (D7). Nearly invisible (alpha 0.001), since fully clear pixels let clicks through.
private final class ClickCatcher: NSView {
    var onClick: () -> Void = {}
    override func mouseDown(with event: NSEvent) { onClick() }
    override func rightMouseDown(with event: NSEvent) { onClick() }
    override func otherMouseDown(with event: NSEvent) { onClick() }
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }

    static func panel(on screen: NSScreen, key: Bool, onClick: @escaping () -> Void) -> NSPanel {
        let panel: NSPanel = key ? KeyPanel(contentRect: screen.frame, styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
                                 : NSPanel(contentRect: screen.frame, styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        panel.level = .popUpMenu
        panel.isOpaque = false
        panel.backgroundColor = NSColor(white: 0, alpha: 0.001)
        panel.hasShadow = false
        panel.setFrame(screen.frame, display: false)
        let catcher = ClickCatcher(frame: NSRect(origin: .zero, size: screen.frame.size))
        catcher.onClick = onClick
        panel.contentView = catcher
        return panel
    }
}

struct RingItem: Hashable {
    enum Kind: Hashable { case format(Format), more, back }
    let kind: Kind
    let slot: Int  // 0 = top, clockwise
}

final class MenuModel: ObservableObject {
    let input: Input
    let main: [RingItem]  // at most 6, each format in its fixed slot (+ "더 보기")
    let more: [RingItem]  // the "더 보기" ring (PRD 6.3), same donut
    var items: [RingItem] { showingMore ? more : main }
    /// Index into `items`; -1 = nothing (nothing to click).
    @Published var selected = -1 {
        didSet {  // a tick under the finger, so the slot can be found without looking
            if selected != oldValue, selected >= 0 { NSHapticFeedbackManager.defaultPerformer.perform(.alignment, performanceTime: .now) }
        }
    }
    @Published var showingMore = false
    @Published var shown = false  // @State needs SwiftUI macros, absent from Command Line Tools
    var pick: (Format) -> Void = { _ in }
    var dismiss: () -> Void = {}

    init(input: Input, outputs: [Format]) {
        self.input = input
        let overflow = outputs.filter { $0.slot == nil }
        let ring = outputs.compactMap { f in f.slot.map { RingItem(kind: .format(f), slot: $0) } }
        main = (ring + (overflow.isEmpty ? [] : [RingItem(kind: .more, slot: Format.moreSlot)])).sorted { $0.slot < $1.slot }
        // "돌아가기" takes the slot "더 보기" was in (the pointer is already there); overflow formats fill its neighbours first.
        // ponytail: room for 5 overflow formats; a third ring if there are ever more.
        let near = [3, 5, 2, 0, 1]  // around moreSlot 4
        more = (zip(overflow, near).map { RingItem(kind: .format($0), slot: $1) } + [RingItem(kind: .back, slot: Format.moreSlot)])
            .sorted { $0.slot < $1.slot }
    }

    func activate(_ item: RingItem) {
        switch item.kind {
        case .format(let f): pick(f)
        case .more: selected = -1; showingMore = true
        case .back: back()
        }
    }

    func back() {
        showingMore = false
        selected = main.firstIndex { $0.kind == .more } ?? -1
    }
}

/// Non-activating panel around the cursor: Finder stays frontmost, but the panel takes keys (PRD 6).
/// Lives while Option is held (D6): releasing Option over a sector runs it; releasing in the hole or outside the donut
/// cancels (D8). The highlight is the sector under the pointer, so it always shows what letting go would do.
/// A click on a sector runs it too. Releasing on "더 보기" swaps in its ring, which is then used with clicks.
final class RadialMenu: NSObject, NSWindowDelegate {
    private static let ring: ClosedRange<CGFloat> = 58...135  // hole radius ... outer edge of the donut, in points
    private static let size: CGFloat = 300
    private let panel: NSPanel          // the cursor's display; holds the donut and takes keys
    private var others: [NSPanel] = []  // other displays: click catchers only
    private let center: NSPoint         // donut center, screen coordinates
    private let model: MenuModel
    private let onClose: () -> Void
    private var monitors: [Any] = []
    private var appObserver: Any?
    private var closed = false

    init(input: Input, outputs: [Format], onPick: @escaping (Format) -> Void, onClose: @escaping () -> Void) {
        model = MenuModel(input: input, outputs: outputs)
        self.onClose = onClose
        let mouse = NSEvent.mouseLocation
        let screen = NSScreen.screens.first { NSMouseInRect(mouse, $0.frame, false) } ?? NSScreen.main!
        let origin = Self.origin(around: mouse, in: screen)
        center = NSPoint(x: origin.x + Self.size / 2, y: origin.y + Self.size / 2)
        var cancel: () -> Void = {}
        panel = ClickCatcher.panel(on: screen, key: true) { cancel() }
        super.init()
        cancel = { [weak self] in self?.close() }
        model.pick = { [weak self] format in
            self?.close()
            onPick(format)
        }
        model.dismiss = { [weak self] in self?.close() }

        let donut = NSHostingView(rootView: RadialView(model: model))
        donut.frame = NSRect(x: origin.x - screen.frame.minX, y: origin.y - screen.frame.minY, width: Self.size, height: Self.size)
        panel.contentView?.addSubview(donut)
        panel.delegate = self
        panel.acceptsMouseMovedEvents = true
        panel.makeKeyAndOrderFront(nil)
        others = NSScreen.screens.filter { $0 != screen }.map { ClickCatcher.panel(on: $0, key: false) { cancel() } }
        others.forEach { $0.orderFront(nil) }

        let track: NSEvent.EventTypeMask = [.mouseMoved, .leftMouseDragged]
        monitors = [
            NSEvent.addLocalMonitorForEvents(matching: .keyDown) { [weak self] in self?.handle($0) ?? $0 },
            NSEvent.addLocalMonitorForEvents(matching: track) { [weak self] in self?.pointerMoved(); return $0 },
            NSEvent.addGlobalMonitorForEvents(matching: track) { [weak self] _ in self?.pointerMoved() },
            // Option-up = run the highlighted sector, or cancel. Arrives locally while the panel is key, globally if not.
            NSEvent.addLocalMonitorForEvents(matching: .flagsChanged) { [weak self] in self?.flagsChanged($0); return $0 },
            NSEvent.addGlobalMonitorForEvents(matching: .flagsChanged) { [weak self] in self?.flagsChanged($0) },
        ].compactMap { $0 }
        // No pointerMoved() here: only a real move selects, so a menu pushed in from a screen edge (pointer already over
        // a sector) can't run on an immediate release.
        // Option may already be up by the time the panel shows (released right at the threshold).
        DispatchQueue.main.async { [weak self] in
            if !NSEvent.modifierFlags.contains(.option) { self?.close() }
        }
        appObserver = NSWorkspace.shared.notificationCenter.addObserver(
            forName: NSWorkspace.didActivateApplicationNotification, object: nil, queue: .main
        ) { [weak self] note in
            let app = note.userInfo?[NSWorkspace.applicationUserInfoKey] as? NSRunningApplication
            if app?.bundleIdentifier != "com.apple.finder" { self?.close() }
        }
    }

    /// Keeps the whole ring on the screen under the cursor.
    private static func origin(around p: NSPoint, in screen: NSScreen) -> NSPoint {
        let f = screen.visibleFrame
        return NSPoint(x: min(max(p.x - size / 2, f.minX), f.maxX - size),
                       y: min(max(p.y - size / 2, f.minY), f.maxY - size))
    }

    private func flagsChanged(_ event: NSEvent) {
        if !model.showingMore, !event.modifierFlags.contains(.option) { released() }
    }

    private func released() {
        guard !closed else { return }
        guard model.main.indices.contains(model.selected) else { return close() }
        model.activate(model.main[model.selected])  // a format runs and closes; "더 보기" swaps in its ring and stays
    }

    /// Highlights the sector under the pointer; the hole, an empty slot and anything outside the donut highlight nothing.
    private func pointerMoved() {
        let p = NSEvent.mouseLocation
        let dx = p.x - center.x, dy = p.y - center.y
        guard Self.ring.contains(hypot(dx, dy)) else { return model.selected = -1 }
        let degrees = (atan2(dx, dy) * 180 / .pi + 360).truncatingRemainder(dividingBy: 360)  // 0 = up, clockwise
        let slot = Int((degrees / 60).rounded()) % 6
        model.selected = model.items.firstIndex { $0.slot == slot } ?? -1
    }

    private func handle(_ event: NSEvent) -> NSEvent? {
        let n = model.items.count
        let sel = model.selected
        switch event.keyCode {
        case 53: model.showingMore ? model.back() : close()                // Esc: "더 보기" ring → main ring → closed
        case 36, 76:                                                        // Return, Enter
            if model.items.indices.contains(sel) { model.activate(model.items[sel]) }
        case 124, 125: model.selected = (sel + 1) % n                       // → ↓ clockwise
        case 123, 126: model.selected = sel < 0 ? n - 1 : (sel + n - 1) % n  // ← ↑ counter-clockwise
        default: return event
        }
        return nil
    }

    // Clicking outside (Finder, Desktop) or Cmd-Tab takes key away from the panel.
    func windowDidResignKey(_ notification: Notification) { close() }

    func close() {
        guard !closed else { return }
        closed = true
        monitors.forEach(NSEvent.removeMonitor)
        if let appObserver { NSWorkspace.shared.notificationCenter.removeObserver(appObserver) }
        panel.orderOut(nil)
        others.forEach { $0.orderOut(nil) }
        onClose()
    }
}

extension RadialMenu {
    /// `Morphy --render-menu <dir>`: PNGs of the ring (light/dark) for design review without pressing Option.
    static func renderSnapshots(to dir: URL) {
        for (name, px) in [("menubar-18", 18), ("menubar-36", 36), ("menubar-big", 180)] {  // menu bar icon, 1x/2x/zoomed
            let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: px, pixelsHigh: px, bitsPerSample: 8, samplesPerPixel: 4,
                                       hasAlpha: true, isPlanar: false, colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0)!
            NSGraphicsContext.saveGraphicsState()
            NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)
            NSColor.white.setFill()
            NSRect(x: 0, y: 0, width: px, height: px).fill()
            AppDelegate.menuBarIcon.draw(in: NSRect(x: 0, y: 0, width: px, height: px))
            NSGraphicsContext.restoreGraphicsState()
            try? rep.representation(using: .png, properties: [:])?.write(to: dir.appendingPathComponent("\(name).png"))
        }
        let image = Input(url: URL(fileURLWithPath: "/tmp/보고서 최종본.png"), kind: .image, outputs: Routes.imageOutputs)
        let sheet = Input(url: URL(fileURLWithPath: "/tmp/매출.xls"), kind: .office, outputs: Routes.office["xls"]!)
        let cases = [("light", NSAppearance.Name.aqua, image, false), ("dark", .darkAqua, image, false),
                     ("sheet", .aqua, sheet, false), ("more-light", .aqua, image, true), ("more-dark", .darkAqua, image, true)]
        for (name, appearance, input, more) in cases {
            let outputs = Format.allCases.filter(input.outputs.contains).sorted { $0.order < $1.order }
            let model = MenuModel(input: input, outputs: outputs)
            model.shown = true
            model.showingMore = more
            model.selected = more ? 0 : 1
            NSAppearance(named: appearance)!.performAsCurrentDrawingAppearance {
                let view = NSHostingView(rootView: RadialView(model: model))
                view.appearance = NSAppearance(named: appearance)
                view.frame = NSRect(x: 0, y: 0, width: 300, height: 300)
                view.layoutSubtreeIfNeeded()
                let rep = view.bitmapImageRepForCachingDisplay(in: view.bounds)!
                view.cacheDisplay(in: view.bounds, to: rep)
                try? rep.representation(using: .png, properties: [:])?.write(to: dir.appendingPathComponent("menu-\(name).png"))
            }
        }
    }
}

/// Brand palette (main #F47B55, sub #FFF7ED), with a warm dark variant; follows the system appearance.
/// White on the main colour is only 2.69:1, so labels on it are deep brown (5.86:1, PRD 6.6). Pairs used, light / dark:
/// ink on tint 8.44 / 8.90, main against the plate 2.53 / 5.84.
private enum Brand {
    static let main = Color(hex: 0xF47B55)
    static let onMain = Color(hex: 0x3A1A0E)
    static let sub = Color(light: 0xFFF7ED, dark: 0x2B211D)    // the donut plate
    static let tint = Color(light: 0xFDE3D5, dark: 0x573529)   // a format's sector: main at 16% / 22% over the plate
    static let faint = Color(light: 0xFEF1E5, dark: 0x392721)  // an unused slot: main at 5% / 7%; opaque, as fill and
                                                               // round stroke overlap
    static let ink = Color(light: 0x6B2E19, dark: 0xFFE4D6)    // labels on tint
    static let edge = Color(nsColor: NSColor(name: nil) {
        $0.isDark ? NSColor(white: 1, alpha: 0.08) : NSColor(Brand.main).withAlphaComponent(0.25)
    })
    static let shadow = Color(nsColor: NSColor(name: nil) {
        $0.isDark ? NSColor(white: 0, alpha: 0.5) : NSColor(Brand.main).withAlphaComponent(0.25)
    })
}

private extension NSAppearance {
    var isDark: Bool { bestMatch(from: [.aqua, .darkAqua]) == .darkAqua }
}

private extension Color {
    init(hex: UInt32) {
        self.init(red: Double(hex >> 16 & 0xFF) / 255, green: Double(hex >> 8 & 0xFF) / 255, blue: Double(hex & 0xFF) / 255)
    }

    /// Resolved per appearance, so the menu switches with System Settings > Appearance.
    init(light: UInt32, dark: UInt32) {
        self.init(nsColor: NSColor(name: nil) { NSColor(Color(hex: $0.isDark ? dark : light)) })
    }
}

private struct RadialView: View {
    @ObservedObject var model: MenuModel
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    var body: some View {
        ZStack {
            // Hole, gaps and corners of the donut's square: a click there cancels too, instead of falling through.
            Color.clear.contentShape(Rectangle()).onTapGesture { model.dismiss() }
            // A donut, not a disc: the hole stays see-through. "더 보기" swaps in a second ring of the same shape.
            Donut()
                .fill(Brand.sub, style: FillStyle(eoFill: true))
                .overlay(Donut().stroke(Brand.edge))
                .shadow(color: Brand.shadow, radius: 10, y: 3)
            // Unused slots stay visible but faint, so the six positions are learnable.
            ForEach(Array(Set(0..<6).subtracting(model.items.map(\.slot))), id: \.self) { slot in
                Sector(slot: slot).rounded(Brand.faint)
            }
            ForEach(Array(model.items.enumerated()), id: \.element) { i, item in
                let selected = i == model.selected
                let mid = (Double(item.slot) * 60 - 90) * .pi / 180
                let r = (Sector.inner + Sector.outer) / 2
                let isFormat = if case .format = item.kind { true } else { false }
                Button { model.activate(item) } label: {
                    ZStack {
                        Sector(slot: item.slot).rounded(selected ? Brand.main : Brand.tint)
                        VStack(spacing: 0) {
                            Text(label(item))
                                .font(.system(size: isFormat ? 18 : 13, weight: .bold, design: .rounded))
                            // Not runnable yet: picking it asks to download the engine first (D4, PRD 6.2).
                            if isFormat && model.input.needsEngine {
                                Text("기능 추가 필요").font(.system(size: 8, weight: .semibold))
                            }
                        }
                        .foregroundStyle(selected ? Brand.onMain : Brand.ink.opacity(isFormat ? 1 : 0.75))
                        .offset(x: cos(mid) * r, y: sin(mid) * r)
                    }
                    .contentShape(Sector(slot: item.slot))
                }
                .buttonStyle(.plain)
                .accessibilityLabel(accessibilityLabel(item))
            }
            center
        }
        .frame(width: 300, height: 300)
        .scaleEffect(model.shown || reduceMotion ? 1 : 0.85)
        .opacity(model.shown ? 1 : 0)
        .onAppear { withAnimation(reduceMotion ? nil : .easeOut(duration: 0.06)) { model.shown = true } }
    }

    /// Backing ring, slightly larger than the sectors on both edges.
    private struct Donut: Shape {
        func path(in rect: CGRect) -> Path {
            let c = CGPoint(x: rect.midX, y: rect.midY)
            var p = Path()
            for r in [Sector.outer + 5, Sector.inner - 5] {
                p.addEllipse(in: CGRect(x: c.x - r, y: c.y - r, width: r * 2, height: r * 2))
            }
            return p
        }
    }

    /// Ring segment for one of the six 60° slots (0 = top, clockwise). Neighbours are split by an even 6pt gap
    /// (straight, parallel edges); `inset` shrinks the outline so a round stroke can give it soft corners.
    private struct Sector: Shape {
        static let inner: CGFloat = 58, outer: CGFloat = 130
        static let corner: CGFloat = 7
        let slot: Int
        var inset: CGFloat = 0

        func path(in rect: CGRect) -> Path {
            let c = CGPoint(x: rect.midX, y: rect.midY)
            let half = 3 + inset, rIn = Self.inner + inset, rOut = Self.outer - inset
            let mid = (Double(slot) * 60 - 90) * .pi / 180  // y points down, so increasing angle is clockwise
            let edge = Double.pi / 6
            let dOut = asin(half / rOut), dIn = asin(half / rIn)  // angle that keeps the gap's width even
            var p = Path()
            p.addArc(center: c, radius: rOut, startAngle: .radians(mid - edge + dOut), endAngle: .radians(mid + edge - dOut), clockwise: false)
            p.addArc(center: c, radius: rIn, startAngle: .radians(mid + edge - dIn), endAngle: .radians(mid - edge + dIn), clockwise: true)
            p.closeSubpath()
            return p
        }

        /// Filled with rounded corners, keeping the original outline.
        func rounded(_ color: Color) -> some View {
            ZStack {
                Sector(slot: slot, inset: Self.corner).fill(color)
                Sector(slot: slot, inset: Self.corner).stroke(color, style: StrokeStyle(lineWidth: Self.corner * 2, lineJoin: .round))
            }
        }
    }

    private func label(_ item: RingItem) -> String {
        switch item.kind {
        case .format(let f): f.rawValue
        case .more: "더 보기"
        case .back: "돌아가기"
        }
    }

    private func accessibilityLabel(_ item: RingItem) -> String {
        switch item.kind {
        case .format(let f): model.input.needsEngine ? "\(f.rawValue)로 변환, 문서 변환 기능 추가 필요" : "\(f.rawValue)로 변환"
        case .more: "더 보기, 형식 \(model.more.count - 1)개"
        case .back: "돌아가기"
        }
    }

    /// File info floating in the hole (PRD 6.1), no card: the donut's middle stays see-through.
    @ViewBuilder private var center: some View {
        VStack(spacing: 1) {
            if model.showingMore {
                Text("더 보기").font(.system(size: 10, weight: .bold))
            } else {
                Image(nsImage: NSWorkspace.shared.icon(forFile: model.input.url.path))
                    .resizable().frame(width: 22, height: 22)
                Text(model.input.url.deletingPathExtension().lastPathComponent).font(.system(size: 9)).lineLimit(1).truncationMode(.middle)
                Text(model.input.typeLabel).font(.system(size: 9, weight: .bold)).foregroundStyle(.secondary)
            }
        }
        .frame(maxWidth: 76)
        .accessibilityElement(children: .combine)
    }
}
