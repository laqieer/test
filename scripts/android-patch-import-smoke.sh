#!/usr/bin/env bash
# Real-app SAF/import/relaunch/upgrade proof. Run only on an explicitly owned AVD.
set -euo pipefail
exec python3 - "$@" <<'PY'
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import threading
import time
import uuid
import xml.etree.ElementTree as ET
import zipfile
from urllib.parse import unquote, urlsplit

PKG = "com.laqieer.febuildergba"
LABEL = "Offline ZIP Proof"
DEFINITION = (
    "NAME=Offline ZIP Proof\nTYPE=BIN\n"
    "DESCRIPTION=Generated offline import fixture; no patch is applied.\n"
    "PATCHED_IF:0x200=0xAB 0xCD\nBIN:0x300=payload.bin\n"
).encode("utf-8")
PAYLOAD = bytes([0xAA, 0x55])
REMOTE_DIR = "/sdcard/Download/zipdb-import-proof"
REMOTE_DIAGNOSTICS = "/sdcard/.zipdb-import-diagnostics-" + uuid.uuid4().hex
DATABASE = "files/config/patch2/FE8U/proof/PATCH_offline.txt"

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("--serial", required=True)
parser.add_argument("--avd", required=True)
parser.add_argument("--base-apk", required=True)
parser.add_argument("--upgrade-apk", required=True)
parser.add_argument("--timeout", type=int, default=90)
args = parser.parse_args()
if not re.fullmatch(r"emulator-[0-9]{4,5}", args.serial):
    parser.error("An explicit emulator serial is required; physical devices are prohibited.")
if not re.fullmatch(r"zipdb-[A-Za-z0-9_-]{1,96}", args.avd):
    parser.error("Use the exact zipdb-prefixed AVD created for this proof.")
if not 1 <= args.timeout <= 300:
    parser.error("--timeout must be between 1 and 300 seconds.")
root = Path.cwd().resolve()
if not (root / "FEBuilderGBA.sln").is_file():
    parser.error("Run from the repository root.")


def local_apk(value):
    path = Path(value).resolve(strict=True)
    if path.suffix.lower() != ".apk" or not any(
        path.is_relative_to(root / allowed)
        for allowed in ("FEBuilderGBA.Android/bin", "TestResults")
    ):
        raise RuntimeError("APK must be an owned build artifact inside this repository.")
    return path


base_apk, upgrade_apk = local_apk(args.base_apk), local_apk(args.upgrade_apk)
output = root / "TestResults" / ("android-patch-import-" + uuid.uuid4().hex)
output.mkdir(parents=True, exist_ok=False)
report = {
    "passed": False, "serial": args.serial, "avd": args.avd, "package": PKG,
    "screenshots": [], "assertions": [],
    "visual_inspection": "Pending independent inspection; automation is not screenshot approval.",
}
installed = False
diagnostics_created = False


def command(argv, timeout=60, check=True):
    result = subprocess.run(argv, stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=timeout)
    if len(result.stdout) > 2 * 1024 * 1024 or len(result.stderr) > 256 * 1024:
        raise RuntimeError("Unexpectedly large tool output.")
    if check and result.returncode:
        detail = result.stderr.decode("utf-8", errors="replace")[:500]
        raise RuntimeError(f"Command failed ({result.returncode}): {argv[0]} {detail}")
    return result


def adb(*parts, check=True, timeout=60):
    return command(["adb", "-s", args.serial, *parts], timeout, check)


def text(*parts, check=True):
    return adb(*parts, check=check).stdout.decode("utf-8", errors="replace").strip()


