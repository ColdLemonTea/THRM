#!/bin/sh
set -eu

application_id=com.tianli0.thrm_ui
bundle_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
data_dir=${XDG_DATA_HOME:-"$HOME/.local/share"}
applications_dir=$data_dir/applications
icons_dir=$data_dir/icons/hicolor/256x256/apps

install -d "$applications_dir" "$icons_dir"
install -m 644 \
  "$bundle_dir/data/flutter_assets/assets/brand/appicon.png" \
  "$icons_dir/$application_id.png"

cat >"$applications_dir/$application_id.desktop" <<EOF
[Desktop Entry]
Type=Application
Name=THRM Flutter Preview
Exec="$bundle_dir/thrm_ui"
Icon=$application_id
Terminal=false
Categories=Utility;
StartupWMClass=$application_id
EOF

if command -v update-desktop-database >/dev/null 2>&1; then
  update-desktop-database "$applications_dir"
fi
if command -v kbuildsycoca6 >/dev/null 2>&1; then
  kbuildsycoca6 >/dev/null 2>&1 || true
elif command -v kbuildsycoca5 >/dev/null 2>&1; then
  kbuildsycoca5 >/dev/null 2>&1 || true
fi

printf '%s\n' 'Wayland icon registered. Restart THRM Flutter Preview.'
