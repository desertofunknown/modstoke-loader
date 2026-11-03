# Prebuilt Package Pointer

Installable Modstoke Loader prebuilts are published as GitHub Release assets instead of being stored directly in Git history.

- Latest package: `https://github.com/desertofunknown/openacai-loader/releases/latest/download/OpenACAILoader.zip`
- Latest manifest: `https://github.com/desertofunknown/openacai-loader/releases/latest/download/OpenACAILoader.manifest.json`
- Latest checksum: `https://github.com/desertofunknown/openacai-loader/releases/latest/download/OpenACAILoader.zip.sha256`

Recommended install path:

1. Download `OpenACAILoader.zip` from the latest GitHub release.
2. Open Modstoke Manager.
3. Select the Sons Of The Forest executable.
4. Install Modstoke Loader and choose this zip when prompted.

Dependency/source links:

- BepInEx: `https://github.com/BepInEx/BepInEx`
- RedLoader/SonsSdk rewrite branch: `https://github.com/ToniMacaroni/RedLoader/tree/rewrite`
- Modstoke Manager: `https://github.com/desertofunknown/openacai-mod-manager`

Modstoke Admin and private companion modules are not included in this package.

Modstoke Manager uses the manifest to verify and repair loader files before launching the game. Mutable user configuration is not hash-enforced.

The current package on this branch is the signed experimental .NET 11 hostfxr preview track. Build output uses maximum standard 7-Zip Deflate settings when available, then release assets carry the install ZIP so GitHub repository file-size warnings are avoided. Keep the .NET 10 LTS package path available on `openacai-net10-port` until Sons Of The Forest startup, title menu, pause menu, and in-world testing are repeatedly verified with this package.
