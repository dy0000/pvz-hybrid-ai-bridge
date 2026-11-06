"""Package public bridge files only; never include game libraries, saves or local state."""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import zipfile

ROOT = Path(__file__).resolve().parents[1]
FILES = ("README.md", "INSTALL_FOR_AI.md", "LICENSE", "NOTICE.md", "CHANGELOG.md", "CONTRIBUTING.md", "AGENTS.md",
         ".gitignore", ".gitattributes", "API.md", "AI_PLAYER_PROMPT.txt", "mcp.example.json",
         "bootstrap.ps1", "install.ps1", "install.cmd", "start.cmd", "doctor.cmd")
DIRECTORIES = ("src", "python", "mod", "scripts", "tests", "docs", "skills", ".github")
SUFFIXES = {".md", ".py", ".cs", ".csproj", ".ps1", ".yml", ".yaml", ".json", ".txt"}
EXCLUDED_PARTS = {"bin", "obj", "__pycache__", ".venv", "runtime", "generated", "research", "artifacts"}


def release_files(root: Path = ROOT) -> list[Path]:
    paths = [root / name for name in FILES]
    for directory in DIRECTORIES:
        paths.extend(p for p in (root / directory).rglob("*") if p.is_file()
                     and not EXCLUDED_PARTS.intersection(p.relative_to(root).parts)
                     and p.suffix in SUFFIXES and not p.name.endswith(".local.json"))
    info = json.loads((root / "packages/build-info.json").read_text(encoding="utf-8-sig"))
    version = json.loads((root / "mod/mod.json").read_text(encoding="utf8"))["version"]
    expected = f"pvz-ai-bridge-{version}.pmod"
    if info["bridgeVersion"] != version or info["packageName"] != expected:
        raise ValueError("Build metadata does not match the Mod version")
    package = root / "packages" / expected
    if hashlib.sha256(package.read_bytes()).hexdigest().upper() != info["packageSha256"].upper():
        raise ValueError("Bridge package hash mismatch")
    with zipfile.ZipFile(package) as archive:
        if set(archive.namelist()) != {"mod.json", "Runtime/ModAssembly.dll"}:
            raise ValueError("The pmod must contain only our manifest and bridge assembly")
    paths.extend((package, root / "packages/build-info.json"))
    for path in paths:
        if not path.is_file() or not path.resolve().is_relative_to(root.resolve()):
            raise ValueError(f"Missing or external public file: {path}")
    return sorted(paths)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    files = release_files()
    version = json.loads((ROOT / "mod/mod.json").read_text(encoding="utf8"))["version"]
    output = args.output or ROOT / "dist" / f"pvz-hybrid-ai-bridge-{version}.zip"
    output.parent.mkdir(parents=True, exist_ok=True)
    manifest = {p.relative_to(ROOT).as_posix(): hashlib.sha256(p.read_bytes()).hexdigest() for p in files}
    with zipfile.ZipFile(output, "w", zipfile.ZIP_DEFLATED) as archive:
        for path in files:
            archive.write(path, "pvz-hybrid-ai-bridge/" + path.relative_to(ROOT).as_posix())
        archive.writestr("pvz-hybrid-ai-bridge/manifest.json", json.dumps(
            {"version": version, "files": manifest}, ensure_ascii=False, indent=2) + "\n")
    digest = hashlib.sha256(output.read_bytes()).hexdigest()
    output.with_suffix(".zip.sha256").write_text(digest + "  " + output.name + "\n", encoding="ascii")
    print(json.dumps({"path": str(output), "files": len(files) + 1, "sha256": digest}, indent=2))


if __name__ == "__main__":
    main()
