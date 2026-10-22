# 문서 변환 엔진(LibreOffice) 내려받기 설계

- 작성일: 2026-10-22
- 상태: 구현됨 (2026-10-22, DECISIONS.md D4). 11장은 권장안으로 결정했다.
- 관련 PRD: 6.2 엔진 미준비 형식 비노출, 13.3 엔진 설치·업데이트, 14 배포, 15 로컬 처리, 21 엔진 설치 UX

## 1. 목표

- Morphy 앱(DMG 1MB 미만)에는 LibreOffice를 넣지 않는다. 처음 Office 문서를 변환할 때만 사용자 동의를 받아 내려받는다.
- 내려받은 엔진은 공식 배포본 그대로 쓴다. 수정하거나 다시 서명하지 않는다.
- 변조된 파일이나 엉뚱한 파일은 설치하지 않는다.
- 이미지와 PDF만 쓰는 사용자는 엔진의 존재를 몰라도 된다.

## 2. 확인한 사실 (2026-10-22)

| 항목 | 값 |
|---|---|
| 최신 안정판 | 26.8.1 (`/libreoffice/stable/` 목록에서 확인) |
| Apple Silicon DMG | `LibreOffice_26.8.1_MacOS_aarch64.dmg`, 298,845,477 bytes |
| Intel DMG | 같은 경로의 `mac/x86_64/` |
| 내려받기 주소 | `https://download.documentfoundation.org/libreoffice/stable/<ver>/mac/<arch>/<file>`. 요청하면 가까운 미러로 넘겨준다 (국내는 ftp.kaist.ac.kr). |
| 무결성 정보 | 같은 주소 뒤에 `.sha256`를 붙이면 SHA-256 해시를 준다. 이 해시는 미러가 아니라 TDF 본 서버가 준다. `.meta4`, PGP 서명 `.asc`도 있다. |
| 서명 | Developer ID Application: The Document Foundation (`7P5S3ZLCN7`), hardened runtime, 공증됨 |
| 공증 티켓 | 앱에 붙어 있지 않다(stapled 아님). 그래서 Gatekeeper가 공증을 확인할 때 인터넷 연결이 필요하다. |
| 설치 후 크기 | 805MB |
| 첫 변환 시간 | 새 사용자 프로필로 DOCX → PDF가 1.1초, 두 번째부터 0.7초. 설치 직후 미리 실행해 둘 필요 없음. |
| 오래된 버전 | 안정판 목록에는 최근 버전만 남는다. 지난 버전은 `downloadarchive.documentfoundation.org`로 옮겨진다. |

## 3. 엔진 찾는 순서

1. `/Applications/LibreOffice.app` (사용자가 직접 설치한 것). 26.8 이상일 때만 쓴다.
2. `~/Library/Application Support/Morphy/Engines.noindex/LibreOffice/<version>/LibreOffice.app` (Morphy가 설치한 엔진)

사용자가 직접 설치한 LibreOffice가 충분히 최신이면 내려받기를 제안하지 않는다. 시험용으로 `defaults write app.morphy.Morphy ignoreInstalledLibreOffice -bool YES`를 설정하면 1번을 건너뛴다.

## 4. 버전 고정 방식

앱 안에 엔진 정보 파일을 함께 넣는다.

```json
{
  "version": "26.8.1",
  "teamIdentifier": "7P5S3ZLCN7",
  "files": {
    "arm64":  { "name": "LibreOffice_26.8.1_MacOS_aarch64.dmg", "bytes": 298845477, "sha256": "ca074e0b…dfc" },
    "x86_64": { "name": "LibreOffice_26.8.1_MacOS_x86_64.dmg",  "bytes": 0, "sha256": "…" }
  }
}
```

- 해시를 앱 안에 고정해 두기 때문에, 실행 중에 받아 온 해시나 서버를 믿을 필요가 없다. 해시가 맞는 파일만 설치한다.
- 엔진 버전은 Morphy를 업데이트할 때만 올린다. 올리기 전에 테스트 문서 검사(`--selftest <corpus>`)를 통과해야 한다. 그래서 품질 기록(PRD 16)에 적힌 엔진 버전과 실제로 쓰는 엔진 버전이 항상 같다.
- 받을 주소는 ① `download.documentfoundation.org/libreoffice/stable/…`를 먼저 쓰고, 안 되면 ② `downloadarchive.documentfoundation.org/libreoffice/old/<ver>/…`를 쓴다.

