import AppKit
import WebKit

/// HTML → paginated A4 PDF or one full-page JPG, drawn by WebKit (Safari's engine). LibreOffice reads HTML as a
/// word-processor document and loses modern layout (flex, grid, web fonts), so it is not used here (DECISIONS.md D11).
/// Offline by design: a content rule blocks every URL that isn't a local file, and the page may read only its own folder.
enum WebPage {
    static let width: CGFloat = 1024        // CSS px: a common desktop layout width
    static let maxJPGHeight: CGFloat = 16_000  // CSS px (32,000 px at 2x); longer pages should go to PDF
    static let timeout: TimeInterval = 20

    static func convert(_ source: URL, to format: Format, in dir: URL, _ token: CancelToken) throws -> URL {
        let out = dir.appendingPathComponent("out.\(format.ext)")
        var result: Result<Void, MorphyError>?
        var renderer: Renderer?
        let start = { renderer = Renderer(source: source, format: format, out: out) { result = $0 }; renderer?.start() }
        let deadline = Date().addingTimeInterval(timeout)
        if Thread.isMainThread {
            // Command line and self-test: no app run loop yet, so drive the main run loop here.
            _ = NSApplication.shared
            start()
            while result == nil, Date() < deadline, !token.cancelled { RunLoop.main.run(until: Date().addingTimeInterval(0.05)) }
        } else {
            DispatchQueue.main.async(execute: start)
            while DispatchQueue.main.sync(execute: { result == nil }), Date() < deadline, !token.cancelled { Thread.sleep(forTimeInterval: 0.05) }
        }
        let finished = DispatchQueue.onMain { () -> Result<Void, MorphyError>? in
            let r = result
            if r == nil { renderer?.stop() }
            renderer = nil
            return r
        }
        switch finished {
        case .success: return out
        case .failure(let error): throw error
        case nil: throw MorphyError(token.cancelled ? "취소했어요." : "페이지를 그리는 데 시간이 너무 오래 걸려요.")
        }
    }

    /// One rule list for every page: block everything, then let local files (and inline data) through.
    /// WebKit's rule regexes have no alternation, so each allowed scheme is its own rule.
    static let offlineRules = "[" + (["{\"trigger\":{\"url-filter\":\".*\"},\"action\":{\"type\":\"block\"}}"]
        + ["file", "data", "blob", "about"].map { "{\"trigger\":{\"url-filter\":\"^\($0):\"},\"action\":{\"type\":\"ignore-previous-rules\"}}" })
        .joined(separator: ",") + "]"
}

private extension DispatchQueue {
    static func onMain<T>(_ work: () -> T) -> T { Thread.isMainThread ? work() : DispatchQueue.main.sync(execute: work) }
}

/// Lives on the main thread for one conversion. `done` is called exactly once.
private final class Renderer: NSObject, WKNavigationDelegate {
    private let source: URL, format: Format, out: URL
    private var done: ((Result<Void, MorphyError>) -> Void)?
    private var webView: WKWebView?
    private var window: NSWindow?

    init(source: URL, format: Format, out: URL, done: @escaping (Result<Void, MorphyError>) -> Void) {
        self.source = source
        self.format = format
        self.out = out
        self.done = done
    }

    func start() {
        WKContentRuleListStore.default().compileContentRuleList(forIdentifier: "morphy-offline", encodedContentRuleList: WebPage.offlineRules) { [weak self] rules, _ in
            guard let self, done != nil else { return }
            guard let rules else { return finish(.failure(MorphyError("HTML을 안전하게 열 준비를 하지 못했어요."))) }
            let config = WKWebViewConfiguration()
            config.websiteDataStore = .nonPersistent()  // no cookies or storage left behind
            config.userContentController.add(rules)
            config.preferences.javaScriptCanOpenWindowsAutomatically = false
            config.preferences.shouldPrintBackgrounds = true  // the PDF should look like the page, colours included
            let frame = NSRect(x: 0, y: 0, width: WebPage.width, height: 800)
            let webView = WKWebView(frame: frame, configuration: config)
            webView.navigationDelegate = self
            // Printing needs the view in a window. It is never shown: borderless, off screen, never ordered front.
            let window = NSWindow(contentRect: frame.offsetBy(dx: -30_000, dy: -30_000), styleMask: .borderless, backing: .buffered, defer: false)
            window.isReleasedWhenClosed = false
            window.contentView = webView
            self.webView = webView
            self.window = window
            webView.loadFileURL(source, allowingReadAccessTo: source.deletingLastPathComponent())
        }
    }

    func stop() {
        webView?.stopLoading()
        finish(.failure(MorphyError("취소했어요.")))
    }

