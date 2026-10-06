"""가벼운 실행파일/명령행 기반 작업 모드 감지와 관련 프로세스 그룹."""
from __future__ import annotations

from .models import ProcessInfo

MODE_LABELS = {"auto": "자동", "developer": "개발", "gaming": "게임", "ai": "AI / LLM",
               "browser": "브라우저", "office": "문서 작업", "media": "미디어",
               "battery": "배터리 절약", "background": "백그라운드"}
GROUPS = {
    "developer": {"code", "devenv", "idea64", "pycharm64", "rider64", "windowsterminal", "wt",
                  "powershell", "pwsh", "cmd", "git", "node", "python", "pythonw", "docker",
                  "dotnet", "msbuild", "cl", "clang", "gcc", "cargo", "rustc", "java", "javac",
                  "chrome", "msedge", "firefox", "codex"},
    "gaming": {"discord", "audiodg", "obs64", "easyanticheat", "easyanticheat_eos", "beservice", "vgc"},
    "ai": {"ollama", "ollama_llama_server", "llama-server", "llama-cli", "lm studio", "lmstudio",
           "koboldcpp", "python", "pythonw", "comfyui", "automatic1111"},
    "browser": {"chrome", "msedge", "firefox", "brave", "vivaldi", "opera"},
    "office": {"winword", "excel", "powerpnt", "outlook", "onenote", "hwp", "acrobat"},
    "media": {"vlc", "wmplayer", "spotify", "potplayer64", "mpv", "obs64", "premiere pro", "resolve"},
}
DEVELOPER_TRIGGERS = GROUPS["developer"] - GROUPS["browser"]
ALWAYS_ACTIVE = (GROUPS["ai"] | GROUPS["media"] | GROUPS["gaming"] |
                 {"ffmpeg", "handbrake", "blender", "aria2c", "qbittorrent", "transmission-qt",
                  "robocopy", "rsync", "rclone", "onedrive", "googledrivefs", "dropbox", "backup",
                  "msbuild", "cl", "clang", "gcc", "rustc", "cargo", "javac", "docker", "node"})


def base(name: str) -> str:
    return name.lower().strip().removesuffix(".exe")


def detect_mode(foreground: str, procs: list[ProcessInfo], ai_pids: set[int],
                manual: str = "auto", fullscreen: bool = False, on_battery: bool = False) -> str:
    if manual != "auto":
        return manual
    fg = base(foreground)
    if any(p.pid in ai_pids and base(p.name) == fg for p in procs):
        return "ai"
    if fg in DEVELOPER_TRIGGERS:
        return "developer"
    if fg in GROUPS["media"]:
        return "media"
    if fg in GROUPS["office"]:
        return "office"
    if fg in GROUPS["browser"]:
        return "browser"
    if fullscreen and fg:
        return "gaming"  # 휴리스틱이며 사용자 수동 선택이 우선
    if ai_pids:
        return "ai"
    return "battery" if on_battery else "background"


def resource_groups(assessments, mode: str) -> list[dict]:
    result = []
    for group, names in GROUPS.items():
        members = [a for a in assessments if base(a.info.name) in names]
        if members:
            result.append({"mode": group, "label": MODE_LABELS[group], "active": group == mode,
                           "count": len(members), "ws": sum(a.info.ws for a in members),
                           "protected": sum(a.protected_reason is not None for a in members)})
    return result
