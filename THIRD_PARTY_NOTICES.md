# Third-party components

RamFlow의 포터블 패키지는 Python, PySide6/Qt, shiboken6, psutil, pywin32 및 PyInstaller 런타임을 포함합니다. 패키지의 `licenses` 폴더에 설치된 배포본의 라이선스 파일을 동봉합니다.

- Python: Python Software Foundation License. https://www.python.org/downloads/source/
- PySide6 / shiboken6 / Qt: Qt Community Edition, LGPL v3 등 해당 모듈의 라이선스. https://doc.qt.io/qtforpython-6/ 및 https://doc.qt.io/qt-6/licensing.html
- psutil: BSD 3-Clause. https://github.com/giampaolo/psutil
- pywin32: Python Software Foundation License 계열 및 포함 구성요소별 라이선스. https://github.com/mhammond/pywin32
- PyInstaller: GPL v2 이상, 부트로더 예외 포함. https://pyinstaller.org/en/stable/license.html

Qt/PySide6 원본 소스는 https://code.qt.io/cgit/qt/ 및 https://code.qt.io/cgit/pyside/pyside-setup.git/ 에서 해당 버전 태그로 얻을 수 있습니다. 실제 패키지 버전은 `licenses/versions.json`에 기록합니다. 이 패키지는 Qt DLL을 `_internal` 폴더에 별도 파일로 배치합니다. 사용자에게 해당 라이브러리를 수정·호환 라이브러리로 교체하거나 그 수정 사항을 디버깅할 권리를 제한하지 않습니다.
