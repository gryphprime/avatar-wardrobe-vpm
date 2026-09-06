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
