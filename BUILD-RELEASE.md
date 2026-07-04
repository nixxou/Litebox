# Building the LiteBox releases

`build-release.ps1` produces the release artifacts under `release\` (git-ignored). One command:

```powershell
powershell -ExecutionPolicy Bypass -File build-release.ps1
```

Output:

```
release\
  LiteBox-Setup-<ver10>.exe                 README.txt          the ONE universal installer (net9+net10)
  light\<ver>_net9\   LiteBox-<ver>.zip      README.txt          manual "extract into Core", net9 (LB 13.27)
  light\<ver>_net10\  LiteBox-<ver>.zip      README.txt          manual "extract into Core", net10 (LB 13.28+)
```

---

## Why the host is multi-file (and single-file is an installer only)

LaunchBox's core assemblies (`Unbroken.LaunchBox.dll`, `.Windows.dll`, `.LocalDb.dll`) are protected by a
**method-body-encryption obfuscator** — a runtime JIT-hook decryptor (`getJit` + `mmap`/`mprotect` +
`RuntimeHelpers.PrepareMethod` + `Marshal.ReadInt64` over the JIT'd code). Method bodies ship **encrypted**
and are decrypted into JIT memory on first call. LiteBox only reimplements the **SDK** (`Unbroken.LaunchBox.Plugins`);
the moment a plugin or LiteBox itself calls a **core** service (`NamingHelper`, `Root`, `GamesDb`, the emulator-
integration plugins…), that decryptor runs.

Under a **.NET single-file bundle** the decryptor's native reads land on the wrong memory (the bundle changes
the load layout) → `AccessViolationException` at module init → every later core call throws
`TypeInitializationException` forever (symptom: the Game Saves page finds nothing, options greyed). Confirmed:
multi-file works; single-file crashes; forcing disk extraction (`IncludeAllContentForSelfExtract`) does **not**
help. So **the host must be the light multi-file form.** A single-file exe can only be the **installer**.

> This is structural, not a licence bypass — the obfuscator isn't rejecting LiteBox (it runs fine multi-file),
> it just can't survive the single-file repackaging. Neutralising it would (a) break the decoder every core
> call depends on and (b) be circumventing a technical protection measure. See
> `ExtendDB/docs/lb-save-management.md` and the `reference-lb-obfuscator-singlefile` memory.

---

## The two forms

| Form | What it is | ~Size | Role |
|------|-----------|-------|------|
| **light** | self-contained, NON-single-file, **stripped** of the .NET runtime DLLs → borrows `LaunchBox\Core`'s runtime (like `LaunchBox.exe`). Ships `LiteBox.exe` + `LiteBox.dll` + the two `.json`. Runs ONLY from `Core`. Built per TFM (net9/net10). | ~13 MB zip | **THE HOST** |
| **installer** | ONE universal self-contained **single-file** exe. **Embeds both lights** (net9+net10) AND the native payload. Never runs as the host. | ~86 MB | **installer / re-launcher** |

**How the light shares Core's runtime.** Published **self-contained** (so its `runtimeconfig.json` has
`includedFrameworks`, exactly like `LaunchBox.exe`), then **stripped** of every runtime DLL — only
`LiteBox.exe`, `LiteBox.dll`, `LiteBox.deps.json`, `LiteBox.runtimeconfig.json` remain. In `Core`, the apphost
loads `coreclr.dll` + all framework assemblies from `Core` itself (LaunchBox is a self-contained flat
deployment). A plain *framework-dependent* build fails there ("You must install .NET"). The light is tied to
the runtime major — a net9 light needs a net9 `Core`, a net10 light a net10 `Core`.

**What the installer does at install** (`Host\Install\Installer.cs` + `LightPayload.cs`):
1. Detects Core's .NET major from `Core\coreclr.dll` (`DetectCoreTfm` → net9 for LB 13.27, net10 for LB 13.28+).
2. Extracts the **matching** light's 4 files into `Core` (NOT a copy of itself — that would crash).
3. Deploys the embedded native payload into `<LB>\ThirdParty` (`NativeInstaller.EnsureDeployed`).
4. Copies **itself** to `<LB>\LiteBox.exe` (the root re-launcher) and launches `Core\LiteBox.exe` (the light host).

Drop the installer anywhere, at the LaunchBox root, or in Core — it self-installs (`--install "<root>"` for
silent). The user double-clicks the root `LiteBox.exe` (the installer) any time; it re-extracts + launches.

---

## Prerequisites

- **.NET SDK 10.x** — it targets **both** `net9.0-windows` and `net10.0-windows`. Check with `dotnet --list-sdks`.
- A **.NET 9 LaunchBox** on disk, used *only* as the net9 compile reference (below). Default `G:\LB`.
- Windows + PowerShell (5.1 is fine).

### The net9 SDK reference (the one non-obvious bit)

A **net9** project **cannot** reference LaunchBox's **net10** build of `Unbroken.LaunchBox.Plugins.dll`
— **CS1705** (that DLL pulls `System.Runtime, Version=10.0.0.0`). So:

- the **net9** light compiles its SDK reference against a **.NET 9** LaunchBox's copy, and
- the **net10** light (and the installer) compile against the primary `..\..\..\LB\Core` (net10).

This split lives in `LiteBox.csproj` via `$(SdkRefDll)` / `$(Lb9Root)` / `$(Lb10Root)`:

- default net9 root = **`G:\LB`**; default net10 root = the primary LB beside the repo (`..\..\..\LB`);
- override: `build-release.ps1 -Lb9Root "D:\LB-net9" -Lb10Root "D:\LB-net10"` (or `dotnet … -p:Lb9Root=… -p:Lb10Root=…`).
- the script reads each root's `Core\LaunchBox.exe` version to name the outputs (`<ver>_net9`, `LiteBox-Setup-<ver10>.exe`).

`Magick.NET*` and `Microsoft.Data.Sqlite` / `SQLitePCLRaw*` are net8 → satisfy both targets; only the SDK
reference is split. All are `Private=false` (compile-time only); at runtime LiteBox binds Core's copy by name.

---

## What the script runs

```powershell
# 1) each light — self-contained NON-single-file, then keep only the 4 app files (strip the runtime).
dotnet publish LiteBox.csproj -c Release -r win-x64 -f <tfm> -p:SelfContained=true -p:PublishSingleFile=false -p:LiteBoxDist=light -o <dir>
#    stage <dir>\{LiteBox.exe,LiteBox.dll,LiteBox.deps.json,LiteBox.runtimeconfig.json}
#      → into  light-payload\<label>\   (for the installer to embed)
#      → and into a zip stage + litebox\thirdparty\<8 native files>  →  release\light\<ver>_<label>\LiteBox-<ver>.zip

