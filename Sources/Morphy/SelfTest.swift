import AppKit
import PDFKit
import ImageIO
import CryptoKit

/// `Morphy --selftest`: image conversion, white flattening, collision naming, original preservation, type sniffing.
enum SelfTest {
    static func run() -> Bool {
        var ok = true
        func check(_ cond: Bool, _ what: String) {
            print(cond ? "ok   " : "FAIL ", what)
            ok = ok && cond
        }
        let fm = FileManager.default
        let dir = fm.temporaryDirectory.appendingPathComponent("morphy-selftest-\(UUID().uuidString)")
        try! fm.createDirectory(at: dir, withIntermediateDirectories: true)
        defer { try? fm.removeItem(at: dir) }

        // 16x16 PNG: left half red, right half transparent.
        let png = dir.appendingPathComponent("사진 🙂.png")
        let ctx = CGContext(data: nil, width: 16, height: 16, bitsPerComponent: 8, bytesPerRow: 0,
                            space: CGColorSpace(name: CGColorSpace.sRGB)!, bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)!
        ctx.setFillColor(red: 1, green: 0, blue: 0, alpha: 1)
        ctx.fill(CGRect(x: 0, y: 0, width: 8, height: 16))
        let dest = CGImageDestinationCreateWithURL(png as CFURL, "public.png" as CFString, 1, nil)!
        CGImageDestinationAddImage(dest, ctx.makeImage()!, nil)
        CGImageDestinationFinalize(dest)
        let hash = { (u: URL) in SHA256.hash(data: try! Data(contentsOf: u)) }
        let before = hash(png)

        let input = try? Input.check(png)
        check(input?.outputs == [.pdf, .jpg, .heic, .tiff, .gif, .webp], "PNG offers PDF/JPG/HEIC/TIFF/GIF/WEBP, not PNG")
        for f in [Format.heic, .tiff, .gif, .webp, .pdf] {
            let r = Pipeline.run(png, to: f, saveIn: dir, CancelToken())
            check(name(r) == "사진 🙂.\(f.ext)", "PNG → \(f.rawValue)")
        }

        let a = Pipeline.run(png, to: .jpg, saveIn: dir, CancelToken())
        let b = Pipeline.run(png, to: .jpg, saveIn: dir, CancelToken())
        check(name(a) == "사진 🙂.jpg", "first result keeps base name")
        check(name(b) == "사진 🙂 (1).jpg", "second result gets (1) suffix")
        check(hash(png) == before, "original untouched")

        if case .done(let jpgs) = a, let jpg = jpgs.first {
            let img = CGImageSourceCreateImageAtIndex(CGImageSourceCreateWithURL(jpg as CFURL, nil)!, 0, nil)!
            let px = CGContext(data: nil, width: 16, height: 16, bitsPerComponent: 8, bytesPerRow: 64,
                               space: CGColorSpace(name: CGColorSpace.sRGB)!, bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)!
            px.draw(img, in: CGRect(x: 0, y: 0, width: 16, height: 16))
            let p = px.data!.assumingMemoryBound(to: UInt8.self)
            let i = 8 * 64 + 15 * 4  // row 8, rightmost column
            check(p[i] > 240 && p[i + 1] > 240 && p[i + 2] > 240, "transparency flattened to white")

            let back = Pipeline.run(jpg, to: .png, saveIn: dir, CancelToken())
            check(name(back) == "사진 🙂 (1).png", "JPG → PNG avoids existing PNG")
        }

        let svg = dir.appendingPathComponent("벡터.svg")
        try! #"<svg xmlns="http://www.w3.org/2000/svg" width="100" height="50"><rect width="100" height="50" fill="red"/><text x="5" y="30">한글</text></svg>"#
            .write(to: svg, atomically: true, encoding: .utf8)
        check((try? Input.check(svg))?.outputs == Routes.imageOutputs, "SVG offers every image output")
        if case .done(let u) = Pipeline.run(svg, to: .png, saveIn: dir, CancelToken()),
           let img = CGImageSourceCreateImageAtIndex(CGImageSourceCreateWithURL(u[0] as CFURL, nil)!, 0, nil) {
            check(img.width == 2048 && img.height == 1024, "SVG → PNG rendered at 2048px long side")
        } else { check(false, "SVG → PNG") }
        if case .done(let u) = Pipeline.run(svg, to: .pdf, saveIn: dir, CancelToken()) {
            let raw = String(decoding: try! Data(contentsOf: u[0]), as: UTF8.self)
            check(!raw.contains("/Subtype /Image") && !raw.contains("/Subtype/Image"), "SVG → PDF stays vector")
        } else { check(false, "SVG → PDF") }
        let notSvg = dir.appendingPathComponent("가짜.svg")
        try! Data("hello".utf8).write(to: notSvg)
        check((try? Input.check(notSvg)) == nil, "non-SVG .svg rejected")

        // 3-page PDF with Korean text; page 2 rotated 90°.
        let pdf = dir.appendingPathComponent("문서.pdf")
        var box = CGRect(x: 0, y: 0, width: 200, height: 100)
        let pctx = CGContext(pdf as CFURL, mediaBox: &box, nil)!
        for i in 1...3 {
            pctx.beginPDFPage(nil)
            NSGraphicsContext.current = NSGraphicsContext(cgContext: pctx, flipped: false)
            ("한글 페이지 \(i)" as NSString).draw(at: CGPoint(x: 10, y: 40), withAttributes: [.font: NSFont.systemFont(ofSize: 14)])
            pctx.endPDFPage()
        }
        pctx.closePDF()
        let doc = PDFDocument(url: pdf)!
        doc.page(at: 1)!.rotation = 90
        doc.write(to: pdf)
        check((try? Input.check(pdf))?.outputs == Routes.pdfOutputs, "PDF offers images + TXT, not PDF")
        if case .done(let pages) = Pipeline.run(pdf, to: .png, saveIn: dir, CancelToken()) {
            check(pages.map(\.lastPathComponent) == ["문서-1.png", "문서-2.png", "문서-3.png"], "PDF → PNG: one file per page, in order")
            let sizes = pages.map { u -> String in
                let img = CGImageSourceCreateImageAtIndex(CGImageSourceCreateWithURL(u as CFURL, nil)!, 0, nil)!
                return "\(img.width)x\(img.height)"
            }
            check(sizes == ["400x200", "200x400", "400x200"], "PDF pages at 144 dpi, rotation applied \(sizes)")
        } else { check(false, "PDF → PNG") }
        if case .done(let t) = Pipeline.run(pdf, to: .txt, saveIn: dir, CancelToken()) {
            let text = (try? String(contentsOf: t[0], encoding: .utf8)) ?? ""
            check(t[0].lastPathComponent == "문서.txt" && text.contains("한글 페이지 2"), "PDF → TXT keeps Korean text")
        } else { check(false, "PDF → TXT") }
        // "사진 🙂.pdf" (from PNG → PDF above) is image-only, like a scan.
        let scan = dir.appendingPathComponent("사진 🙂.pdf")
        if case .failed(let why) = Pipeline.run(scan, to: .txt, saveIn: dir, CancelToken()) {
            check(why.contains("글자를 찾지 못했어요"), "image-only PDF → TXT explains there is no text")
        } else { check(false, "image-only PDF → TXT should fail") }
        let locked = dir.appendingPathComponent("잠김.pdf")
        doc.write(to: locked, withOptions: [.userPasswordOption: "pw", .ownerPasswordOption: "pw"])
        check((try? Input.check(locked)) == nil, "password-protected PDF rejected")

        let fake = dir.appendingPathComponent("fake.png")
        try! Data("not an image".utf8).write(to: fake)
        check((try? Input.check(fake)) == nil, "extension/content mismatch rejected")
        check((try? Input.check(dir)) == nil, "folder rejected")
        let zip = dir.appendingPathComponent("압축.zip")
        try! Data("PK".utf8).write(to: zip)
        do { _ = try Input.check(zip); check(false, "unknown format rejected") } catch {
            check((error as? MorphyError)?.isUnsupported == true, "unknown format and folder → silent no-menu, not an alert")
        }

        let unwritable = Pipeline.run(png, to: .jpg, saveIn: nil, CancelToken())
        if case .failed = unwritable { check(true, "no folder → explicit failure") } else { check(false, "no folder → explicit failure") }

        engineChecks(dir, check)
        webChecks(dir, png, check)
        let tableAfterText = "설명입니다.\n| 월 | 매출 |\n|---|---:|\n| 1월 | 100 |"
        check(Office.separateTables(tableAfterText) == "설명입니다.\n\n| 월 | 매출 |\n|---|---:|\n| 1월 | 100 |", "markdown: blank line added before a table under text")
        check(Office.separateTables("글\r\n월 | 매출\r\n---|---\r\n1월 | 100\r\n").contains("글\r\n\n월 | 매출"), "markdown: also for tables without outer pipes and CRLF")
        for unchanged in ["# 제목\n\n| a | b |\n|---|---|\n| 1 | 2 |", "```\ncode\n| a | b |\n|---|---|\n```", "a | b 는 표가 아니에요\n다음 줄"] {
            check(Office.separateTables(unchanged) == unchanged, "markdown: left alone — \(unchanged.prefix(12))")
        }
        let release = { (v: String, path: String, sha: String) in
            Data(#"{"version":"\#(v)","path":"\#(path)","sha256":"\#(sha)","bytes":10}"#.utf8)
        }
        let sha = String(repeating: "a", count: 64)
        check(Updater.parse(release("0.2.0", "/releases/Morphy-0.2.0.dmg", sha), current: "0.1.0")?.url.absoluteString
              == "https://morphy.tpals0409dev.workers.dev/releases/Morphy-0.2.0.dmg", "update: newer release on our site accepted")
        check(Updater.parse(release("0.1.0", "/releases/Morphy-0.1.0.dmg", sha), current: "0.1.0") == nil, "update: same version ignored")
        check(Updater.parse(release("0.10.0", "/releases/Morphy-0.10.0.dmg", sha), current: "0.9.0") != nil, "update: versions compare as numbers")
        check(Updater.parse(release("0.2.0", "https://evil.example/Morphy-0.2.0.dmg", sha), current: "0.1.0") == nil, "update: foreign path refused")
        check(Updater.parse(release("0.2.0", "/releases/Morphy-0.2.0.dmg", "bad"), current: "0.1.0") == nil, "update: bad SHA-256 refused")
        print("Office engine:", Office.soffice?.path ?? "not installed (Office routes ask to download it)")
        // `--selftest <corpus dir>` (scripts/make_corpus.py output) also checks Office → PDF on real files.
        if let i = CommandLine.arguments.firstIndex(of: "--selftest"), i + 1 < CommandLine.arguments.count {
            officeChecks(URL(fileURLWithPath: CommandLine.arguments[i + 1]), dir, check)
        }
        return ok
    }

    /// Engine download safety with fake DMGs: wrong hash and a foreign signature must both be refused, leaving nothing behind.
    private static func engineChecks(_ dir: URL, _ check: (Bool, String) -> Void) {
        check(LibreOffice.isNewEnough("26.8.1.1") && LibreOffice.isNewEnough("27.0") && !LibreOffice.isNewEnough("26.2.6"),
              "installed LibreOffice used only from 26.8 on")
        let fm = FileManager.default
        let stage = dir.appendingPathComponent("dmg-src")
        try! fm.createDirectory(at: stage, withIntermediateDirectories: true)
        try! fm.copyItem(at: Bundle.main.bundleURL, to: stage.appendingPathComponent("LibreOffice.app"))  // signed by Morphy's team
        let dmg = dir.appendingPathComponent("fake.dmg")
        EngineInstaller.shell("/usr/bin/hdiutil", ["create", "-srcfolder", stage.path, "-format", "UDZO", "-ov", dmg.path])
        let size = (try? dmg.resourceValues(forKeys: [.fileSizeKey]))?.fileSize ?? 0
        let sha = EngineInstaller.shell("/usr/bin/shasum", ["-a", "256", dmg.path]).output.prefix(64)
        let target = dir.appendingPathComponent("Engines/LibreOffice/9.9/LibreOffice.app")

        func refused(_ sha: String, _ team: String, _ expected: String, _ what: String) {
            do {
                try EngineInstaller.verifyAndInstall(dmg: dmg, bytes: size, sha256: sha, teamID: team, as: target)
                check(false, what)
            } catch {
                check((error as? MorphyError)?.message.contains(expected) == true, what)
            }
        }
        refused(String(repeating: "0", count: 64), LibreOffice.teamID, "올바르지 않아요", "engine DMG with wrong SHA-256 refused")
        refused(String(sha), LibreOffice.teamID, "서명을 확인할 수 없어요", "engine DMG signed by another team refused")
        let leftovers = ((try? fm.contentsOfDirectory(atPath: target.deletingLastPathComponent().deletingLastPathComponent().path)) ?? [])
            + ((try? fm.contentsOfDirectory(atPath: EngineInstaller.cache.path)) ?? []).filter { $0.hasPrefix("mount-") }
        check(!fm.fileExists(atPath: target.path) && leftovers.isEmpty, "refused engine leaves no install, staging or mount")
    }

    /// HTML → PDF/JPG through WebKit: layout, scripts, local images, page breaks, and no network access (D11).
    private static func webChecks(_ dir: URL, _ png: URL, _ check: (Bool, String) -> Void) {
        let fm = FileManager.default
        let page = dir.appendingPathComponent("웹 페이지.html")
        try? fm.copyItem(at: png, to: dir.appendingPathComponent("local.png"))
        try! """
            <!doctype html><html lang="ko"><head><meta charset="utf-8"><style>
            .grid{display:grid;grid-template-columns:repeat(3,1fr);gap:8px}.card{background:#f47b55;padding:16px}h2{break-before:page}
            </style></head><body><h1>웹 페이지 변환</h1><p id="js">스크립트 전</p><p id="net">확인 중</p>
            <div class="grid"><div class="card">하나</div><div class="card">둘</div><div class="card">셋</div></div>
            <img src="local.png" width="64"><h2>둘째 쪽</h2><p>새 페이지에서 시작해요.</p>
            <script>
            document.getElementById('js').textContent = '스크립트가 바꾼 문장';
            fetch('https://www.apple.com/', {mode: 'no-cors'})
              .then(() => document.getElementById('net').textContent = '외부 연결됨')
              .catch(() => document.getElementById('net').textContent = '외부 차단됨');
            </script></body></html>
            """.write(to: page, atomically: true, encoding: .utf8)
        check((try? Input.check(page))?.outputs == [.pdf, .jpg], "HTML offers PDF and JPG")
        let html = dir.appendingPathComponent("웹.htm")
        try? fm.copyItem(at: page, to: html)
        check((try? Input.check(html))?.outputs == [.pdf, .jpg], ".htm is HTML too")

        if case .done(let urls) = Pipeline.run(page, to: .pdf, saveIn: dir, CancelToken()), let doc = PDFDocument(url: urls[0]) {
            let text = doc.string ?? ""
            let a4 = doc.page(at: 0).map { abs($0.bounds(for: .mediaBox).width - 595) < 2 } ?? false
            check(doc.pageCount == 2 && a4, "HTML → PDF: A4, CSS page break kept (\(doc.pageCount) pages)")
            check(text.contains("스크립트가 바꾼 문장") && text.contains("하나"), "HTML → PDF: scripts ran, Korean text kept")
            check(text.contains("외부 차단됨"), "HTML → PDF: network requests are blocked")
        } else { check(false, "HTML → PDF") }

        // The app converts on a background queue while the main thread runs its loop.
        var state: Job.State?
        DispatchQueue.global().async { let r = Pipeline.run(page, to: .jpg, saveIn: dir, CancelToken()); DispatchQueue.main.async { state = r } }
        let deadline = Date().addingTimeInterval(40)
        while state == nil, Date() < deadline { RunLoop.main.run(until: Date().addingTimeInterval(0.05)) }
        if case .done(let urls) = state, let src = CGImageSourceCreateWithURL(urls[0] as CFURL, nil),
           let img = CGImageSourceCreateImageAtIndex(src, 0, nil) {
            check(img.width == Int(WebPage.width * 2) && img.height > img.width / 2, "HTML → JPG from a background queue: whole page at 2x (\(img.width)×\(img.height))")
        } else { check(false, "HTML → JPG from a background queue: \(String(describing: state))") }
    }

    private static func officeChecks(_ corpus: URL, _ out: URL, _ check: (Bool, String) -> Void) {
        let fm = FileManager.default
        let files = (try? fm.contentsOfDirectory(at: corpus, includingPropertiesForKeys: nil))?
            .filter { Routes.office[$0.pathExtension] != nil } ?? []
        check(!files.isEmpty, "corpus has Office files")
        for file in files {
            let before = SHA256.hash(data: try! Data(contentsOf: file))
            guard let outputs = try? Input.check(file).outputs else { check(false, "\(file.lastPathComponent) accepted"); continue }
            for format in outputs.sorted(by: { $0.order < $1.order }) {
                let label = "\(file.lastPathComponent) → \(format.rawValue)"
                let result = Pipeline.run(file, to: format, saveIn: out, CancelToken())
                guard case .done(let urls) = result else { check(false, "\(label): \(result)"); continue }
                if format == .csv {
                    // Same-named inputs share the output dir, so strip collision suffixes like " (1)".
                    let names = urls.map { $0.lastPathComponent.replacingOccurrences(of: #" \(\d+\)\.csv$"#, with: ".csv", options: .regularExpression) }
                    let base = file.deletingPathExtension().lastPathComponent
                    // Corpus: "매출 표.*" has sheets 매출 + 두번째 시트, "단일 시트.xlsx" has one.
                    let expected = base == "매출 표" ? ["매출 표-두번째 시트.csv", "매출 표-매출.csv"] : ["\(base).csv"]
                    check(names.sorted() == expected.sorted(), "\(label) → one CSV per sheet \(names)")
                }
                let raw = urls.map { String(decoding: try! Data(contentsOf: $0), as: UTF8.self) }.joined()
                if file.pathExtension == "md", format == .html {
                    check(raw.contains("<table") && !raw.contains("|---"), "\(label) has a real table, not pipe text")
                }
                switch format {
                case .pdf:
                    // Missing Hangul font = blank Korean text that still extracts fine, so check the embedded fonts.
                    let korean = ["AppleSDGothicNeo", "ArialUnicodeMS", "AppleGothic", "Nanum", "NotoSansKR", "NotoSansCJK"].contains { raw.contains($0) }
                    check(korean, "\(label) embeds a Hangul font")
                    // LibreOffice's Korean PDFs have no space glyphs; PDF → TXT must still keep word spacing.
                    if file.lastPathComponent == "보고서 한글.docx", case .done(let t) = Pipeline.run(urls[0], to: .txt, saveIn: out, CancelToken()) {
                        let text = (try? String(contentsOf: t[0], encoding: .utf8)) ?? ""
                        check(text.contains("모르피 테스트 보고서"), "… and that PDF → TXT keeps word spacing")
                    }
                case .csv, .html:
                    let hangul = raw.unicodeScalars.contains { (0xAC00...0xD7A3).contains($0.value) }
                    check(hangul && !raw.contains("\u{FFFD}"), "\(label) keeps Korean text")
                default:
                    check(true, label)
                }
            }
            check(SHA256.hash(data: try! Data(contentsOf: file)) == before, "\(file.lastPathComponent) original untouched")
            check(!((try? fm.contentsOfDirectory(atPath: corpus.path)) ?? []).contains { $0.hasPrefix(".~lock") }, "no LibreOffice lock file beside original")
        }

        // Cancel mid-conversion: no result file, engine process gone.
        guard let file = files.first else { return }
        let token = CancelToken()
        let cancelDir = out.appendingPathComponent("cancel")
        try! fm.createDirectory(at: cancelDir, withIntermediateDirectories: true)
        var result: Job.State?
        let done = DispatchSemaphore(value: 0)
        DispatchQueue.global().async { result = Pipeline.run(file, to: .pdf, saveIn: cancelDir, token); done.signal() }
        Thread.sleep(forTimeInterval: 0.3)
        let pid = token.process?.processIdentifier
        token.cancel()
        done.wait()
        if case .cancelled = result { check(true, "cancel → cancelled state") } else { check(false, "cancel → cancelled state (got \(String(describing: result)))") }
        check(((try? fm.contentsOfDirectory(atPath: cancelDir.path)) ?? []).isEmpty, "cancel leaves no result file")
        check(pid.map { kill($0, 0) != 0 } ?? true, "cancel stops the engine process")
    }

    private static func name(_ s: Job.State) -> String? {
        if case .done(let u) = s, u.count == 1 { u[0].lastPathComponent } else { nil }
    }
}
