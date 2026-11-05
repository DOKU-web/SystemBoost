#!/usr/bin/env python3
"""Compiles every generated Metal shader, so a bad one fails the build and not the first launch.

The engine compiles them from source at run time, and the Swift compiler only sees strings.
"""
import re
import subprocess
import sys
import tempfile
from pathlib import Path

SOURCE = Path(__file__).parent.parent / "native/framegen-mac/Sources/SBFrameGenMac/GeneratedShaders.swift"
SHADER = re.compile(r'^ +"(\w+)": #"""\n(.*?)^"""#,$', re.M | re.S)


def main():
    shaders = SHADER.findall(SOURCE.read_text(encoding="utf-8"))
    if not shaders:
        sys.exit("no shaders found in " + str(SOURCE))
    failed = 0
    with tempfile.TemporaryDirectory() as tmp:
        for name, text in shaders:
            path = Path(tmp) / f"{name}.metal"
            path.write_text(text, encoding="utf-8")
            r = subprocess.run(["xcrun", "-sdk", "macosx", "metal", "-std=macos-metal2.1", "-c", str(path),
                                "-o", str(path.with_suffix(".air"))], capture_output=True, text=True)
            print(name, "ok" if r.returncode == 0 else "FAILED")
            if r.returncode != 0:
                print(r.stderr)
                failed += 1
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
