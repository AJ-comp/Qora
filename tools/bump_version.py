#!/usr/bin/env python3
"""Start a release: write the new Qora and extension versions everywhere they appear.

    python tools/bump_version.py 0.44.0 0.37.0 [--date YYYY-MM-DD] [--no-samples]

Run it on an up-to-date main to prepare the release commit; versions never change on other branches
(RELEASING.md). It sets the version constant, the grammar header, vscode/package.json and
package-lock.json, docs/llms.txt and the README release-notes range, and opens a new entry at the top of
CHANGELOG.md and vscode/CHANGELOG.md (an existing "## Unreleased" entry becomes the release entry). Then
it rebuilds src/Qora and regenerates the README OpenQASM samples. What it cannot write for you is left
marked: TODO lines in the changelog entries, and the "what's new" lines of the READMEs, which
tools/check_release.py keeps naming until they are written.
"""
from __future__ import annotations

import argparse
import datetime
import json
import re
import subprocess
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import check_release as guard  # noqa: E402  (shares the file list and the sample check)

ROOT = guard.ROOT
CLI = 'src/Qora/bin/Debug/net10.0/Qora.dll'


def edit(path: str, change) -> None:
    file = ROOT / path
    text = file.read_text(encoding='utf-8')
    new = change(text)
    if new == text:
        raise SystemExit(f'{path}: nothing to change; is the current version written there as expected?')
    file.write_text(new, encoding='utf-8')


def replace(old: str, new: str, count: int = -1):
    def change(text: str) -> str:
        if old not in text:
            raise SystemExit(f'expected to find {old!r}')
        return text.replace(old, new, count)
    return change


def set_lock_version(old: str, new: str):
    """package-lock.json: only the root "version" and the packages[""] "version" lines."""
    def change(text: str) -> str:
        lines = text.split('\n')
        root = lines.index(f'  "version": "{old}",')
        lines[root] = f'  "version": "{new}",'
        start = next(i for i, line in enumerate(lines) if line.strip() == '"": {')
        package = next(i for i in range(start, len(lines)) if lines[i].strip() == f'"version": "{old}",')
        lines[package] = lines[package].replace(old, new)
        result = '\n'.join(lines)
        data = json.loads(result)
        assert data['version'] == new and data['packages']['']['version'] == new
        return result
    return change


def open_entry(path: str, heading: str, lead: str, todo: str) -> None:
    """Insert the release entry above the newest one, or turn a leading '## Unreleased' into it."""
    def change(text: str) -> str:
        heads = list(re.finditer(r'^## .*$', text, re.M))
        if not heads:
            raise SystemExit(f'{path}: no "## " entry found')
        first = heads[0]
        if first.group(0).split()[1:2] == ['Unreleased']:
            end = heads[1].start() if len(heads) > 1 else len(text)
            body = text[first.end():end].strip('\n')
            return text[:first.start()] + f'{heading}\n\n{lead}{body if body.strip() else todo}\n\n' + text[end:]
        return text[:first.start()] + f'{heading}\n\n{lead}{todo}\n\n' + text[first.start():]
    edit(path, change)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__.split('\n\n')[0])
    parser.add_argument('qora', help='new Qora language version, X.Y.Z')
    parser.add_argument('extension', help='new VS Code extension version, X.Y.Z')
    parser.add_argument('--date', default=datetime.date.today().isoformat(), help='release date for the changelogs')
    parser.add_argument('--no-samples', action='store_true', help='do not rebuild src/Qora and regenerate the README samples')
    parser.add_argument('--any-branch', action='store_true', help='allow running outside main (for testing only)')
    args = parser.parse_args()

    q0, e0 = guard.qora_version(), guard.ext_version()
    q, e = args.qora, args.extension
    for label, old, new in (('Qora', q0, q), ('extension', e0, e)):
        if not guard.semver(new):
            parser.error(f'{label} version {new!r} is not X.Y.Z')
        if not guard.semver(old) or guard.semver(new) <= guard.semver(old):
            parser.error(f'the new {label} version {new} must be higher than the current {old}')
    if guard.git('rev-parse', '-q', '--verify', f'refs/tags/v{q}'):
        parser.error(f'v{q} is already a release tag; choose a new version')
    branch = guard.git('rev-parse', '--abbrev-ref', 'HEAD')
    if branch != 'main' and not args.any_branch:
        parser.error(f'run this on main (you are on {branch}); versions change only in the release commit on main')

    edit(guard.QORA_TYPES, replace(f'public const string Value = "{q0}";', f'public const string Value = "{q}";', 1))
    edit(guard.GRAMMAR, replace(f'/// Qora v{q0} — ', f'/// Qora v{q} — ', 1))
    edit(guard.PACKAGE, replace(f'\n  "version": "{e0}",\n', f'\n  "version": "{e}",\n', 1))
    edit(guard.LOCK, set_lock_version(e0, e))
    edit(guard.LLMS, lambda text: text.replace(f'# Qora v{q0} — ', f'# Qora v{q} — ', 1)
         .replace(f'generated by Qora v{q0}', f'generated by Qora v{q}'))
    for path in guard.READMES:
        edit(path, lambda text: text.replace(f'(v0.1 → v{q0})', f'(v0.1 → v{q})')
             .replace(f'generated by Qora v{q0}', f'generated by Qora v{q}'))
    open_entry(guard.CHANGELOG, f'## {q} — {args.date}',
               f'VS Code extension **{e}** bundles the Qora {q} compiler.\n\n', '- TODO: describe the language changes.')
    open_entry(guard.EXT_CHANGELOG, f'## {e} — {args.date}',
               f'- Bundles **Qora {q}** for Windows x64, macOS Apple Silicon, and Linux x64.\n',
               '- TODO: describe the extension changes, or delete this line.')
    print(f'Qora {q0} → {q}, extension {e0} → {e}: version constant, grammar header, package.json, '
          f'package-lock.json, docs/llms.txt, README ranges and both changelog entries updated.')

    if not args.no_samples:
        build = subprocess.run(['dotnet', 'build', 'src/Qora', '-c', 'Debug', '-nologo', '-v', 'q'], cwd=ROOT)
        if build.returncode != 0:
            print(f'warning: the build failed, so the README samples were not regenerated. Build src/Qora and run: '
                  f'python tools/check_release.py --cli {CLI} --fix-samples')
        else:
            errors: list[str] = []
            guard.check_samples(errors, str(ROOT / CLI), fix=True)
            for error in errors:
                print(f'warning: {error}')

    print(f"""
Still to write by hand (tools/check_release.py names whatever is missing):
  - CHANGELOG.md and vscode/CHANGELOG.md: replace the TODO lines in the new entries.
  - README.md, README.ko.md, README.ja.md: the "what's new" lines near the top must name **Qora {q}**.
  - vscode/README.md, README.ko.md, README.ja.md, README.vi.md: the version paragraph must name **{e}** and **Qora {q}**.
Then run the tests and `python tools/check_release.py --cli {CLI}`, commit as
"feat: release Qora {q} and VS Code extension {e}" and push to main. The Release workflow does the rest.""")
    return 0


if __name__ == '__main__':
    sys.exit(main())
