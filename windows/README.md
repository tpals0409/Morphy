# Morphy Windows

Windows 11 x64용 C#/.NET 10 WPF 앱입니다. 탐색기나 바탕화면에서 파일 하나를 선택하고 왼쪽 Alt를 0.15초 누른 뒤 원하는 형식으로 포인터를 이동하여 놓으면 변환합니다. 가운데와 바깥에서 놓거나 Esc를 누르면 취소합니다.

## 빌드와 실행

.NET 10 SDK와 PowerShell 7을 사용합니다. 저장소의 `.tools/dotnet/dotnet.exe`가 있으면 우선 사용합니다.

```powershell
.\build-windows.ps1 -SelfTest
.\build-windows.ps1 -Run
.\scripts\package-windows.ps1 -Version 0.1.0
```

개발 실행 파일은 `build/windows-dev/Morphy.exe`, 설치 파일은 `build/windows-release/Morphy-0.1.0-win-x64-Setup.exe`입니다. .NET 런타임을 포함하여 대상 PC에 SDK가 필요하지 않습니다. 개발 폴더를 복사할 때는 네이티브 DLL과 ThirdParty 폴더도 함께 복사합니다. 재빌드 전 실행 중인 앱을 트레이에서 종료하세요.

관리자 권한 없이 `%LOCALAPPDATA%/Programs/Morphy`에 설치하고 시작 메뉴와 Windows 설치된 앱 목록에 등록합니다. 설정과 로그는 `%LOCALAPPDATA%/Morphy`에 저장합니다. 로그인 자동 실행은 기본적으로 꺼져 있습니다.

## 구현 기능

- 트레이, 최초 사용 안내, 단일 인스턴스, 설정 저장, 로그인 자동 실행 옵션.
- 탐색기 활성 탭·바탕화면 단일 선택 읽기, 편집 중 입력 제외, 왼쪽 Alt 원형 메뉴, 클릭·방향키·Enter 실행과 Esc 취소.
- 라이트·다크 테마, 고대비 색상, 접근성 이름, 더 보기, 다중 DPI 좌표 처리.
- 작업 대기열, 취소, Windows 트레이 알림, 실패 재시도, 결과 열기와 탐색기에서 보기.
- 로컬 변환, 원본 보존, 이름 충돌 시 번호 부여, 검증 후 저장, 쓰기 불가능한 폴더의 대체 경로 선택.

| 입력 | 출력 |
| --- | --- |
| PNG/JPG/HEIC/GIF/TIFF/WEBP/BMP/SVG | PDF/JPG/HEIC/GIF/PNG/TIFF/WEBP |
| PDF | JPG/HEIC/GIF/PNG/TIFF/WEBP/TXT |
| DOCX | PDF |
| DOC/ODT/RTF/TXT | PDF/DOCX |
| Markdown | PDF/DOCX/HTML |
| XLSX | PDF/CSV |
| XLS/ODS | PDF/XLSX/CSV |
| CSV | PDF/XLSX |
| PPTX | PDF |
| PPT/ODP | PDF/PPTX |

같은 형식은 메뉴에서 제외합니다. PDF 이미지는 페이지별, Excel CSV는 시트별로 저장합니다. SVG의 PDF 출력은 벡터를 유지합니다. CP949 텍스트·CSV를 지원합니다. 앱 설치 과정에서 LibreOffice를 함께 다운로드하고 SHA-256·게시자 서명을 검증하여 사용자 폴더에 설치합니다. 이미 준비된 엔진은 다시 다운로드하지 않습니다. 다운로드·검증·설치 진행률을 표시하며, 실패·취소 시 앱 설치를 완료하지 않습니다. 엔진 제거는 트레이에서 가능하며, 따로 제거한 경우 다음 문서 변환 시 다시 추가합니다.

입력은 최대 100MB입니다. 암호화 PDF, 애니메이션 이미지, 링크와 내려받지 않은 클라우드 파일은 지원하지 않습니다. 다중 페이지 TIFF는 첫 프레임을 사용합니다. 외부 SVG 자원과 문서의 외부 데이터 연결을 제한합니다. 변환은 별도 프로세스에서 시간·메모리 제한을 적용합니다.

## 검증

```powershell
.\build-windows.ps1 -SelfTest
# Python에 python-docx, openpyxl, python-pptx, pypdf 필요
.\scripts\test-windows-office.ps1
Start-Process .\build\windows-release\Morphy-0.1.0-win-x64-Setup.exe -ArgumentList '--selftest' -Wait
.\build\windows-dev\Morphy.exe --render-menu C:\absolute\preview-folder
```

자체 검사는 이미지 코덱, PDF 재열기·렌더링, 벡터 SVG, CP949, 한글 경로, 덮어쓰기 방지, 손상 파일, 취소, 업데이트 메타데이터와 설치 파일 경로·해시 검사를 포함합니다. 통합 검사는 실제 앱과 LibreOffice로 문서 변환, 다중 시트 CSV, 한글 본문, PDF 마지막 페이지 텍스트를 확인합니다. 설치 자체 검사는 압축 해제와 설치 실패 복구를 검사하며 실제 사용자 등록은 변경하지 않습니다. GitHub Actions에도 검증을 구성했습니다.

수동 확인이 필요한 항목:

- 실제 탐색기 활성 탭과 바탕화면 선택, 이름 변경·주소·검색 입력, 짧은 Alt 및 Alt 조합.
- 메뉴 가운데·바깥·Esc 취소, 클릭 전달 방지, 선택 변경과 앱 전환.
- 화면 가장자리, 혼합 DPI·다중 모니터, Windows 알림 설정.
- 실제 사용자 설치·제거와 로그인 자동 실행, 게시된 릴리스를 통한 업데이트.

자동 테스트는 실제 조작이나 macOS와의 시각적 결과 동등성 검증을 대신하지 않습니다.

## 배포와 업데이트

업데이트는 `https://morphy.tpals0409dev.workers.dev/api/release/windows`에서 버전·파일 경로·SHA-256·크기를 확인합니다. 지정한 Worker의 버전별 Windows EXE 경로만 허용하며, 사용자가 선택해야 다운로드·설치합니다. 기존 설정과 문서 엔진을 유지합니다. 서명된 앱은 같은 게시자의 서명된 업데이트만 허용합니다.

코드 서명 시 패키징 스크립트에 `-CertificateThumbprint`와 필요하면 `-SignTool`을 지정합니다. 현재 인증서가 없어 로컬 설치 파일은 서명되지 않았습니다. 실제 릴리스 게시와 서명은 완료하지 않았습니다.

배포에는 설치 파일, `SHA256SUMS.txt`, `scripts/package-codec-sources.ps1`로 생성한 `WicHeic-v1.1.16-corresponding-source.zip`을 함께 제공하세요. 라이선스와 소스 제공 사항은 [THIRD-PARTY.md](THIRD-PARTY.md)를 참고하세요.

GitHub Actions 아티팩트를 Mac에서 받아 기존 Worker와 R2에 배포하는 절차는 [Windows 배포 안내](../docs/windows-deployment.md)에 있습니다. `windows-release.json`은 패키징 시 실제 EXE를 기준으로 자동 생성합니다. Worker 변경은 Mac의 기존 프로젝트에 통합해야 합니다.
