#!/bin/sh
# Publishes amptray-monitor (the service) and amptray (the desktop widget) self-contained (one file
# each, no .NET install needed) and packs a tarball per architecture into artifacts/out, each with
# install.sh, the systemd unit, the udev rule, the tmpfiles entry and the desktop entry.
#   ./build/build-linux.sh            # version from src/AmpTray/AmpTray.csproj
#   ./build/build-linux.sh 0.2.0      # what CI runs for tag v0.2.0
#   RIDS=linux-x64 ./build/build-linux.sh
set -eu

root=$(cd "$(dirname "$0")/.." && pwd)
version=${1:-}
if [ -z "$version" ]; then
    version=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$root/src/AmpTray/AmpTray.csproj" | head -n 1)
fi
numeric=${version%%-*}   # file versions must be numeric: drop any -prerelease suffix
out="$root/artifacts/out"
mkdir -p "$out"

for rid in ${RIDS:-linux-x64 linux-arm64}; do
    echo "== Publishing $version ($rid)"
    pub="$root/artifacts/publish-$rid"
    rm -rf "$pub"
    for proj in AmpTray.Monitor AmpTray.Avalonia; do
        dotnet publish "$root/src/$proj/$proj.csproj" -c Release -r "$rid" --self-contained \
            -p:Version="$version" -p:FileVersion="$numeric" \
            -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true \
            -p:PublishTrimmed=false -o "$pub" --nologo -v q
    done

    name="amptray-$version-$rid"
    stage="$root/artifacts/stage/$name"
    rm -rf "$stage"
    mkdir -p "$stage"
    cp "$pub/amptray-monitor" "$pub/amptray" "$stage/"
    cp "$root/build/linux/install.sh" "$root/build/linux/uninstall.sh" \
       "$root/build/linux/amptray-monitor.service" "$root/build/linux/70-amptray.rules" \
       "$root/build/linux/amptray.conf" "$root/build/linux/amptray.desktop" \
       "$root/LICENSE" "$stage/"
    cp "$root/src/AmpTray.Avalonia/Assets/app.png" "$stage/amptray.png"
    chmod 755 "$stage/amptray-monitor" "$stage/amptray" "$stage/install.sh" "$stage/uninstall.sh"
    chmod 644 "$stage/amptray-monitor.service" "$stage/70-amptray.rules" "$stage/amptray.conf" "$stage/amptray.desktop" "$stage/amptray.png" "$stage/LICENSE"
    tar -C "$root/artifacts/stage" -czf "$out/AmpTray-$version-$rid.tar.gz" "$name"
done

echo "== Done"
ls -l "$out"
