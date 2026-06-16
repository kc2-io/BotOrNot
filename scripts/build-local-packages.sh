#!/usr/bin/env bash
# Build the patched FortniteReplayReader NuGet packages into local-packages/.
# Run this once after cloning, or whenever patches/apply-patches.py changes.
# Requires: git, python3, dotnet (10.0+)
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
VER="3.0.2-botornot"
FRD="$(mktemp -d)"
OUT="$REPO_ROOT/local-packages"

mkdir -p "$OUT"
trap 'rm -rf "$FRD"' EXIT

echo "==> Cloning FortniteReplayDecompressor at 2fc699e..."
git clone https://github.com/Shiqan/FortniteReplayDecompressor.git "$FRD" -q
git -C "$FRD" checkout 2fc699e -q

echo "==> Applying patches..."
python3 "$REPO_ROOT/patches/apply-patches.py" "$FRD"

# Point the upstream source at our local output so it can find its own deps
cat > "$FRD/NuGet.config" << EOF
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
    <add key="local" value="$OUT" />
  </packageSources>
</configuration>
EOF

pack() {
  local proj="$1"
  local name
  name="$(basename "$(dirname "$proj")")"
  echo "==> Packing $name..."
  dotnet pack "$proj" -c Release -o "$OUT" -p:PackageVersion="$VER" -p:Nowarn=NU5125 --nologo -v q
}

# Step 1: OozSharp (no project deps)
pack "$FRD/src/OozSharp/OozSharp.csproj"

# Step 2: Unreal.Encryption (depends on OozSharp)
python3 - << 'PYEOF'
import sys
path = sys.argv[1]
with open(path) as f: t = f.read()
t = t.replace('<ProjectReference Include="..\\OozSharp\\OozSharp.csproj" />', f'<PackageReference Include="OozSharp" Version="{sys.argv[2]}" />')
with open(path, 'w') as f: f.write(t)
PYEOF
python3 - "$FRD/src/Unreal.Encryption/Unreal.Encryption.csproj" "$VER" << 'PYEOF'
import sys
path = sys.argv[1]
ver = sys.argv[2]
with open(path) as f: t = f.read()
t = t.replace('<ProjectReference Include="..\\OozSharp\\OozSharp.csproj" />', f'<PackageReference Include="OozSharp" Version="{ver}" />')
with open(path, 'w') as f: f.write(t)
PYEOF
pack "$FRD/src/Unreal.Encryption/Unreal.Encryption.csproj"

# Step 3: Unreal.Core (no project deps)
pack "$FRD/src/Unreal.Core/Unreal.Core.csproj"

# Step 4: FortniteReplayReader (depends on Unreal.Core + Unreal.Encryption)
python3 - "$FRD/src/FortniteReplayReader/FortniteReplayReader.csproj" "$VER" << 'PYEOF'
import sys
path = sys.argv[1]
ver = sys.argv[2]
with open(path) as f: t = f.read()
t = t.replace('<ProjectReference Include="..\\Unreal.Core\\Unreal.Core.csproj" />', f'<PackageReference Include="Unreal.Core" Version="{ver}" />')
t = t.replace('<ProjectReference Include="..\\Unreal.Encryption\\Unreal.Encryption.csproj" />', f'<PackageReference Include="Unreal.Encryption" Version="{ver}" />')
with open(path, 'w') as f: f.write(t)
PYEOF
pack "$FRD/src/FortniteReplayReader/FortniteReplayReader.csproj"

echo ""
echo "Done. Packages in $OUT:"
ls "$OUT"/*.nupkg | xargs -n1 basename
