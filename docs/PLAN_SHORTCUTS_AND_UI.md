# 구현 계획서 — 바로가기 런처 + UI 개편

작성 2026-10-16 · 대상: TaskMonitor (Window_Navigation) `ea45c62` 이후

## 0. 목표

1. 귀퉁이 바에 **내가 고른 앱·폴더·파일·웹 주소 바로가기**를 등록한다.
2. 아이콘을 누르면 **앱 실행 / 폴더 열기 / 파일 열기**가 바로 된다.
3. 바로가기의 **원래 아이콘을 그대로** 바에 쓰고, 바로가기 묶음을 **접었다 폈다** 할 수 있다.
4. 펼친 상태에서 아이콘 클릭 → 이동 또는 실행.
5. 모니터 UI(바·목록 창)를 Windows 11 느낌으로 다시 다듬는다.

하지 않는 것(1차): 관리자 권한 자동 실행, 실행 중인 창으로 전환(작업 표시줄 대체), 클라우드 동기화, 단축키 전역 등록(2차 후보).

## 1. 화면

```
접힘   ┌────────────────────────────────────────────────────────────┐
       │ ⋮⋮ [▸ 🚀 8] │ ▶ 실행 2 ⚠ 확인 1 │ ≡ │ ▶ run_sbs.py sample2 · 62% · ≈14:32 │
       └────────────────────────────────────────────────────────────┘
펼침   ┌──────────────────────────────────────────────────────────────────────────────┐
       │ ⋮⋮ [▾] [VS][📁3DMaker][📁MPDMaker][YUV][🌐Jira][…] [+] │ ▶ 실행 2 … │ ≡ │ … │
       └──────────────────────────────────────────────────────────────────────────────┘
```

- 바 왼쪽(또는 설정에 따라 오른쪽)에 **바로가기 영역**. 접힘 상태에서는 `▸` + 대표 아이콘 1개 + 개수.
- 펼치면 아이콘(기본 20px, 설정 16/20/24/32)이 한 줄로 나열. 마우스를 올리면 이름·경로 툴팁.
- `+` 버튼 또는 **파일·폴더·.lnk를 바에 끌어다 놓기**로 등록.
- 아이콘 우클릭: 열기 / 파일 위치 열기 / 이름 바꾸기 / 아이콘 바꾸기 / 인자·시작 폴더 편집 / 왼쪽·오른쪽으로 이동 / 제거.
- 아이콘 끌어서 순서 바꾸기(2단계).
- 그룹(예: "3D 도구", "폴더")은 2단계: 그룹마다 접고 펴기, 접힌 그룹은 그룹 아이콘 하나로 표시.
- 대상이 사라진 바로가기는 흐리게 + ⚠ 툴팁("경로 없음"), 클릭 시 실행하지 않고 편집 창을 띄움.
- 접힘/펼침 상태는 저장해서 다시 켜도 유지. "마우스를 올리면 잠깐 펼치기" 옵션(기본 꺼짐).

## 2. 데이터 — `%LOCALAPPDATA%\TaskMonitor\shortcuts.json`

```jsonc
{
  "schema": "taskmonitor-shortcuts/1",
  "expanded": true,
  "icon_size": 20,
  "groups": [ { "id": "g1", "name": "3D 도구", "expanded": true } ],
  "items": [
    {
      "id": "s-7f3a",
      "name": "3DMaker 폴더",
      "kind": "folder",                       // app | folder | file | url | shortcut
      "target": "C:\\Users\\82107\\Desktop\\Project\\ETRI\\2026\\3D\\01_KITS\\02_3DMaker",
      "args": null,
      "workdir": null,
      "lnk": null,                             // .lnk 로 등록했으면 원본 .lnk 경로 (실행은 .lnk 로)
      "icon": { "source": "auto", "path": null, "index": 0 },  // auto | file(ico/png/exe,index)
      "group": "g1",
      "order": 10
    }
  ]
}
```

- 저장은 기존과 같은 **임시 파일 → 교체** 방식. 손상 시 `shortcuts.json.bak`(직전 정상본)으로 복구.
- 상태 폴더(`--dir`)를 바꾸면 바로가기도 그 폴더 기준(데모·테스트 분리).

## 3. 등록 — 무엇을 넣으면 어떻게 저장하나

| 넣은 것 | kind | 처리 |
|---|---|---|
| `.exe` | app | 경로 그대로. 이름 = 파일 설명(FileVersionInfo) 또는 파일 이름 |
| 폴더 | folder | 경로 그대로. 이름 = 폴더 이름 |
| `.lnk` (시작 메뉴·바탕화면 바로가기) | shortcut | **.lnk 자체를 저장**하고 실행도 .lnk로 → 인자·시작 폴더·관리자 설정이 원본 그대로 유지. 표시용으로 대상 경로를 `IShellLinkW`로 읽어 둠 |
| `.url` / `http(s)://` 문자열 | url | 기본 브라우저로 열기 |
| 그 밖의 파일(.sln, .xlsx, .py …) | file | 연결된 프로그램으로 열기 |
| 스토어 앱(UWP) | shortcut | 시작 메뉴에서 바탕화면으로 끌어 만든 .lnk 또는 `shell:AppsFolder\<AUMID>` (2단계: 앱 목록에서 고르기) |

