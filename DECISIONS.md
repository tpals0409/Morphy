# Decisions

PRD 22 asks for changes to the core invocation to be recorded here.

## D1 · 2026-10-21 · Shift long-press replaces Shift tap (release behaviour and timing superseded by D2)

- **Before (PRD 5.4 proposal):** press and release Shift alone, and the menu opens on release.
- **Now:** hold Shift alone for 0.25s, and the menu opens while Shift is still held. Releasing Shift afterwards does nothing.
- **Why:** product owner's call. A quick tap is easy to make by accident, so requiring a deliberate hold avoids opening the menu unintentionally.
- **Conflict rules:** any key, click, drag or scroll cancels the hold, as does moving the pointer more than 24pt (the user is probably aiming a Shift-click) or pressing another modifier.
- **Open:** tune the 0.25s / 24pt values in beta.
- **2026-10-21 tuning:** the first values (0.4s / 6pt) cancelled about half of real holds within 30–150ms, because a hand resting on the mouse or trackpad drifts the pointer. Changed to 0.25s / 24pt. Finder's selection is now read at Shift-down, so its 50–115ms AppleScript call overlaps the hold instead of adding to it.

## D2 · 2026-10-22 · Hold, point and release to run (marking menu) (release-to-run superseded by D5)

Replaces D1's "releasing Shift afterwards does nothing" and the hold time. It also changes PRD 5.4: "Shift를 놓는 것만으로 변환을 실행하지 않는다" and "메뉴가 열린 뒤 Shift를 계속 누를 필요가 없다".

- **Open:** hold Shift alone for **0.15s** (was 0.25s). Before the menu shows, D1's cancel rules still apply: any key, click, drag, scroll or extra modifier, or moving the pointer more than 24pt (the user is probably aiming a Shift-click).
- **Point:** with Shift held, moving the pointer **30pt or more** from where the menu opened selects the ring item nearest that direction. The pointer doesn't need to reach the button, and moving past the ring keeps the selection. Directions are measured from the opening point, not the panel centre, so a menu pushed in from a screen edge still works.
  - Arrow keys select too; nothing is selected at first.
  - Each selection change gives a Force Touch haptic tick.
- **Release Shift:**
  - with a format selected → convert immediately;
  - with nothing selected (pointer back in the middle) → close;
  - on "더 보기" → its list stays open and is used with click or arrows + Enter. This is the only place where Shift no longer needs to be held.
- **Also:** a click on an item while holding runs it immediately, and Esc closes.
- **Look:** a donut of six 60° sectors, with unused slots drawn faint. The appear animation is 0.06s.
- **Why:** the product owner chose it. One continuous gesture, and once the slots are learned it works without looking. The opening animation and the 0.25s wait felt slow.
- **Safety:** releasing Shift can now run a conversion, but only after the user has pointed in a direction (or picked with the arrow keys). A wrong direction only adds a new file beside the original; nothing is overwritten (PRD 10.2).
- **Bug found while doing this:** while the menu is the key window, Shift-up goes to Morphy's panel instead of the global monitor. The trigger kept believing Shift was still down, so every other press after using the menu was ignored. It now judges each event by its own key code instead of remembered state. Every press that doesn't open the menu now logs the reason.

## D3 · 2026-10-22 · One file at a time

Narrows PRD 4 (MVP: "다중 파일 일괄 변환"), F03 (multiple selection), F05 (outputs common to every selected file), 6.1 (centre summary for several files) and 9.3.

- **Now:** the menu opens only when exactly one item is selected. With several items selected, Shift does nothing: no menu and no alert, the same as an unsupported format. The reason is logged.
- **Why:** the product owner's call, to keep the first version simple. With one file there is no question of which outputs every file shares, and nothing can be left out without the user knowing.
- **What stays:** the queue still runs one job after another, so starting another conversion while one is running works. A single conversion can still produce several files (one CSV per sheet, one image per PDF page).
- **Removed:** the "N개 파일" centre summary, the "no common format" alert and the 50-file limit. Restore them together if batch conversion comes back.

## D4 · 2026-10-22 · LibreOffice is downloaded on first use

Settles PRD 13.3 / 21 (engine bundling, install and update UX). The full design is in [docs/engine-download.md](docs/engine-download.md).

- **Download, don't bundle:** Morphy itself stays under 1MB. LibreOffice 26.8.1 for arm64 is downloaded only when the user agrees, and installed to `~/Library/Application Support/Morphy/Engines/LibreOffice/`.
- **Pinned and verified:** the version, size and SHA-256 live inside Morphy. A download installs only if the size and hash match, the Apple signature belongs to The Document Foundation (`7P5S3ZLCN7`) and Gatekeeper accepts it. Nothing in the DMG is run, and the copy is re-checked before an atomic rename.
- **Existing install first:** a LibreOffice already in /Applications at version 26.8 or later is used, and nothing is downloaded.
- **Auto-run:** the conversion that triggered the download runs as soon as the install finishes.
- **Ring:** without the engine, Office outputs still appear on the ring with a small "엔진 필요" label. Picking one asks to download first. This bends PRD 6.2 ("엔진이 준비되지 않은 형식은 실행 가능한 항목으로 표시하지 않는다"). The item is labelled as not ready and never runs without the engine, but the user must be able to choose the format the download is for, so it can run automatically afterwards.
- **Hidden, worded as a feature:** the engine lives in `Engines.noindex/` (out of Spotlight). It is unregistered from Launch Services and PlugInKit after install and after every conversion, because LibreOffice re-registers itself each time it runs. The UI calls it "문서 변환 기능", and LibreOffice is named only in the consent dialog's licence line.
- **arm64 only for now:** Intel waits for the PRD 21 decision.
- **Consequence:** downloading and running executable code rules out the Mac App Store, so Morphy will be distributed directly (PRD 14).

