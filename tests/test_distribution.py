"""Installation boundaries use small synthetic game files; no live game is started."""
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest
from unittest import mock

ROOT = Path(__file__).resolve().parents[1]


def module(name):
    spec = importlib.util.spec_from_file_location(name, ROOT / "scripts" / (name + ".py"))
    result = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(result)
    return result


class DistributionTests(unittest.TestCase):
    def test_generated_configuration_handles_unicode_paths_and_mcp_is_offline(self):
        install = module("install")
        with tempfile.TemporaryDirectory(prefix="pvz-安装 ") as folder:
            root = Path(folder)
            install.write_configuration(root, sys.executable)
            server = json.loads((root / "generated/mcp.json").read_text(encoding="utf8"))["mcpServers"]["pvz_hybrid"]
            self.assertEqual(server["args"], ["-X", "utf8", str(root / "scripts/run_mcp.py")])
            import tomllib
            toml = tomllib.loads((root / "generated/codex.toml").read_text(encoding="utf8"))
            self.assertEqual(toml["mcp_servers"]["pvz_hybrid"], server)

    def test_release_excludes_runtime_secrets_and_build_outputs(self):
        packager = module("package_release")
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            for name in packager.FILES:
                (root / name).write_text("public", encoding="utf8")
            shutil.copytree(ROOT / "mod", root / "mod")
            shutil.copytree(ROOT / "packages", root / "packages")
            for name in ("src/obj/secret.json", "src/bin/PlantsVsZombies.dll", "scripts/__pycache__/secret.py",
                         "generated/mcp.json", "runtime/connection.json", "game-sandbox/game.exe", "config.local.json",
                         "scripts/config.local.json", "packages/old.pmod"):
                path = root / name
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_text("private")
            names = {p.relative_to(root).as_posix() for p in packager.release_files(root)}
            self.assertFalse(any("secret" in name or "local.json" in name or "PlantsVsZombies.dll" in name for name in names))
            self.assertEqual({n for n in names if n.startswith("packages/")},
                             {"packages/build-info.json", "packages/pvz-ai-bridge-0.5.16.pmod"})

    @unittest.skipUnless(sys.platform == "win32", "PowerShell installation is Windows-only")
    def test_sandbox_install_preserves_source_and_refuses_running_target_and_incompatible_build(self):
        with tempfile.TemporaryDirectory(prefix="pvz-安装 ") as folder:
            base = Path(folder)
            root, source, appdata = base / "project", base / "original", base / "appdata"
            root.mkdir()
            source.mkdir()
            appdata.mkdir()
            shutil.copytree(ROOT / "scripts", root / "scripts", ignore=shutil.ignore_patterns("__pycache__"))
            shutil.copytree(ROOT / "packages", root / "packages")
            shutil.copytree(ROOT / "python", root / "python", ignore=shutil.ignore_patterns("__pycache__"))
            shutil.copy(ROOT / "install.ps1", root / "install.ps1")
            exe = "植物大战僵尸杂交版发布版0.31.0.Csharp.exe"
            for relative in (exe, exe.replace(".exe", ".pck"), "data_PlantsVsZombies_windows_x86_64/PlantsVsZombies.dll",
                             "data_PlantsVsZombies_windows_x86_64/GodotSharp.dll", "override.cfg"):
                path = source / relative
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_bytes(b"synthetic fixture - never execute")
            before = {p.relative_to(source): p.read_bytes() for p in source.rglob("*") if p.is_file()}
            metadata = root / "packages/build-info.json"
            info = json.loads(metadata.read_text(encoding="utf-8-sig"))
            info["gameAssemblySha256"] = hashlib.sha256(b"synthetic fixture - never execute").hexdigest()
            metadata.write_text(json.dumps(info), encoding="utf8")
            env = {**os.environ, "APPDATA": str(appdata)}
            # Windows runners commonly expose TEMP through an 8.3 path alias.
            import ctypes
            short_path = ctypes.create_unicode_buffer(32768)
            if ctypes.windll.kernel32.GetShortPathNameW(str(appdata), short_path, len(short_path)):
                env["APPDATA"] = short_path.value
            # Do not feed PowerShell 7's module path to Windows PowerShell 5.1.
            env = {key: value for key, value in env.items() if key.upper() != "PSMODULEPATH"}

            def run(code, success=True):
                result = subprocess.run(["powershell.exe", "-NoProfile", "-NonInteractive", "-Command", code],
                                        env=env, capture_output=True, text=True, encoding="utf8", errors="replace", timeout=30)
                self.assertEqual(result.returncode == 0, success, result.stdout + result.stderr)
                return result

            def quote(value):
                return "'" + str(value).replace("'", "''") + "'"

            setup = ("function Get-CimInstance { @() }; & " + quote(root / "install.ps1") +
                     " -GamePath " + quote(source) + " -Client Generic -NonInteractive -PythonPath " + quote(sys.executable))
            run(setup)
            state = json.loads((root / "sandbox.local.json").read_text(encoding="utf-8-sig"))
            saves = Path(state["userDataDirectory"])
            self.assertTrue(saves.resolve().is_relative_to(appdata.resolve()))
            (saves / "player.save").write_bytes(b"keep this save")
            # The native and Python sides must derive exactly the same save path.
            sys.path.insert(0, str(ROOT / "python"))
            from pvz_ai import client
            with mock.patch.object(client, "__file__", str(root / "python/pvz_ai/client.py")), mock.patch.dict(os.environ, env):
                self.assertEqual(client.default_config().parent.parent.resolve(), saves.resolve())
            # Repeat setup adopts only this installation's verified sandbox.
            run(setup)
            install = "& " + quote(root / "scripts/install.ps1")
            installed = saves / "Mods" / info["packageName"]
            installed_before = installed.read_bytes()
            run("function Get-CimInstance { [pscustomobject]@{ExecutablePath=" +
                quote(root / "game-sandbox" / exe) + ";Name='game.exe';ProcessId=1234} }; " + install, success=False)
            self.assertEqual(installed.read_bytes(), installed_before)
            self.assertEqual((saves / "player.save").read_bytes(), b"keep this save")
            # An incompatible source must not be copied into a fresh project.
            other = base / "other-project"
            other.mkdir()
            shutil.copytree(ROOT / "scripts", other / "scripts", ignore=shutil.ignore_patterns("__pycache__"))
            shutil.copytree(ROOT / "packages", other / "packages")
            run("& " + quote(other / "scripts/setup_sandbox.ps1") + " -SourceDirectory " + quote(source), success=False)
            self.assertFalse((other / "game-sandbox").exists())
            self.assertEqual({p.relative_to(source): p.read_bytes() for p in source.rglob("*") if p.is_file()}, before)


if __name__ == "__main__":
    unittest.main()
