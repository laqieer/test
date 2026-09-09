#!/usr/bin/env bash
# Contract tests only: every subprocess is mocked; these never constitute device proof.
set -euo pipefail
exec python3 - <<'PY'
import contextlib
import hashlib
import io
import json
import os
from pathlib import Path
import re
import shutil
import struct
import subprocess
import sys
import uuid
import xml.etree.ElementTree as ET
import zlib
from unittest.mock import patch

repo = Path.cwd().resolve()
runner = repo / "scripts" / "android-patch-import-smoke.sh"
source = runner.read_text(encoding="utf-8").split("<<'PY'\n", 1)[1].rsplit("\nPY", 1)[0]
compiled = compile(source, str(runner) + ":python", "exec")
owned = repo / "TestResults" / ("patch-import-runner-tests-" + uuid.uuid4().hex)
owned.mkdir(parents=True, exist_ok=False)
PKG = "com.laqieer.febuildergba"


class Device:
    def __init__(self, mode):
        self.mode = mode
        self.calls = []
        self.page = "launcher"
        self.clock = 0
        self.version = 1
        self.documents = {}
        self.database = False
        self.status = ""
        self.selected = ""
        self.tap_actions = {}
        self.dump_count = 0
        self.hierarchy_present = True
        self.primary_picker = mode in ("primary-storage-picker", "primary-storage-missing")
        self.internal_visible = False
        self.at_fixture = False
        self.picker_page = "rom-picker"
        self.actions = []
        self.granted_document = ""
        self.grants = set()
        self.session_changed = False
        self.file_geometry_reads = 0
        self.diagnostics_dir = ""
        self.hierarchy_bytes = b""
        self.hierarchy_in_fixture = False

    def monotonic(self):
        self.clock += 0.2
        return self.clock

    def ui(self):
        items = []
        if self.page == "launcher":
            items.append(("Main_AndroidOpenRom_Button", "Open ROM", "open-rom"))
        elif self.page == "editors":
            items += [("Main_LauncherFilter_Input", "Type to filter editors...", "filter"),
                      ("Main_Launcher_PatchManager_Button", "Patch Manager", "open-library")]
        elif self.page == "library":
            items += [("PatchManager_ImportPatchDatabase_Button", "Import Patch Database ZIP", "open-zip"),
                      ("PatchManager_StatusMessage_Label", self.status, "none")]
            if self.database:
                items.append(("", "Offline ZIP Proof", "none"))
        elif self.page == "confirm":
            items += [("MessageBoxContent_Yes_Button", "Yes", "yes"),
                      ("MessageBoxContent_No_Button", "No", "no")]
        elif self.page in ("rom-picker", "zip-picker") and self.primary_picker and not self.at_fixture:
            items += [("", "Show roots", "roots"), ("", "More options", "picker-options")]
        elif self.page == "picker-roots":
            items.append(("", "Downloads", "indexed-downloads"))
            if self.internal_visible and self.mode != "primary-storage-missing":
                items.append(("", "sdk_gphone64_x86_64", "primary-storage"))
        elif self.page == "picker-options":
            items.append(("", "Show internal storage", "show-internal"))
        elif self.page == "primary-storage":
            items.append(("", "Download", "download-directory"))
        elif self.page == "download-directory":
            items.append(("", "zipdb-import-proof", "fixture-directory"))
        elif self.page == "indexed-downloads":
            items.append(("", "No items", "none"))
        elif self.page == "rom-picker":
            if self.mode == "diagnostic-write-isolation":
                if self.hierarchy_in_fixture:
                    items.append(("", "hierarchy.xml", "diagnostic-file"))
                items += [("", "zipdb-invalid.zip", "wrong-document"),
                          ("", "zipdb-proof.gba", "rom"), ("", "zipdb-valid.zip", "wrong-document")]
            else:
                items.append(("", "zipdb-proof.gba", "rom"))
        elif self.page == "zip-picker":
            items += [("", "zipdb-valid.zip", "valid"), ("", "zipdb-invalid.zip", "invalid")]
        tree = ET.Element("hierarchy")
        self.tap_actions = {}
        if self.page == "rom-picker" and any(label == "zipdb-proof.gba" for _, label, _ in items):
            self.file_geometry_reads += 1
        for i, (identifier, label, action) in enumerate(items):
            y = 20 + i * 50
            if label == "zipdb-proof.gba":
                if self.mode == "geometry-shifts" and self.file_geometry_reads <= 2:
                    y += 50
                if self.mode == "geometry-never-stable" and self.file_geometry_reads % 2:
                    y += 50
            self.tap_actions[(50, y + 10)] = action
            ET.SubElement(tree, "node", {
                "resource-id": identifier, "text": label, "content-desc": "",
                "enabled": "true", "bounds": f"[10,{y}][90,{y+20}]",
            })
        return ET.tostring(tree, encoding="utf-8", xml_declaration=True)

    def run(self, argv, **kwargs):
        self.calls.append(argv)
        if argv[0] == "dotnet":
            assert len(argv) == 3 and Path(argv[1]).name == "SyntheticProofFixtures.dll"
            if self.mode == "fixture-generator-fails":
                return subprocess.CompletedProcess(argv, 1, b"", b"mock generator failure")
            target = Path(argv[2])
            assert target.name == "zipdb-proof.gba" and not target.exists()
            # Opaque fake-process output only: real tree validity is tested in C#.
            data = b"mock-generated-ROM" + bytes(16 * 1024 * 1024 - len(b"mock-generated-ROM"))
            with target.open("xb") as fixture:
                fixture.write(data)
            receipt = {"Format": "fe8u-synthetic-huffman-v1", "Length": len(data),
                       "Sha256": hashlib.sha256(data).hexdigest()}
            if self.mode == "fixture-receipt-mismatch":
                receipt["Sha256"] = "0" * 64
            return subprocess.CompletedProcess(argv, 0, json.dumps(receipt).encode(), b"")
        if argv[0] == "git":
            return subprocess.CompletedProcess(argv, 0, b"e09f646d\n" if argv[1] == "rev-parse" else b"", b"")
        if argv[0] == "aapt2":
            version = 2 if argv[-1].endswith("upgrade.apk") else 1
            package = "unexpected.package" if self.mode == "wrong-package" else PKG
            data = f"package: name='{package}' versionCode='{version}'\napplication-debuggable\n"
            return subprocess.CompletedProcess(argv, 0, data.encode(), b"")
        assert argv[:3] == ["adb", "-s", "emulator-5554"], argv
        args = argv[3:]
        status, out = 0, b""
        if args == ["get-state"]:
            out = b"device"
        elif args == ["emu", "avd", "name"]:
            out = b"zipdb-other\nOK" if self.mode == "wrong-avd" else b"zipdb-fake\nOK"
        elif args[:3] == ["shell", "getprop", "ro.kernel.qemu"]:
            out = b"0" if self.mode == "physical" else b"1"
        elif args[:3] == ["shell", "getprop", "ro.product.cpu.abi"]:
            out = b"x86_64"
        elif args[:3] == ["shell", "getprop", "ro.product.model"]:
            out = b"sdk_gphone64_x86_64"
        elif args[:3] == ["shell", "getprop", "ro.build.version.sdk"]:
            out = b"34"
        elif args[:4] == ["shell", "pm", "list", "packages"]:
            if "-U" in args:
                out = ("package:" + PKG + " uid:10192").encode()
            else:
                out = ("package:" + PKG).encode() if self.mode == "existing-package" else b""
        elif args == ["shell", "pidof", PKG]:
            out = b"3871" if self.session_changed else b"3870"
        elif args == ["shell", "cat", "/proc/sys/kernel/random/boot_id"]:
            out = b"11111111-1111-1111-1111-111111111111"
        elif args[:2] in (["shell", "svc"], ["shell", "wm"]):
            pass
        elif args[:2] == ["shell", "mkdir"]:
            if args[2:] == ["-p", "/sdcard/Download/zipdb-import-proof"]:
                pass
            else:
                assert len(args) == 3 and args[2].startswith("/sdcard/.zipdb-import-diagnostics-"), args
                assert not args[2].startswith("/sdcard/Download/zipdb-import-proof"), args
                if self.mode == "diagnostic-directory-conflict":
                    status = 1
                else:
                    self.diagnostics_dir = args[2]
        elif args[:2] == ["shell", "touch"]:
            assert args[2] == self.diagnostics_dir + "/.nomedia", args
        elif args[:2] == ["shell", "rmdir"]:
            assert self.diagnostics_dir and args[2] == self.diagnostics_dir, args
            if self.mode == "diagnostic-cleanup-refused":
                status = 1
        elif args[:3] == ["shell", "ls", "-A"]:
            assert args[3] == "/sdcard/Download/zipdb-import-proof", args
            out = "\n".join(sorted(self.documents)).encode()
        elif args[0] == "push":
            self.documents[Path(args[1]).name] = Path(args[1]).read_bytes()
        elif args[0] == "install":
            if self.mode == "install-fails":
                status = 1
            elif args[-1].endswith("upgrade.apk"):
                self.version = 2
                if self.mode == "upgrade-loses-library":
                    self.database = False
            elif self.mode == "preexisting-grant":
                self.grants.add("zipdb-invalid.zip")
            elif self.mode == "stale-rom-grant":
                self.grants.add("zipdb-proof.gba")
        elif args[:3] == ["shell", "cmd", "package"]:
            out = (PKG + "/test.MainActivity").encode()
        elif args[:3] == ["shell", "am", "start"]:
            self.page = "launcher"
        elif args[:3] == ["shell", "am", "force-stop"]:
            self.granted_document = ""
            self.grants.clear()
        elif args == ["shell", "dumpsys", "activity", "permissions", PKG]:
            grant_uid = 99999 if self.mode == "foreign-uid-grant" else 10192
            lines = [f"  * UID {grant_uid} holds:"]
            for filename in sorted(self.grants):
                uri = ("content://com.android.externalstorage.documents/document/"
                       "primary%3ADownload%2Fzipdb-import-proof%2F" + filename)
                if self.mode == "uncontrolled-document-provider":
                    uri = uri.replace("com.android.externalstorage.documents", "unexpected.example")
                lines += [f"    UriPermission{{123 {uri} [user 0]}}",
                          f"      targetUserId=0 sourcePkg=com.android.externalstorage targetPkg={PKG}"]
            out = "\n".join(lines).encode()
        elif args[:3] == ["shell", "rm", "-f"]:
            assert self.diagnostics_dir and args[3] in (
                self.diagnostics_dir + "/hierarchy.xml", self.diagnostics_dir + "/.nomedia"), args
            assert not args[3].startswith("/sdcard/Download/zipdb-import-proof/"), args
            if args[3].endswith("/hierarchy.xml"):
                self.hierarchy_present = False
                self.hierarchy_bytes = b""
        elif args[:3] == ["shell", "test", "-s"]:
            assert args[3] == self.diagnostics_dir + "/hierarchy.xml", args
            status = 0 if self.hierarchy_present else 1
        elif args[:3] == ["shell", "uiautomator", "dump"]:
            assert self.diagnostics_dir and args[3] == self.diagnostics_dir + "/hierarchy.xml", args
            assert not args[3].startswith("/sdcard/Download/zipdb-import-proof/"), args
            self.dump_count += 1
            if self.mode == "hierarchy-fails":
                status = 1
            elif self.mode == "hierarchy-never-ready" or (
                    self.mode == "hierarchy-initially-empty" and self.dump_count == 1):
                assert not self.hierarchy_present, "Previous hierarchy must be removed before dumping."
                out = b"ERROR: null root node returned by UiTestAutomationBridge.\n"
            else:
                # A dump snapshots the UI before materializing its XML output.
                # Keeping that write outside the visible tree preserves this geometry.
                self.hierarchy_bytes = b"malformed hierarchy" if self.mode == "hierarchy-malformed" else self.ui()
                self.hierarchy_present = True
        elif args[:2] == ["exec-out", "cat"]:
            assert self.hierarchy_present, "Do not read a missing or stale hierarchy."
            assert args[2] == self.diagnostics_dir + "/hierarchy.xml", args
            out = self.hierarchy_bytes
        elif args[:3] == ["shell", "input", "tap"]:
            action = self.tap_actions[(int(args[3]), int(args[4]))]
            self.actions.append(action)
            self.page = {"open-rom": "rom-picker", "rom": "editors", "open-library": "library",
                         "open-zip": "zip-picker", "valid": "confirm", "no": "library"}.get(action, self.page)
            if action in ("open-rom", "open-zip"):
                self.picker_page = self.page
                self.at_fixture = False
            if action == "rom":
                self.granted_document = ("zipdb-invalid.zip" if self.mode == "wrong-rom-document"
                                         else "zipdb-proof.gba")
                self.grants.add(self.granted_document)
                if self.mode == "app-session-changes":
                    self.session_changed = True
            if action in ("roots", "picker-options", "primary-storage", "download-directory",
                          "indexed-downloads"):
                self.page = "picker-roots" if action == "roots" else action
            if action == "show-internal":
                self.internal_visible, self.page = True, self.picker_page
            if action == "fixture-directory":
                self.at_fixture, self.page = True, self.picker_page
            if action == "yes":
                self.database, self.page, self.status = True, "library", "Imported; list refreshed"
                if self.mode == "refresh-missing":
                    self.status = "Imported but not refreshed"
            if action == "invalid":
                self.page, self.status = "library", "Patch database import failed"
                if self.mode == "rejection-loses-library":
                    self.database = False
        elif args[:2] == ["shell", "input"]:
            if args == ["shell", "input", "keyevent", "4"]:
                self.page = self.picker_page
        elif args[:3] == ["shell", "run-as", PKG]:
            op = args[3:]
            if op[0] == "head":
                out = f"1.0-{self.version}".encode()
            elif op[0] == "cat":
                pid = "3871" if self.session_changed else "3870"
                assert op[1] == f"/proc/{pid}/stat", op
                fields = ["S"] + ["0"] * 18 + ["2000" if self.session_changed else "1000"] + ["0"] * 3
                out = (pid + " (" + PKG + ") " + " ".join(fields)).encode()
            elif op[0] == "pwd":
                out = ("/data/user/0/" + PKG).encode()
            elif op[0] == "test":
                status = 1
            elif op[0] == "sha256sum":
                if not self.database:
                    status = 1
                else:
                    if op[1].endswith("payload.bin"):
                        data = bytes([0xAA, 0x55])
                    else:
                        import zipfile
                        with zipfile.ZipFile(io.BytesIO(self.documents["zipdb-valid.zip"])) as archive:
                            data = archive.read("FE8U/proof/PATCH_offline.txt")
                    out = (hashlib.sha256(data).hexdigest() + "  " + op[1]).encode()
            else:
                raise AssertionError(args)
        elif args[:2] == ["shell", "sha256sum"]:
            data = self.documents[args[-1].rsplit("/", 1)[-1]]
            if self.mode == "document-modified":
                data += b"changed"
            out = hashlib.sha256(data).hexdigest().encode()
        else:
            raise AssertionError(args)
        return subprocess.CompletedProcess(argv, status, out, b"fake failure" if status else b"")

    def popen(self, argv, stdout, **kwargs):
        if argv[3:9] == ["exec-out", "run-as", PKG, "content", "read", "--uri"]:
            assert argv[:3] == ["adb", "-s", "emulator-5554"], argv
            assert argv[9].endswith("%2F" + self.granted_document), argv
            data = self.documents[self.granted_document]
            if self.mode == "native-reader-error":
                data = (b"Error while accessing provider\njava.lang.SecurityException: Permission Denial: "
                        b"attribution package com.android.shell does not belong to uid")
            elif self.mode == "native-reader-overrun":
                data += bytes(8192)
            class ReadProcess:
                returncode = 0
                def __init__(self):
                    self.stdout = io.BytesIO(data)
                def wait(self, timeout=None):
                    return 0
                def poll(self):
                    return 0
                def kill(self):
                    pass
            return ReadProcess()
        assert argv == ["adb", "-s", "emulator-5554", "exec-out", "screencap", "-p"], argv
        def chunk(kind, data):
            return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data))
        png = b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", 1, 1, 8, 2, 0, 0, 0))
        stdout.write(png + chunk(b"IDAT", zlib.compress(b"\0\0\0\0")) + chunk(b"IEND", b""))
        class Process:
            returncode = 0
            def communicate(self, timeout=None):
                return None, b""
        return Process()


