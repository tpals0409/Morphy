import AppKit
import ImageIO
import PDFKit
import UniformTypeIdentifiers

struct MorphyError: Error {
    let message: String
    let detail: String
    init(_ message: String, _ detail: String = "") {
        self.message = message
        self.detail = detail
    }

    /// A format (or folder/app) Morphy has no route for: the menu simply doesn't open, no alert.
    static let unsupported = MorphyError("이 형식은 아직 지원하지 않아요.")
    var isUnsupported: Bool { message == Self.unsupported.message }
}

/// Output formats. `slot` is the fixed position on the ring (0 = top, clockwise, 6 slots) so users can learn it.
/// Formats share a slot only when no input ever offers both (e.g. HEIC is image-only, CSV/HTML are document-only).
/// Saving as WEBP is rare outside web work (and needs cwebp), so it lives under "더 보기"; WEBP *input* is common.
/// `nil` = listed under "더 보기", which always sits in `moreSlot` (PRD 6.3: at most 6 on the ring).
enum Format: String, CaseIterable {
    case pdf = "PDF", jpg = "JPG", png = "PNG", webp = "WEBP", gif = "GIF", heic = "HEIC", tiff = "TIFF"
    case docx = "DOCX", xlsx = "XLSX", pptx = "PPTX", csv = "CSV", html = "HTML", txt = "TXT"

    static let moreSlot = 4
    var slot: Int? {
        switch self {
        case .pdf, .txt: 0  // TXT is only offered for PDF input, which never offers PDF
        case .jpg: 1
        case .heic, .csv, .html: 2
        case .gif, .docx, .xlsx, .pptx: 3
        case .png: 5
        case .webp, .tiff: nil
        }
    }
    var order: Int { slot ?? 10 + Format.allCases.firstIndex(of: self)! }
    var ext: String { rawValue.lowercased() }
    var type: UTType { [.pdf: UTType.pdf, .jpg: .jpeg, .png: .png, .heic: .heic, .tiff: .tiff, .gif: .gif, .webp: .webP, .txt: .plainText][self] ?? .data }
}

/// Supported routes (PRD 7). Only what is listed here ever appears on the ring.
enum Routes {
    /// WEBP only when its encoder is installed (PRD 6.2: no menu item without a ready engine).
    static var imageOutputs: Set<Format> { Set<Format>([.pdf, .jpg, .png, .heic, .tiff, .gif]).union(WebP.cwebp == nil ? [] : [.webp]) }
    static let images: [String: Format?] = [  // extension → its own format (excluded from outputs)
        "png": .png, "jpg": .jpg, "jpeg": .jpg, "jpe": .jpg, "jfif": .jpg, "heic": .heic, "heif": .heic,
        "tif": .tiff, "tiff": .tiff, "gif": .gif, "bmp": nil, "webp": .webp,
    ]
    static let office: [String: Set<Format>] = [
        "docx": [.pdf], "doc": [.pdf, .docx], "odt": [.pdf, .docx], "rtf": [.pdf, .docx],
        "txt": [.pdf, .docx], "md": [.pdf, .docx, .html],
        "xlsx": [.pdf, .csv], "xls": [.pdf, .xlsx, .csv], "ods": [.pdf, .xlsx, .csv], "csv": [.pdf, .xlsx],
        "pptx": [.pdf], "ppt": [.pdf, .pptx], "odp": [.pdf, .pptx],
    ]
    static let textInputs: Set<String> = ["txt", "md", "csv"]
    /// PDF in: every image format (one file per page) or its text layer.
    static var pdfOutputs: Set<Format> { imageOutputs.subtracting([.pdf]).union([.txt]) }
}

struct Input {
    enum Kind { case image, office, pdf }
    let url: URL
    let kind: Kind
    let outputs: Set<Format>
    var typeLabel: String { url.pathExtension.uppercased() }
    /// Office routes are offered without the engine; picking one asks to download it first (D4).
    var needsEngine: Bool { kind == .office && Office.soffice == nil }

