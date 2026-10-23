# Windows 빌드와 웹 배포

Windows는 미서명 베타 설치본을 배포한다. GitHub는 소스 및 빌드 결과 보관에 사용하고, 사용자 다운로드와 앱 업데이트는 `https://morphy.tpals0409dev.workers.dev`(Cloudflare Worker, R2, D1)로 제공한다.

## 1. main 자동 빌드·배포

`Windows build and release`는 main push에서 다음 순서로 실행한다.

1. 앱 프로젝트의 기준 버전(현재 0.1.4)과 `/api/release/windows`의 게시 버전 중 높은 버전에서 patch를 1 올린다. 첫 배포는 0.1.5이며 이후 0.1.6, 0.1.7처럼 증가한다. 소스 버전을 자동 커밋하지 않으므로 CI 루프가 생기지 않는다.
2. 앱 자체 검사, 실제 Office·한국어 문서 변환, 설치 검사, Worker 라우트 검사와 배포 스크립트 검사를 실행한다.
3. 결정한 버전을 EXE 및 설치본에 넣고 GitHub Actions artifact를 만든다.
4. R2에 설치본과 대응 소스를 업로드한 뒤, 마지막에 D1의 Windows 릴리스 정보를 갱신한다.
5. 공개 API, 웹 다운로드 링크, 실제로 내려받은 EXE의 크기·SHA-256까지 확인한다.

웹 다운로드와 앱의 **업데이트 확인…**은 같은 `/api/release/windows` 정보를 사용한다. 게시가 끝나면 기존 앱은 다음 확인에서 새 버전을 안내하고, 사용자 동의 후 설치한다. PC에 설치할 때마다 서버 버전을 수동 입력할 필요가 없다. Mac DMG 배포는 기존 절차를 유지한다.

### 인증 가능한 PC에서 한 번 설정

