"""실제 빌드에 사용한 라이브러리 버전과 라이선스 파일을 포터블 폴더에 동봉한다."""
import importlib.metadata as metadata
import json
import shutil
import sys
from pathlib import Path

root = Path(__file__).resolve().parents[1]
names = ["PySide6", "PySide6_Essentials", "PySide6_Addons", "shiboken6", "psutil", "pywin32", "pyinstaller"]
versions = {name: metadata.version(name) for name in names}
versions["Python"] = sys.version.split()[0]
for folder in sys.argv[1:]:
    output = Path(folder) / "licenses"
    output.mkdir(parents=True, exist_ok=True)
    for name in names:
        distribution = metadata.distribution(name)
        for item in distribution.files or []:
            relative = Path(str(item))
            if ".." in relative.parts:
                continue
            if "/licenses/" not in str(item).lower() and not relative.name.lower().startswith(("license", "copying", "notice")):
                continue
            source = Path(distribution.locate_file(item))
            if source.is_file():
                target = output / name / relative
                target.parent.mkdir(parents=True, exist_ok=True)
                shutil.copy2(source, target)
    python_license = Path(sys.base_prefix) / "LICENSE.txt"
    if python_license.is_file():
        shutil.copy2(python_license, output / "Python-LICENSE.txt")
    for file in (root / "licenses").glob("*.txt"):
        shutil.copy2(file, output / file.name)
    (output / "versions.json").write_text(json.dumps(versions, indent=2), encoding="utf-8")
    shutil.copy2(root / "THIRD_PARTY_NOTICES.md", Path(folder) / "THIRD_PARTY_NOTICES.md")