    /// Validates a selected file and resolves its routes. Runs at menu time and again right before converting (PRD 5.5, 7.6).
    static func check(_ url: URL) throws -> Input {
        let v = try? url.resourceValues(forKeys: [.isDirectoryKey, .isSymbolicLinkKey, .isAliasFileKey, .isRegularFileKey,
                                                  .isReadableKey, .fileSizeKey, .ubiquitousItemDownloadingStatusKey])
        guard let v else { throw MorphyError("파일을 찾을 수 없어요. 파일을 다시 선택해 주세요.") }
        if v.isDirectory == true { throw MorphyError.unsupported }  // folders, app bundles
        if v.isSymbolicLink == true || v.isAliasFile == true { throw MorphyError("별칭과 심볼릭 링크는 지원하지 않아요.") }
        if let s = v.ubiquitousItemDownloadingStatus, s != .current { throw MorphyError("아직 내려받지 않은 클라우드 파일이에요. Finder에서 먼저 내려받아 주세요.") }
        guard v.isRegularFile == true, v.isReadable == true else { throw MorphyError("파일을 읽을 수 없어요. 원본 파일을 확인해 주세요.") }
        if (v.fileSize ?? 0) > 100_000_000 { throw MorphyError("100MB보다 큰 파일은 아직 변환할 수 없어요.") }
        let mismatch = MorphyError("확장자와 실제 형식이 다르거나 손상된 파일이에요.")

        let ext = url.pathExtension.lowercased()
        if let own = Routes.images[ext] {
            guard let src = CGImageSourceCreateWithURL(url as CFURL, nil),
                  CGImageSourceGetStatus(src) == .statusComplete, CGImageSourceGetCount(src) > 0,
                  let actual = (CGImageSourceGetType(src) as String?).flatMap(UTType.init),
                  let expected = ext == "jfif" ? .jpeg : UTType(filenameExtension: ext), actual.conforms(to: expected)
            else { throw mismatch }
            if ["gif", "webp"].contains(ext), CGImageSourceGetCount(src) > 1 { throw MorphyError("움직이는 이미지는 지원하지 않아요.") }
            return Input(url: url, kind: .image, outputs: Routes.imageOutputs.subtracting([own].compactMap { $0 }))
        }
        if ext == "pdf" {
            guard (try? FileHandle(forReadingFrom: url).read(upToCount: 1024)).map({ String(decoding: $0, as: UTF8.self).contains("%PDF") }) == true,
                  let doc = PDFDocument(url: url) else { throw mismatch }
            if doc.isLocked { throw MorphyError("암호가 걸린 PDF는 지원하지 않아요.") }
            guard doc.pageCount > 0 else { throw mismatch }
            return Input(url: url, kind: .pdf, outputs: Routes.pdfOutputs)
        }
        if ext == "svg" {
            let head = String(decoding: (try? FileHandle(forReadingFrom: url).read(upToCount: 4096)) ?? Data(), as: UTF8.self)
            guard head.contains("<svg"), let svg = NSImage(contentsOf: url), svg.size.width > 0, svg.size.height > 0 else { throw mismatch }
            return Input(url: url, kind: .image, outputs: Routes.imageOutputs)
        }
        guard let outputs = Routes.office[ext] else { throw MorphyError.unsupported }

        let magic = (try? FileHandle(forReadingFrom: url).read(upToCount: 5)) ?? Data()
        let zip = magic.starts(with: [0x50, 0x4B, 0x03, 0x04]), ole = magic.starts(with: [0xD0, 0xCF, 0x11, 0xE0])
        switch ext {
        case "docx", "xlsx", "pptx":
            if ole { throw MorphyError("암호가 걸린 문서는 지원하지 않아요.") }  // encrypted OOXML is an OLE container
            guard zip else { throw mismatch }
        case "odt", "ods", "odp": guard zip else { throw mismatch }
        case "doc", "xls", "ppt": guard ole else { throw mismatch }
        case "rtf": guard magic.starts(with: Array("{\\rtf".utf8)) else { throw mismatch }
        default: guard Office.utf8Text(url) != nil else { throw MorphyError("텍스트 인코딩을 알 수 없어요. UTF-8로 저장해 주세요.") }
        }
        return Input(url: url, kind: .office, outputs: outputs)
    }
}

// ponytail: unsynchronized flags; a stale read only delays a cancel by one poll tick.
final class CancelToken {
    private(set) var cancelled = false
    var process: Process?
    func cancel() {
        cancelled = true
        process?.terminate()
    }
}