[저장소 Actions Secrets](https://github.com/tpals0409/Morphy/settings/secrets/actions)에 다음 repository secrets를 등록한다.

| 이름 | 값 |
| --- | --- |
| `CLOUDFLARE_API_TOKEN` | 배포 계정 범위의 `Workers R2 Storage: Edit`, `D1: Edit` 권한을 가진 API 토큰 |
| `CLOUDFLARE_ACCOUNT_ID` | `wrangler.jsonc`의 R2·D1이 속한 Cloudflare 계정 ID |

Wrangler 로컬 로그인은 GitHub 러너에 전달되지 않으므로 Secrets 등록이 필요하다. 토큰은 Git/채팅/문서에 저장하지 않는다. 기존 D1 migration 0002까지 적용된 계정이어야 한다. CI는 Worker를 다시 배포하지 않고 R2·D1의 Windows 릴리스만 게시한다.

Secrets 미등록 시 빌드 artifact는 남지만 publish 작업은 명시적인 오류로 실패한다. 등록 후 해당 실행의 **Re-run failed jobs**를 누르거나 main에서 workflow를 수동 실행한다. 수동 `version`은 비워 두면 자동 증가하며, 필요할 때만 현재 버전보다 높은 세 자리 버전을 지정한다.

main 릴리스 작업은 직렬 실행한다. 빌드 중 main에 새 커밋이 생기면 이전 빌드는 게시하지 않고 최신 main 실행이 게시한다. PR과 main 외 브랜치의 수동 실행은 검증·artifact 생성만 하며 배포 인증 정보를 사용하지 않는다. 동일 artifact의 게시 재시도는 이미 등록된 내용과 정확히 일치하면 성공 처리하고, 같은 버전의 다른 파일이나 이전 버전으로의 되돌림은 거부한다.

### artifact를 수동으로 게시해야 할 때

성공한 main 빌드의 artifact 이름은 `Morphy-Windows-버전-커밋SHA`이며 30일간 보관한다. 설치 EXE는 Git에 포함하지 않는다.

```sh
git pull --ff-only
gh run list --repo tpals0409/Morphy --workflow windows.yml --branch main
gh run download RUN_ID --repo tpals0409/Morphy --dir ./windows-artifact
python3 scripts/publish-release.py --artifact ./windows-artifact
```

검증만 수행하며 Python 3.11 이상이 필요하다. 인증된 PC에서 실제 게시하려면 아래 3절을 따른다.

## 2. 웹사이트 구조

Mac과 Windows는 같은 Worker `morphy`(저장소 루트의 `wrangler.jsonc`, `src/index.js`)로 배포한다. 파일은 R2 버킷 `morphy-releases`에, 현재 버전 정보는 D1 `morphy`의 `releases` 표에 있다. 표에서 플랫폼별로 가장 최근 줄이 현재 버전이다.

| 경로 | 동작 |
| --- | --- |
| `/download`, `/api/release` | Mac 최신 DMG로 이동, Mac 버전·경로·SHA-256·bytes |
| `/download/windows`, `/api/release/windows` | Windows 최신 설치 파일로 이동, Windows 버전 정보 (앱 업데이트 확인에도 사용) |
| `/releases/<파일>` | R2에서 DMG, 설치 EXE, 코덱 대응 소스 ZIP을 내려받기 |

D1에 줄이 있어도 R2의 파일 크기가 맞지 않으면 503을 돌려준다. 업로드가 끝나지 않은 파일을 안내하지 않기 위해서다. 아직 Windows 줄이 없으면 API는 404이고, 홈페이지는 "Windows 개발 중"을 그대로 보여 준다. 줄이 생기면 Windows 다운로드 버튼과 안내 문구, 코덱 소스 링크가 나타난다.

## 3. 게시

저장소 루트에서, 로그인된 Wrangler(`npx wrangler login`)로 실행한다. 설치 파일과 대응 소스를 R2에 먼저 올리고, 모두 성공한 뒤 마지막으로 D1에 줄을 넣는다. 동일 버전·동일 내용으로 재시도하면 아무것도 바꾸지 않고 성공 처리한다. 동일 버전의 다른 파일이나 이미 게시된 버전보다 낮은 버전은 거부한다.

```sh
python3 scripts/publish-release.py --artifact ./windows-artifact --publish
```

이미 공개한 버전의 EXE를 다른 내용으로 덮어쓰지 않는다. 수정 배포는 `0.1.1`처럼 버전을 올려 다시 빌드한다. 업데이트는 설치 버전보다 높은 버전만 표시한다. 롤백은 잘못된 버전의 D1 줄을 지우면 바로 이전 버전이 현재 버전이 된다. 이미 설치한 사용자의 앱을 자동 다운그레이드하지는 않는다.

```sh
npx wrangler d1 execute morphy --remote --command "DELETE FROM releases WHERE platform = 'windows' AND version = '0.1.1'"
```

## 4. 배포 확인

```sh
curl -fsS https://morphy.tpals0409dev.workers.dev/api/release
curl -fsS https://morphy.tpals0409dev.workers.dev/api/release/windows
curl -I https://morphy.tpals0409dev.workers.dev/download
curl -I https://morphy.tpals0409dev.workers.dev/download/windows
```

Mac 링크가 여전히 DMG를 가리키는지, Windows 링크가 EXE를 가리키는지 확인한다. Windows EXE를 실제 내려받아 JSON의 SHA-256과 비교하고 깨끗한 Windows 11 계정에서 설치한다. 문서 엔진이 없는 PC에서 다운로드→검증→설치→앱 실행을 확인한다. 다음 버전 배포 때는 이전 앱에서 업데이트 알림, 설정·엔진 보존, 사용자 선택 후 설치까지 확인한다. 현재 코드 서명은 적용하지 않았다.

참고: [GitHub 아티팩트 다운로드](https://docs.github.com/en/actions/how-tos/manage-workflow-runs/download-workflow-artifacts), [Cloudflare R2 Worker API](https://developers.cloudflare.com/r2/api/workers/workers-api-reference/), [Cloudflare D1](https://developers.cloudflare.com/d1/).

## 기본 설치 경험

Windows 설치 파일을 실행하면 별도의 기능 선택 질문 없이 Morphy 설치를 시작한다. 이미지·문서 변환에 필요한 구성 요소를 모두 기본으로 준비하며, 이미 설치된 문서 엔진은 재사용한다. 설치 중에는 취소할 수 있고, 필요한 구성 요소 준비에 실패하면 설치를 완료한 것으로 처리하지 않는다.

## SignPath Foundation 신청 준비

현재 상태는 **신청 준비 중**이다. 인증서 발급이나 프로덕션 서명 활성화가 완료된 상태가 아니다.

- 신청: https://signpath.org/apply.html
- 자격·정책: https://signpath.org/terms.html
- 공개 정책 초안: `web/code-signing.html` (푸시·웹 배포 후 `/code-signing.html`)
- 소유자 결정에 따라 Morphy 자체 코드에는 루트 `LICENSE`의 MIT 라이선스를 적용했다. 외부 구성 요소의 라이선스는 `windows/THIRD-PARTY.md`를 따른다.
- 신청 담당자의 이름·이메일, MFA 설정, 코드 서명 승인 담당자 및 약관 동의는 소유자가 확인해야 한다. GitHub noreply 주소를 신청 연락처로 사용하지 않는다.
- 신청서에는 초기 프로젝트라는 사실과 공개 GitHub Actions 검사 기록을 기재하며, 확인되지 않은 사용자 수나 평판을 주장하지 않는다.

### 승인 후 CI 연결 순서

SignPath가 발급한 organization ID, project slug, artifact configuration, signing policy 및 GitHub 연동 권한이 확정된 후 활성화한다. 현재 무서명 배포 CI에 임의의 ID나 활성화 플래그를 넣지 않는다.

1. 공식 GitHub Actions 빌드의 `Morphy.exe`를 먼저 서명하고, 유효한 Authenticode 서명을 확인한다. 외부 DLL을 Morphy 소유 코드로 재서명하지 않는다.
2. **서명된 앱** 기준으로 `bundle-manifest.json`을 생성하고 설치본 payload를 만든다. 앱 서명 후 기존 manifest를 재사용하면 설치 중 해시 검증이 실패한다.
3. 설치 EXE도 서명하고 확인한 뒤 최종 `SHA256SUMS.txt`와 `windows-release.json`의 크기·해시를 생성한다.
4. SignPath 정책에 필요한 승인 절차가 끝난 서명본만 R2에 게시한다. 서명 실패 시 무서명 파일로 대신 게시하지 않는다.
5. CI의 수동 승인 대기 시간과 timeout을 검토한다. main push 후 빌드는 자동화하되, Foundation의 승인 정책을 우회하지 않는다.
6. 기존 앱의 `Updates.Download`은 현재 앱이 서명되어 있으면 다음 설치본의 게시자 일치를 검사한다. 서명된 앱→서명된 업데이트까지 검증한 뒤 정책 페이지의 준비 중 상태를 갱신한다.

공식 연동 문서: https://docs.signpath.io/trusted-build-systems/github