def sha(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def assert_fact(condition, description):
    if not condition:
        raise RuntimeError(description)
    report["assertions"].append(description)


def wait_for(probe, description):
    deadline = time.monotonic() + args.timeout
    while time.monotonic() < deadline:
        value = probe()
        if value:
            return value
        time.sleep(0.5)
    raise RuntimeError("Timed out: " + description)


def screenshot(name):
    path = output / (name + ".png")
    with path.open("wb") as stream:
        process = subprocess.Popen(["adb", "-s", args.serial, "exec-out", "screencap", "-p"],
                                   stdout=stream, stderr=subprocess.PIPE)
        try:
            _, error = process.communicate(timeout=30)
        except subprocess.TimeoutExpired:
            process.kill()
            process.communicate()
            raise
    if process.returncode or not 8 <= path.stat().st_size <= 16 * 1024 * 1024:
        raise RuntimeError("Screenshot capture failed: " + error.decode("utf-8", errors="replace")[:200])
    with path.open("rb") as stream:
        if stream.read(8) != b"\x89PNG\r\n\x1a\n":
            raise RuntimeError("Screenshot is not a PNG.")
    report["screenshots"].append({"file": path.name, "sha256": sha(path)})


def nodes():
    remote = REMOTE_DIAGNOSTICS + "/hierarchy.xml"
    adb("shell", "rm", "-f", remote)
    adb("shell", "uiautomator", "dump", remote, timeout=30)
    # A cold-start frame may have no accessibility root yet. Never reuse an old
    # dump; let the existing bounded wait retry only when no new file was made.
    if adb("shell", "test", "-s", remote, check=False).returncode:
        return []
    xml = text("exec-out", "cat", remote)
    start = xml.find("<?xml")
    if start < 0:
        raise RuntimeError("No accessibility hierarchy was returned.")
    return list(ET.fromstring(xml[start:]).iter("node"))


def find_node(identifier=None, label=None, contains=None):
    for node in nodes():
        values = [node.get("text", ""), node.get("content-desc", "")]
        resource = node.get("resource-id", "")
        matches = ((identifier and (resource == identifier or resource.endswith("/" + identifier)
                                    or identifier in values))
                   or (label and label in values)
                   or (contains and any(contains in value for value in values)))
        if matches and node.get("enabled", "true") == "true":
            bounds = re.fullmatch(r"\[(\d+),(\d+)\]\[(\d+),(\d+)\]", node.get("bounds", ""))
            if bounds and int(bounds[3]) > int(bounds[1]) and int(bounds[4]) > int(bounds[2]):
                return dict(node.attrib)
    return None


def tap_node(node):
    x1, y1, x2, y2 = map(int, re.findall(r"\d+", node["bounds"]))
    adb("shell", "input", "tap", str((x1 + x2) // 2), str((y1 + y2) // 2))


def tap(identifier=None, label=None, contains=None):
    tap_node(wait_for(lambda: find_node(identifier, label, contains), identifier or label or contains))


def tap_fixture(filename):
    samples = []
    previous = None

    def stable_target():
        nonlocal previous
        node = find_node(label=filename)
        if not node:
            previous = None
            return None
        signature = (node["bounds"], node.get("resource-id", ""), node.get("package", ""),
                     node.get("text") == filename, node.get("content-desc") == filename)
        samples.append({"bounds": node["bounds"], "text_matches": signature[3],
                        "description_matches": signature[4]})
        if signature == previous:
            return node
        previous = signature
        if len(samples) >= 8:
            raise RuntimeError("No stable fixture geometry after eight fresh snapshots.")
        return None

    node = wait_for(stable_target, "stable fixture geometry for " + filename)
    targets = report.setdefault("picker_targets", [])
    if len(targets) >= 32:
        raise RuntimeError("Unexpectedly many fixture picker targets.")
    x1, y1, x2, y2 = map(int, re.findall(r"\d+", node["bounds"]))
    targets.append({
        "expected_document": filename, "bounds": node["bounds"],
        "tap": [(x1 + x2) // 2, (y1 + y2) // 2],
        "text_matches": node.get("text") == filename,
        "description_matches": node.get("content-desc") == filename,
        "resource_id": node.get("resource-id", ""),
        "package": node.get("package", ""),
        "geometry_samples": samples,
    })
    tap_node(node)


def pick_document(filename):
    if find_node(label=filename):
        tap_fixture(filename)
        return
    # Downloads/Recent can omit adb-pushed documents even inside a visible
    # directory. Use the actual primary-storage provider through normal UI.
    model = text("shell", "getprop", "ro.product.model")

    def primary_storage():
        for label in ("Internal storage", "Internal shared storage", model):
            if label:
                node = find_node(label=label)
                if node:
                    return node
        return None

    tap(label="Show roots")
    primary = primary_storage()
    if not primary:
        adb("shell", "input", "keyevent", "4")
        tap(label="More options")
        show = find_node(label="Show internal storage")
        if show:
            tap_node(show)
        else:
            adb("shell", "input", "keyevent", "4")
        tap(label="Show roots")
        primary = wait_for(primary_storage, "owned emulator primary storage")
    tap_node(primary)
    tap(label="Download")
    tap(label="zipdb-import-proof")
    tap_fixture(filename)


def native_provider_receipt(uri, filename):
    fixture = output / filename
    limit = fixture.stat().st_size + 4096
    process = subprocess.Popen(
        ["adb", "-s", args.serial, "exec-out", "run-as", PKG, "content", "read", "--uri", uri],
        stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
    timed_out = threading.Event()

    def expire():
        timed_out.set()
        try:
            process.kill()
        except OSError:
            pass

    timer = threading.Timer(30, expire)
    timer.daemon = True
    digest = hashlib.sha256()
    count = 0
    prefix = bytearray()
    timer.start()
    try:
        while chunk := process.stdout.read(64 * 1024):
            count += len(chunk)
            if count > limit:
                raise RuntimeError("Native diagnostic exceeded the selected fixture bound.")
            digest.update(chunk)
            if len(prefix) < 768:
                prefix.extend(chunk[:768 - len(prefix)])
        result = process.wait(timeout=5)
        matches = not timed_out.is_set() and result == 0 and count == fixture.stat().st_size and \
            digest.hexdigest() == fixture_hashes[filename]
        classification = {"kind": "matched-fixture" if matches else "unmatched-diagnostic-output"}
        if not matches and prefix.startswith(b"Error while accessing provider"):
            diagnostic = prefix.decode("utf-8", errors="replace")
            exception = re.search(r"(?:java|android)\.[A-Za-z0-9_.$]*(?:Exception|Error)", diagnostic)
            classification = {
                "kind": "content-cli-error",
                "exception": exception[0] if exception else None,
                "flags": [flag for flag in ("Permission Denial", "attribution", "does not belong",
                                           "com.android.shell", "requires", "not found")
                          if flag.lower() in diagnostic.lower()],
            }
        return {
            "is_separate_diagnostic": True,
            "matches_fixture": matches,
            "diagnostic_output_bytes": count, "diagnostic_output_sha256": digest.hexdigest(),
            "exit_code": result, "timed_out": timed_out.is_set(),
            "error_classification": classification,
            "meaning": "Separate provider read under app UID; not the original managed stream. "
                       "An exit-zero error diagnostic is not document data.",
        }
    finally:
        timer.cancel()
        if process.poll() is None:
            process.kill()
            process.wait(timeout=5)
        process.stdout.close()


def live_app_identity():
    package = text("shell", "pm", "list", "packages", "-U", PKG)
    uid = re.fullmatch(r"package:" + re.escape(PKG) + r" uid:(\d+)", package)
    pid = text("shell", "pidof", PKG)
    if not uid or not re.fullmatch(r"[1-9]\d*", pid):
        raise RuntimeError("Cannot identify exactly one live owned app process.")
    stat = text("shell", "run-as", PKG, "cat", "/proc/" + pid + "/stat")
    fields = stat.rpartition(")")[2].split()
    if not stat.startswith(pid + " (") or len(fields) < 20 or not fields[19].isdigit():
        raise RuntimeError("Cannot bind the owned app process start time.")
    boot = text("shell", "cat", "/proc/sys/kernel/random/boot_id")
    if not re.fullmatch(r"[0-9a-fA-F]{8}(?:-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}", boot):
        raise RuntimeError("Cannot bind the owned emulator boot session.")
    return {"uid": int(uid[1]), "pid": int(pid), "start_ticks": fields[19], "boot_id": boot}


def require_app_identity(identity):
    if live_app_identity() != identity:
        raise RuntimeError("App session changed during document attribution.")


def app_grant_snapshot(identity):
    require_app_identity(identity)
    metadata = text("shell", "dumpsys", "activity", "permissions", PKG)
    require_app_identity(identity)
    current_uid = None
    pending = None
    uris = set()
    for line in metadata.splitlines():
        uid = re.search(r"\* UID (\d+) holds:", line)
        if uid:
            current_uid, pending = int(uid[1]), None
        uri = re.search(r"UriPermission\{[^ ]+ (content://[^\s]+) ", line)
        if uri:
            pending = uri[1] if current_uid == identity["uid"] else None
        if pending and re.search(r"\btargetPkg=" + re.escape(PKG) + r"(?:\s|$)", line):
            uris.add(pending)
            pending = None
            if len(uris) > 16:
                raise RuntimeError("Too many app-specific URI grants for bounded attribution.")
    return uris


def begin_rom_selection():
    identity = live_app_identity()
    return {"identity": identity, "uris": app_grant_snapshot(identity)}


def verify_rom_document_receipt(before):
    def grant():
        after = app_grant_snapshot(before["identity"])
        new = after - before["uris"]
        if not new:
            return None
        if len(new) != 1:
            raise RuntimeError("New ROM document grants are ambiguous for the same live app session.")
        return new.pop(), after

    uri, after = wait_for(grant, "new URI grant for the same live app session")
    parsed = urlsplit(uri)
    prefix = "/document/primary:Download/zipdb-import-proof/"
    decoded = unquote(parsed.path)
    if (parsed.scheme != "content" or parsed.netloc != "com.android.externalstorage.documents"
            or parsed.query or parsed.fragment or not decoded.startswith(prefix)):
        raise RuntimeError("ROM picker granted a document outside the controlled fixture provider.")
    filename = decoded[len(prefix):]
    if filename not in fixture_hashes:
        raise RuntimeError("ROM picker granted an unexpected document outside the fixture allowlist.")
    receipts = report.setdefault("rom_document_receipts", [])
    if len(receipts) >= 4:
        raise RuntimeError("Unexpectedly many ROM document receipts.")
    receipt = {
        "expected": "zipdb-proof.gba", "granted": filename, "app_session": before["identity"],
        "before_count": len(before["uris"]), "after_count": len(after), "new_count": 1,
        "before_grant_hashes": sorted(hashlib.sha256(u.encode()).hexdigest() for u in before["uris"]),
        "after_grant_hashes": sorted(hashlib.sha256(u.encode()).hexdigest() for u in after),
        "new_grant_hash": hashlib.sha256(uri.encode()).hexdigest(),
        "attribution": "New app-UID grant between before/after snapshots with unchanged PID/start/boot; "
                       "not a measurement of original managed stream bytes.",
    }
    receipts.append(receipt)
    receipt["native_reader"] = native_provider_receipt(uri, filename)
    require_app_identity(before["identity"])
    if filename != "zipdb-proof.gba":
        raise RuntimeError("ROM picker granted " + filename + " instead of zipdb-proof.gba.")


def launch():
    component = text("shell", "cmd", "package", "resolve-activity", "--brief", PKG).splitlines()[-1]
    if not component.startswith(PKG + "/") or not re.fullmatch(r"[A-Za-z0-9_.$/]+", component):
        raise RuntimeError("Unexpected launcher component.")
    adb("shell", "am", "start", "-n", component)
    wait_for(lambda: find_node("Main_AndroidOpenRom_Button", "Open ROM"), "real app launcher")


def open_library():
    before = begin_rom_selection()
    tap("Main_AndroidOpenRom_Button", "Open ROM")
    pick_document("zipdb-proof.gba")
    verify_rom_document_receipt(before)
    tap("Main_LauncherFilter_Input", contains="Type to filter editors")
    adb("shell", "input", "text", "Patch")
    adb("shell", "input", "keyevent", "111")  # dismiss only this AVD's keyboard
    tap(label="Patch Manager")
    wait_for(lambda: find_node("PatchManager_ImportPatchDatabase_Button", "Import Patch Database ZIP"),
             "actual Patch Manager")


def choose_zip(name):
    tap("PatchManager_ImportPatchDatabase_Button", "Import Patch Database ZIP")
    pick_document(name)


def database_hash():
    result = adb("shell", "run-as", PKG, "sha256sum", DATABASE, check=False)
    if result.returncode:
        return ""
    return result.stdout.decode("ascii", errors="replace").split()[0]


def verify_library():
    expected = hashlib.sha256(DEFINITION).hexdigest()
    assert_fact(database_hash() == expected, "Canonical database descriptor matches the generated fixture.")
    payload = text("shell", "run-as", PKG, "sha256sum",
                   "files/config/patch2/FE8U/proof/payload.bin").split()[0]
    assert_fact(payload == hashlib.sha256(PAYLOAD).hexdigest(), "Canonical payload hash is unchanged.")
    wait_for(lambda: find_node(label=LABEL), "actual imported library row")
    return expected


def stamp_version():
    return text("shell", "run-as", PKG, "head", "-n", "1", "files/.config_version")


try:
    assert_fact(text("get-state") == "device", "Explicit emulator is connected.")
    assert_fact(text("shell", "getprop", "ro.kernel.qemu") == "1", "Device is an emulator, not physical hardware.")
    assert_fact(text("emu", "avd", "name").splitlines()[0] == args.avd, "AVD identity matches this proof's owner.")
    assert_fact(text("shell", "pm", "list", "packages", PKG) == "", "No pre-existing app will be replaced.")
    sdk = Path(os.environ.get("ANDROID_HOME", os.environ.get("ANDROID_SDK_ROOT", "")))
    tools = sorted((sdk / "build-tools").glob("*/aapt2*"), reverse=True)
    aapt = shutil.which("aapt2") or next((str(p) for p in tools if p.name in ("aapt2", "aapt2.exe")), None)
    if not aapt:
        raise RuntimeError("Android build-tools aapt2 is required to verify APK identity.")
    versions = []
    for apk in (base_apk, upgrade_apk):
        badging = command([aapt, "dump", "badging", str(apk)]).stdout.decode("utf-8", errors="replace")
        match = re.search(r"^package: name='([^']+)' versionCode='(\d+)'", badging, re.M)
        assert_fact(bool(match) and match[1] == PKG and "application-debuggable" in badging,
                    "Proof APK has the expected package and ordinary Debug configuration.")
        versions.append(int(match[2]))
    assert_fact(versions[1] > versions[0], "Upgrade APK increases the normal Android application version.")
    report["apks"] = [{"name": p.name, "sha256": sha(p), "version_code": v}
                      for p, v in zip((base_apk, upgrade_apk), versions)]
    report["source_head"] = command(["git", "rev-parse", "HEAD"]).stdout.decode("ascii").strip()
    report["source_dirty"] = bool(command(["git", "status", "--porcelain"]).stdout.strip())

    rom = output / "zipdb-proof.gba"
    generator = root / "scripts/SyntheticProofFixtures/bin/Debug/net10.0/SyntheticProofFixtures.dll"
    if not generator.is_file():
        raise RuntimeError("Build the reviewed SyntheticProofFixtures project in Debug before the proof.")
    generated = json.loads(command(["dotnet", str(generator), str(rom)]).stdout.decode("utf-8"))
    assert_fact(generated == {
        "Format": "fe8u-synthetic-huffman-v1",
        "Length": 16 * 1024 * 1024,
        "Sha256": sha(rom),
    } and rom.stat().st_size == generated["Length"],
        "The shared synthetic-ROM generator receipt matches the actual source fixture.")
    report["fixture_generator"] = dict(generated, assembly_sha256=sha(generator))
    valid = output / "zipdb-valid.zip"
    invalid = output / "zipdb-invalid.zip"
    with zipfile.ZipFile(valid, "w", compression=zipfile.ZIP_STORED) as archive:
        archive.writestr("FE8U/proof/PATCH_offline.txt", DEFINITION)
        archive.writestr("FE8U/proof/payload.bin", PAYLOAD)
    with zipfile.ZipFile(invalid, "w") as archive:
        archive.writestr("FE8U/../../zipdb-import-escape.txt", b"generated rejection fixture")
    fixture_hashes = {p.name: sha(p) for p in (rom, valid, invalid)}
    if REMOTE_DIAGNOSTICS == REMOTE_DIR or REMOTE_DIAGNOSTICS.startswith(REMOTE_DIR + "/"):
        raise RuntimeError("Diagnostics must not be written inside the browsed fixture tree.")
    # mkdir without -p refuses a collision; cleanup later removes only our two
    # known files and this empty directory, never a recursive or shared path.
    adb("shell", "mkdir", REMOTE_DIAGNOSTICS)
    diagnostics_created = True
    report["owned_diagnostic_directory"] = REMOTE_DIAGNOSTICS
    adb("shell", "touch", REMOTE_DIAGNOSTICS + "/.nomedia")
    report["diagnostics_outside_fixture_tree"] = True
    adb("shell", "wm", "size", "1280x800")
    adb("shell", "wm", "density", "160")
    report["viewport"] = "1280x800 at 160 dpi (owned emulator tablet viewport)"
    adb("shell", "svc", "wifi", "disable")
    adb("shell", "svc", "data", "disable")
    adb("shell", "mkdir", "-p", REMOTE_DIR)
    for path in (rom, valid, invalid):
        adb("push", str(path), REMOTE_DIR + "/" + path.name)
    assert_fact(sorted(text("shell", "ls", "-A", REMOTE_DIR).splitlines()) == sorted(fixture_hashes),
                "The browsed fixture directory contains exactly the three generated documents.")
    adb("install", "--streaming", str(base_apk), timeout=180)
    installed = True
    launch()
    before_stamp = stamp_version()
    report["app_private_base"] = text("shell", "run-as", PKG, "pwd")
    report["abi"] = text("shell", "getprop", "ro.product.cpu.abi")
    report["api"] = text("shell", "getprop", "ro.build.version.sdk")
    open_library()
    choose_zip(valid.name)
    tap("MessageBoxContent_No_Button", "No")
    assert_fact(database_hash() == "", "Declining confirmation leaves no installed database.")
    choose_zip(valid.name)
    wait_for(lambda: find_node("MessageBoxContent_Yes_Button", "Yes"), "replacement consent")
    screenshot("confirmation")
    tap("MessageBoxContent_Yes_Button", "Yes")
    wait_for(database_hash, "validated promotion")
    wait_for(lambda: find_node(contains="list refreshed"), "truthful import refresh")
    verify_library()
    screenshot("imported")
    choose_zip(invalid.name)
    wait_for(lambda: find_node(contains="import failed"), "unsafe archive rejection")
    verify_library()
    assert_fact(adb("shell", "run-as", PKG, "test", "-e", "files/config/zipdb-import-escape.txt",
                    check=False).returncode != 0, "Rejected traversal creates no escaped file.")
    screenshot("rejected")
    adb("shell", "am", "force-stop", PKG)
    launch()
    open_library()
    verify_library()
    screenshot("relaunch")
    adb("shell", "am", "force-stop", PKG)
    adb("install", "--streaming", "-r", str(upgrade_apk), timeout=180)
    launch()
    after_stamp = stamp_version()
    assert_fact(before_stamp != after_stamp and after_stamp.endswith("-" + str(versions[1])),
                "Real app upgrade refreshes the bundled-config version stamp.")
    report["config_stamps"] = [before_stamp, after_stamp]
    open_library()
    verify_library()
    screenshot("upgrade")
    for name, digest in fixture_hashes.items():
        actual = text("shell", "sha256sum", REMOTE_DIR + "/" + name).split()[0]
        assert_fact(actual == digest, "SAF source document remains unchanged: " + name)
    report["fixture_hashes"] = fixture_hashes
    report["passed"] = True
except Exception as error:
    report["error"] = str(error)[:2000]
    if installed:
        try:
            screenshot("failure")
        except Exception:
            pass
    print("FAIL: " + report["error"], file=sys.stderr)
finally:
    if installed:
        try:
            stopped = adb("shell", "am", "force-stop", PKG, check=False)
            if stopped.returncode:
                report["passed"] = False
                report["stop_error"] = "Owned app force-stop failed."
        except Exception as error:
            report["passed"] = False
            report["stop_error"] = str(error)[:500]
    if diagnostics_created:
        try:
            cleaned = True
            for leaf in ("hierarchy.xml", ".nomedia"):
                if adb("shell", "rm", "-f", REMOTE_DIAGNOSTICS + "/" + leaf, check=False).returncode:
                    cleaned = False
            if adb("shell", "rmdir", REMOTE_DIAGNOSTICS, check=False).returncode:
                cleaned = False
            report["diagnostics_cleaned"] = cleaned
            if not cleaned:
                report["passed"] = False
                report["diagnostic_cleanup_error"] = "Owned diagnostic directory cleanup failed."
        except Exception:
            report["passed"] = False
            report["diagnostic_cleanup_error"] = "Owned diagnostic directory cleanup failed."
    (output / "report.json").write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print("Android import proof report: " + str(output / "report.json"))
sys.exit(0 if report["passed"] else 1)
PY