enum Pipeline {
    /// Check → convert into a temp dir on the destination volume → validate → commit without overwriting (PRD 9.1, 10).
    static func run(_ source: URL, to format: Format, saveIn folder: URL?, _ token: CancelToken) -> Job.State {
        do {
            let input = try Input.check(source)
            guard input.outputs.contains(format) else { throw MorphyError.unsupported }
            guard let folder else { throw MorphyError("원본 폴더에 저장할 권한이 없어요. 다른 폴더를 선택해 주세요.") }
            let tmp = try FileManager.default.url(for: .itemReplacementDirectory, in: .userDomainMask, appropriateFor: folder, create: true)
            defer { try? FileManager.default.removeItem(at: tmp) }

            let outs = switch input.kind {
            case .office: try Office.convert(source, to: format, in: tmp, token)
            case .pdf: try PDFPages.convert(source, to: format, in: tmp, token)
            case .image: [try Images.convert(source, to: format, in: tmp)]
            }
            for out in outs { try validate(out, format) }
            if token.cancelled { return .cancelled }
            // One result keeps the source name; several keep their own: "name-시트.csv" per sheet, "name-01.png" per page.
            let base = { (out: URL) in outs.count == 1 ? source.deletingPathExtension().lastPathComponent : out.deletingPathExtension().lastPathComponent }
            return .done(try outs.map { try commit($0, base: base($0), ext: format.ext, in: folder) })
        } catch {
            if token.cancelled { return .cancelled }
            return .failed((error as? MorphyError)?.message ?? "변환에 실패했어요. 다시 시도할 수 있어요.")
        }
    }

    /// Re-opens the result; only readable output of the right type gets a real file name.
    static func validate(_ url: URL, _ format: Format) throws {
        let ok: Bool
        switch format {
        case .pdf:
            ok = (PDFDocument(url: url)?.pageCount ?? 0) > 0
        case .jpg, .png, .heic, .tiff, .gif, .webp:
            if let src = CGImageSourceCreateWithURL(url as CFURL, nil),
               let t = CGImageSourceGetType(src) as String?, UTType(t)?.conforms(to: format.type) == true {
                ok = CGImageSourceCreateImageAtIndex(src, 0, nil) != nil
            } else {
                ok = false
            }
        case .docx, .xlsx, .pptx:
            ok = (try? FileHandle(forReadingFrom: url).read(upToCount: 4)) == Data([0x50, 0x4B, 0x03, 0x04])
        case .csv, .html, .txt:
            ok = !((try? String(contentsOf: url, encoding: .utf8)) ?? "").isEmpty
        }
        if !ok { throw MorphyError("변환 결과를 확인하지 못했어요. 다시 시도할 수 있어요.") }
    }

    /// `report.pdf`, `report (1).pdf`, … RENAME_EXCL makes check-and-create atomic, so nothing is ever overwritten.
    static func commit(_ tmp: URL, base: String, ext: String, in folder: URL) throws -> URL {
        for i in 0..<1000 {
            let dst = folder.appendingPathComponent(i == 0 ? "\(base).\(ext)" : "\(base) (\(i)).\(ext)")
            if renamex_np(tmp.path, dst.path, UInt32(RENAME_EXCL)) == 0 { return dst }
            switch errno {
            case EEXIST: continue
            case ENOSPC: throw MorphyError("저장 공간이 부족해요.")
            case EACCES, EPERM, EROFS: throw MorphyError("원본 폴더에 저장할 권한이 없어요. 다른 폴더를 선택해 주세요.")
            default: throw MorphyError("결과를 저장하지 못했어요. 다시 시도할 수 있어요.")
            }
        }
        throw MorphyError("같은 이름의 파일이 너무 많아요.")
    }
}

enum Images {
    static func convert(_ source: URL, to format: Format, in dir: URL) throws -> URL {
        let out = dir.appendingPathComponent("out.\(format.ext)")
        let unreadable = MorphyError("파일을 읽을 수 없어요. 원본 파일을 확인해 주세요.")
        var image: CGImage
        var props: [CFString: Any] = [:]
        if source.pathExtension.lowercased() == "svg" {
            guard let svg = NSImage(contentsOf: source) else { throw unreadable }
            if format == .pdf { return try vectorPDF(svg, to: out) }
            guard let raster = rasterize(svg) else { throw unreadable }
            image = raster
        } else {
            guard let src = CGImageSourceCreateWithURL(source as CFURL, nil),
                  let decoded = format == .pdf || format == .webp ? oriented(src) : CGImageSourceCreateImageAtIndex(src, 0, nil)
            else { throw unreadable }
            image = decoded
            let srcProps = CGImageSourceCopyPropertiesAtIndex(src, 0, nil) as? [CFString: Any]
            if format != .pdf { props[kCGImagePropertyOrientation] = srcProps?[kCGImagePropertyOrientation] }
        }
        return try write(image, as: format, to: out, props: props)
    }

