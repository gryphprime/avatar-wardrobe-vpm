# Avatar Wardrobe VPM repository

Public source and VPM distribution for Avatar Wardrobe.

- **Listing:** https://gryphprime.github.io/avatar-wardrobe-vpm/index.json
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
   and click **Add repository to VCC / ALCOM**. Confirm the repository in ALCOM.
   If the link does not open ALCOM, open **Repositories → Add Repository** in
   ALCOM and paste this listing URL:

   ```text
   https://gryphprime.github.io/avatar-wardrobe-vpm/index.json
   ```

2. In ALCOM's settings, enable **Show Prerelease Packages**. The initial release
   is a beta and may otherwise be hidden.
3. Close the target project in Unity. In ALCOM, open the project's
   **Manage Packages** page, refresh the package list, search for
   **Avatar Wardrobe**, and install it. Review and apply the package changes.
4. Open the project in Unity. After compilation finishes, choose
   **Tools → Avatar Wardrobe**.

VPM resolves the required VRChat SDK Avatars and Modular Avatar packages.
If Modular Avatar cannot be found, add its repository using the
[Modular Avatar installation page](https://modular-avatar.nadena.dev/docs/intro).
An existing `Assets/OutfitToggleGenerator` installation is migrated automatically.

The first VPM beta still needs a fresh installation smoke test. Start with a
separate test project before installing it in your main avatar project.

## Package

`Packages/dev.gryphprime.avatar-wardrobe` contains only the distributable tool.
Avatar assets, scenes, project settings, generated content and caches are excluded.
The VPM build uses runtime/editor assemblies and package-relative resource paths.
The initial release is a beta pending a fresh Unity installation smoke test.

## Release

1. Update the package source and its `package.json` version and release URL.
2. Run `python3 scripts/build_release.py` to validate and build the archive.
3. Commit the source, then push a tag matching the version, such as `v1.0.0-beta.2`.
4. The release workflow uploads the archive and deploys the VPM listing to Pages.

The workflow merges the previous live listing before publishing, keeping old
versions available. Release archives include SHA-256 values in the listing.
Never overwrite an existing version; publish a new version for changed content.
