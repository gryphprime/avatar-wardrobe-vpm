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

Every push to `main` automatically publishes a regular GitHub and VPM release
and updates the repository listing. No manual tag or prerelease setting is needed.
This includes documentation-only pushes.

Versions are generated as `1.0.N`, where `N` is the release workflow run number.
The build stamps that version and download URL into the packaged `package.json`;
the source manifest is a template. The release tag points to the pushed source
commit. Rerunning a workflow keeps the same version and verifies existing assets.

For a local packaging check, run `python3 scripts/build_release.py --version 1.0.0`.
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
