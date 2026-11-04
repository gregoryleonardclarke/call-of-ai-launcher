# Call of AI Launcher

Separate .NET 10 / Avalonia desktop launcher. Local Windows prototype, not yet a public release.

## Local Windows test

Double-click `Test Launcher.cmd`. The local test channel uses a signed copy of the existing
Windows export, not a newly exported version of the game. It never uploads that build.
Two main buttons are visible: QUIT and INSTALL / UPDATE / PLAY, with a smaller Repair below.
Startup verifies the launcher first, automatically installs a newer signed launcher and restarts,
then checks the game. INSTALL means absent (or executable missing), UPDATE means older,
and PLAY means current or newer. Actions stay disabled until version checks succeed.
Failures are displayed and startup retries every 15 seconds; each manifest check times out after 30 seconds.
Repair always re-downloads the latest signed game package, even when already current.
QUIT cancels active work safely before closing. The game must be closed
before updating. The launcher preserves version directories and does not delete Godot user data.

The production executable lives under `artifacts/win-x64/CallOfAILauncher.exe`.
Keep the entire published folder together: this is a self-contained folder distribution,
not a single-file executable. The test command passes `--local-test` and uses an isolated
`local-test/installed` directory. Without that argument it uses the public GitHub feed
and the user's LocalApplicationData/CallOfAI directory.

## Build and tests

```sh
dotnet run --project tests/Tests.csproj
dotnet publish src/Launcher.csproj -c Release -r win-x64 --self-contained true -o artifacts/win-x64
```

The manual Actions workflow produces launcher candidate artifacts for win-x64, linux-x64,
osx-x64 and osx-arm64. It does not publish releases. Mac and Linux are experimental;
the initial Linux build runner baseline is Ubuntu 22.04. No macOS app bundle/notarization
or OS executable signing is configured. Do not advise users to disable system-wide security.
Native game exports, especially EOS frameworks/libraries, still need separate validation.

## Release protocol

Production feed: GitHub Releases tag `stable` in gregoryleonardclarke/call-of-ai-launcher.
Signed manifests are named `game-<rid>.json` and `launcher-<rid>.json`.
Each manifest contains a base64 JSON payload and RSA-PSS SHA-256 signature. Payload binds
component, version, platform, archive URL, SHA-256, byte count, entry point and patch notes.
Production downloads require HTTPS. File URLs work only in explicit local test mode.
The embedded public key verifies manifests. Never publish the private signing key.

Use the release tool with a clean, reviewed export folder:

```sh
dotnet run --project tools/ReleaseTool.csproj -- pack <folder> <entry> <version> <rid> <https-zip-url> <private-key-file> <output.zip> <manifest.json> <game-or-launcher> "Release notes"
```

Upload versioned ZIP assets first; update the signed stable manifest last. Never overwrite
an old archive with different bytes. Keep launcher AssemblyVersion consistent with its manifest.
For launcher packages, entry is `CallOfAILauncher.exe` on Windows, `CallOfAILauncher` otherwise.
Launcher updates are staged in a new folder and restart automatically. The child process waits
for its parent to exit before taking the instance lock. Local-test feed arguments are preserved.
The original downloaded executable also forwards to a selected equal/newer installed launcher.
The manifest version must match the packaged assembly version; mismatches stop with an error
rather than repeatedly restarting. Local tests require a signed launcher manifest as well as a game manifest.

Game installs use fresh staging folders; verification/extraction must finish before switching
current.json atomically. One prior pointer enables rollback; older folders are retained and
currently require manual cleanup when no game/launcher processes use them. Interrupted installs
can leave orphan staging folders, but never select them as current. Download resumption is not
implemented. Rollback is explicit; broken game versions are not automatically detected.

## Security and remaining release gates

- Signing private key was generated outside this repository. Back it up securely before release.
- Review EOS client policy/configuration and vendor redistribution rights before uploading game builds.
- No source assets, local EOS credentials, export archives or private keys belong in this repository.
- This prototype detects the game process it launched; independently launched game processes and
  games left running across launcher restarts are not yet tracked. Versioned installs avoid
  overwriting their files, but users should close all game instances before repair/rollback.
- ZIP symlinks are rejected. macOS framework/app packages may need safe normalization before packaging.
- OS signing, macOS bundle packaging, native dependency checks and manual platform testing are pending.
- This is not a sandbox against other software running as the same local user.
- No public build, CI execution or GitHub write authentication has been validated yet.