# 2) the ONE universal installer — self-contained SINGLE-FILE (net10), embeds both lights + the native payload.
dotnet publish LiteBox.csproj -c Release -r win-x64 -f net10.0-windows -p:SelfContained=true -p:PublishSingleFile=true -p:LiteBoxDist=standalone -p:LightPayloadDir=<light-payload> -o <dir>
#    → release\LiteBox-Setup-<ver10>.exe
```

The csproj embeds, only for `LiteBoxDist=standalone`:
- the **8 native payload files** (`natives/…`) — from `.\thirdparty\`;
- when `-p:LightPayloadDir=<dir>` is passed, the **light payload** as `light/net9/*` + `light/net10/*`.

Three lists to keep in sync for the native payload:
1. `.\thirdparty\` (the files), 2. the `natives/…` `EmbeddedResource` block in `LiteBox.csproj`,
3. `NativeInstaller.Payload` in `Host\Install\NativeInstaller.cs`.

The light's 4 app-file names are shared by `build-release.ps1` (`$appFiles`), the `light/<tfm>/*`
`EmbeddedResource` block, and `LightPayload.Files` — keep those three in sync.

---

## Verify

After `build-release.ps1`:

1. **Both lights + installer built** — the script throws on any publish failure.
2. **Artifacts** exist: `release\LiteBox-Setup-<ver10>.exe` and `release\light\<ver>_{net9,net10}\LiteBox-<ver>.zip`.
3. **Light zip contents** = exactly `LiteBox.exe` + `LiteBox.dll` + `LiteBox.deps.json` +
   `LiteBox.runtimeconfig.json` + `litebox\thirdparty\<8 files>`; the inner exe stays `LiteBox.exe`; README beside, not inside.
4. **Installer install** (decisive) — from a temp folder, `LiteBox-Setup-<ver10>.exe --install "<LB root>"`, then check:
   - `Core\LiteBox.exe` ≈ **523 KB** (the light apphost, NOT ~86 MB) + `LiteBox.dll` + the two `.json`;
   - `<LB>\ThirdParty\…` has the native payload;
   - `<LB>\LiteBox.exe` ≈ 86 MB (the installer/re-launcher);
   - console shows `[installer] Core .NET major = 10 → net10` (or 9), `extracted light host`.
5. **Light host boots without the obfuscator crash** — `Core\LiteBox.exe --probe-saves "<a game>"` prints
   `NamingHelper.RootFolder = …` and a save `group … active=True` (NOT `THREW … AccessViolation`). The
   definitive check is human: launch `Core\LiteBox.exe`, the UI boots, Edit Game → Game Saves scans.
6. **Sizes** ≈ installer 86 MB, each light zip 13 MB.

---

## When a future LaunchBox SDK update breaks the build (CS0535)

LiteBox's data objects extend generated dummies — `Generated\Dummies.g.cs` has one `Dummy<Iface>` class per
SDK interface, and `HostGame : DummyGame`, `HostEmulator : DummyEmulator`, etc. rely on them to auto-implement
the ~86 members of `IGame` and friends. When LaunchBox **adds** interface members, the build fails with
**CS0535** ("`Dummy…` does not implement interface member …").

Fix:

1. Get the exact member types (reflect the new SDK — e.g. `MetadataLoadContext` over
   `LB\Core\Unbroken.LaunchBox.Plugins.dll`), then add them to the matching `Dummy…` class in
   `Dummies.g.cs` (style: `public virtual global::System.<Type> <Name> { get; set; }`), **or**
2. delete the file and regenerate once the project compiles: `dotnet run --project . -- --gen-stubs`.

Extra members are harmless on an older SDK, so the **net10 superset satisfies the net9 build too** — patch the
file once, against the newest SDK.

> This already happened for the net10 SDK (13.28): it added `StartupScreenPostLaunchDisplayTime` (int) +
> `MonitorStartupShutdownWithProcess` (bool) to both `IGame` and `IEmulator`, plus `ForceFrontendFocusOnShutdown`
> (bool) to `IEmulator`. Those five are patched into `Dummies.g.cs`.
