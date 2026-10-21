#!/usr/bin/env python3
"""Announce a published release in Discord through an incoming webhook.

The webhook URL is read from DISCORD_WEBHOOK_URL so it never appears in
arguments or logs. Without it, the announcement is skipped.
"""
import argparse
import json
import os
import sys
import urllib.error
import urllib.request
from pathlib import Path

LISTING_URL = 'https://gryphprime.github.io/avatar-wardrobe-vpm/'
ADD_TO_VCC_URL = 'vcc://vpm/addRepo?url=https%3A%2F%2Fgryphprime.github.io%2Favatar-wardrobe-vpm%2Findex.json'
EMBED_COLOR = 0x8A5CF6
DESCRIPTION_LIMIT = 4096  # Discord's embed description limit.


def build_payload(tag, notes, release_url):
    notes = notes.strip()
    links = (f'\n\n**Update:** open VRChat Creator Companion or ALCOM and update Avatar Wardrobe.\n'
             f'**New here?** [Add the VPM listing]({LISTING_URL})')
    room = DESCRIPTION_LIMIT - len(links)
    if len(notes) > room:
        cut = notes.rfind('\n', 0, room - 1)
        notes = notes[:cut if cut > 0 else room - 1].rstrip() + '\n…'
    return {
        'username': 'Avatar Wardrobe',
        'allowed_mentions': {'parse': []},
        'embeds': [{
            'title': f'Avatar Wardrobe {tag} released',
            'url': release_url,
            'description': notes + links,
            'color': EMBED_COLOR,
        }],
    }


def post(webhook_url, payload):
    request = urllib.request.Request(
        webhook_url + ('&' if '?' in webhook_url else '?') + 'wait=true',
        data=json.dumps(payload).encode('utf-8'),
        headers={'Content-Type': 'application/json',
                 # Discord rejects the default urllib user agent.
                 'User-Agent': 'avatar-wardrobe-release (https://github.com/gryphprime/avatar-wardrobe-vpm, 1.0)'},
        method='POST')
    try:
        with urllib.request.urlopen(request, timeout=30) as response:
            response.read()
    except urllib.error.HTTPError as error:
        # Report Discord's message without echoing the webhook URL.
        raise SystemExit(f'Discord rejected the announcement: HTTP {error.code} {error.read().decode("utf-8", "replace")[:500]}')


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--tag', required=True)
    parser.add_argument('--notes', default='RELEASE_NOTES.md')
    parser.add_argument('--release-url', required=True)
    parser.add_argument('--dry-run', action='store_true', help='print the payload instead of posting it')
    args = parser.parse_args(argv)
    payload = build_payload(args.tag, Path(args.notes).read_text(encoding='utf-8'), args.release_url)
    if args.dry_run:
        print(json.dumps(payload, indent=2, ensure_ascii=False))
        return
    webhook_url = os.environ.get('DISCORD_WEBHOOK_URL', '').strip()
    if not webhook_url:
        print('DISCORD_WEBHOOK_URL is not set; skipping the Discord announcement.')
        return
    post(webhook_url, payload)
    print(f'Announced {args.tag} in Discord.')


if __name__ == '__main__':
    sys.exit(main())
