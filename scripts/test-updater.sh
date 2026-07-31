#!/usr/bin/env bash
# Phase-2 test: drive the rebuilt RevitMCPUpdater.exe through the cases that
# matter — happy-path extract, AutoCAD profile, legacy Revit shortcut — without
# touching the user's real Autodesk Addins folder.
#
# Usage: bash scripts/test-updater.sh
set -euo pipefail
cd "$(dirname "$0")/.."

UPDATER="updater/bin/Release/net8.0-windows/RevitMCPUpdater.exe"
[ -f "$UPDATER" ] || { echo "FAIL: updater not built. Run: dotnet build updater/Updater.csproj -c Release"; exit 1; }

# Workspace
ROOT_TMP="$(mktemp -d)"
trap "rm -rf '$ROOT_TMP'" EXIT
TEST_ZIP="$ROOT_TMP/test-plugin.zip"
TARGET_DIR="$ROOT_TMP/target"
MISSING_ZIP="$ROOT_TMP/does-not-exist.zip"

# Build a tiny zip with one file we can later assert on.
# PowerShell's Compress-Archive needs Windows paths; convert via cygpath.
mkdir -p "$ROOT_TMP/src"
echo "hello-from-test" > "$ROOT_TMP/src/marker.txt"
SRC_WIN=$(cygpath -w "$ROOT_TMP/src/marker.txt")
ZIP_WIN=$(cygpath -w "$TEST_ZIP")
powershell -Command "Compress-Archive -Path '$SRC_WIN' -DestinationPath '$ZIP_WIN'" >/dev/null
[ -f "$TEST_ZIP" ] || { echo "FAIL: could not build test zip at $TEST_ZIP"; exit 1; }

fail=0
pass=0
check() {
  local name="$1"; shift
  local want_code="$1"; shift
  if "$@" > "$ROOT_TMP/last.log" 2>&1; then
    actual_code=0
  else
    actual_code=$?
  fi
  if [ "$actual_code" = "$want_code" ]; then
    echo "[ OK ] $name (exit $actual_code)"
    pass=$((pass+1))
  else
    echo "[FAIL] $name — wanted exit $want_code, got $actual_code"
    cat "$ROOT_TMP/last.log" | head -10 | sed 's/^/        /'
    fail=$((fail+1))
  fi
}

assert_log_contains() {
  local needle="$1"
  if grep -qF "$needle" "$ROOT_TMP/last.log"; then
    echo "[ OK ]   log contains: $needle"
    pass=$((pass+1))
  else
    echo "[FAIL]   log missing : $needle"
    fail=$((fail+1))
  fi
}

assert_file_contains() {
  local path="$1"; local needle="$2"
  if [ -f "$path" ] && grep -qF "$needle" "$path"; then
    echo "[ OK ]   extracted $path"
    pass=$((pass+1))
  else
    echo "[FAIL]   missing or wrong: $path"
    fail=$((fail+1))
  fi
}

assert_file_absent() {
  local path="$1"
  if [ ! -e "$path" ]; then
    echo "[ OK ]   path was not created: $path"
    pass=$((pass+1))
  else
    echo "[FAIL]   unexpected path exists: $path"
    fail=$((fail+1))
  fi
}

echo "── T1: happy-path extract via --addins-dir override ──"
check "extract" 0 "$UPDATER" --zip "$TEST_ZIP" --no-wait --addins-dir "$TARGET_DIR"
assert_file_contains "$TARGET_DIR/marker.txt" "hello-from-test"

echo "── T2: missing zip surfaces exit code 3 ──"
check "missing-zip" 3 "$UPDATER" --zip "$MISSING_ZIP" --no-wait --addins-dir "$TARGET_DIR"
assert_log_contains "ERROR: zip not found"

echo "── T3: legacy Revit shortcut (--revit-year only, no --product) routes to revit ──"
check "legacy-revit" 3 "$UPDATER" --zip "$MISSING_ZIP" --no-wait --revit-year 2025 --addins-dir "$TARGET_DIR/legacy"
assert_log_contains "Product:       revit"

echo "── T4: AutoCAD profile resolves to ApplicationPlugins\\<name>.bundle ──"
check "autocad-bundle" 3 "$UPDATER" --zip "$MISSING_ZIP" --no-wait --product autocad --bundle-name TestBundle
assert_log_contains "Product:       autocad"
assert_log_contains "Process name:  acad"
assert_log_contains "ApplicationPlugins"
assert_log_contains "TestBundle.bundle"

echo "── T5: AutoCAD default bundle name (AutoCADMCP) when --bundle-name omitted ──"
check "autocad-default-bundle" 3 "$UPDATER" --zip "$MISSING_ZIP" --no-wait --product autocad
assert_log_contains "AutoCADMCP.bundle"

echo "── T6: unknown product surfaces exit code 2 ──"
check "unknown-product" 2 "$UPDATER" --zip "$MISSING_ZIP" --no-wait --product solidworks

echo "── T7: Zip Slip entry is rejected before touching the target ──"
TRAVERSAL_ZIP="$ROOT_TMP/traversal.zip"
TRAVERSAL_ZIP_WIN=$(cygpath -w "$TRAVERSAL_ZIP")
export TRAVERSAL_ZIP_WIN
powershell -NoProfile -Command '
  Add-Type -AssemblyName System.IO.Compression
  Add-Type -AssemblyName System.IO.Compression.FileSystem
  $archive = [IO.Compression.ZipFile]::Open(
    $env:TRAVERSAL_ZIP_WIN,
    [IO.Compression.ZipArchiveMode]::Create)
  try {
    $entry = $archive.CreateEntry("../escaped.txt")
    $writer = [IO.StreamWriter]::new($entry.Open())
    try { $writer.Write("must-not-escape") } finally { $writer.Dispose() }
  }
  finally {
    $archive.Dispose()
  }
' >/dev/null
echo "preserve-before-rejected-update" > "$TARGET_DIR/preserved.txt"
check "zip-slip" 1 "$UPDATER" --zip "$TRAVERSAL_ZIP" --no-wait --addins-dir "$TARGET_DIR"
assert_log_contains "unsafe path segment"
assert_file_absent "$ROOT_TMP/escaped.txt"
assert_file_contains "$TARGET_DIR/preserved.txt" "preserve-before-rejected-update"

echo "── T8: normal replacement keeps a .bak copy of the previous file ──"
REPLACE_ZIP="$ROOT_TMP/replacement.zip"
mkdir -p "$ROOT_TMP/replacement-src"
echo "hello-after-replacement" > "$ROOT_TMP/replacement-src/marker.txt"
REPLACE_SRC_WIN=$(cygpath -w "$ROOT_TMP/replacement-src/marker.txt")
REPLACE_ZIP_WIN=$(cygpath -w "$REPLACE_ZIP")
powershell -NoProfile -Command \
  "Compress-Archive -Path '$REPLACE_SRC_WIN' -DestinationPath '$REPLACE_ZIP_WIN'" \
  >/dev/null
check "replace-existing" 0 "$UPDATER" --zip "$REPLACE_ZIP" --no-wait --addins-dir "$TARGET_DIR"
assert_file_contains "$TARGET_DIR/marker.txt" "hello-after-replacement"
assert_file_contains "$TARGET_DIR/marker.txt.bak" "hello-from-test"

echo
echo "passed: $pass  failed: $fail"
[ "$fail" = 0 ] || exit 1