등록 경로: ① 바에 드래그 앤 드롭 ② `+` → 파일/폴더 선택 창 ③ 상세 창의 "바로가기" 탭에서 편집(2단계).

## 4. 실행 — 누르면

- 모두 `ShellExecuteEx`(`Process.Start` + `UseShellExecute=true`)로 실행 → Windows가 탐색기에서 더블클릭한 것과 같은 방식으로 처리.
  - app: `FileName=target, Arguments=args, WorkingDirectory=workdir ?? exe 폴더`
  - folder: `explorer.exe "<폴더>"` (이미 열린 같은 폴더 창을 앞으로 가져오기는 2단계 옵션)
  - shortcut: `.lnk` 경로를 그대로 ShellExecute
  - url/file: 대상 그대로 ShellExecute
- 실행 전 대상 존재 확인(폴더·파일·exe). 없으면 실행하지 않고 알림.
- 모니터 자체는 일반 권한으로 실행하므로 관리자 권한 앱은 Windows UAC 창이 정상적으로 뜸(우회하지 않음).
- 바 창은 포커스를 뺏지 않는 창이라, 실행된 앱이 앞으로 오도록 `AllowSetForegroundWindow(ASFW_ANY)` 호출 후 실행.
- Ctrl+클릭 = 파일 위치 열기, 가운데 클릭 = 한 번 더 실행(새 창) — 2단계.

## 5. 아이콘 — 바로가기 아이콘을 그대로

- 1순위: `SHCreateItemFromParsingName` → `IShellItemImageFactory.GetImage(size, SIIGBF_ICONONLY)`
  → 탐색기와 같은 아이콘(.lnk 화살표 없이, 고해상도). 폴더는 폴더별 사용자 아이콘(desktop.ini)까지 반영.
- 2순위(실패 시): `SHGetFileInfo(SHGFI_ICON | SHGFI_LARGEICON)` → `Imaging.CreateBitmapSourceFromHIcon` → `DestroyIcon`.
- 사용자 지정 아이콘: `.ico`/`.png`, 또는 `.exe/.dll,인덱스`.
- DPI: 모니터 배율 × 아이콘 크기로 요청(예: 20px × 150% = 30px → 32px 요청 후 축소).
- 캐시: `icons\<sha1(target|lnk|size|mtime)>.png`. 시작 시 캐시로 즉시 표시하고, 백그라운드에서 다시 뽑아 바뀌었으면 교체.
- URL은 브라우저 아이콘 대신 지구본 기본 아이콘(웹에서 파비콘을 받지 않음 — 네트워크 접근 없음 유지).

## 6. 코드 구조 (추가·변경 파일)

| 파일 | 내용 |
|---|---|
| `Core/Shortcuts.cs` (신규) | `ShortcutItem`, `ShortcutGroup`, `ShortcutStore`(로드·저장·백업·순서), 종류 판별. WPF 의존 없음 → Check 도구에서 단위 확인 가능 |
| `ShellIcons.cs` (신규) | 아이콘 추출(IShellItemImageFactory / SHGetFileInfo), 캐시 |
| `ShellLinks.cs` (신규) | `.lnk` 읽기(IShellLinkW + IPersistFile COM), `.url` 읽기 |
| `ShortcutLauncher.cs` (신규) | 존재 확인, ShellExecute, AllowSetForegroundWindow, 오류 메시지 |
| `ShortcutEditWindow.xaml` (신규) | 이름·대상·인자·시작 폴더·아이콘·그룹 편집 |
| `BarWindow.xaml(.cs)` | 바로가기 영역(ItemsControl + 접기 버튼 + `+`), 드래그 앤 드롭(`AllowDrop`), 우클릭 메뉴 |
| `ViewModels.cs` | `ShortcutVm`(아이콘 BitmapSource, 이름, 툴팁, 경로 없음 여부), 펼침 상태 |
| `AppSettings.cs` | `ShortcutsPosition`(left/right), `ShortcutIconSize`, `ShortcutsHoverExpand` |
| `docs/UI.md`, `README.md` | 사용법 |

바 레이아웃 주의: 바는 `SizeToContent` + 화면 폭 95% 제한. 바로가기가 많으면 **바로가기 영역에 최대 폭**(기본 화면 폭 35%)을 두고 넘치면 `»` 메뉴로 나머지 표시.