## D5 · 2026-10-22 · Click runs, releasing Shift cancels (superseded by D8)

Replaces D2's "release Shift → run". Holding Shift for 0.15s still opens the menu (D1/D2).

- **Now:** the menu lives while Shift is held. A format runs only on a **click** on its sector (or Enter after choosing with the arrow keys). **Releasing Shift closes the menu without running anything** (cancel), and so do Esc, a click outside and an app switch. Clicking "더 보기" swaps in its ring, which also closes when Shift is released.
- **Highlight:** the highlighted sector is always the one under the pointer, measured from the ring's centre. The hole, an empty slot and anything outside the donut highlight nothing. A click therefore always does what the highlight shows. The haptic tick on each change stays.
- **Why:** the product owner found that releasing Shift created files they didn't mean to. A click is an explicit act; letting go of a key isn't. Letting go stays the quick way to back out.
- **Removed:** direction measured from the opening point and the 30pt dead zone. PRD 5.4's "Shift를 놓는 것만으로 변환을 실행하지 않는다" holds again. Its "메뉴가 열린 뒤 Shift를 계속 누를 필요가 없다" does not: Shift is held until the click.

## D6 · 2026-10-22 · Option (⌥) instead of Shift

Changes the key in PRD 5.4 and D1/D2/D5. Everything else about the gesture stays: hold 0.15s alone to open, click to run, release to cancel.

- **Now:** hold **Option** (left or right) alone. Releasing Option cancels.
- **Why:** to select several files in Finder you hold a key first and then click: Shift for a range, ⌘ to add, Control for the context menu. If the user pauses over a file before clicking, the menu opened first. Shift collided with exactly that. Option isn't used for selecting. Its Finder uses (⌥-drag to copy, ⌥-click on a list disclosure triangle) start with a drag or a click, which already cancels the hold, and typing ⌥ characters while renaming is excluded by the text-field check.
- **Rejected:** fn/🌐, which is bound to system actions (emoji, input source). A double-tap (⌥⌥) doesn't fit hold-to-keep / release-to-cancel.
- **Later:** PRD 5.4's configurable shortcut could offer "right Option only" for anyone who uses left ⌥ heavily.
- **Note:** the on/off preference key moved from `shiftDisabled` to `triggerDisabled`, so a previous "off" doesn't carry over.

## D7 · 2026-10-22 · A click outside the donut cancels and is swallowed

- **Now:** while the menu is open, every display is covered by a nearly invisible layer (alpha 0.001). A click anywhere outside the donut's sectors (elsewhere on screen, in the hole, in a gap between sectors), whether left, right or other button, closes the menu without running anything, and **the click never reaches Finder**, so files under it are not selected or deselected.
- **Why:** before, the click that dismissed the menu also fell through to Finder and changed the selection the user had just made.
- **Kept:** releasing Option, Esc and switching apps still cancel (D5). Clicking a sector still runs it.
- **Side effect:** while the menu is open, Finder can't be clicked, scrolled or hovered. That is intended for a modal pop-up and lasts only while Option is held.

## D8 · 2026-10-22 · Release over a sector runs it

Replaces D5's "only a click runs". Opening (hold Option 0.15s, D6) and outside clicks (D7) are unchanged.

- **Now:** with Option held, move the pointer onto a sector and **let go of Option**: that format runs. Letting go in the hole or outside the donut cancels. A click on a sector still runs it, and Esc and app switches still cancel.
- **Why not D2 again:** D2 selected by direction from where the menu opened, so a 30pt drift could pick a sector, and letting go then created files by accident. Now the selection is the sector physically under the pointer. The pointer starts in the 58pt hole and has to be moved onto the ring, and only real mouse moves select: a menu pushed in from a screen edge with the pointer already over a sector doesn't run on an immediate release.
- **Why not D5:** the product owner found clicking while holding a key awkward on a trackpad.
- **"더 보기":** letting go on it swaps in its ring and keeps the menu open (Option is up by then). That ring is used with clicks or arrows + Enter.

## D9 · 2026-10-23 · Update from the menu bar menu

Answers docs/release.md's open "앱 자동 업데이트" item without Sparkle.

- **Now:** Morphy checks `https://morphy.tpals0409dev.workers.dev/api/release` at launch and every 6 hours. When the site has a higher version, the menu bar menu shows **"업데이트 설치 (0.1.0 → 0.2.0)…"**; otherwise it shows **"업데이트 확인 · 현재 0.1.0"**, which checks on click. Installing asks first, refuses while conversions or the engine download run, downloads the DMG, replaces the app where it is, and relaunches.
- **Safety, same rules as the Windows app:** only a higher three-part version; only `/releases/Morphy-X.Y.Z.dmg` on our own site; the site's size and SHA-256; the new app must pass `codesign --verify --deep --strict` with the running app's TeamIdentifier, so Accessibility and Automation grants carry over and no one else's build can replace it; a Gatekeeper-approved install never accepts an update Gatekeeper refuses. Nothing in the DMG runs before the swap, and a failed check leaves the app untouched.
- **Why not Sparkle:** it would add a framework, an EdDSA key to keep and an appcast to publish, while the website already serves the version, hash and file that the Windows updater uses.
- **Limit:** the swap needs write access to the app's folder. A standard (non-admin) user with Morphy in /Applications gets an error asking to check permissions.
- **Testing:** `Morphy --update -updateOrigin http://localhost:8787` runs the same check and install against `wrangler dev`.
