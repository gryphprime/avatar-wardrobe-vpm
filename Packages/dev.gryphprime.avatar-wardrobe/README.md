# Avatar Wardrobe — VPM

Browser-based wardrobe indexing, outfit installation, generated toggles and
preset uploads for Unity 2022.3 VRChat avatar projects.

## License

View-only. Installation and use require separate permission from gryphprime.
See LICENSE and THIRD_PARTY_NOTICES.md. Availability in a VPM listing does not
grant permission to use the software.

## Install (authorized users)

Add https://gryphprime.github.io/avatar-wardrobe-vpm/index.json to ALCOM or VCC.
Install Avatar Wardrobe from the regular package list. VPM resolves VRChat SDK
Avatars and Modular Avatar; add the Modular Avatar repository if necessary.
Legacy Assets/OutfitToggleGenerator installations are migrated automatically.
Open Tools > Avatar Wardrobe after Unity compiles.

Windows includes a private Python runtime. macOS requires python3 on PATH;
optional Apple Intelligence features require macOS 26 or later.

This initial VPM has C# compilation and packaging checks, but has not yet
been validated through a full fresh VCC/ALCOM installation and avatar upload.
