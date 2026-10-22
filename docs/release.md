# 웹 배포 (DMG)

Morphy는 App Store가 아니라 웹에서 DMG로 배포한다. 문서 변환 엔진을 처음 쓸 때 내려받는 방식(D4)을 택했기 때문에 App Store 심사 기준과 맞지 않는다.

이 방식으로 배포하면 다른 Mac에서도 경고 없이 열린다. 이를 위해 세 가지가 필요하다.

1. **Developer ID Application** 인증서로 서명
2. 강화 런타임(hardened runtime)과 서명 시각(timestamp)
3. Apple 공증(notarization)과 공증 확인서 첨부(stapling)

2번은 이미 모든 빌드에 적용돼 있다. 1번과 3번은 아래 처음 한 번 준비를 마치면 `./build.sh --release`가 처리한다.

## 처음 한 번 준비

### 1. Developer ID Application 인증서

- 유료 Apple Developer Program(연 $99)에 가입해야 한다. 인증서는 계정 소유자(Account Holder)만 만들 수 있다.
- 지금 키체인에는 개발용 `Apple Development` 인증서만 있다. 이 인증서로는 다른 Mac에서 Gatekeeper를 통과할 수 없다.
- 만드는 방법:
  1. developer.apple.com → Certificates → **+** → "Developer ID Application"을 고른다.
  2. CSR 파일은 키체인 접근 → 인증서 지원 → "인증 기관에서 인증서 요청"으로 만든다.
  3. 내려받은 `.cer` 파일을 더블클릭해 키체인에 설치한다.
- 확인: 아래 명령을 실행하면 `Developer ID Application: …` 줄이 보여야 한다.

```bash
security find-identity -v -p codesigning
```

### 2. 공증용 앱 암호를 키체인에 저장

1. appleid.apple.com → 로그인 및 보안 → **앱 암호**에서 새 암호를 만든다.
2. 다음 명령을 한 번 실행한다. 이메일, 팀 ID, 앱 암호를 입력한다. 키체인에 `morphy-notary`라는 이름으로 저장된다.

```bash
xcrun notarytool store-credentials morphy-notary --apple-id "<Apple ID 이메일>" --team-id "<팀 ID>"
```

- 팀 ID는 Developer ID 인증서 이름 끝 괄호 안에 있는 값이다.
- 앱 암호는 터미널 프롬프트에 직접 입력한다. 채팅이나 파일에 적지 않는다.

## 배포할 때마다

1. `Info.plist`의 `CFBundleShortVersionString`(예: 0.1.0 → 0.2.0)과 `CFBundleVersion`(빌드 번호)을 올린다.
2. 실행한다.

```bash
./build.sh --release
```

`--release`가 하는 일:

1. self-test를 돌린다. 실패하면 중단한다.
2. Developer ID로 서명한다. 강화 런타임, 서명 시각, `Morphy.entitlements`를 함께 적용한다.
3. `build/Morphy-<버전>.dmg`를 만들고 DMG도 서명한다.
4. `notarytool submit --wait`로 공증을 받는다. 보통 몇 분 걸린다.
5. 공증 확인서를 DMG에 붙인다(`stapler staple`). 그러면 인터넷이 없어도 Gatekeeper를 통과한다.
6. `spctl`로 Gatekeeper 통과 여부를 확인하고, 웹 페이지에 올릴 SHA-256을 출력한다.

공증이 거절되면 `xcrun notarytool log <제출 ID> --keychain-profile morphy-notary`로 이유를 본다.

## 권한(entitlements)

| 권한 | 이유 |
|---|---|
| `com.apple.security.automation.apple-events` | Finder에서 선택한 파일을 읽는다. 강화 런타임은 이 권한이 없으면 Apple Events를 막는다. |

LibreOffice, `cwebp`, `pdftotext`는 별도 프로세스로 실행하므로 추가 권한이 필요 없다. 샌드박스는 쓰지 않는다. 쓰려면 App Store 배포가 전제된다.

## 배포 전 확인

- [ ] 다른 Mac(또는 새 사용자 계정)에서 DMG를 웹으로 내려받아 연다. 경고 없이 열리는지 확인한다. 브라우저로 받은 파일에 붙는 격리 표시가 있는 상태로 시험해야 한다.
- [ ] 처음 실행할 때 손쉬운 사용 권한과 Finder 제어 권한 요청이 뜨는지 확인한다.
- [ ] 이미지 변환이 되는지, 문서 변환 기능 추가 흐름이 동작하는지 확인한다.
- [ ] 웹 페이지에 버전, SHA-256, 최소 macOS 14, Apple Silicon 전용을 표기한다.

## 아직 정하지 않은 것

- **앱 자동 업데이트:** 웹 배포에는 App Store 업데이트가 없다. 업데이트 확인 기능이 필요하다(예: Sparkle, PRD 14·21).
- **cwebp, pdftotext:** 지금은 Homebrew에서 찾는다. 다른 Mac에서는 WEBP 저장이 메뉴에 나오지 않고, PDF → TXT는 PDFKit으로 대신한다(띄어쓰기 품질이 떨어질 수 있다). libwebp를 앱에 넣고 PDFium으로 바꾸는 일은 별도로 진행한다.
