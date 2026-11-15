# KoView 공개 배포 준비

## 설치 파일 검증

새 버전 번호로 빌드한 뒤 MSI의 ICE 결과를 확인한다. 같은 버전 번호로 다른 MSI를 배포하지 않는다.

```powershell
.\Installer\Build.ps1 -StagePath obj/installer-release
```

- ICE61은 동일 버전 자동 제거를 비활성화해 해결했다. 이전 버전에서 새 버전으로 업그레이드하는 경로는 유지한다.
- ICE69는 시작 메뉴와 레지스트리의 실행 파일 경로를 설치 디렉터리 기준으로 바꿔 해결했다.
- ICE91은 사용자 프로필 설치 경로에 대한 경고다. 패키지는 `InstallScope="perUser"`로 설치되며, 다른 사용자의 프로필로 파일을 복사하는 시스템 전체 설치를 제공하지 않는다. ICE91은 숨기지 않는다.

Microsoft 문서 [ICE61](https://learn.microsoft.com/en-us/windows/win32/msi/ice61), [ICE69](https://learn.microsoft.com/en-us/windows/win32/msi/ice69), [ICE91](https://learn.microsoft.com/en-us/windows/win32/msi/ice91).

## 별도 Windows 환경 시험

KoView를 설치한 이력이 없는 Windows x64 VM이나 시험 PC에서 일반 사용자로 MSI를 실행한다. 시스템에 .NET 10 Desktop Runtime이 없어도 KoView 설치본이 시작되어야 한다. MSI, 시험 스크립트, 재생 가능한 AVI 샘플을 해당 컴퓨터로 복사한 뒤 실행한다.

```powershell
.\Test-FreshInstall.ps1 -MsiPath .\KoView-x64.msi -SampleAvi '.\sample.avi'
```

스크립트는 신규 설치, AVI 연결 등록, 시작 메뉴 바로가기, 실제 설치 경로에서 libmpv 로딩을 확인한다. 이어서 Windows 기본 앱 화면에서 AVI 기본 앱을 KoView로 선택하고, 탐색기에서 같은 파일을 더블클릭해 영상과 볼륨 표시를 확인한다. Windows의 앱 제거 화면에서 설치를 제거한 뒤 바로가기와 KoView AVI 등록 항목이 제거되는지 확인한다.

## MSI 서명

배포자 명의의 신뢰 가능한 코드 서명 인증서와 사용 가능한 개인 키로 서명한다. Windows SDK의 x64 SignTool 경로와 인증서 발급자가 제공한 RFC 3161 HTTPS 타임스탬프 URL을 사용한다.

```powershell
.\Installer\Sign-Msi.ps1 -CertificateThumbprint $thumbprint -TimestampUrl $timestampUrl -SignTool $signToolPath
```

스크립트는 SHA-256으로 임시 복사본을 서명하고, 타임스탬프와 Authenticode 검증이 통과하면 MSI를 교체한다. 서명 후 MSI를 다시 빌드하면 새 파일을 다시 서명해야 한다. 로컬 컴퓨터 인증서 저장소에는 `-LocalMachine`을 사용한다. 클라우드 서명 서비스가 제공하는 별도 SignTool 설정은 서비스 지침에 따라 적용하고, 완성된 MSI의 서명과 타임스탬프를 확인한다. [Microsoft SignTool](https://learn.microsoft.com/en-us/windows/win32/seccrypto/signtool)

## libmpv 대응 소스와 고지

배포 파일에 포함되는 현재 고지는 [Mpv-Distribution-Notice.txt](../engine/notices/Mpv-Distribution-Notice.txt)에 있다. 원본 DLL의 SHA-256, mpv와 FFmpeg의 정확한 커밋, Windows 빌드 레시피 커밋을 기록했다. 확보한 세 소스 아카이브는 내부 검토용이다. FFmpeg 빌드 레시피에는 `--enable-gpl`과 `--enable-version3`가 포함된다. [공급업체 빌드 레시피](https://github.com/shinchiro/mpv-winbuild-cmake/blob/bda10a2816ce9908d419b3f4b564227330457e76/packages/ffmpeg.cmake)

공개 배포 전에 현재 DLL에 포함된 나머지 의존성의 정확한 버전 또는 커밋, 각 라이선스, 적용 패치, 빌드 옵션과 스크립트를 확인해야 한다. 공급업체의 해당 실행 빌드 기록에서 확인하거나, 버전이 고정된 입력으로 엔진을 새로 빌드하고 그 바이너리를 다시 시험한다. 현재 DLL을 유지할 경우 KoView와 결합 배포에 적용되는 라이선스 조건도 함께 확인한다. 완성된 대응 소스와 고지는 MSI와 같은 공개 다운로드 위치에서 제공하고, 파일 해시와 다운로드 링크를 함께 게시한다. [mpv 라이선스](https://github.com/mpv-player/mpv/blob/bcde6b93cca4d128d84e1a60d1a6db2cd81c889e/Copyright) · [GNU GPL FAQ](https://www.gnu.org/licenses/gpl-faq.en.html)