## 5. 흐름

```
Office 문서 선택 + Option(⌥) 꾹 → 원형 메뉴 (형식마다 "엔진 필요" 표시) → 형식 선택
 └─ 엔진 없음 → 내려받기 안내창
      ├─ [나중에] → 아무것도 하지 않음. 다음에 다시 물어봄
      └─ [내려받기] → 공간 확인 → 내려받기 → 해시 확인 → 마운트 → 서명 확인
                     → 설치(원자적 rename) → 언마운트 및 DMG 삭제 → 요청했던 변환 실행
```

### 5.1 안내창 문구 (초안)

> **문서 변환 기능을 추가할까요?**
> Word·Excel·PowerPoint 문서를 변환하려면 이 기능을 한 번 내려받아야 해요. 약 300MB를 받고, 추가하면 805MB를 차지해요.
> 파일 내용은 외부로 보내지 않아요.
> 오픈소스 LibreOffice(The Document Foundation, MPL-2.0)를 공식 서버에서 받아 사용해요.
> [추가하기] [나중에]

- 버튼은 PRD 11의 메시지 원칙(원인과 다음 행동을 함께 제시)을 따른다.
- 안내창 아래에 작은 글씨로 라이선스를 표기한다: LibreOffice는 The Document Foundation이 배포하는 MPL-2.0 소프트웨어다.

### 5.2 진행 중

- 메뉴바 아이콘 옆에 `⬇︎ 45%`를 표시한다. 메뉴에는 "문서 엔진 내려받는 중 · 134/299MB"와 [취소]를 둔다.
- 진행 중에 Office 문서로 Shift를 누르면 "엔진을 내려받는 중이에요 (45%). 끝나면 바로 변환할 수 있어요."라고 안내한다.
- 이미지 변환과 PDF 변환은 영향을 받지 않는다.

### 5.3 완료

- 엔진 설치를 시작하게 한 변환 1건을 자동으로 실행한다. 사용자가 이미 그 변환을 하겠다고 고른 상태이기 때문이다.
- 알림: "문서 변환 엔진을 설치했어요. 보고서.docx를 PDF로 변환하는 중이에요."

## 6. 단계별 처리와 실패

| 단계 | 처리 | 실패할 때 안내 (다음 행동 포함) |
|---|---|---|
| 공간 확인 | 남은 공간이 DMG + 설치 크기 + 여유분, 약 **1.2GB** 이상인지 확인 | "저장 공간이 1.2GB 필요해요. 공간을 확보한 뒤 다시 시도해 주세요." |
| 내려받기 | `URLSessionDownloadTask`로 `~/Library/Caches/Morphy/`에 받는다. 끊기면 이어받기용 데이터(resumeData)를 저장한다. | "내려받기가 중단됐어요. [이어받기]" (네트워크 오류, 타임아웃) |
| 크기·해시 확인 | 파일 크기와 SHA-256이 엔진 정보 파일과 같은지 확인 | "받은 파일이 올바르지 않아요. [다시 받기]". 잘못 받은 파일은 즉시 삭제 |
| 마운트 | `hdiutil attach -nobrowse -readonly -mountpoint <임시 경로>` | "엔진 설치 파일을 열 수 없어요. [다시 받기]" |
| 서명 확인 | `codesign --verify --deep --strict`, TeamIdentifier가 `7P5S3ZLCN7`인지, `spctl --assess`가 통과하는지 확인 | "엔진의 서명을 확인할 수 없어요." 설치하지 않고 받은 파일을 삭제 |
| 복사 | `ditto`로 `Engines/LibreOffice/.installing-<uuid>/`에 복사한 뒤, 복사본의 서명을 다시 확인 | "엔진을 설치하지 못했어요. [다시 시도]" |
| 확정 | `<version>/` 이름으로 원자적 rename, 이전 버전 폴더 삭제 | 실패하면 `.installing-*` 폴더 정리 |
| 정리 | 언마운트, DMG 삭제 | 실패는 무시하고 다음 실행 때 정리 |

- **취소:** 어느 단계에서든 취소할 수 있다. 진행 중인 작업을 멈추고, 마운트를 풀고, 임시 파일을 지운다.
- **강제 종료:** 다음 실행 때 `.installing-*` 폴더, 남은 마운트, 이어받기 데이터를 정리한다. PRD 9.6의 임시 파일 정리와 같은 방식이다.
- **이어받기:** 남은 이어받기 데이터는 7일이 지나면 지운다.

