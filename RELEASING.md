# Releasing Qora

Every release ships two versions together:

- the **Qora language** version in `src/Qora.Core/Ir/QoraTypes.cs`, released as the tag `vX.Y.Z`;
- the **VS Code extension** version in `vscode/package.json`; the extension bundles that compiler.

A release always raises both (Qora 0.43.0 shipped with extension 0.36.0, Qora 0.42.0 with 0.35.0, and so on).

## Rules

1. **Versions change only in the release commit on `main`.** Branches and pull requests keep main's
   versions, and CI rejects a branch that changes them. Until the release, write upcoming changes under
   `## Unreleased` at the top of `CHANGELOG.md` and `vscode/CHANGELOG.md`.
2. **A released version is never reused or edited.** Once `vX.Y.Z` exists, its changelog entries stay as
   released, and a commit that still says X.Y.Z must contain the tagged commit.
3. **Pushing new versions to `main` is the release.** After CI passes, the Release workflow publishes the
   extension to the Marketplace and creates the tag and the GitHub release. There is no manual step.

## Steps

1. Start from an up-to-date `main`.
2. Run `python tools/bump_version.py <qora> <extension>`, for example
   `python tools/bump_version.py 0.44.0 0.37.0`. It writes the new versions everywhere they are mechanical
   (version constant, grammar header, `package.json`, `package-lock.json`, `docs/llms.txt`, README ranges),
   opens the new changelog entries (an `## Unreleased` entry becomes the release entry), rebuilds
   `src/Qora` and regenerates the README OpenQASM samples.
3. Write the release notes: replace the `TODO` lines in the new entries of `CHANGELOG.md` and
   `vscode/CHANGELOG.md`, and update the "what's new" lines near the top of `README.md`, `README.ko.md`
   and `README.ja.md` and the version paragraph of the four `vscode/README*.md`. The language entry becomes
   the GitHub release notes as written.
4. Check:

   ```bash
   dotnet test Qora.slnx -c Release
   npm test --prefix vscode
   python -m unittest discover -s tools -p 'test_*.py'
   python tools/check_release.py --cli src/Qora/bin/Debug/net10.0/Qora.dll
   ```

   For language changes, also run the Braket execution validation (`python tools/braket_validation.py`,
   see its header) and record the result in the changelog entry.
5. Commit as `feat: release Qora X.Y.Z and VS Code extension A.B.C` and push to `main`.

## What the automation does

**CI** (`.github/workflows/ci.yml`) runs on every push and pull request: the .NET tests, the extension
tests, the runner tests and the release guard (`tools/check_release.py`).

**Release** (`.github/workflows/publish-vsce.yml`) runs after CI passes for a push to `main`:

1. *plan*: runs the release guard again, then compares the repository with the Marketplace, the tags and
   the GitHub releases;
2. *build*: builds the self-contained CLI and a `.vsix` for Windows x64, macOS arm64 and Linux x64, and
   publishes them to the Marketplace if that extension version is not there yet;
3. *tag and GitHub release*: creates `vX.Y.Z` on the release commit and the release
   "Qora X.Y.Z / VS Code extension A.B.C" with the changelog entry and the three `.vsix` files, if they do
   not exist yet.

Each piece is created only when it is missing. A run with nothing new does nothing, and a release that
stopped half way is finished by the next run, or by *Actions → Release → Run workflow*. A *dry run* only
builds the packages.

The release guard fails when:

- a version string disagrees with `QoraTypes.cs` or `package.json`: the grammar header,
  `package-lock.json`, the newest changelog entries, the README samples and ranges, `docs/llms.txt` or the
  extension READMEs; or when a newest changelog entry still contains `TODO`;
- a README OpenQASM sample differs from the compiler output (`--fix-samples` rewrites the samples);
- a released changelog entry was changed, a released version is reused from another line of history, the
  Qora version is below the newest tag, or the Qora and extension versions do not move together;
- on a branch or in a pull request, either version differs from where the branch left `main`.

Publishing to the Marketplace needs the repository secret `VSCE_PAT` (an Azure DevOps personal access
token with *Marketplace → Manage*).
