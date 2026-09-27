# Qemik working and release flows

## Scope and defaults

Qemik is a Windows QEMU desktop manager built with C# / .NET 10, Avalonia 12,
and SQLite. The solution is `native/Qemik.slnx`, with `Qemik.Core`,
`Qemik.Desktop`, `Qemik.Cli`, and `Qemik.Tests` projects. Use PowerShell and the
root `build.ps1`. This is not an Atlas project: do not introduce `gobake`,
`recipe.piml`, or `atlas.hub` release steps into Qemik. `gobake` applies only
when rebuilding the sibling Forge installer toolchain.

Preserve existing work in a dirty checkout. Airlift, clockt, Gitland, and xboard
are reference implementations, not publishing destinations. Editing this document
does not trigger a release or authorize changes to those repositories.

## Commit messages

Use the user's configured Git identity and a normal commit title and body. Do not
add `Co-Authored-By`, AI/assistant attribution, or "Generated with" lines unless
explicitly requested. Do not change global Git configuration.

For multiline messages or messages containing quotes, write a temporary UTF-8
message file under ignored `artifacts/` and use `git commit -F <file>`. Check the
exit code and verify the resulting commit before tagging. Supply release notes
through `--notes-file`, never a multiline command-line argument.

## Current tooling and first-release prerequisites

The existing build entry points are:

```powershell
.\build.ps1 -Test
.\build.ps1 -Test -Publish -Runtime win-x64
```

`-Publish` currently publishes the self-contained desktop folder to
`dist/win-x64`, including `Qemik.exe`, its dependencies, and assets. It does not
publish the CLI or create an installer. `artifacts/` holds diagnostics and
screenshots. Both directories are Git-ignored.

At the introduction of this document, the repository has no `version.ps1`,
`forge.toml`, `build-installer.ps1`, application license file, configured Git
remote, or release tags. Reinspect these facts before a release; this is a
bootstrap checklist, not a permanent assertion that these files are absent.
Do not report these tools as available until they exist and have been validated.

Before the first RELEASE, implement and validate the following tooling as part
of preparing the release:

- `version.ps1`: report/check versions, `-Bump patch`, `-Set X.Y.Z`, and `-DryRun`.
  Fail on missing required version locations or inconsistent values.
- `forge.toml` and `build-installer.ps1`: package the tested Windows payload as
  `dist/installer/Qemik-Setup-X.Y.Z.exe` using sibling Forge. Support
  `-SkipBuild` only for a verified payload of the requested version.
- Automated packaging checks for version, required files, embedded Forge
  identity/theme, and preservation of user data during install/upgrade/uninstall.
- Resolve the actual publishing repository with the user if no destination has
  already been authorized. Do not infer `fezcode/Qemik`, copy another app's
  remote, create a repository, or change visibility merely from this template.
- Use the project's chosen application license and dependency notices. If the
  application license remains unspecified, resolve it before public packaging;
  do not copy Airlift's MIT agreement and assume it applies here.

Continue local preparation while any genuinely missing publishing information
is being clarified. A missing remote does not prevent local builds or packaging.

## Version source of truth

`native/Directory.Build.props` `<Version>` is the authoritative application
version. Read its current value rather than copying a version from another app
or a historical release note. Dependency versions, QEMU versions, distribution
versions, and test fixtures are independent; never bump them through a global
search-and-replace.

Before the first release bump, introduce a shared assembly-derived app-version
helper and replace Qemik's hardcoded application version strings. Existing
locations include the desktop `Program.cs` version output, the main-window
footer, the About content in `MainWindow.Engine.cs`, and the HTTP User-Agent in
`Qemik.Core/OsImages.cs`; search for other occurrences too. Runtime version text
must follow the assembly stamp. Do not claim that helper already exists.

Once the release tooling exists, `version.ps1` must keep the props version,
Forge `[app] version`, and any current-release filenames/links in documentation
consistent. Installer copy and registry values should interpolate
`${app.version}`. Preserve historical notes and use `docs/releases/X.Y.Z.md`
for each release's notes.