    /// Encodes an upright image in any image output format.
    static func write(_ image: CGImage, as format: Format, to out: URL, props: [CFString: Any] = [:]) throws -> URL {
        var image = image, props = props
        if format == .webp { return try WebP.encode(image, to: out) }
        guard let dest = CGImageDestinationCreateWithURL(out as CFURL, format.type.identifier as CFString, 1, nil) else {
            throw MorphyError("변환에 실패했어요. 다시 시도할 수 있어요.")
        }
        if format == .jpg || format == .heic { props[kCGImageDestinationLossyCompressionQuality] = 0.9 }
        if format == .jpg { image = try flattenOnWhite(image) }
        CGImageDestinationAddImage(dest, image, props as CFDictionary)
        guard CGImageDestinationFinalize(dest) else { throw MorphyError("변환에 실패했어요. 저장 공간을 확인해 주세요.") }
        return out
    }

    /// SVG → PDF stays vector: draw the SVG rep straight into a PDF context.
    private static func vectorPDF(_ svg: NSImage, to out: URL) throws -> URL {
        var box = CGRect(origin: .zero, size: svg.size)
        guard let ctx = CGContext(out as CFURL, mediaBox: &box, nil) else { throw MorphyError("변환에 실패했어요. 다시 시도할 수 있어요.") }
        ctx.beginPDFPage(nil)
        NSGraphicsContext.saveGraphicsState()
        NSGraphicsContext.current = NSGraphicsContext(cgContext: ctx, flipped: false)
        svg.draw(in: box)
        NSGraphicsContext.restoreGraphicsState()
        ctx.endPDFPage()
        ctx.closePDF()
        return out
    }

    /// SVG has no pixel size; render at 2x, at least 2048px on the long side (icons would come out tiny), at most 8192px.
    private static func rasterize(_ svg: NSImage) -> CGImage? {
        let long = max(svg.size.width, svg.size.height)
        let scale = min(max(2, 2048 / long), 8192 / long)
        let w = Int(svg.size.width * scale), h = Int(svg.size.height * scale)
        guard let ctx = CGContext(data: nil, width: w, height: h, bitsPerComponent: 8, bytesPerRow: 0,
                                  space: CGColorSpace(name: CGColorSpace.sRGB)!, bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)
        else { return nil }
        NSGraphicsContext.saveGraphicsState()
        NSGraphicsContext.current = NSGraphicsContext(cgContext: ctx, flipped: false)
        svg.draw(in: NSRect(x: 0, y: 0, width: w, height: h))
        NSGraphicsContext.restoreGraphicsState()
        return ctx.makeImage()
    }

    /// PDF pages ignore EXIF orientation, so bake the rotation in (iPhone photos would come out sideways).
    private static func oriented(_ src: CGImageSource) -> CGImage? {
        let p = CGImageSourceCopyPropertiesAtIndex(src, 0, nil) as? [CFString: Any]
        let side = max(p?[kCGImagePropertyPixelWidth] as? Int ?? 0, p?[kCGImagePropertyPixelHeight] as? Int ?? 0)
        return CGImageSourceCreateThumbnailAtIndex(src, 0, [kCGImageSourceCreateThumbnailFromImageAlways: true,
                                                           kCGImageSourceCreateThumbnailWithTransform: true,
                                                           kCGImageSourceThumbnailMaxPixelSize: side] as CFDictionary)
    }

