#!/usr/bin/env python3
"""Build reproducible source and installable archives for AsfPlayStatus."""
from __future__ import annotations

import hashlib
import re
import sys
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
PROJECT = ROOT / "AsfPlayStatus" / "AsfPlayStatus.csproj"
SOURCE_FILES = (
    ".gitignore", "scripts/package_release.py", ".github/workflows/release.yml",
    "README.md", "CHANGELOG.md", "build.ps1", "deploy.sh",
    "AsfPlayStatus/AsfPlayStatus.csproj", "AsfPlayStatus/PlayStatusController.cs",
    "AsfPlayStatus/PlayStatusModels.cs", "AsfPlayStatus/PlayStatusPlugin.cs",
    "tests/AsfPlayStatus.RegressionHarness/AsfPlayStatus.RegressionHarness.csproj",
    "tests/AsfPlayStatus.RegressionHarness/AsfStubs.cs",
    "tests/AsfPlayStatus.RegressionHarness/NuGet.Config",
    "tests/AsfPlayStatus.RegressionHarness/Program.cs",
)


def current_version() -> str:
    project = PROJECT.read_text(encoding="utf-8")
    match = re.search(r"<Version>([^<]+)</Version>", project)
    if not match or not re.fullmatch(r"\d+\.\d+\.\d+", match.group(1)):
        raise SystemExit("AsfPlayStatus.csproj must define a three-part Version")
    version = match.group(1)
    for field in ("AssemblyVersion", "FileVersion"):
        if f"<{field}>{version}.0</{field}>" not in project:
            raise SystemExit(f"{field} does not match project Version")
    readme = (ROOT / "README.md").read_text(encoding="utf-8")
    changelog = (ROOT / "CHANGELOG.md").read_text(encoding="utf-8")
    latest = re.search(r"(?m)^##\s+v?(\d+\.\d+\.\d+)", changelog)
    if not latest or latest.group(1) != version:
        raise SystemExit("CHANGELOG first release heading must match the current version")
    if not re.search(rf"(?im)^>\s*(?:Version:|版本：)\s*\*\*v{re.escape(version)}\*\*(?:\s*[｜|].*)?$", readme):
        raise SystemExit(f"README current version does not match project Version {version}")
    if not re.search(rf"(?im)^##\s+v?{re.escape(version)}\s*$", changelog):
        raise SystemExit(f"CHANGELOG latest version does not match project Version {version}")
    return version


def add_file(archive: zipfile.ZipFile, source: Path, name: str) -> None:
    info = zipfile.ZipInfo(name, date_time=(2000, 1, 1, 0, 0, 0))
    info.create_system = 3
    info.compress_type = zipfile.ZIP_DEFLATED
    info.external_attr = (0o100755 if name.endswith(".sh") else 0o100644) << 16
    archive.writestr(info, source.read_bytes())


def zip_files(path: Path, files: tuple[str, ...]) -> None:
    with zipfile.ZipFile(path, "w", compression=zipfile.ZIP_DEFLATED) as archive:
        for name in files:
            source = ROOT / name
            if not source.is_file():
                raise SystemExit(f"Required release file is missing: {name}")
            add_file(archive, source, name)


def latest_notes(version: str) -> str:
    text = (ROOT / "CHANGELOG.md").read_text(encoding="utf-8")
    match = re.search(rf"(?m)^##\s+v?{re.escape(version)}\s*$", text)
    assert match is not None
    remainder = text[match.end():]
    next_heading = re.search(r"(?m)^##\s+", remainder)
    section = remainder[:next_heading.start()] if next_heading else remainder
    return f"# AsfPlayStatus v{version}\n\n{section.strip()}\n"


def main() -> None:
    if sys.argv[1:] == ["--version"]:
        print(current_version())
        return
    if sys.argv[1:]:
        raise SystemExit("usage: package_release.py [--version]")
    version = current_version()
    dll = ROOT / "AsfPlayStatus" / "bin" / "Release" / "net10.0" / "AsfPlayStatus.dll"
    if not dll.is_file():
        raise SystemExit(f"Build output is missing: {dll.relative_to(ROOT)}")
    dist = ROOT / "dist"
    dist.mkdir(exist_ok=True)
    source_zip = dist / f"asf-playstatus-v{version}-source.zip"
    install_zip = dist / f"asf-playstatus-v{version}-install.zip"
    zip_files(source_zip, SOURCE_FILES)
    with zipfile.ZipFile(install_zip, "w", compression=zipfile.ZIP_DEFLATED) as archive:
        add_file(archive, dll, "AsfPlayStatus.dll")
        add_file(archive, ROOT / "README.md", "README.md")
        add_file(archive, ROOT / "CHANGELOG.md", "CHANGELOG.md")
    (dist / "release-notes.md").write_text(latest_notes(version), encoding="utf-8")
    with (dist / "SHA256SUMS").open("w", encoding="ascii", newline="\n") as sums:
        for path in (source_zip, install_zip):
            digest = hashlib.sha256(path.read_bytes()).hexdigest()
            sums.write(f"{digest}  {path.name}\n")


if __name__ == "__main__":
    main()
