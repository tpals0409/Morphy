import AppKit

if CommandLine.arguments.contains("--selftest") { exit(SelfTest.run() ? 0 : 1) }
// Engine install outside the UI: `--install-engine` downloads like the app does; `--install-engine-from <dmg>`
// verifies and installs an already downloaded official DMG.
if CommandLine.arguments.count == 3, CommandLine.arguments[1] == "--install-engine-from" {
    do {
        try EngineInstaller.verifyAndInstall(dmg: URL(fileURLWithPath: CommandLine.arguments[2]), bytes: LibreOffice.bytes,
                                             sha256: LibreOffice.sha256, teamID: LibreOffice.teamID, as: LibreOffice.managed)
        print("installed", LibreOffice.managed.path)
        exit(0)
    } catch {
        print("refused:", (error as? MorphyError)?.message ?? "\(error)")
        exit(1)
    }
}
if CommandLine.arguments.count == 2, CommandLine.arguments[1] == "--install-engine" {
    var last = -1
    EngineInstaller.shared.onChange = {
        if case .downloading(let got) = EngineInstaller.shared.state, Int(got * 10 / Int64(LibreOffice.bytes)) != last {
            last = Int(got * 10 / Int64(LibreOffice.bytes))
            print("downloading \(last * 10)%")
        }
        if EngineInstaller.shared.state == .installing { print("verifying and installing") }
    }
    EngineInstaller.shared.install { error in
        print(error.map { "failed: \($0.message)" } ?? "installed \(LibreOffice.managed.path)")
        exit(error == nil ? 0 : 1)
    }
    RunLoop.main.run()
}
if CommandLine.arguments.count == 3, CommandLine.arguments[1] == "--render-menu" {
    RadialMenu.renderSnapshots(to: URL(fileURLWithPath: CommandLine.arguments[2]))
    exit(0)
}

// Morphy --convert <file> <PDF|PNG|JPG>: runs the real pipeline (saves beside the original) for corpus checks.
if CommandLine.arguments.count == 4, CommandLine.arguments[1] == "--convert", let format = Format(rawValue: CommandLine.arguments[3]) {
    let url = URL(fileURLWithPath: CommandLine.arguments[2])
    let result = Pipeline.run(url, to: format, saveIn: url.deletingLastPathComponent(), CancelToken())
    print(result)
    if case .done = result { exit(0) } else { exit(1) }
}

let app = NSApplication.shared
let delegate = AppDelegate()
app.delegate = delegate
app.setActivationPolicy(.accessory)
app.run()
