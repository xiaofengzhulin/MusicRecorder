#!/bin/bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
DIST_DIR="$SCRIPT_DIR/dist/macos-arm64"
APP_DIR="$DIST_DIR/MusicRecorder.app"
BUILD_DIR="$SCRIPT_DIR/.build/apple-silicon"
MODULE_CACHE="$BUILD_DIR/module-cache"

cd "$SCRIPT_DIR"

if [[ "$(uname -m)" != "arm64" ]]; then
    echo "此脚本用于 Apple Silicon（arm64）Mac；当前架构：$(uname -m)" >&2
    exit 1
fi

mkdir -p "$BUILD_DIR" "$MODULE_CACHE"

choose_sdk() {
    local default_sdk
    default_sdk="$(xcrun --sdk macosx --show-sdk-path)"
    local candidates=("$default_sdk")
    local sdk
    while IFS= read -r sdk; do
        [[ "$sdk" == "$default_sdk" ]] || candidates+=("$sdk")
    done < <(find /Library/Developer/CommandLineTools/SDKs -maxdepth 1 -name 'MacOSX*.sdk' -type d 2>/dev/null | sort -Vr)

    for sdk in "${candidates[@]}"; do
        if printf 'import SwiftUI\n' | swiftc \
            -sdk "$sdk" \
            -target arm64-apple-macosx13.0 \
            -module-cache-path "$MODULE_CACHE" \
            -typecheck - >/dev/null 2>&1; then
            printf '%s\n' "$sdk"
            return 0
        fi
    done
    return 1
}

SDK_PATH="$(choose_sdk)" || {
    echo "找不到与当前 Swift 工具链匹配的 macOS SDK。请更新 Xcode Command Line Tools。" >&2
    exit 1
}

COMMON_FLAGS=(
    -sdk "$SDK_PATH"
    -target arm64-apple-macosx13.0
    -swift-version 6
    -module-cache-path "$MODULE_CACHE"
)

echo "[1/4] 运行文件命名与重名处理测试"
swiftc "${COMMON_FLAGS[@]}" \
    "$SCRIPT_DIR/Sources/MusicRecorderMac/FileNaming.swift" \
    "$SCRIPT_DIR/Tests/Smoke/main.swift" \
    -o "$BUILD_DIR/MusicRecorderSmokeTests"
"$BUILD_DIR/MusicRecorderSmokeTests"

swiftc "${COMMON_FLAGS[@]}" \
    "$SCRIPT_DIR/Sources/MusicRecorderMac/NeteaseHistoryReader.swift" \
    "$SCRIPT_DIR/Tests/NeteaseSmoke/main.swift" \
    -o "$BUILD_DIR/NeteaseHistorySmokeTests"
"$BUILD_DIR/NeteaseHistorySmokeTests"

swiftc "${COMMON_FLAGS[@]}" \
    "$SCRIPT_DIR/Sources/MusicRecorderMac/AutomaticRecordingPolicy.swift" \
    "$SCRIPT_DIR/Tests/AutomaticRecordingSmoke/main.swift" \
    -o "$BUILD_DIR/AutomaticRecordingSmokeTests"
"$BUILD_DIR/AutomaticRecordingSmokeTests"

echo "[2/4] 构建 arm64 Release"
swiftc "${COMMON_FLAGS[@]}" \
    -parse-as-library \
    -O \
    -whole-module-optimization \
    "$SCRIPT_DIR"/Sources/MusicRecorderMac/*.swift \
    -framework SwiftUI \
    -framework AppKit \
    -framework ScreenCaptureKit \
    -framework AVFoundation \
    -framework CoreMedia \
    -framework CoreGraphics \
    -o "$BUILD_DIR/MusicRecorder"

echo "[3/4] 组装应用包"
if [[ -d "$APP_DIR" && "$APP_DIR" == "$SCRIPT_DIR/dist/macos-arm64/MusicRecorder.app" ]]; then
    rm -rf "$APP_DIR"
fi
mkdir -p "$APP_DIR/Contents/MacOS" "$APP_DIR/Contents/Resources"
cp "$BUILD_DIR/MusicRecorder" "$APP_DIR/Contents/MacOS/MusicRecorder"
cp "$SCRIPT_DIR/Resources/Info.plist" "$APP_DIR/Contents/Info.plist"
printf 'APPL????' > "$APP_DIR/Contents/PkgInfo"

echo "[4/4] 临时签名并校验"
xattr -cr "$APP_DIR"
codesign --force --deep --sign - "$APP_DIR"
codesign --verify --deep --strict --verbose=2 "$APP_DIR"
file "$APP_DIR/Contents/MacOS/MusicRecorder"

echo
echo "构建完成：$APP_DIR"
echo "首次运行请授予“屏幕与系统音频录制”权限；专用播放器可能需要“自动化”，通用媒体键控制需要“辅助功能”权限。"
