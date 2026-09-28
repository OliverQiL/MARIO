#!/bin/bash
# One-time per machine (Unity must be closed): downloads Unity's official XR Simulation
# sample environments (~40 MB) into ContentPackages/ and imports them into
# Assets/UnityXRContent, then selects the simulated room and adds keyboard input
# (SimulationSetup). Both folders are gitignored.
set -e
cd "$(dirname "$0")/.."
V=2.1.1
N=com.unity.xr-content.xr-sim-environments
UNITY=${UNITY:-/Applications/Unity/Hub/Editor/6000.6.0f1/Unity.app/Contents/MacOS/Unity}
mkdir -p ContentPackages
[ -f "ContentPackages/$N-$V.tgz" ] || curl -fSL -o "ContentPackages/$N-$V.tgz" "https://github.com/Unity-Technologies/$N/releases/download/$V/$N-$V.tgz"
TMP=$(mktemp -d)
tar -xzf "ContentPackages/$N-$V.tgz" -C "$TMP" "package/UnityPackage~/XRSimEnvironments.unitypackage"
"$UNITY" -batchmode -quit -projectPath . -importPackage "$TMP/package/UnityPackage~/XRSimEnvironments.unitypackage" -logFile Logs/sim-import.log
"$UNITY" -batchmode -quit -projectPath . -executeMethod SimulationSetup.Run -logFile Logs/sim-setup.log
rm -rf "$TMP"
grep "^\[SimulationSetup\]" Logs/sim-setup.log
