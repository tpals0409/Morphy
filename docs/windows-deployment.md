# Windows 빌드와 Mac에서의 R2 배포

Windows는 미서명 0.1.0 베타를 일반 버전으로 배포한다. GitHub는 소스 및 빌드 결과 보관에 사용하고, 사용자 다운로드와 앱 업데이트는 기존 `https://morphy.tpals0409dev.workers.dev` 및 R2로 제공한다. 이 작업은 Mac Worker 원본에 직접 접근하지 않으므로 아래 라우트 모듈을 기존 프로젝트에 통합해야 한다.

## 1. GitHub Actions에서 설치 파일 받기

`Windows build and verification`은 main push, PR, 수동 실행을 지원한다. 수동 실행 시 `version`에 세 자리 버전(예: `0.1.0`)을 넣는다. .NET 빌드, 자체 검사, 실제 문서 변환, 설치 복구, Worker 라우트 검사를 통과해야 아티팩트가 생성된다. 최초 다운로드에는 GitHub 로그인이 필요하며 아티팩트 보관 기간은 30일이다.

Mac에서 저장소를 받은 뒤 Actions의 성공한 실행을 골라 다운로드한다. 웹 UI의 Artifacts 다운로드도 가능하다.

```sh
git pull --ff-only
gh run list --repo tpals0409/Morphy --workflow windows.yml --branch main
gh run view RUN_ID --repo tpals0409/Morphy
gh run download RUN_ID --repo tpals0409/Morphy --dir ./windows-artifact
```

반드시 원하는 커밋의 **성공한 실행**을 선택한다. 아티팩트 이름은 `Morphy-Windows-버전-커밋SHA`다. 내부 `windows-release/`에 설치 EXE, SHA256SUMS.txt, windows-release.json, LGPL 코덱 대응 소스 ZIP과 설치 검사 기록이 있고, `windows-integration/report.json`에 변환 결과가 있다. 설치 EXE는 Git 소스에 포함되지 않는다.

```sh
python3 /path/to/Morphy/scripts/publish-windows-r2.py --artifact /path/to/windows-artifact
```

이 명령은 기본적으로 검증만 한다. Python 3.11 이상이 필요하다.

## 2. 기존 Mac Worker에 Windows 라우트 추가

`deployment/cloudflare/windows-routes.mjs`를 **기존 Worker 프로젝트**로 복사한다. 아래는 연결 예시이며 기존 Worker 전체를 대체하는 코드가 아니다. `env.EXISTING_R2_BINDING`은 현재 프로젝트에 있는 실제 R2 바인딩 이름으로 바꾼다. 버킷 이름이나 바인딩을 새로 추측해서 만들지 않는다.

```js
import { handleWindows } from './windows-routes.mjs';

// 기존 fetch(request, env, ctx)의 맨 앞에 추가:
const windowsResponse = await handleWindows(request, env.EXISTING_R2_BINDING);
if (windowsResponse) return windowsResponse;
// 이 아래의 기존 홈페이지/Mac 라우팅은 그대로 유지
```

| 경로 | 동작 |
| --- | --- |
| `/download`, `/api/release`, 기존 DMG | 기존 Mac 처리 그대로 |
| `/download/windows` | 현재 Windows 설치 파일로 리다이렉트 |
| `/api/release/windows` | R2 `windows/latest.json`의 버전·경로·SHA-256·bytes 제공 |
| `/releases/Morphy-X.Y.Z-win-x64-Setup.exe` | 해당 R2 파일 스트리밍 |
| `/releases/WicHeic-v1.1.16-corresponding-source.zip` | 대응 코덱 소스 제공 |

홈페이지에는 기존 Mac 버튼을 유지하고 `/download/windows`로 연결되는 Windows 버튼을 추가한다. 옆에 `Windows 11 x64 · 0.1.0 베타 · 미서명`을 표시하고, Windows 보안 경고가 표시될 수 있음을 설명한다. 설치 시 문서 엔진 약 375MB를 추가 다운로드하며 인터넷 연결과 2GB 이상의 여유 공간이 필요하다는 점도 안내한다. 코덱 소스 링크를 다운로드 영역에 함께 제공한다.

기존 Worker의 Wrangler 설정·R2 바인딩을 유지한 채 배포한다. 아직 Windows 메타데이터가 없으면 새 API는 404이며 Mac 배포에는 영향을 주지 않는다.

```sh
cd /path/to/existing-worker
npx wrangler deploy --dry-run
npx wrangler deploy
```

## 3. R2에 Windows 파일 업로드

기존 Worker 프로젝트에서 로그인된 Wrangler를 사용한다. 아래 스크립트는 설치 파일과 대응 소스를 먼저 올리고, 모두 성공한 뒤 **Windows 전용 메타데이터만 마지막으로** 올린다. 기존 Mac `/api/release`용 객체는 수정하지 않는다.

```sh
cd /path/to/existing-worker
npx wrangler whoami
python3 /path/to/Morphy/scripts/publish-windows-r2.py \
  --artifact /path/to/windows-artifact \
  --bucket ACTUAL_EXISTING_BUCKET_NAME --publish
```

이미 공개한 버전의 EXE를 다른 내용으로 덮어쓰지 않는다. 수정 배포는 `0.1.1`처럼 버전을 올려 다시 빌드한다. 업데이트는 설치 버전보다 높은 버전만 표시한다. 롤백은 이전의 검증된 JSON을 `windows/latest.json`에 다시 올리면 신규 다운로드 안내가 이전 파일을 가리키지만, 이미 설치한 사용자의 앱을 자동 다운그레이드하지는 않는다.

## 4. 배포 확인

```sh
curl -fsS https://morphy.tpals0409dev.workers.dev/api/release
curl -fsS https://morphy.tpals0409dev.workers.dev/api/release/windows
curl -I https://morphy.tpals0409dev.workers.dev/download
curl -I https://morphy.tpals0409dev.workers.dev/download/windows
```

Mac 링크가 여전히 DMG를 가리키는지, Windows 링크가 EXE를 가리키는지 확인한다. Windows EXE를 실제 내려받아 JSON의 SHA-256과 비교하고 깨끗한 Windows 11 계정에서 설치한다. 문서 엔진이 없는 PC에서 다운로드→검증→설치→앱 실행을 확인한다. 다음 버전 배포 때는 이전 앱에서 업데이트 알림, 설정·엔진 보존, 사용자 선택 후 설치까지 확인한다. 현재 코드 서명은 적용하지 않았다.

참고: [GitHub 아티팩트 다운로드](https://docs.github.com/en/actions/how-tos/manage-workflow-runs/download-workflow-artifacts), [Cloudflare R2 Worker API](https://developers.cloudflare.com/r2/api/workers/workers-api-reference/).
