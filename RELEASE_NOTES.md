Upload results fix: one failed Look no longer marks the whole upload as failed.

- The upload result now shows each Look's own outcome. Before, if any Look failed, every Look in the batch showed as failed, even the ones that uploaded.
- Hover a failed Look to see why it failed. The error message now says how many Looks failed instead of repeating the whole summary.
- Retry uploads only the Looks that failed. Before, it used the older uploader's retry and found nothing to retry.

View-only license; see LICENSE and THIRD_PARTY_NOTICES.md.
