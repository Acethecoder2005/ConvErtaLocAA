"""Package a private Windows runtime from a clean, pinned build environment."""
from __future__ import annotations
import hashlib
import importlib.metadata
import json
from pathlib import Path, PurePosixPath
import platform
import shutil
import struct
import sys

REPO = Path(__file__).resolve().parents[1]
DISTRIBUTIONS = ["Pillow", "pypdf", "reportlab", "pypdfium2", "charset-normalizer", "pi-heif"]

def ignore(directory, names):
    excluded = {"__pycache__", "site-packages", "test", "tests", "idlelib", "tkinter", "turtledemo", "ensurepip"}
    return [name for name in names if name in excluded or name.endswith((".pyc", ".pyo"))]

def copy_file(source, destination):
    destination.parent.mkdir(parents=True, exist_ok=True)
    shutil.copy2(source, destination)

def main():
    if sys.platform != "win32" or struct.calcsize("P") != 8 or sys.version_info[:2] != (3, 12):
        raise SystemExit("Build using 64-bit Python 3.12 on Windows.")
    destination = Path(sys.argv[1]).resolve()
    if destination.exists():
        raise SystemExit("Build destination already exists. Choose a new empty destination.")
    destination.mkdir(parents=True)
    runtime = destination / "runtime"
    runtime.mkdir()
    base = Path(sys.base_prefix)
    for name in ["python.exe", "python3.dll", "python312.dll", "vcruntime140.dll", "vcruntime140_1.dll", "LICENSE.txt"]:
        copy_file(base / name, runtime / name)
    shutil.copytree(base / "DLLs", runtime / "DLLs", ignore=ignore)
    shutil.copytree(base / "Lib", runtime / "Lib", ignore=ignore)
    packages = runtime / "Lib" / "site-packages"
    packages.mkdir()
    versions = {}
    for name in DISTRIBUTIONS:
        distribution = importlib.metadata.distribution(name)
        versions[name] = distribution.version
        if not distribution.files:
            raise RuntimeError("No package file manifest for " + name)
        for record in distribution.files:
            relative = PurePosixPath(str(record).replace(chr(92), "/"))
            if relative.is_absolute() or ".." in relative.parts:
                continue  # Console scripts outside site-packages are not required.
            if "__pycache__" in relative.parts or relative.suffix in {".pyc", ".pyo"} or relative.name == "direct_url.json":
                continue
            source = Path(distribution.locate_file(record))
            if source.is_file():
                copy_file(source, packages.joinpath(*relative.parts))
    allowed = [
        "source/ConvErtaLocAA.cs", "source/engine.py", "source/test_engine.py", "source/build.ps1",
        "assets/converter-logo.png", "assets/converter.ico",
        "tools/package_windows.py", "tools/collect_decoder_sources.py",
        "README.md", "THIRD_PARTY_NOTICES.md", "requirements.txt",
    ]
    for relative in allowed:
        copy_file(REPO / relative, destination / relative)
    manifest = {
        "app": "ConvErtaLocAA", "version": "0.2.0", "platform": "Windows x64",
        "python": platform.python_version(), "dependencies": versions,
        "runtime_network_required": False, "heic_output": False,
        "sha256": {relative: hashlib.sha256((destination / relative).read_bytes()).hexdigest() for relative in allowed},
    }
    (destination / "BUILD-INFO.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    print("Packaged runtime and application allowlist:", destination)

if __name__ == "__main__":
    main()