    private func finish(_ result: Result<Void, MorphyError>) {
        guard let done else { return }
        self.done = nil
        webView?.navigationDelegate = nil
        // Detaching the web view inside a WebKit or print callback trips a WebKit assertion; let go on the next turn.
        let (webView, window) = (self.webView, self.window)
        self.webView = nil
        self.window = nil
        DispatchQueue.main.async { window?.contentView = nil; _ = webView }
        done(result)
    }

    // Links, scripted redirects and pop-ups never leave the file.
    func webView(_ webView: WKWebView, decidePolicyFor action: WKNavigationAction, decisionHandler: @escaping (WKNavigationActionPolicy) -> Void) {
        let scheme = action.request.url?.scheme ?? ""
        decisionHandler(action.targetFrame?.isMainFrame == false || ["file", "about", "data"].contains(scheme) ? .allow : .cancel)
    }

    func webView(_ webView: WKWebView, didFail navigation: WKNavigation!, withError error: Error) { failToOpen() }
    func webView(_ webView: WKWebView, didFailProvisionalNavigation navigation: WKNavigation!, withError error: Error) { failToOpen() }
    private func failToOpen() { finish(.failure(MorphyError("HTML 파일을 열 수 없어요. 원본 파일을 확인해 주세요."))) }

    func webView(_ webView: WKWebView, didFinish navigation: WKNavigation!) {
        // Give scripts and web fonts a moment to settle, then measure the whole page.
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.5) { [weak self] in
            webView.evaluateJavaScript("Math.max(document.documentElement.scrollHeight, document.body ? document.body.scrollHeight : 0)") { value, _ in
                guard let self, self.done != nil else { return }
                let height = max(CGFloat((value as? NSNumber)?.doubleValue ?? 800), 100)
                self.format == .pdf ? self.printPDF() : self.snapshot(height: height)
            }
        }
    }

    private func snapshot(height: CGFloat) {
        guard let webView, let window else { return }
        guard height <= WebPage.maxJPGHeight else {
            return finish(.failure(MorphyError("페이지가 너무 길어서 JPG 한 장에 담을 수 없어요. PDF로 바꿔 주세요.")))
        }
        let size = NSSize(width: WebPage.width, height: height)
        window.setContentSize(size)
        webView.frame = NSRect(origin: .zero, size: size)
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.3) { [weak self] in
            let config = WKSnapshotConfiguration()
            config.rect = NSRect(origin: .zero, size: size)
            config.afterScreenUpdates = true
            config.snapshotWidth = NSNumber(value: Double(size.width * 2))  // 2x pixels even with no Retina screen attached
            webView.takeSnapshot(with: config) { image, _ in
                guard let self, self.done != nil else { return }
                // At 2x for sharp text, whatever screen (or none) the Mac has.
                var rect = NSRect(x: 0, y: 0, width: size.width * 2, height: size.height * 2)
                guard let image, let cg = image.cgImage(forProposedRect: &rect, context: nil, hints: nil) else {
                    return self.finish(.failure(MorphyError("페이지를 이미지로 만들지 못했어요.")))
                }
                do {
                    _ = try Images.write(cg, as: .jpg, to: self.out)
                    self.finish(.success(()))
                } catch {
                    self.finish(.failure(error as? MorphyError ?? MorphyError("변환에 실패했어요. 다시 시도할 수 있어요.")))
                }
            }
        }
    }

    private func printPDF() {
        guard let webView, let window else { return }
        let info = NSPrintInfo(dictionary: [.jobDisposition: NSPrintInfo.JobDisposition.save, .jobSavingURL: out])
        info.paperSize = NSSize(width: 595.28, height: 841.89)  // A4
        info.topMargin = 36
        info.bottomMargin = 36
        info.leftMargin = 36
        info.rightMargin = 36
        info.horizontalPagination = .fit
        info.verticalPagination = .automatic
        info.isHorizontallyCentered = false
        info.isVerticallyCentered = false
        let op = webView.printOperation(with: info)
        op.showsPrintPanel = false
        op.showsProgressPanel = false
        op.view?.frame = webView.bounds
        // run() would block the main thread WebKit draws on; the modal run reports back through printed(_:success:contextInfo:).
        op.runModal(for: window, delegate: self, didRun: #selector(printed(_:success:contextInfo:)), contextInfo: nil)
    }

    @objc private func printed(_ op: NSPrintOperation, success: Bool, contextInfo: UnsafeMutableRawPointer?) {
        finish(success ? .success(()) : .failure(MorphyError("PDF를 만들지 못했어요. 다시 시도할 수 있어요.")))
    }
}