## RELEASE workflow

Only an explicit request to **RELEASE** triggers the complete publishing flow.
That request authorizes bumping, testing, packaging, committing, pushing,
tagging, uploading, and publishing without routine permission questions.
Ordinary fixes, builds, installer requests, or commits do not imply a release.
Reading, copying, or editing release instructions does not trigger them.
Honor any test or approval gate the user explicitly adds.

Default to a **patch bump, including for features**. Use an exact version when
the user specifies one; reserve a minor bump for an explicitly identified
milestone. Do not ask the user to confirm the default patch bump.

Perform these steps in order. Resolve a failed required step before advancing
to steps that depend on it; never publish a known-broken build.

1. **Inspect and select the version.** Inspect the worktree, current branch,
   configured remote, local/remote tags, and GitHub releases/drafts. Confirm the
   intended Qemik destination and branch from actual repository configuration
   and the user's instructions. Use the authenticated `gh` account without
   printing tokens. Complete any first-release prerequisites above. If an
   interrupted attempt already bumped the intended version, resume that attempt
   instead of bumping again. Run `./version.ps1 -Bump patch` or `-Set X.Y.Z`,
   then `./version.ps1` to verify consistency.

2. **Write release notes.** Create `docs/releases/X.Y.Z.md` with the title
   `# Qemik vX.Y.Z` and Changes, Windows installation, and Validation sections.
   Describe user-visible changes and actual verification. Distinguish Qemik's
   installer from the separately installed QEMU engine. Reuse this file verbatim
   as the GitHub release body.

3. **Build and test.** Run `./build.ps1 -Test -Publish -Runtime win-x64`.
   Require successful build, tests, and publish; do not skip the test step or
   hardcode a passing test count. Enable the disposable QEMU checks with
   `QEMIK_QEMU_DIR` pointing at a verified installation. Report any skipped
   opt-in checks and their prerequisites. Review the generated Avalonia
   screenshots for changed UI. Follow the running-VM safeguards below before
   replacing a locked output folder.

4. **Package and verify.** Run `./build-installer.ps1 -SkipBuild` against the
   just-tested payload. Verify `dist/installer/Qemik-Setup-X.Y.Z.exe` is nonempty,
   contains the matching version and required files, and was produced by this
   attempt. Record size and SHA-256, and inspect the embedded Forge manifest
   and Mica theme. Surface the exact Setup path and launch the new installer for
   the user to install/test when doing so will not disrupt active guests. Keep
   "Open Qemik" selected on Finish; do not relaunch an old installed executable
   and describe it as the new version. Respect a user-requested testing gate.
   Report installer checks that could not be performed rather than claiming
   they passed.

5. **Commit and push.** Review `git diff`, `git status`, and `git diff --check`.
   Stage the version, release notes/tooling, and intended pending work. Exclude
   generated installers, diagnostics, VM data, secrets, and unrelated changes.
   Commit using a message file, verify the commit landed, and record its hash.
   Push to the confirmed remote and release branch (normally `origin main`
   once configured). Never force-push or silently ship a different branch.

6. **Tag, upload, and publish.** Tag that verified commit `vX.Y.Z` and push the
   tag. An existing tag must point at that commit; never move a published tag.
   Inspect existing releases/drafts and resume a matching draft rather than
   creating duplicates. Create a draft titled `Qemik vX.Y.Z` with `--verify-tag`
   and `--notes-file docs/releases/X.Y.Z.md`, then upload only the matching
   `Qemik-Setup-X.Y.Z.exe` installer asset. Verify the asset's exact name,
   expected nonzero size, digest when available, and uploaded state before
   publishing with `gh release edit ... --draft=false`. Use an explicit
   `--repo` with the confirmed destination for every `gh release` command.
   Never publish an empty release. Use `--clobber` only to repair this attempt's
   incomplete asset in its matching draft; preserve unrelated drafts/assets.
   After publication, verify the release URL, tag/commit, and downloadable asset.