## 7. 단계와 완료 기준

| 단계 | 내용 | 완료 기준 |
|---|---|---|
| **S1** 데이터·실행 | ShortcutStore, Launcher, `+`(파일/폴더 선택), 접기/펴기, 기본 아이콘(SHGetFileInfo) | exe·폴더·파일·.lnk·URL 각각 등록 → 클릭 실행/열기, 재시작 후 유지, 없는 경로는 실행 안 함 |
| **S2** 아이콘 품질 | IShellItemImageFactory, DPI별 크기, 캐시, 사용자 지정 아이콘 | 바로가기 화살표 없는 원본 아이콘, 125/150% 배율에서 흐리지 않음, 시작 시 캐시로 즉시 표시 |
| **S3** 편의 | 드래그 앤 드롭 등록, 우클릭 편집 창, 순서 이동, `»` 넘침 메뉴 | 탐색기·시작 메뉴에서 끌어 놓아 등록, 이름·인자 수정, 순서 변경 저장 |
| **S4** (선택) | 그룹별 접기, 마우스 올리면 펼치기, 같은 폴더 창 재사용, 스토어 앱 목록 | — |

검증: Check 도구에 `--shortcuts` 추가(저장 파일 해석·대상 존재·종류 판별 출력), 데모용 `tools\demo_shortcuts.bat`(메모장·탐색기 폴더·URL 등록 파일 생성), PC에서 클릭 실행 확인 후 커밋.
예상 작업량: S1 반나절, S2 반나절, S3 반나절~하루.

## 8. UI 개편 — 후보

예쁜 UI가 웹 전용은 아닙니다. 네이티브에서도 Windows 11 스타일(Fluent, Mica/Acrylic 반투명 배경, 둥근 모서리, 다크 모드)을 낼 수 있습니다.

| 후보 | 모양 | 장점 | 비용·위험 |
|---|---|---|---|
| **A. WPF 유지 + .NET 내장 Fluent 테마** (`ThemeMode`, .NET 9+) | Win11 기본 컨트롤 느낌, 시스템 다크/라이트 따라감 | 패키지 추가 없음, 지금 코드 그대로 | 아직 "실험적" 표시 기능, 컨트롤 종류는 기본 수준 |
| **B. WPF 유지 + WPF UI 라이브러리** (lepoco/wpfui, MIT, NuGet 1개) | Win11 설정 앱 같은 모양: Fluent 컨트롤, 내비게이션, 스낵바, 트레이 | 완성도 높은 Fluent 컨트롤 다수, 기존 Core·창 구조 유지, 점진 적용 | 외부 패키지 1개 추가 |
| C. WinUI 3 (Windows App SDK) | 가장 "순정" Win11 | Microsoft 공식 최신 UI | UI 층 전면 재작성, 배포 복잡, 항상 위·AppBar·포커스 안 뺏는 창 같은 저수준 처리가 WPF보다 번거로움 |
| D. WPF + WebView2 (HTML/CSS로 화면) | 웹처럼 자유로운 디자인 | 디자인 자유도 최고, 웹 UI 라이브러리 사용 가능 | 메모리 100MB+, C#↔JS 연결 코드, 상시 떠 있는 작은 바에는 과함 |
| E. Avalonia(+FluentAvalonia) / Electron·Tauri | 웹 수준 / 크로스플랫폼 | — | 재작성. 이 프로그램(Windows 전용, 상시 실행)에는 이득이 적음 |

**추천: B (또는 A로 시작해 부족하면 B).** 판정·감지 로직(Core)과 창 구조를 그대로 두고 화면만 바꿀 수 있습니다.

개편 방향(B 기준):
- **바**: 알약 모양, Mica/Acrylic 반투명 배경, Segoe Fluent Icons 기호, 상태는 색 점 + 짧은 글자, 진행률은 칩 아래 얇은 막대.
  바로가기 영역과 작업 영역 사이 구분선. 마우스를 올리면 칩이 살짝 커지는 정도의 절제된 효과.
- **목록 창**: 표(GridView) → **작업 카드 목록**(왼쪽) + 상세(오른쪽) 2단. 카드에 상태 배지·진행 막대·예상 완료.
  상세는 섹션 카드(요약 / 단계 / 결과 / 로그)로 나누고 로그는 접기.
- 다크/라이트는 Windows 설정을 따름. 상태 색은 두 테마에서 대비 확인.

순서 제안: 바로가기 **S1** → UI 개편(바 먼저, 목록 창 다음) → 바로가기 **S2–S3**. UI를 먼저 바꾸면 바로가기 영역을 새 디자인에 맞춰 한 번에 만들 수 있어서, S1은 최소 화면으로 기능만 붙이고 디자인은 개편 때 같이 다듬습니다.