    /// JPEG has no alpha: composite transparent areas onto white (PRD 7.4).
    private static func flattenOnWhite(_ image: CGImage) throws -> CGImage {
        let space = image.colorSpace?.model == .rgb ? image.colorSpace! : CGColorSpace(name: CGColorSpace.sRGB)!
        guard let ctx = CGContext(data: nil, width: image.width, height: image.height, bitsPerComponent: 8, bytesPerRow: 0,
                                  space: space, bitmapInfo: CGImageAlphaInfo.noneSkipLast.rawValue)
        else { throw MorphyError("변환에 실패했어요. 다시 시도할 수 있어요.") }
        let rect = CGRect(x: 0, y: 0, width: image.width, height: image.height)
        ctx.setFillColor(CGColor(gray: 1, alpha: 1))
        ctx.fill(rect)
        ctx.draw(image, in: rect)
        guard let flat = ctx.makeImage() else { throw MorphyError("변환에 실패했어요. 다시 시도할 수 있어요.") }
        return flat
    }
}

/// PDF in: one image per page, or the text layer. Scanned PDFs have no text layer; OCR is out of scope (PRD 4).
enum PDFPages {
    static let scale: CGFloat = 2  // 144 dpi: sharp on Retina screens, A4 ≈ 1190×1684 px

    static func convert(_ source: URL, to format: Format, in dir: URL, _ token: CancelToken) throws -> [URL] {
        guard let doc = PDFDocument(url: source), !doc.isLocked else { throw MorphyError("PDF를 열 수 없어요. 원본 파일을 확인해 주세요.") }
        let base = source.deletingPathExtension().lastPathComponent
        if format == .txt {
            let text = try text(source, doc)
            guard !text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
                throw MorphyError("글자를 찾지 못했어요. 스캔한 PDF는 아직 텍스트로 바꿀 수 없어요.")
            }
            let out = dir.appendingPathComponent("\(base).txt")
            try text.write(to: out, atomically: false, encoding: .utf8)
            return [out]
        }
        // Zero-padded so Finder sorts pages in order: name-01 … name-12.
        let digits = String(doc.pageCount).count
        return try (0..<doc.pageCount).map { i in
            if token.cancelled { throw MorphyError("취소됐어요.") }
            guard let page = doc.page(at: i)?.pageRef, let image = render(page) else {
                throw MorphyError("\(i + 1)쪽을 그리지 못했어요. 원본 파일을 확인해 주세요.")
            }
            let n = String(format: "%0\(digits)d", i + 1)
            return try Images.write(image, as: format, to: dir.appendingPathComponent("\(base)-\(n).\(format.ext)"))
        }
    }

    static var pdftotext: URL? {
        ["/opt/homebrew/bin/pdftotext", "/usr/local/bin/pdftotext"].map { URL(fileURLWithPath: $0) }
            .first { FileManager.default.isExecutableFile(atPath: $0.path) }
    }

    /// poppler's pdftotext when installed: many PDFs (LibreOffice's Korean output among them) have no space glyphs and
    /// only move the pen between words, which PDFKit reads as "모르피테스트보고서". PDFKit is the fallback.
    private static func text(_ source: URL, _ doc: PDFDocument) throws -> String {
        guard let pdftotext else { return (0..<doc.pageCount).compactMap { doc.page(at: $0)?.string }.joined(separator: "\n\n") }
        let p = Process(), pipe = Pipe()
        p.executableURL = pdftotext
        p.arguments = ["-enc", "UTF-8", "-q", source.path, "-"]
        p.standardOutput = pipe
        try p.run()
        let data = pipe.fileHandleForReading.readDataToEndOfFile()
        p.waitUntilExit()
        guard p.terminationStatus == 0 else { throw MorphyError("PDF를 열 수 없어요. 원본 파일을 확인해 주세요.") }
        return String(decoding: data, as: UTF8.self).replacingOccurrences(of: "\u{0C}", with: "\n")  // form feed between pages
    }

    /// Crop box on white (pages have no background of their own), with the page's /Rotate applied.
    static func render(_ page: CGPDFPage) -> CGImage? {
        let box = page.getBoxRect(.cropBox)
        let size = abs(page.rotationAngle) % 180 == 90 ? CGSize(width: box.height, height: box.width) : box.size
        guard let ctx = CGContext(data: nil, width: Int(size.width * scale), height: Int(size.height * scale), bitsPerComponent: 8,
                                  bytesPerRow: 0, space: CGColorSpace(name: CGColorSpace.sRGB)!,
                                  bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else { return nil }
        ctx.setFillColor(CGColor(gray: 1, alpha: 1))
        ctx.fill(CGRect(x: 0, y: 0, width: ctx.width, height: ctx.height))
        ctx.scaleBy(x: scale, y: scale)  // getDrawingTransform only ever scales down, so scale up first
        ctx.concatenate(page.getDrawingTransform(.cropBox, rect: CGRect(origin: .zero, size: size), rotate: 0, preserveAspectRatio: true))
        ctx.drawPDFPage(page)
        return ctx.makeImage()
    }
}

