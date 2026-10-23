# Windows 빌드와 웹 배포

Windows는 미서명 0.1.0 베타를 일반 버전으로 배포한다. GitHub는 소스 및 빌드 결과 보관에 사용하고, 사용자 다운로드와 앱 업데이트는 `https://morphy.tpals0409dev.workers.dev`(Cloudflare Worker, R2, D1)로 제공한다.

## 1. GitHub Actions에서 설치 파일 받기

`Windows build and verification`은 main push, PR, 수동 실행을 지원한다. 수동 실행 시 `version`에 세 자리 버전(예: `0.1.0`)을 넣는다. .NET 빌드, 자체 검사, 실제 문서 변환, 설치 복구, Worker 다운로드 라우트 검사를 통과해야 아티팩트가 생성된다. 최초 다운로드에는 GitHub 로그인이 필요하며 아티팩트 보관 기간은 30일이다.

Mac에서 저장소를 받은 뒤 Actions의 성공한 실행을 골라 다운로드한다. 웹 UI의 Artifacts 다운로드도 가능하다.

```sh
git pull --ff-only
gh run list --repo tpals0409/Morphy --workflow windows.yml --branch main
gh run view RUN_ID --repo tpals0409/Morphy
gh run download RUN_ID --repo tpals0409/Morphy --dir ./windows-artifact
```

반드시 원하는 커밋의 **성공한 실행**을 선택한다. 아티팩트 이름은 `Morphy-Windows-버전-커밋SHA`다. 내부 `windows-release/`에 설치 EXE, SHA256SUMS.txt, windows-release.json, LGPL 코덱 대응 소스 ZIP과 설치 검사 기록이 있고, `windows-integration/report.json`에 변환 결과가 있다. 설치 EXE는 Git 소스에 포함되지 않는다.

```sh
python3 scripts/publish-release.py --artifact ./windows-artifact
```

이 명령은 기본적으로 검증만 한다. Python 3.11 이상이 필요하다.

## 2. 웹사이트 구조

Mac과 Windows는 같은 Worker `morphy`(저장소 루트의 `wrangler.jsonc`, `src/index.js`)로 배포한다. 파일은 R2 버킷 `morphy-releases`에, 현재 버전 정보는 D1 `morphy`의 `releases` 표에 있다. 표에서 플랫폼별로 가장 최근 줄이 현재 버전이다.

| 경로 | 동작 |
| --- | --- |
| `/download`, `/api/release` | Mac 최신 DMG로 이동, Mac 버전·경로·SHA-256·bytes |
| `/download/windows`, `/api/release/windows` | Windows 최신 설치 파일로 이동, Windows 버전 정보 (앱 업데이트 확인에도 사용) |
| `/releases/<파일>` | R2에서 DMG, 설치 EXE, 코덱 대응 소스 ZIP을 내려받기 |

D1에 줄이 있어도 R2의 파일 크기가 맞지 않으면 503을 돌려준다. 업로드가 끝나지 않은 파일을 안내하지 않기 위해서다. 아직 Windows 줄이 없으면 API는 404이고, 홈페이지는 "Windows 개발 중"을 그대로 보여 준다. 줄이 생기면 Windows 다운로드 버튼과 안내 문구, 코덱 소스 링크가 나타난다.

## 3. 게시

저장소 루트에서, 로그인된 Wrangler(`npx wrangler login`)로 실행한다. 설치 파일과 대응 소스를 R2에 먼저 올리고, 모두 성공한 뒤 마지막으로 D1에 줄을 넣는다. 이미 게시한 버전이면 아무것도 올리지 않고 멈춘다.

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
