# 개발 안내

## 빌드와 실행

```bash
cd TaskMonitor
dotnet build -c Release
```

결과는 `TaskMonitor/bin/Release/net10.0-windows/TaskMonitor.exe`입니다. 이 빌드는 .NET 10 데스크톱 런타임이 설치된 PC에서만 돕니다.

## 배포용 exe

.NET이 없는 PC에서도 도는 exe 한 개를 만듭니다. 결과는 `dist/TaskMonitor.exe`(약 50MB)와 `dist/THIRD-PARTY-NOTICES.txt`입니다.

```bash
dotnet publish TaskMonitor -c Release -p:PublishProfile=win-x64
```

배포 전에 `dist/TaskMonitor.exe`만 빈 폴더에 복사해 자체 검증을 돌려, 단일 파일이 스스로 완결되는지 확인합니다.

자동 업데이트는 GitHub의 최신 Release를 읽습니다. 그래서 새 버전을 낼 때 다음을 지킵니다.

- `TaskMonitor.csproj`의 `<Version>`을 올리고, Release 태그를 `v<같은 버전>`으로 붙입니다. 태그가 실행 중인 버전보다 높아야 받습니다.
- 파일 이름은 `TaskMonitor.exe` 그대로 올립니다. GitHub가 올린 파일마다 SHA-256(`digest`)을 붙이고, 앱은 이 값이 맞을 때만 바꿔 넣습니다.
- 이 해시는 전송 중 손상만 막습니다. 저장소 계정이 털리면 악성 파일도 해시가 맞습니다. 코드 서명을 하게 되면 받은 파일이 같은 게시자로 서명됐는지도 확인해야 합니다.
- 받는 쪽 동작은 자체 검증이 localhost에 가짜 Release를 띄워 시험합니다(`Updater.TestApiUrl`). 실행 중인 앱은 GitHub 주소만 씁니다.

아이콘은 `tools/make-icon.ps1`이 그립니다. 바꾸려면 스크립트를 고친 뒤 다시 실행합니다.

```bash
powershell -ExecutionPolicy Bypass -File tools/make-icon.ps1
```

## 자체 검증

실제 시스템 호출과 기록, 경고, ETW 교정을 확인합니다. 우리가 띄운 테스트 프로세스에만 작업을 겁니다. 마지막 줄이 "모든 검사 통과"이면 통과입니다.

```bash
TaskMonitor.exe --selftest out.txt
```

화면 없이 샘플 4회를 찍어 파일로 씁니다. Windows 카운터와 비교할 때 씁니다.

```bash
TaskMonitor.exe --check out.txt
```

## 실행 옵션

| 인수 | 동작 |
|---|---|
| `--tray` | 창 없이 트레이로 시작합니다. "Windows 시작 시 실행"이 이 인수로 등록합니다. |
| `--selftest <파일>` | 자체 검증. |
| `--check <파일>` | CPU 샘플 4회 기록. |
| `--replace-taskmgr`, `--restore-taskmgr` | 작업 관리자 대체를 켜고 끕니다. 관리자 권한으로 실행해야 하며 옵션 메뉴가 UAC로 띄웁니다. |
| `--elevated-start-on`, `--elevated-start-off` | 관리자 권한 실행 작업을 등록하고 지웁니다. 위와 같습니다. |
| `--from-task` | 관리자 권한 실행 작업이 붙입니다. 이 인수가 없고 일반 권한이면 작업으로 넘기고 끝납니다. |

작업 관리자 대체로 열리면 Windows가 첫 인수로 taskmgr.exe 경로를 붙입니다. 모르는 인수는 무시합니다.

한 번에 하나만 실행됩니다. 두 번째 실행은 떠 있는 창을 앞으로 불러오고 끝납니다.

## 분석 문서

설계와 측정 방식, 검증 기록은 [taskmgr-analysis.md](taskmgr-analysis.md)에 있습니다.
