# 입력 매핑 진단 절차

## 자동 검증

PowerShell에서 프로젝트 루트 기준:

```powershell
$env:DOTNET_CLI_HOME = Join-Path (Get-Location) '.dotnet-home'
$env:APPDATA = Join-Path (Get-Location) '.appdata'
$env:NUGET_PACKAGES = Join-Path (Get-Location) '.packages'
.\.dotnet\dotnet.exe build FocusBind.slnx --no-restore
.\.dotnet\dotnet.exe run --project tests\FocusBind.Tests --no-restore
```

`--probe-hooks`는 화면을 사용할 수 있는 일반 PowerShell에서 훅을 설치하고 커서를 움직이지 않는 마우스 이동 이벤트를 한 번 전송한다. `callbacksAfter`가 `callbacksBefore`보다 커야 한다. 창이 없는 비대화형 셸에서는 `SKIP`을 출력한다.

```powershell
.\.dotnet\dotnet.exe run --project tests\FocusBind.Tests --no-restore -- --probe-hooks
```

## 실제 창 비교

1. 기존 FocusBind를 종료하고 최신 빌드를 실행한다. 메인 창에서 **매핑 시작**을 누른다.
2. 일반 텍스트 편집기 창에 포커스를 둔 채 상단 숫자 `1`을 한 번 누른다. FocusBind로 돌아와 **훅 입력**, **후보 입력**, **훅 콜백** 증가 여부를 확인한다. 대상 프로필이 아닌 창이므로 **트리거**는 증가하지 않아야 한다.
3. 대상 게임 창에 포커스를 두고 상단 숫자 `1`을 한 번 누른다. FocusBind로 돌아와 같은 수치와 **마지막 외부 창**을 확인한다.
4. 메인 창에 표시된 진단 로그 경로를 복사한다. 세션별 로그는 `%LOCALAPPDATA%\FocusBind\diagnostics-*.log`에 생성된다. 원시 키 입력과 창 제목은 저장하지 않는다.

로그 해석:

| 관찰 | 우선 확인할 단계 |
| --- | --- |
| 일반 창에서도 `callbacks`가 증가하지 않음 | 훅 설치 또는 메시지 루프 |
| 일반 창에서는 증가하지만 게임에서는 증가하지 않음 | 대상 창의 입력 전달 방식, 권한 또는 게임 차단 |
| 게임에서 마우스 콜백은 증가하지만 키보드 콜백이 없음 | 저수준 키보드 훅 미수신; `rawKeyboardInputs`와 `raw trigger fired` 확인 |
| 키보드 콜백은 증가하고 `candidate`가 없음 | 트리거 키 종류 또는 키 코드 |
| `candidate`가 있고 `target active`가 없음 | 창 조건 또는 프로필 선택 |
| `trigger fired`가 있고 `run completed`가 없음 | 실행 중단 사유와 출력 오류 |

`health` 줄은 5초마다 훅 콜백·후보 입력·실행 횟수를 남긴다. 대상 창을 활성화했을 때 `target active`가 나오는지, 그 구간에서 `callbacks`가 증가하는지 비교한다.

Raw Input 백그라운드 키보드 경로는 **원본 입력 차단이 꺼진 키 매핑**에만 사용한다. 저수준 훅과 Raw Input이 동시에 키를 받으면 해당 매핑은 Raw Input 경로에서만 실행해 중복을 피한다. 게임 창에서 `rawKeyboardInputs`가 증가하고 `raw trigger fired` 뒤에 `run completed`가 나오는지 확인한다.

대상 창의 권한이 더 높다면 메인 창의 **관리자 권한으로 재시작**을 누른 뒤 다시 비교한다. 로그의 `ownIntegrityRid`와 게임 `integrityRid`가 같거나 FocusBind 쪽이 높은지 확인한다. 중간(8192) 대 높음(12288)에서는 키 입력이 들어오지 않았다. 양쪽이 높음(12288)이 된 게임 로그에서는 트리거와 실행이 26회씩 완료됐다. 게임 내부의 실제 클릭 위치는 별도 수동 확인이 필요하다.

좌표 클릭 로그에는 `coordinate move target=... actual=...`가 남는다. 두 값이 같아야 버튼을 누른다. `coordinate click released`가 없으면 중단 사유를 확인한다. 게임이 이동보다 클릭을 먼저 처리하는 경우를 막기 위해 이동·버튼 누름·복원 사이에 짧은 대기를 둔다. 여전히 게임의 현재 포인터 위치가 눌리면 편집기의 **클릭 후 커서 원위치**를 끄고 비교한다.

커서가 움직이지 않는 방식을 시험할 때는 편집기에서 해당 매핑의 **좌표 클릭 방식 → 창 메시지**를 선택해 저장하고 매핑을 다시 시작한다. 로그에 `window message click posted`가 나와도 게임 내부에서 클릭이 처리됐는지는 별도로 확인한다. 원래 방식과 비교 후 동작하지 않으면 **커서 이동**으로 되돌린다.

대상 게임의 `1` 매핑은 `WM_MOUSEMOVE`를 먼저 보내고 60ms 뒤 버튼 메시지를 보내도 실제 커서 위치가 클릭됐다. 대상 창 클래스 `UnityWndClass`에서 창 메시지 방식은 이제 실행 전에 차단하며, 진단 로그에 `Unity window uses the physical cursor position` 오류를 남긴다. 편집기에서 **커서 이동**으로 바꾸고 저장한 후 다시 시작한다.

자동 시나리오에는 저장된 프로필에서 창 식별→키 트리거 판정→좌표 변환→클릭 출력까지 이어지는 파이프라인 테스트가 포함된다. 실제 Windows 입력 훅과 대상 게임의 입력 수용 여부는 위 실제 창 비교로 확인한다.

## 빠른 클릭 비교 검증 (2026-11-10)

현재 상태: 기본 경로 빌드 성공, 자동 시나리오 28개 PASS. 아래 실제 게임 비교는 아직 수행하지 않았다. 로그의 완료만으로 클릭 성공을 판정하지 않는다.

1. 랑그릿사의 창 크기·DPI를 기록하고 화면을 캡처한다. 화면 중앙과 모서리 근처에 실제 반응을 확인할 수 있는 지점을 각각 선택한다.
2. 같은 매핑·좌표에서 기본 `커서 이동`과 `빠른 이동·클릭·복원`을 비교한다. 매번 저장하고 편집기를 닫은 뒤 매핑을 다시 시작한다.
3. 각 모드에서 `클릭 후 커서 원위치`를 켠 경우/끈 경우 모두 테스트한다. 트리거 전 커서는 목표와 떨어진 곳에 둔다. 이동·클릭·복원과 게임 안의 실제 반응을 각각 확인한다.
4. 창 크기 변경 중 또는 반복 실행 중 다른 창으로 전환해 잘못된 클릭·눌린 버튼 잔류가 없는지 확인한다. 사용자가 커서를 움직일 때 복원이 이를 덮어쓰지 않는지도 확인한다.
5. 대상 앱·창 크기·DPI·매핑·선택 모드·복원 옵션·절차·기대/실제 결과·진단 로그 경로를 기록한다. 아직 확인하지 않은 항목은 미검증으로 남긴다.

로그에서 `coordinate click mode=FastCursor moveLeadMs=0 holdMs=0 restoreDelayMs=20` 및 `coordinate move target=... actual=...`, `coordinate click released`와 취소/오류 사유를 비교한다. 표시된 밀리초는 설정된 대기 시간이며 실제 스케줄링 시간과 같다고 보장하지 않는다.