cases = ["success", "wrong-avd", "physical", "existing-package", "wrong-package",
         "install-fails", "hierarchy-fails", "refresh-missing", "rejection-loses-library",
         "upgrade-loses-library", "document-modified", "hierarchy-initially-empty",
         "hierarchy-never-ready", "hierarchy-malformed", "primary-storage-picker",
         "primary-storage-missing", "wrong-rom-document", "native-reader-error",
         "native-reader-overrun", "preexisting-grant", "stale-rom-grant", "app-session-changes",
         "geometry-shifts", "geometry-never-stable", "foreign-uid-grant", "uncontrolled-document-provider",
         "diagnostic-write-isolation", "diagnostic-directory-conflict", "diagnostic-cleanup-refused",
         "fixture-generator-missing", "fixture-generator-fails", "fixture-receipt-mismatch"]
success_cases = {"success", "hierarchy-initially-empty", "primary-storage-picker", "native-reader-error",
                 "preexisting-grant", "geometry-shifts", "diagnostic-write-isolation"}
try:
    interference = Device("diagnostic-write-isolation")
    interference.page = "rom-picker"
    before = ET.fromstring(interference.ui())
    rom_node = next(n for n in before if n.get("text") == "zipdb-proof.gba")
    bounds = [int(v) for v in re.findall(r"\d+", rom_node.get("bounds"))]
    old_point = ((bounds[0] + bounds[2]) // 2, (bounds[1] + bounds[3]) // 2)
    assert interference.tap_actions[old_point] == "rom"
    interference.hierarchy_in_fixture = True
    interference.ui()
    assert interference.tap_actions[old_point] == "wrong-document"

    for mode in cases:
        case = owned / mode
        (case / "TestResults").mkdir(parents=True)
        (case / "FEBuilderGBA.sln").write_text("owned fixture", encoding="utf-8")
        for name in ("base.apk", "upgrade.apk"):
            (case / "TestResults" / name).write_bytes(b"generated fake APK")
        if mode != "fixture-generator-missing":
            generator = case / "scripts/SyntheticProofFixtures/bin/Debug/net10.0/SyntheticProofFixtures.dll"
            generator.parent.mkdir(parents=True)
            generator.write_bytes(b"mock-only DLL marker; never executed")
        device = Device(mode)
        argv = ["-", "--serial", "emulator-5554", "--avd", "zipdb-fake",
                "--base-apk", "TestResults/base.apk", "--upgrade-apk", "TestResults/upgrade.apk", "--timeout", "1"]
        os.chdir(case)
        with patch.object(sys, "argv", argv), patch("subprocess.run", device.run), \
             patch("subprocess.Popen", device.popen), patch("shutil.which", return_value="aapt2"), \
             patch("time.sleep", return_value=None), patch("time.monotonic", device.monotonic), \
             contextlib.redirect_stdout(io.StringIO()), contextlib.redirect_stderr(io.StringIO()):
            try:
                exec(compiled, {"__name__": "__main__"})
                raise AssertionError("Runner did not return an exit status.")
            except SystemExit as result:
                code = result.code
        report_path, = (case / "TestResults").glob("android-patch-import-*/report.json")
        report = json.loads(report_path.read_text(encoding="utf-8"))
        assert (code == 0) == (mode in success_cases), (mode, report)
        assert report["passed"] == (mode in success_cases), (mode, report)
        if mode.startswith("fixture-"):
            assert not any(c[0] == "adb" and c[3] in ("install", "push") for c in device.calls), mode
        if mode in success_cases:
            assert report["fixture_generator"]["Format"] == "fe8u-synthetic-huffman-v1"
            assert report["fixture_generator"]["Length"] == 16 * 1024 * 1024
        if mode in ("wrong-avd", "physical", "existing-package", "wrong-package"):
            assert not any(c[0] == "adb" and c[3] in ("install", "push", "uninstall") for c in device.calls), mode
        assert not any(c[0] == "adb" and "uninstall" in c for c in device.calls), mode
        if mode in success_cases:
            assert len(report["screenshots"]) == 5
            assert report["config_stamps"] == ["1.0-1", "1.0-2"]
        if mode == "hierarchy-never-ready":
            assert "Timed out:" in report["error"], report
        if mode == "hierarchy-malformed":
            assert "No accessibility hierarchy" in report["error"], report
        if mode == "primary-storage-picker":
            assert "show-internal" in device.actions
            assert "fixture-directory" in device.actions
            assert "indexed-downloads" not in device.actions
        if mode == "primary-storage-missing":
            assert "Timed out:" in report["error"], report
        if mode == "wrong-rom-document":
            assert "granted zipdb-invalid.zip instead of zipdb-proof.gba" in report["error"], report
            assert report["rom_document_receipts"][0]["native_reader"]["matches_fixture"]
        if mode == "native-reader-error":
            assert not report["rom_document_receipts"][0]["native_reader"]["matches_fixture"]
            assert report["rom_document_receipts"][0]["native_reader"]["is_separate_diagnostic"]
            assert report["rom_document_receipts"][0]["native_reader"]["error_classification"]["exception"] == \
                "java.lang.SecurityException"
        if mode == "native-reader-overrun":
            assert "Native diagnostic exceeded the selected fixture bound" in report["error"], report
        if mode == "preexisting-grant":
            first = report["rom_document_receipts"][0]
            assert first["before_count"] == 1 and first["after_count"] == 2 and first["new_count"] == 1
            assert first["granted"] == "zipdb-proof.gba"
        if mode == "stale-rom-grant":
            assert "new URI grant for the same live app session" in report["error"], report
        if mode == "app-session-changes":
            assert "App session changed" in report["error"], report
        if mode == "geometry-shifts":
            assert len(report["picker_targets"][0]["geometry_samples"]) >= 3
        if mode == "geometry-never-stable":
            assert "stable fixture geometry" in report["error"], report
            assert "rom" not in device.actions
        if mode == "foreign-uid-grant":
            assert "new URI grant for the same live app session" in report["error"], report
        if mode == "uncontrolled-document-provider":
            assert "outside the controlled fixture provider" in report["error"], report
        if mode == "diagnostic-write-isolation":
            assert not device.hierarchy_in_fixture
            assert "wrong-document" not in device.actions
            assert report["owned_diagnostic_directory"] == device.diagnostics_dir
            assert report["diagnostics_cleaned"]
        if mode == "diagnostic-directory-conflict":
            assert not any(c[0] == "adb" and c[3:5] in (["shell", "rm"], ["shell", "rmdir"])
                           for c in device.calls), mode
        if mode == "diagnostic-cleanup-refused":
            assert report["diagnostic_cleanup_error"] == "Owned diagnostic directory cleanup failed."
    print(f"Passed {len(cases)} fake-adb contract scenarios; no device was contacted.")
finally:
    os.chdir(repo)
    if owned.is_symlink() or owned.parent != repo / "TestResults":
        raise RuntimeError("Refusing unexpected fixture cleanup ownership.")
    shutil.rmtree(owned)
PY
