"""PyInstaller GUI 진입점. 콘솔 없이 실행하며 --tray를 지원한다."""
from winmemoryflow.ui.app import run_ui
import sys

if __name__ == "__main__":
    run_ui(smoke_test="--smoke-test" in sys.argv)
