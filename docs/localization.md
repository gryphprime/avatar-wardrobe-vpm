# Localization

Avatar Wardrobe ships English (`en`), Japanese (`ja`), Korean (`ko`) and Simplified Chinese (`zh`). `Packages/dev.gryphprime.avatar-wardrobe/Web/lang.json` is the shared browser/Unity catalog. Each language has the same keys. Keep numeric and named placeholders unchanged, including `{0}`, `{preset}` and `{avatar}`.

Browser static copy uses `data-i18n`, with `-ph`, `-title`, `-aria` and `-alt` hooks for attributes. Dynamic UI uses the current translation function; independent modules use the runtime localizer. Same-origin API requests carry the selected language. Unity recognizes Korean and Chinese system languages, keeps request language across async continuations, and invalidates its catalog when the language asset is reimported.

Creator names, asset names, user-authored preset/menu names, file paths, SDK identifiers and historical logs remain source data. Never translate these by walking and rewriting arbitrary rendered text. Translate the labels surrounding them instead.

The Korean and Chinese catalogs, and newly covered Japanese entries, were generated with machine translation and reviewed for key workflow terminology and placeholder preservation. Further native-speaker wording improvements are welcome.

Run `node --test tests/localization.test.js` and the browser test suite after changing localization. Verify language switching in the live UI, especially Organization, Settings, the wearing list, item details and upload dialogs. Preserve unsaved form values while rebuilding translated controls.
