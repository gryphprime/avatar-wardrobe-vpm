#!/bin/zsh
set -euo pipefail

source_dir=${0:A:h}
bundle_dir="$source_dir/../Plugins/macOS/OutfitToggleAppleIntelligence.bundle"
binary_dir="$bundle_dir/Contents/MacOS"
mkdir -p "$binary_dir"

cat > "$bundle_dir/Contents/Info.plist" <<'PLIST'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
  <key>CFBundleExecutable</key><string>OutfitToggleAppleIntelligence</string>
  <key>CFBundleIdentifier</key><string>local.outfit-toggle.apple-intelligence</string>
  <key>CFBundlePackageType</key><string>BNDL</string>
</dict></plist>
PLIST

swiftc -parse-as-library -emit-library -module-name OutfitToggleAppleIntelligence \
  -target arm64-apple-macosx26.0 \
  "$source_dir/OutfitToggleAppleIntelligence.swift" \
  -o "$binary_dir/OutfitToggleAppleIntelligence"
find "$bundle_dir" -name '._*' -delete
codesign --force --sign - "$bundle_dir"
