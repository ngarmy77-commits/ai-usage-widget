# AI Usage Widget

Claude Code와 Codex의 **5시간·주간 사용률**을 Windows 작업 표시줄 트레이에서 바로 확인하는 작은 위젯입니다.

- **설치 필요 없음**: 40KB 단일 exe. Windows 10/11에 기본으로 있는 .NET Framework 4.x만 씁니다.
- **인터넷 접속 없음, 로그인 정보를 읽지 않음**: PC 안의 기록만 읽어서 회사 PC에서도 쓸 수 있습니다.
- **필요할 때만 열기**: 평소에는 트레이 숫자 아이콘만 보이고, 클릭하면 창이 열리고, 다른 곳을 누르면 닫힙니다.

> English: A tiny Windows tray widget that shows Claude Code and Codex rate-limit usage (5-hour / weekly).
> Single 40KB exe, no install, no network access, never reads your login tokens — it only reads local files.

## 화면

```
Claude                 터미널 Claude · 마지막 사용 10/22 10:02 기준
  5시간 ██████░░░░  26%   13:40 · 3시간 14분 남음
  주간  ███░░░░░░░  15%   10/23(금) 08:00 · 21시간 34분 남음
Codex · Plus                        마지막 사용 10/22 09:57 기준
  5시간 ██░░░░░░░░  12%   12:58 · 2시간 32분 남음
  주간  ████░░░░░░  41%   10/27(화) 15:33 · 5일 5시간 남음
오늘 토큰
  Claude   입력 24.86M (캐시 24.48M) · 출력 191.6K · 요청 156회
  Codex    입력 2.75M (캐시 2.59M) · 출력 18.4K · 세션 1개
```

트레이 아이콘의 숫자는 Claude·Codex 5시간 사용률 중 높은 값입니다. 70% 미만은 초록, 90% 미만은 주황, 90% 이상은 빨강으로 표시됩니다.

## 설치

1. [Releases](../../releases)에서 `AIUsageWidget-v*.zip`을 받아 압축을 풉니다.
2. `AIUsageWidget.exe`를 계속 둘 폴더(예: `문서\AIUsageWidget`)에 놓고 실행합니다.
   - "Windows의 PC 보호" 창이 나오면 **추가 정보 → 실행**을 누릅니다. 코드 서명이 없는 개인 제작 프로그램이라 나오는 경고입니다.
3. 처음 실행하면 두 가지를 묻습니다.
   - **Claude 연동**: `%USERPROFILE%\.claude\settings.json`에 상태표시줄(`statusLine`) 연동을 추가합니다. 원본은 같은 폴더에 백업합니다. 이미 다른 상태표시줄을 쓰고 있으면 바꿀지 따로 묻습니다.
   - **Windows 시작 시 자동 실행** 여부
4. 터미널에서 Claude Code(`claude`)를 다시 시작해서 한 번 대화하면 Claude 사용률이 표시되기 시작합니다.
   Claude 데스크탑앱(Code 탭)은 사용률을 넘겨주지 않습니다. 아래 "알아 둘 점"을 참고하세요.

자세한 사용법은 [docs/사용설명서.txt](docs/사용설명서.txt)에 있습니다.

## 동작 원리

| 항목 | 출처 | 갱신 |
|---|---|---|
| Claude 5시간·주간 | 터미널 Claude Code(CLI)가 상태표시줄 명령에 넘겨주는 `rate_limits` | 터미널 Claude 세션이 열려 있는 동안 1분마다 |
| Codex 5시간·주간 | `~/.codex/sessions/**/rollout-*.jsonl`의 마지막 `rate_limits` | Codex를 쓸 때 |
| 오늘 토큰 | `~/.claude/projects/**/*.jsonl`, Codex 세션 로그 | 5분마다 |

- 같은 exe가 `--statusline` 인자로 실행되면 상태표시줄 명령으로 동작합니다. 받은 값을 `%APPDATA%\UsageWidget\statusline\<세션ID>.json`에 저장합니다.
- 세션마다 마지막으로 받은 값이 달라서, 오래 쉬고 있는 세션은 예전 값을 갖고 있습니다. 같은 사용 기간(초기화 시각이 같은 기간) 안에서는 사용률이 줄지 않으므로, **초기화 시각이 가장 늦은 값 중 사용률이 가장 높은 값**을 최신 값으로 고릅니다.
- 초기화 시각이 지난 값은 "초기화 지남"으로, 값이 없으면 "확인필요"로 표시합니다. 숫자를 추정해서 채우지 않습니다.
- Claude 값은 터미널 Claude가 마지막으로 응답을 받은 시점 기준이고, Codex 값은 Codex를 쓸 때만 갱신됩니다.

## 변경하는 파일

- `%USERPROFILE%\.claude\settings.json`의 `statusLine` 항목: 동의한 경우에만, 바꾸기 전에 원본을 백업합니다.
- `%APPDATA%\UsageWidget\`: 위젯 설정과 Claude 사용률 기록
- `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`의 `AIUsageWidget` 값: 자동 실행에 동의한 경우에만

## 빌드

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1
```

Windows 내장 `csc.exe`(.NET Framework 4.x, C# 5)로 `dist\AIUsageWidget.exe`와 `dist\AIUsageWidget-v<버전>.zip`을 만듭니다. Visual Studio나 .NET SDK는 필요 없습니다.

점검용 명령:

```powershell
.\dist\AIUsageWidget.exe --dump   # 현재 읽은 값을 JSON으로 출력
```

## 제거

1. 트레이 아이콘을 오른쪽 클릭해서 "Windows 시작 시 자동 실행"을 해제하고 종료합니다.
2. `%USERPROFILE%\.claude\settings.json`에서 `statusLine` 항목을 지우거나 백업으로 되돌립니다.
3. `%APPDATA%\UsageWidget` 폴더와 exe를 삭제합니다.

## 알아 둘 점

- **Claude 사용률은 터미널 Claude Code(CLI)를 쓸 때만 갱신됩니다.** Claude 데스크탑앱(Code 탭 포함)은 상태표시줄을 실행하지 않아서 사용률을 넘겨주지 않습니다. 그래서 데스크탑앱만 쓰는 동안에는 위젯의 Claude 값이 마지막으로 터미널을 쓴 시점에 멈춰 있습니다. 사용량은 계정 전체로 합산되므로, 다음에 터미널 Claude가 응답을 받으면 데스크탑앱에서 쓴 양까지 포함한 값으로 따라잡습니다.
- **비공식 도구**입니다. Anthropic, OpenAI와 관련이 없으며, Claude·Codex는 각 회사의 상표입니다.
- Claude 사용률은 Claude Code의 공식 상태표시줄 입력을 씁니다. Codex 사용률과 두 도구의 토큰은 공개 문서가 없는 로컬 로그 형식을 읽습니다. 그래서 각 도구가 업데이트되면 일부 값이 "확인필요"로 바뀌거나 표시되지 않을 수 있습니다.
- 표시 값은 참고용입니다. 정확한 사용량은 각 서비스의 공식 화면에서 확인하세요.

## 라이선스

[MIT](LICENSE)