/// ImageIO reads WebP but cannot write it, so encoding goes through libwebp's `cwebp`.
enum WebP {
    static var cwebp: URL? {
        ["/opt/homebrew/bin/cwebp", "/usr/local/bin/cwebp"].map { URL(fileURLWithPath: $0) }
            .first { FileManager.default.isExecutableFile(atPath: $0.path) }
    }

    /// Lossy quality 90 like JPEG; alpha is kept. `image` must already be upright (cwebp ignores EXIF orientation).
    static func encode(_ image: CGImage, to out: URL) throws -> URL {
        let failed = MorphyError("변환에 실패했어요. 다시 시도할 수 있어요.")
        guard let cwebp else { throw MorphyError("변환 엔진을 사용할 수 없어요.") }
        let png = out.deletingPathExtension().appendingPathExtension("src.png")
        guard let dest = CGImageDestinationCreateWithURL(png as CFURL, UTType.png.identifier as CFString, 1, nil) else { throw failed }
        CGImageDestinationAddImage(dest, image, nil)
        guard CGImageDestinationFinalize(dest) else { throw failed }
        let p = Process()
        p.executableURL = cwebp
        p.arguments = ["-quiet", "-q", "90", "-metadata", "icc", png.path, "-o", out.path]
        p.standardError = FileHandle.nullDevice
        try p.run()
        p.waitUntilExit()  // ponytail: no timeout/cancel; cwebp takes well under a second for normal photos
        guard p.terminationStatus == 0 else { throw failed }
        return out
    }
}

enum Office {
    static var soffice: URL? { LibreOffice.app?.appendingPathComponent("Contents/MacOS/soffice") }

    /// LibreOffice `--convert-to` targets. CSV: comma, double quote, UTF-8, quote only when needed, formula results.
    private static let targets: [Format: String] = [
        .pdf: "pdf", .docx: "docx:MS Word 2007 XML", .xlsx: "xlsx:Calc MS Excel 2007 XML",
        .pptx: "pptx:Impress MS PowerPoint 2007 XML", .html: "html:HTML (StarWriter)",
        .csv: "csv:Text - txt - csv (StarCalc):44,34,76,1,,0,false,true,false,false,false,-1",  // -1: every sheet → "name-시트.csv"
    ]

