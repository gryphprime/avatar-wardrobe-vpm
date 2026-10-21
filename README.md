# Avatar Wardrobe VPM repository

Public source and VPM distribution for Avatar Wardrobe.

**Repository listing URL:**

```text
https://gryphprime.github.io/avatar-wardrobe-vpm/index.json
```

- **Repository page:** https://gryphprime.github.io/avatar-wardrobe-vpm/
- **User guide:** https://gryphprime.github.io/avatar-wardrobe/

## License

**View-only, all rights reserved.** Installing or using the package requires
separate permission from gryphprime. Modification and redistribution are not
granted. GitHub platform rights and existing third-party licenses are preserved.
See [LICENSE](LICENSE) and [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).

## Install with ALCOM

These steps are for users authorized to install and use Avatar Wardrobe under
its license.

1. Open the [repository page](https://gryphprime.github.io/avatar-wardrobe-vpm/)
   and click **Open in VCC / ALCOM**. Confirm the repository in ALCOM.
   If the link does not open ALCOM, click **Copy repository URL** on the page,
   then open **Repositories → Add Repository** in
   ALCOM and paste this listing URL:

   ```text
   https://gryphprime.github.io/avatar-wardrobe-vpm/index.json
   ```

2. Close the target project in Unity. In ALCOM, open the project's
   **Manage Packages** page, refresh the package list, search for
   **Avatar Wardrobe**, and install it. Review and apply the package changes.
3. Open the project in Unity. After compilation finishes, choose
   **Tools → Avatar Wardrobe**.

VPM resolves the required VRChat SDK Avatars and Modular Avatar packages.
If Modular Avatar cannot be found, add its repository using the
[Modular Avatar installation page](https://modular-avatar.nadena.dev/docs/intro).
An existing `Assets/OutfitToggleGenerator` installation is migrated automatically.

The VPM package still needs a fresh installation smoke test. Start with a
separate test project before installing it in your main avatar project.

## Package

`Packages/dev.gryphprime.avatar-wardrobe` contains only the distributable tool.
Avatar assets, scenes, project settings, generated content and caches are excluded.
The VPM build uses runtime/editor assemblies and package-relative resource paths.
Fresh Unity installation smoke testing remains pending.

## Release

Packages are published only from version tags. Pushes to `main` run validation
and refresh the documentation site, keeping the live VPM listing unchanged.

To release:

1. On `main`, set the version: `python3 scripts/build_release.py --set-version 1.0.29`.
   This updates `package.json` (version and download URL) and
   `Editor/WardrobeVersion.json`. Add the changes to `RELEASE_NOTES.md`, then
   commit and push.
2. Tag that commit and push the tag: `git tag v1.0.29 && git push origin v1.0.29`.

The tag workflow refuses to publish unless the tag points to a commit on `main`
and matches both version files. It then builds the archive, creates the GitHub
release from `RELEASE_NOTES.md`, and adds the version to the VPM listing.
Rerunning it verifies the existing assets instead of replacing them.
Once the listing is live, it posts the release notes to Discord through the
webhook stored in the `DISCORD_WEBHOOK_URL` repository secret (skipped if the
secret is unset). Only the run that creates the release announces it.
Preview the message with
`python3 scripts/announce_discord.py --tag v1.0.29 --release-url URL --dry-run`.

For a local packaging check, run `python3 scripts/build_release.py --version 1.0.0`
(this rewrites the version files; do not commit the result).
The workflow merges the previous live listing, keeping old versions available.
Release archives include SHA-256 values in the listing and are never overwritten.
Release versus prerelease channels will be introduced later at launch.

See [development validation](DEVELOPMENT.md) for the review fixes, local test fixture, and required Unity CI runner provisioning.

## Reporting problems

Open **Settings → Report a problem** for bug reports. In an item's detail modal,
**Item was misclassified** opens a separate report with the selected variant's
classification metadata and a field for the expected category. Sending a report
does not change local classifications or install state.

Both forms preview the submitted information. Email is optional; app version and
browser diagnostics can be unchecked. Reports include an anonymous ID stored for
this browser origin. Item reports include bounded item/material/part names and a
prefab filename, without project paths, avatar assignments, screenshots, logs or
asset files. Reports go to Aelchor's private reporting inbox and are retained for
90 days. Avoid putting secrets or personal information in the description.

The API must be deployed at `https://reporting.aelchor.com` before releasing this
client. Bugs use `/v1/reports`; classification reports use
`/v1/misclassifications`. Network errors preserve the form, and retrying an
unchanged submission reuses its request ID to avoid duplicate reports.
