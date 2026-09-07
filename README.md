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

Development pushes to `dev` and pull requests run checks without publishing.
Stable release publication is a manual workflow on `main`. The maintainer must
confirm that a fresh-project installation and representative avatar build were
validated for that source commit. Documentation changes do not publish releases.

Versions are generated as `1.0.N` from the release workflow run number. The build
stamps the version and download URL into the package; source manifests are templates.
Published assets remain immutable and the listing retains prior versions.

For a local packaging check, run `python3 scripts/build_release.py --version 1.0.0`.

See [implementation progress](docs/implementation-progress.md) for this development
branch's delivered features and remaining validation gates.
