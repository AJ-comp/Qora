"""Run with: python -m unittest discover -s tools -p test_check_release.py."""
import os
import sys
import unittest

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from check_release import TARGETS, decide_release, sections, top_entry  # noqa: E402

ALL_ASSETS = [f'qora-{target}.vsix' for target in TARGETS]


def marketplace(*entries):
    """A `vsce show --json` object with one versions entry per (version, target) pair."""
    return {'versions': [{'version': version, 'targetPlatform': target} for version, target in entries]}


def release(*names):
    return {'assets': [{'name': name} for name in names]}


OLD = marketplace(*[('0.36.0', target) for target in TARGETS])


class DecideReleaseTests(unittest.TestCase):
    def test_a_new_version_publishes_every_platform_from_the_head_commit(self):
        plan = decide_release('0.37.0', '', 'intro', 'head', OLD, None)
        self.assertEqual(list(TARGETS), plan['publish_targets'])
        self.assertEqual('head', plan['sha'])
        self.assertTrue(plan['tag_needed'] and plan['release_needed'] and plan['build'])
        self.assertEqual([], plan['missing_assets'])

    def test_a_partial_publish_is_finished_from_the_tagged_commit(self):
        partial = marketplace(('0.37.0', 'win32-x64'), *[('0.36.0', target) for target in TARGETS])
        plan = decide_release('0.37.0', 'tagged', 'intro', 'head', partial, None)
        self.assertEqual(['darwin-arm64', 'linux-x64'], plan['publish_targets'])
        self.assertEqual('tagged', plan['sha'])
        self.assertFalse(plan['tag_needed'])
        self.assertTrue(plan['release_needed'] and plan['build'])

    def test_published_packages_without_tag_or_release_build_the_version_commit(self):
        published = marketplace(*[('0.37.0', target) for target in TARGETS])
        plan = decide_release('0.37.0', '', 'intro', 'head', published, None)
        self.assertEqual([], plan['publish_targets'])
        self.assertEqual('intro', plan['sha'])
        self.assertTrue(plan['tag_needed'] and plan['release_needed'] and plan['build'])

    def test_a_release_missing_one_vsix_gets_only_that_file(self):
        published = marketplace(*[('0.37.0', target) for target in TARGETS])
        plan = decide_release('0.37.0', 'tagged', 'intro', 'head', published,
                              release('qora-win32-x64.vsix', 'qora-darwin-arm64.vsix'))
        self.assertEqual(['qora-linux-x64.vsix'], plan['missing_assets'])
        self.assertFalse(plan['release_needed'] or plan['tag_needed'] or plan['publish_targets'])
        self.assertEqual('tagged', plan['sha'])
        self.assertTrue(plan['build'])

    def test_a_complete_release_does_nothing(self):
        published = marketplace(*[('0.37.0', target) for target in TARGETS])
        plan = decide_release('0.37.0', 'tagged', 'intro', 'head', published, release(*ALL_ASSETS))
        self.assertEqual(([], False, False, [], False),
                         (plan['publish_targets'], plan['tag_needed'], plan['release_needed'],
                          plan['missing_assets'], plan['build']))

    def test_a_universal_package_counts_for_every_platform(self):
        universal = {'versions': [{'version': '0.37.0'}]}
        plan = decide_release('0.37.0', 'tagged', 'intro', 'head', universal, release(*ALL_ASSETS))
        self.assertEqual([], plan['publish_targets'])
        self.assertFalse(plan['build'])

    def test_package_only_builds_the_head_commit_and_publishes_nothing(self):
        plan = decide_release('0.37.0', '', 'intro', 'head', OLD, None, package_only=True)
        self.assertEqual(dict(sha='head', publish_targets=[], tag_needed=False, release_needed=False,
                              missing_assets=[], build=True), plan)


class ChangelogTests(unittest.TestCase):
    TEXT = ('# Changelog\n\nIntro.\n\n## Unreleased\n\n- next\n\n## 0.2.0 — 2026-01-02\n\n- two\n\n'
            '### Detail\n\n- more\n\n## 0.1.0 — 2026-01-01\n\n- one\n')

    def test_sections_are_keyed_by_version_and_keep_their_subsections(self):
        entries = sections(self.TEXT)
        self.assertEqual(['Unreleased', '0.2.0', '0.1.0'], list(entries))
        self.assertIn('### Detail', entries['0.2.0'])
        self.assertNotIn('0.1.0', entries['0.2.0'])

    def test_the_newest_entry_skips_unreleased(self):
        heading, entry = top_entry(self.TEXT)
        self.assertEqual('## 0.2.0 — 2026-01-02', heading)
        self.assertIn('- two', entry)


if __name__ == '__main__':
    unittest.main()
