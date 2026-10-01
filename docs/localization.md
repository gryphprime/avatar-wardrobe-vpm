# Localization

Avatar Wardrobe ships English (`en`), Japanese (`ja`), Korean (`ko`) and Simplified Chinese (`zh`). `Packages/dev.gryphprime.avatar-wardrobe/Web/lang.json` is the shared browser/Unity catalog. Each language has the same keys. Keep numeric and named placeholders unchanged, including `{0}`, `{preset}` and `{avatar}`.

Browser static copy uses `data-i18n`, with `-ph`, `-title`, `-aria` and `-alt` hooks for attributes. Dynamic UI uses the current translation function; independent modules use the runtime localizer. Same-origin API requests carry the selected language. Unity recognizes Korean and Chinese system languages, keeps request language across async continuations, and invalidates its catalog when the language asset is reimported.

Creator names, asset names, user-authored preset/menu names, file paths, SDK identifiers and historical logs remain source data. Never translate these by walking and rewriting arbitrary rendered text. Translate the labels surrounding them instead.

The Korean and Chinese catalogs, and newly covered Japanese entries, were generated with machine translation and reviewed for key workflow terminology and placeholder preservation. Further native-speaker wording improvements are welcome.

The VPM installation page uses the same four languages in `docs/localization.js`.
Every instruction, heading, link label, copy-button result, and page title is translated.
Keep the language options in `docs/index.html` aligned with the shared catalog. URLs,
package names, paths, and exact application menu labels remain unchanged.
The page chooses an explicit `?lang=en|ja|ko|zh` first, then a saved choice, then the
first supported browser language. Changing language saves the choice and preserves
the `#update` anchor. If storage is unavailable, switching and copying still work.
The English instructions remain available without JavaScript. `docs/VPM_USAGE.txt`
links to this page and each language for the BOOTH download.

Run `node --test tests/vpm-page-localization.test.js` after changing the VPM page,
and check language switching and the copy button in a browser. Documentation-only
deployments preserve the live VPM listing and do not publish a new package release.

Run `node --test tests/localization.test.js` and the browser test suite after changing localization. Verify language switching in the live UI, especially Organization, Settings, the wearing list, item details and upload dialogs. Preserve unsaved form values while rebuilding translated controls.
