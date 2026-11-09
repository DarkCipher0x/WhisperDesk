# WhisperDesk release pipeline

**Status: implemented (approved 2026-10-21).** It follows the DarkCipher release standard (one automated
pipeline per app, a single sanitised source of truth, a kill switch, dry runs). Each app session owns its own pipeline;
this one is WhisperDesk's.

## One command

```powershell
.\release.ps1                 # build + check + publish everything for the version in WhisperDesk.csproj
.\release.ps1 -DryRun         # every step, nothing leaves the PC; prints what would be pushed, uploaded, deployed, posted
.\release.ps1 -Resume         # continue a version whose run stopped part-way (reads ..\WhisperDesk-release\<ver>\state.json)
.\release.ps1 -LocalOnly      # build and check only (the old build.ps1 -NoPublish)
```

`build.ps1` stays the build step (it's what CI and contributors run) and never publishes on its own. `release.ps1` is
the only entry point that publishes, and runs the steps below strictly in order: each step reads the previous step's
record in `..\WhisperDesk-release\<ver>\state.json` and refuses to start unless it says `done` for this exact version and
input hashes. A step can be re-run (`-Resume`, or `-Step <name>` for one step), never skipped.

## Steps

| # | Step | Public? | Done when | Already-done detection (idempotency) |
|---|------|---------|-----------|--------------------------------------|
| 0 | **Preflight** | no | all checks pass | (always re-runs) |
| 1 | **Build** `dist\<ver>\` | no | files + SHA256SUMS written | state records their hashes; a version already released is never rebuilt |
| 2 | **Verify build** | no | all checks pass | (always re-runs) |
| 3 | **Sanitise** `WhisperDesk-public` stage | no | stage tree passes every gate | stage tree hash recorded |
| 4 | **Secret + privacy scan** | no | clean | scan of exactly that tree hash recorded |
| 5 | **Mirror**: commit + tag + push | yes | `origin/main` and tag `v<ver>` on GitHub | tag exists with the identical tree → done; tag with a different tree → stop |
| 6 | **GitHub Release** | yes | release published, every asset's GitHub digest = SHA256SUMS | published release → done, never modified |
| 7 | **Website drop** | yes* | drop handed to the website and deployed | drop manifest hash recorded; deploy confirmed by step 8 |
| 8 | **Website verification** | no | every live file = drop = `-public` at the tag / Release assets | (always re-runs) |
| 9 | **Discord** | yes | card posted | posted-log entry `whisperdesk-<ver>` with the message id |

\* Production website deploys stay with the Website & Deployments session and need the maintainer's explicit go (until a
restricted deploy user exists). Step 7 hands over a verified drop and waits; step 8 runs once the deploy is reported.

### 0. Preflight
- **Kill switch** (see below) read and printed; it is read again at the start of every public step.
- Version agrees everywhere: `WhisperDesk.csproj <Version>`, a `## <ver>` CHANGELOG section, and no hard-coded older
  version left in the READMEs (the public README badge and version lines become `1.9.0` placeholders filled in
  by step 3).
- Not yet released: no published GitHub Release `v<ver>`.
- Remotes: the public clone's `origin` must be exactly `https://github.com/DarkCipher0x/WhisperDesk.git`; branch
  `main`; clean; fast-forward only. No `--force`, ever, without the maintainer's explicit OK.
- Tools present and pinned: .NET SDK, WiX (dotnet tool), MSIX build tools (locked NuGet), gitleaks (hash-checked).

### 1. Build (`build.ps1`)
Unchanged checks (locked restore, vulnerability audit, security tests), but output moves to **`dist\<ver>\`**
like CaseKeeper and ServerManagement (old flat files stay where they are):
`WhisperDesk-<ver>-x64.msi`, `-x64-portable.zip`, `-User-Admin-Guide.pdf`, `-Addon-SDK.zip`,
`-Addon-WordExport.zip`, `-SHA256SUMS.txt`, and `store\WhisperDesk-<ver>-x64.msix` (never published by the
pipeline: Partner Center only).

### 2. Verify build
- Every expected file exists; SHA256SUMS matches every file (and doesn't list itself).
- **Store parity:** the MSIX's program files are byte-identical to the portable zip's (the rule: downloads match the
  Store submission for the same version).
- Version inside the binaries: `WhisperDesk.exe` file/product version and the MSIX manifest = `<ver>`.
- **Privacy audit inside the binaries** (`tools\audit-release.ps1`, already built): MSI admin-extract, zips, an
  unbundled publish of the app; private terms, `binary:` terms, no symbols, keys, recordings, sessions or voiceprints.

### 3. Sanitise (allowlist, rebuilt from scratch)
- The stage folder is deleted and recreated every run; nothing is patched incrementally.
- **`release\public-allowlist.txt`** lists every path pattern that may be published (e.g. `WhisperDesk/**/*.cs`,
  `WhisperDesk/**/*.xaml`, `WhisperDesk/Installer/WhisperDesk.wxs`, `WhisperDesk/docs/img/*.png`, …). A file not
  matched is not published, and the run prints new unmatched files so a new source file is added deliberately.
  A short hard-deny list (keys, symbols, media, `*.local.txt`, `bin/obj/dist/publish`) still wins over the allowlist.
- Repo-root files come from `public-repo\` (README, `.github`, sln, `.gitignore`, `.gitattributes`); `1.9.0` is
  filled in.
- The SDK contract DLL is rebuilt path-free; the guide PDF comes from `dist\<ver>\`.
- Generated images (`Packaging\Images`, `Packaging\StoreListing`) stay out; `build.ps1` redraws the tiles.

### 4. Secret + privacy scan (gate before anything public)
- **gitleaks** (pinned version, SHA-256 verified, in `tools\bin\`, git-ignored) over the stage tree and over the
  website drop, with the default rules plus custom ones: Discord webhook URLs, GitHub tokens (`ghp_`, `gho_`,
  `github_pat_`), private-key blocks, SSH/deploy keys, `.env`-style secrets.
- The existing gates: private terms (`tools\privacy-terms.local.txt`, never published), local user paths, every image
  on the approved-images list (viewed by eye), no media or key files.
- Any hit stops the pipeline. Nothing is pushed, uploaded, deployed or posted.

### 5. Mirror
The stage tree replaces the clone's tree; one commit `WhisperDesk <ver> - sanitised public source` and an annotated
tag `v<ver>`, authored as DarkCipher0x with the GitHub no-reply address; `origin` re-verified; push `main`, then the tag.
The repository is private now but every push is treated as public.

### 6. GitHub Release (CaseKeeper's rules)
- Created as a **draft**, assets uploaded, then each asset's GitHub `digest` (sha256) is compared with SHA256SUMS;
  only when all match is the draft published.
- **A published release is never modified.** A run that finds a draft resumes it; a run that finds a published
  release stops (a fix is a new version: bug fix x.y.Z+1, feature x.Y+1.0).
- Notes: the CHANGELOG section for `<ver>` plus the download table.
- Assets: everything in `dist\<ver>\` except the MSIX.

### 7. Website drop (single source of truth)
The pipeline writes `..\WhisperDesk-release\<ver>\website-drop\` and hands it to the Website & Deployments session:
- `source\WhisperDesk-src-v<ver>.zip`, made with `git archive v<ver>` from the public clone: the website's source
  download **is** the tagged public tree (no more `package-source.ps1` copy from the private folder).
- `downloads\`: the Release assets, copied from `dist\<ver>\` and checked byte-for-byte against the GitHub digests.
- `manifest.json`: version, date, every file's name, size and SHA-256, the tag commit, and the release notes
  (from the CHANGELOG), for `releases.js` / `sources.js` and the release-notes block on the Software page.
- One naming scheme everywhere: `WhisperDesk-<ver>-Addon-SDK.zip`, `WhisperDesk-<ver>-Addon-WordExport.zip`. The
  website switches to it at the next version; 1.8.0 keeps its published names.
- The drop is scanned in step 4 too. The website takes WhisperDesk files only from a drop, never from `dist`,
  `Releases\` or the private folder.

### 8. Website verification
After the deploy is reported, the pipeline downloads every WhisperDesk URL from darkciphersystems.com (downloads,
source zip, `releases.js`, `sources.js`) and compares SHA-256 with the drop manifest; the source zip's contents are
also compared file-by-file with `git archive v<ver>`. Any mismatch marks the step `failed`, is listed in the state
file and printed, and step 9 does not run.

### 9. Discord
- The card is generated from the CHANGELOG section (summary + highlights) into the website's card format, previewed
  in the dry run, and posted with `?wait=true` so the message id is recorded (that's what makes it deletable).
- The webhook URL comes from **Windows Credential Manager** (target `DarkCipher/discord/whisperdesk`), never a file;
  posting is skipped with a clear message when it isn't set.
- Store cards stay gated by the Store listing being public.
- **Discord hold:** while `%USERPROFILE%\.darkcipher\discord-hold-whisperdesk` exists (its text says why), step 9
  records `blocked` and posts nothing; everything else still publishes. Delete the file and `-Resume` to post.

## Kill switch

Public steps (5, 6, 7, 9) run only when publishing is on. Either of these turns it off immediately, without editing
anything in the pipeline, and is checked at the start of every public step (so it also stops a run in progress
between steps):
- the file `%USERPROFILE%\.darkcipher\publishing-disabled` exists (any content; it can say why), or
- the environment variable `DCX_PUBLISH=off`.

With it off, `release.ps1` still builds, checks, sanitises and scans, and records the public steps as `blocked`; a
later `-Resume` (with it back on) carries on from there. The name should be the same for every DarkCipher app.

## Failure behaviour and recovery

| Step fails | State left behind | Resume |
|---|---|---|
| 0–4 | nothing public changed; dist and stage on disk | fix, `-Resume` (or a fresh run) |
| 5 push | commit + tag local only, or on GitHub without the tag | `-Resume` pushes what's missing; identical tree is detected |
| 6 upload | a **draft** release with some assets | `-Resume` replaces the draft's assets and publishes |
| 6 digest mismatch | draft not published | re-upload; never publishes a mismatched file |
| 7 deploy | site unchanged or website session reports failure | website session rolls back from its pre-deploy backup; `-Resume` |
| 8 verify | site live but different from the release | reported; Discord withheld; fix the site, `-Resume` re-verifies |
| 9 post | nothing posted, or posted (id recorded) | `-Resume` posts only if no id is recorded |

**Rolling back a bad release:** no history rewrite. Mirror: a new commit reverting it, then a fixed version; the tag
stays (moving a tag needs the maintainer's explicit OK). GitHub Release: published releases aren't edited; the fix is x.y.Z+1
(in an emergency, the maintainer can mark it pre-release by hand). Website: the website session restores its pre-deploy backup.
Discord: the recorded message id lets the pipeline delete the card (`-RetractDiscord`).

## Trade-offs and risks

- **Automatic publishing can spread a mistake to four places at once.** Mitigations: the scans and the binary audit
  run before anything public; the website and Discord steps run only after the GitHub Release verified; the website
  step needs the maintainer's deploy OK until a restricted deploy user exists; the kill switch stops it between any two steps.
- **Allowlist upkeep:** a new source file isn't published until it's added to the allowlist. The run lists unmatched
  files so it's caught at release time instead of leaking by default.
- **The private folder isn't in git**, so there's no history for what each version was built from. Proposed: make it
  a local git repository (and, if wanted, push it to a private `DarkCipher0x/WhisperDesk-private`).
- **dist layout change** (`dist\<ver>\`) means the website's sync must read the drop, not the flat folder; that's
  intended (single source).
