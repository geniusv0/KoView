# KoView

Windows x64용 libmpv 플레이어입니다. MSI에는 .NET 10 LTS Desktop 런타임이 포함됩니다.

소스에서 개발용으로 빌드하려면 .NET 10 SDK와 Windows x64용 `libmpv-2.dll`이 필요합니다. 아래 엔진 안내에 따라 DLL을 `engine/libmpv-2.dll`에 넣은 뒤 빌드하세요.

```powershell
dotnet build -c Release
.\bin\Release\net10.0-windows\KoView.exe
```

- 우클릭 설정 메뉴의 **파일 열기**, **Ctrl+O** 또는 파일 드래그로 영상을 엽니다.
- 사진 한 장을 파일 열기로 선택하면 같은 폴더의 지원되는 사진과 동영상이 이름순으로 재생 목록에 표시됩니다.
- **Ctrl+N**에서 RTSP·HTTP 주소 또는 YouTube 영상·재생목록 링크를 엽니다. 앱 내 YouTube 재생에는 Microsoft Edge WebView2 런타임이 필요합니다.
- **Ctrl+L**에서 재생 목록을 검색하거나 항목을 드래그해 순서를 바꿉니다. 설정 메뉴에서 최근 연 항목과 즐겨찾기를 열고 현재 프레임을 PNG로 저장할 수 있습니다.
- 재생 목록에서 열 수 없는 항목은 경고 표시를 남기고 건너뜁니다. RTSP가 일시적으로 끊기면 최대 세 번 재연결하며 **Esc**로 취소할 수 있습니다.
- KoView를 다시 열면 마지막 로컬 재생 목록과 음량·음소거, 프레임 숨김, 슬라이드 간격 설정을 복원합니다.
- **Space**로 재생과 일시정지를 전환합니다.
- 영상의 왼쪽·오른쪽 가장자리에 마우스를 두면 이전·다음 파일 화살표가 나타납니다. 클릭하면 목록을 순환해 이동합니다.
- 영상이나 플레이어 창을 마우스 오른쪽 버튼으로 클릭하면 설정 메뉴가 열립니다.
- **← / →**를 짧게 눌렀다 떼면 한 프레임 이동 후 멈춥니다. 0.5초 이상 누르면 10초씩 이동하고, 누르고 있는 동안 0.5초마다 반복합니다.
- 창 테두리가 숨겨져 있어도 가장자리나 모서리를 마우스로 끌어 크기를 조절할 수 있습니다. 모서리를 끌면 영상 비율이 유지됩니다.
- 설정 메뉴의 **바탕화면 재생** 또는 **Ctrl+D**로 KoView 창이 있는 모니터의 바탕화면에서 재생합니다. 조작 창에 포커스가 있을 때 단축키를 쓸 수 있으며, **Esc**를 누르거나 해당 모니터가 분리되면 일반 창으로 돌아옵니다.

## libmpv 엔진

개발에 사용한 바이너리는 [shinchiro의 Windows x64 빌드](https://github.com/shinchiro/mpv-winbuild-cmake/releases/tag/20261111)의 `mpv-dev-x86_64-20261111-git-bcde6b93cc.7z`에서 가져왔습니다. 압축을 풀어 `libmpv-2.dll`을 `engine` 폴더에 넣으면 빌드 출력에 복사됩니다. [DLL 해시 및 엔진 고지](engine/notices/Mpv-Distribution-Notice.txt) · [설치 파일 빌드·배포 절차](Installer/RELEASE.md)