    /// Converts a private copy so LibreOffice never touches the original or drops lock files beside it.
    static func convert(_ source: URL, to format: Format, in dir: URL, _ token: CancelToken) throws -> [URL] {
        guard let soffice, let target = targets[format] else { throw MorphyError("문서 변환 기능이 아직 추가되지 않았어요.") }
        let ext = source.pathExtension.lowercased()
        let copy = dir.appendingPathComponent("in").appendingPathComponent(source.lastPathComponent)
        try FileManager.default.createDirectory(at: copy.deletingLastPathComponent(), withIntermediateDirectories: true)
        if Routes.textInputs.contains(ext) {
            // Korean CSV/TXT is often CP949 (Excel's default); LibreOffice gets UTF-8 either way.
            guard let text = utf8Text(source) else { throw MorphyError("텍스트 인코딩을 알 수 없어요. UTF-8로 저장해 주세요.") }
            try (ext == "md" ? separateTables(text) : text).write(to: copy, atomically: false, encoding: .utf8)
        } else {
            try FileManager.default.copyItem(at: source, to: copy)
        }

        let p = Process()
        p.executableURL = soffice
        // Own profile so a LibreOffice the user already has open doesn't swallow the job.
        let profile = FileManager.default.temporaryDirectory.appendingPathComponent("morphy-lo-profile")
        var args = ["-env:UserInstallation=\(profile.absoluteString)", "--headless", "--norestore", "--nolockcheck"]
        if ext == "csv" { args.append("--infilter=CSV:44,34,76,1") }
        if ext == "txt" { args.append("--infilter=Text (encoded):UTF8") }
        p.arguments = args + ["--convert-to", target, "--outdir", dir.path, copy.path]
        p.environment = ProcessInfo.processInfo.environment.merging(["FONTCONFIG_FILE": try fontConfig().path]) { $1 }
        p.standardOutput = FileHandle.nullDevice
        p.standardError = FileHandle.nullDevice
        token.process = p
        defer {
            token.process = nil
            // Running LibreOffice re-registers it with Launch Services every time; Morphy's own copy must stay hidden (D4).
            if soffice.path.hasPrefix(LibreOffice.root.path) { EngineInstaller.hide(LibreOffice.managed) }
        }
        try p.run()

        let deadline = Date().addingTimeInterval(120)
        while p.isRunning {
            if token.cancelled || Date() > deadline {
                p.terminate()
                let t = Date()
                while p.isRunning && Date().timeIntervalSince(t) < 3 { Thread.sleep(forTimeInterval: 0.1) }
                if p.isRunning { kill(p.processIdentifier, SIGKILL) }
                throw MorphyError("변환 시간이 초과됐어요. 다시 시도할 수 있어요.")
            }
            Thread.sleep(forTimeInterval: 0.1)
        }
        // The private copy sits in dir/in/, so every top-level file with the target extension is a result.
        let outs = ((try? FileManager.default.contentsOfDirectory(at: dir, includingPropertiesForKeys: nil)) ?? [])
            .filter { $0.pathExtension == format.ext }
            .sorted { $0.lastPathComponent < $1.lastPathComponent }
        guard p.terminationStatus == 0, !outs.isEmpty else { throw MorphyError("변환에 실패했어요. 다시 시도할 수 있어요.") }
        if format == .csv {  // BOM so Excel opens Korean UTF-8 CSV correctly
            for out in outs { try (Data([0xEF, 0xBB, 0xBF]) + Data(contentsOf: out)).write(to: out) }
        }
        return outs
    }

    /// LibreOffice's Markdown import starts a table only after a blank line; GitHub and most editors also accept a
    /// table right under a line of text. Adds that blank line before a table header, outside fenced code blocks.
    static func separateTables(_ text: String) -> String {
        let delimiter = #/^\|?\s*:?-+:?\s*(\|\s*:?-+:?\s*)*\|?$/#
        let lines = text.components(separatedBy: "\n")
        var out: [String] = []
        var fenced = false
        for (i, line) in lines.enumerated() {
            let trimmed = line.trimmingCharacters(in: .whitespacesAndNewlines)
            if trimmed.hasPrefix("```") || trimmed.hasPrefix("~~~") { fenced.toggle() }
            if !fenced, trimmed.contains("|"), i + 1 < lines.count, let previous = out.last,
               !previous.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
                let next = lines[i + 1].trimmingCharacters(in: .whitespacesAndNewlines)
                if next.contains("|"), next.wholeMatch(of: delimiter) != nil { out.append("") }
            }
            out.append(line)
        }
        return out.joined(separator: "\n")
    }

    /// UTF-8 first, then CP949 (EUC-KR superset).
    static func utf8Text(_ url: URL) -> String? {
        guard let data = try? Data(contentsOf: url) else { return nil }
        if let s = String(data: data, encoding: .utf8) { return s }
        let cp949 = CFStringConvertEncodingToNSStringEncoding(CFStringEncoding(CFStringEncodings.dosKorean.rawValue))
        return String(data: data, encoding: String.Encoding(rawValue: cp949))
    }

    /// Headless LibreOffice renders via fontconfig, which only knows its bundled fonts (no Hangul).
    /// Without this, Korean text is silently invisible in the PDF.
    private static func fontConfig() throws -> URL {
        let url = FileManager.default.temporaryDirectory.appendingPathComponent("morphy-fonts.conf")
        try """
        <?xml version="1.0"?>
        <!DOCTYPE fontconfig SYSTEM "fonts.dtd">
        <fontconfig>
          <dir>/System/Library/Fonts</dir>
          <dir>/Library/Fonts</dir>
          <dir>~/Library/Fonts</dir>
          <cachedir>~/Library/Caches/Morphy/fontconfig</cachedir>
        </fontconfig>
        """.write(to: url, atomically: true, encoding: .utf8)
        return url
    }
}
