import io
import os
import sys
import unittest
from contextlib import redirect_stdout
from pathlib import Path
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'scripts'))
import announce_discord  # noqa: E402


class AnnounceDiscordTests(unittest.TestCase):
    def test_payload_names_the_release_and_links_the_listing(self):
        payload = announce_discord.build_payload('v1.2.3', 'Fix things.\n', 'https://example.test/r')
        embed = payload['embeds'][0]
        self.assertEqual(embed['title'], 'Avatar Wardrobe v1.2.3 released')
        self.assertEqual(embed['url'], 'https://example.test/r')
        self.assertTrue(embed['description'].startswith('Fix things.'))
        self.assertIn(announce_discord.LISTING_URL, embed['description'])
        self.assertEqual(payload['allowed_mentions'], {'parse': []})

    def test_long_notes_are_trimmed_to_the_embed_limit_on_a_line_break(self):
        notes = '\n'.join('- change %d %s' % (i, 'x' * 60) for i in range(200))
        description = announce_discord.build_payload('v1.0.0', notes, 'u')['embeds'][0]['description']
        self.assertLessEqual(len(description), announce_discord.DESCRIPTION_LIMIT)
        self.assertIn('\n…\n', description)
        self.assertIn(announce_discord.LISTING_URL, description)

    def test_missing_webhook_skips_without_posting(self):
        with mock.patch.dict(os.environ, {'DISCORD_WEBHOOK_URL': ''}), \
                mock.patch.object(announce_discord, 'post') as post, redirect_stdout(io.StringIO()) as out:
            announce_discord.main(['--tag', 'v1.0.0', '--release-url', 'u',
                                   '--notes', str(Path(__file__).resolve().parents[1] / 'RELEASE_NOTES.md')])
        post.assert_not_called()
        self.assertIn('skipping', out.getvalue())


if __name__ == '__main__':
    unittest.main()