## 7. 보안과 개인정보

- 사용자 파일, 파일명, 경로는 전혀 보내지 않는다. 요청은 TDF 서버와 미러로 가는 HTTPS GET뿐이다 (PRD 15).
- 해시(앱 안에 고정)와 Apple 서명·공증(TDF 팀 ID)을 둘 다 확인한다. 미러가 변조되더라도 둘 다 통과할 수는 없다.
- DMG 안의 어떤 것도 DMG에서 직접 실행하지 않는다. 확인을 마치고 복사한 앱만 실행한다.
- URLSession으로 받은 파일에는 macOS의 격리 표시(quarantine)가 붙지 않는다. 그래서 Gatekeeper 확인을 `spctl --assess`로 직접 한다.

## 8. 시스템에서 숨기기

엔진은 Morphy의 부품이다. 사용자가 두 번째 앱을 설치했다고 느끼면 안 된다.

- **Spotlight:** 엔진은 `Engines.noindex` 폴더에 둔다. 폴더 이름이 `.noindex`로 끝나면 Spotlight가 색인하지 않는다.
- **"다음으로 열기" (Launch Services):** 설치 직후 `lsregister -u`로 등록을 해제한다. LibreOffice는 **실행될 때마다 스스로 다시 등록**되기 때문에, 변환이 끝날 때마다 다시 해제한다. 실제로 확인한 동작이다.
- **훑어보기:** LibreOffice 안에 든 미리보기 확장 프로그램(QuickLookPreview, QuickLookThumbnail)은 `pluginkit -r`로 등록을 해제한다. 그래야 Finder 미리보기를 macOS가 계속 맡는다.
- **기존 설치 옮기기:** 앱을 실행할 때 예전 경로(`Engines/`)에 있는 엔진을 새 경로로 옮기고, 등록을 다시 해제한다.
- **문구:** 화면에는 "문서 변환 기능"이라고 쓴다. LibreOffice라는 이름은 안내창의 작은 고지와 라이선스 표기에만 쓴다.

## 9. 관리 메뉴

메뉴바 메뉴에 다음 항목을 추가한다.

- `문서 변환 엔진: LibreOffice 26.8.1 · 805MB` (사용자가 직접 설치한 엔진을 쓰면 `/Applications의 LibreOffice 사용 중`)
- [엔진 삭제]: 확인을 받은 뒤 휴지통으로 보낸다. 영구 삭제가 아니어서 되돌릴 수 있다.
- 엔진이 없을 때: [문서 변환 엔진 설치…]. 위 5.1과 같은 흐름이다.

## 10. 구현 범위 추정

| 구성 | 위치 | 대략적인 크기 |
|---|---|---|
| 엔진 정보 파일과 엔진 찾기 순서 | `Convert.swift`의 `Office.soffice` | 작음 |
| 내려받기, 확인, 설치 | 새 파일 `EngineInstaller.swift` (URLSession, hdiutil, codesign, spctl 프로세스 실행) | 약 200줄 |
| 안내창, 진행 표시, 관리 메뉴 | `App.swift` | 중간 |
| 테스트 | 해시 불일치 거부, 다른 팀 ID 거부, 설치 도중 강제 종료 후 정리 | 로컬에서 만든 가짜 DMG로 확인 |

## 11. 결정이 필요한 것

1. **설치 후 자동 실행**: 엔진 설치를 시작하게 한 변환을 자동으로 실행할지 (권장), 아니면 다시 Shift를 누르게 할지.
2. **사용자가 직접 설치한 LibreOffice 우선 사용**: 최소 버전 이상이면 그것을 쓸지 (권장, 내려받지 않아도 됨), 아니면 항상 Morphy가 받은 엔진만 쓸지 (품질이 일정함).
3. **최소 버전**: 26.8.x로 할지. 한글 PDF 띄어쓰기와 글꼴 처리를 검증한 버전이다.
4. **Intel 지원**: x86_64 해시도 엔진 정보 파일에 넣을지 (PRD 21의 Intel 지원 여부와 함께 결정).
5. **App Store 배포**: 내려받은 실행 코드를 실행하는 방식이라 App Store 심사 기준과 맞지 않는다. 이 방식을 택하면 사실상 직접 배포로 확정된다 (PRD 14).
