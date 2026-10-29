#!/bin/sh
# Publishes amptray-monitor self-contained (one file, no .NET install needed) and packs a tarball
# per architecture into artifacts/out, each with install.sh, the systemd unit and the udev rule.
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
    dotnet publish "$root/src/AmpTray.Monitor/AmpTray.Monitor.csproj" -c Release -r "$rid" --self-contained \
        -p:Version="$version" -p:FileVersion="$numeric" \
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true \
        -o "$pub" --nologo -v q

    name="amptray-$version-$rid"
    stage="$root/artifacts/stage/$name"
    rm -rf "$stage"
    mkdir -p "$stage"
    cp "$pub/amptray-monitor" "$stage/"
    cp "$root/build/linux/install.sh" "$root/build/linux/uninstall.sh" \
       "$root/build/linux/amptray-monitor.service" "$root/build/linux/70-amptray.rules" \
       "$root/LICENSE" "$stage/"
    chmod 755 "$stage/amptray-monitor" "$stage/install.sh" "$stage/uninstall.sh"
    chmod 644 "$stage/amptray-monitor.service" "$stage/70-amptray.rules" "$stage/LICENSE"
    tar -C "$root/artifacts/stage" -czf "$out/AmpTray-$version-$rid.tar.gz" "$name"
done

echo "== Done"
ls -l "$out"
