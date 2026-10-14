# 소스 빌드

이 저장소의 `src`는 로컬 호환성 모드와 설치기 소스입니다. 게임 코드·게임 DLL·사용자 세이브·BepInEx 배포본은 포함하지 않습니다. 원작자의 공식 릴리스가 아니며 출처는 NOTICE.txt를 참고하세요.

## 준비

- Windows x64, .NET SDK(net472 및 net8.0 빌드 지원), NuGet 복원을 위한 네트워크
- 본인의 Dinkum 설치: 이 버전은 Steam 빌드 25568182에 고정됩니다.
- 해당 게임의 BepInEx 6 Unity.Mono modern core
- 별도 공식 BepInEx 6 pre.1 legacy core

```powershell
./Build.ps1 -GamePath 'C:/Program Files (x86)/Steam/steamapps/common/Dinkum' -LegacyLoaderPath 'C:/your/BepInEx-pre1/BepInEx/core'
```

완성 설치기는 `build/Dinkum-Modpack-Setup.exe`입니다. 빌드 스크립트는 게임 설치 폴더에 파일을 쓰거나 게임을 실행하지 않습니다. 게임/로더 DLL은 빌드 참조만 합니다. 설치기에 포함되는 제3자 라이브러리는 MIT 라이선스의 Mono.Cecil이며 고지문도 포함합니다.

`src/EquipmentEnhancement/RulesTests.csproj`와 `src/EquipmentRecovery/Tests/Tests.csproj`는 게임 실행 없이 규칙을 검사합니다. 설치기 내 과거 업그레이드/로더 fixture 테스트에는 배포하지 않는 별도 테스트 재료가 필요합니다. 빌드 성공은 원격 멀티·20인 검증을 뜻하지 않습니다.

실제 게임에서 검증한 빌드의 배포 ZIP은 Releases에서 받으세요. 공개 소스의 빌드 도구 버전 차이로 바이트 해시가 달라질 수 있으며, 강화 제거·업데이트 안전장치는 알 수 없는 DLL을 거부할 수 있습니다. 검증되지 않은 자체 빌드를 중요한 세이브에 덮어쓰지 마세요.

## 다음 릴리스

사용법·검증내역·NOTICE를 갱신하고 새 버전 ZIP을 검증한 후, 쓰기 권한이 있는 계정으로 Git에 로그인해 다음을 실행합니다.

```powershell
./Publish-Release.ps1 -Package 'C:/releases/verified-new-version.zip' -Tag 'v4.3.0'
```

인증은 Git credential helper를 이용합니다. 비밀번호·토큰을 소스에 넣지 마세요. 기존 릴리스는 덮어쓰지 않으며, 초안에 ZIP을 업로드하여 서버 SHA-256 검증을 마친 후 테스트 배포로 공개합니다. 모든 변경은 먼저 검토·커밋해야 합니다.
