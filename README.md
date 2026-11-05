# Unity Sprint Mod for Assassin's Creed Black Flag Resynced

Source code of the installer for the [Unity Sprint Mod (Arno's Sprint for Edward)](https://www.nexusmods.com/assassinscreedblackflagresynced/mods/641).
The mod gives Edward the sprint animation from Assassin's Creed Unity. It is visual only: speed, controls and movement are unchanged.

## What the installer does

The release is a single Windows Installer package, `AC Black Flag Resynced - Unity Sprint Mod.msi`. It:

1. Asks for the game folder and checks that it contains `ACBlackFlag.exe` and `DataPC_boot_patch_01.forge`, and that the game is not running.
2. Installs three files to `C:\Program Files\Unity Sprint Mod\`: `SprintPatch.exe` (the patch engine), `oo2core_9_win64.dll` (the Oodle compression library) and `Unity_Sprint_680cms.Animation` (the new sprint).
3. Runs `SprintPatch.exe install <game folder>`, which:
   - reads `DataPC_boot_patch_01.forge` and finds Edward's full-speed run animation inside it,
   - checks that this animation is the exact original (SHA-256), and stops without changing anything if it is not,
   - writes a new copy of the archive with only that one animation replaced, recompressed with Oodle the same way the game packs it,
   - verifies the new copy (the new animation, and every other entry byte for byte),
   - keeps the original as `DataPC_boot_patch_01.forge.bak` and puts the new file in its place.
4. Stores the chosen game folder in one registry value (`Software\UnitySprintMod\GameDir`) so Repair and Remove find it again.

Uninstalling runs `SprintPatch.exe uninstall <game folder>`, which puts the `.bak` original back. If the game file is already the original, it changes nothing. Repair applies the mod again, for example after a game update replaced the file.

The installer does not connect to the internet, installs no services and leaves nothing running.

## Files

| File | What it is |
| --- | --- |
| `src/SprintPatch.cs` | The patch engine (C#, .NET Framework 4). Reads and rebuilds the game archive. |
| `src/ModActions.cpp` | The installer's custom actions (C++, native DLL): checks the game folder, runs the engine and relays its progress to the installer window. |
| `src/Product.wxs` | The installer definition (WiX Toolset 3.14), including its own welcome, progress and finish pages. |
| `src/en-us.wxl` | Wording for the standard installer pages. |
| `src/art.png` | Artwork for the installer pages. |
| `build.ps1` | Builds the MSI. |

## Inputs that are not in this repository

Two files are needed to build but are not included here:

| File | SHA-256 | Why it is not here |
| --- | --- | --- |
| `oo2core_9_win64.dll` | `d484c81158b4e6edb593de75b1573992e304b008c133c62e7d383eacfd3da995` | Oodle is RAD Game Tools (Epic Games) software. This copy ships with [AnvilToolkit](https://www.nexusmods.com/site/mods/455) in its `Libs` folder. |
| `Unity_Sprint_680cms.Animation` | `37128cb7739139d1e077efc8113ba7326fbe4fcf28b946e246b894186c8e9fba` | Game animation data (Arno's sprint, retargeted to Edward and fitted to the game's footstep timing). |

Both are inside the released MSI. `build.ps1 -FromMsi` takes them out of it with an administrative install (`msiexec /a`), which only unpacks files and does not install anything. The build checks both hashes, and `SprintPatch.exe` checks them again at install time.

## Building

Requirements (Windows):

- [WiX Toolset 3.14](https://wixtoolset.org/docs/wix3/)
- Visual Studio with the C++ desktop build tools (the release was built with Visual Studio 18)
- The .NET Framework 4 C# compiler that ships with Windows (`%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe`)

Steps:

1. Download the mod's zip from Nexus Mods and unzip it.
2. In this folder, run:

   ```
   powershell -ExecutionPolicy Bypass -File build.ps1 -FromMsi "path\to\AC Black Flag Resynced - Unity Sprint Mod.msi" -WixDir "path\to\WiX Toolset 3.14\bin"
   ```

   `-WixDir` can be left out if the WiX installer set the `WIX` environment variable. The Visual C++ tools are found with `vswhere`; pass `-VcVars "path\to\vcvars64.bat"` if they are not.
3. The result is `out\AC Black Flag Resynced - Unity Sprint Mod.msi`.

A rebuilt MSI is the same size as the released one and contains the same Oodle library and animation. `SprintPatch.exe` and `ModActions.dll` will not be byte-identical, because the compilers stamp each build with its own time and ID.

## Signing

The released MSI, `SprintPatch.exe` and `ModActions.dll` are signed with a self-signed code-signing certificate (publisher "Niki") and timestamped by DigiCert. That step is not part of `build.ps1`. It was done with commands like:

```
signtool sign /fd SHA256 /f <certificate.pfx> /p <password> /tr http://timestamp.digicert.com /td SHA256 <file>
```

with `SprintPatch.exe` and `ModActions.dll` signed before the MSI was built from them, and the MSI signed last. The certificate is not in this repository.

## Licence

The source code in this repository (`build.ps1` and the code files in `src/`) is released under the [MIT License](LICENSE).
It does not cover `src/art.png`, the Oodle library or the game's animation data, which belong to their respective owners.