On retry, inspect completed steps and resume without duplicate bumps, commits,
tags, or releases. On success, report version, commit, test results, installer
path/name, size, SHA-256, release URL, and any unperformed verification. Leave
an interrupted upload as a draft and report its concrete failure.

## Build and installer maintenance

- Qemik targets Windows x64 today. Do not claim other runtimes are supported
  solely because `build.ps1` accepts `-Runtime`.
- Publish with `--self-contained true -p:PublishSingleFile=false`. Ship the
  complete folder, including runtime DLLs, SQLite/native libraries,
  `.deps.json`, `.runtimeconfig.json`, and `Assets`. A standalone `Qemik.exe`
  is not a complete distribution. If the CLI is included later, publish and
  validate it separately and keep its payload paths consistent with Forge.
- Use Forge's Mica wizard with Qemik's own icon
  (`native/Qemik.Desktop/Assets/qemik.ico`) and a stable Qemik-specific identity
  such as `com.fezcode.qemik` when first establishing the manifest. Preserve the
  chosen ID thereafter; never reuse another app's upgrade/uninstall identity.
- Keep wizard text explicit and nonempty, optional Desktop/Start Menu shortcuts,
  and a finish-page launch of the newly installed desktop executable as the
  normal user. Include a license step only for an actual project agreement.
- Forge requires sibling `../Forge/build/forge.exe` and `uninstall.exe`. If
  rebuilding is needed, run `gobake build` in Forge. Check GUI process exit codes
  with `Start-Process -Wait -PassThru`, quote paths, and keep background build
  windows hidden. Verify embedded packaging, not just source TOML or timestamps.
- Do not bundle QEMU installers, guest ISOs, guest disks, or installed engine
  files into Qemik Setup. The application installs/selects QEMU separately.
- Preserve previous release installers and unrelated output. Never clean the
  entire `dist` directory. Resolve and verify absolute targets before any
  recursive cleanup; constrain it to the selected generated output folder.

## Running VMs, user data, and validation

- A build or release is not permission to force-stop a user's guest, reset it,
  unmount its disks, or discard its work. Qemik closing is normally blocked
  while guests are running. If output is locked, build into a separate staging
  folder and carry that exact folder into packaging. Extend the build/packaging
  scripts to support that path if necessary. Wait for a clean shutdown before
  replacing a running installation; ask only when user action is truly required.
- Preserve `%LOCALAPPDATA%\Fezcode\Qemik` and custom `--data-dir` libraries,
  `library.db`, machine firmware, disk images, downloaded media, blueprints,
  and shared-folder configuration across upgrades. Uninstall preserves these
  by default. Any removal choice needs a precise reviewed scope; never follow
  library paths to delete external guest disks or shared host folders.
- Use disposable test libraries and disks. An overlay backed by an existing
  user disk requires that source VM to be fully stopped; clone its NVRAM too.
  Do not start two guests against the same writable disk.
- Test clipboard integration with explicit synthetic text. Do not read or
  transfer the user's real host clipboard as a test fixture. Test sharing only
  against a dedicated temporary folder.
- For relevant integration changes, verify keyboard press/release and focus,
  bidirectional text clipboard, resizing, native GPU rendering, and display
  close/reopen without stopping the guest. A connected framebuffer or detected
  GPU alone does not prove guest acceleration. Verify the guest renderer before
  making that claim; never equate VirGL with GPU passthrough or CUDA.
- Distinguish protocol tests, disposable QEMU checks, actual guest checks, and
  audible/hardware checks in reporting. A passing audio-device startup test is
  not proof of audible playback. Test skips are not passes.

These conventions are adapted from sibling Airlift, clockt, Gitland, and xboard
`AGENTS.md` files, with Qemik's build layout and active-VM/data safeguards.
