(() => {
  'use strict';

  // Only these bundled translations supply HTML; URL and storage values select a language.
  const translations = {
    en: {
      pageTitle: 'Avatar Wardrobe — VPM installation and usage',
      language: 'Language',
      intro: 'VPM repository for authorized users of Avatar Wardrobe.',
      openApp: 'Open in VCC / ALCOM',
      appHint: 'Your browser may ask permission to open the app. If nothing happens, use the manual steps below.',
      addHeading: '1. Add the repository',
      addIntro: 'Use <strong>Open in VCC / ALCOM</strong> above and confirm the repository in the app, or add it manually:',
      addStep1: 'Open ALCOM and go to <strong>Repositories → Add Repository</strong>.',
      addStep2: 'Paste this repository URL and confirm the repository.',
      copyUrl: 'Copy repository URL',
      copied: 'Copied. Paste the URL into ALCOM’s Add Repository dialog.',
      copyFallback: 'Select and copy the URL above, then paste it into ALCOM.',
      linkHelpHeading: 'Why doesn’t the app link open?',
      linkHelp: 'The link uses the <code>vcc:</code> URL scheme. An installed application must register that scheme, and the browser must allow opening it.',
      linkSettings: 'If your ALCOM settings offer <strong>Use ALCOM for vcc: URL Scheme</strong>, enable it and click <strong>Register URL Scheme Handler Now</strong>. These controls are platform-dependent; if they are absent, use the manual steps above.',
      installHeading: '2. Install Avatar Wardrobe',
      installStep1: 'Close the target project in Unity.',
      installStep2: 'In ALCOM, open your project’s <strong>Manage Packages</strong> page.',
      installStep3: 'Refresh the package list, search for <strong>Avatar Wardrobe</strong>, and install it.',
      installStep4: 'Review and apply the package changes.',
      regularReleases: 'Regular releases appear without enabling prerelease packages.',
      dependencies: 'VPM resolves the required VRChat SDK Avatars and Modular Avatar packages. If Modular Avatar cannot be found, add its repository using the <a href="https://modular-avatar.nadena.dev/docs/intro">Modular Avatar installation page</a>.',
      migration: 'An existing <code>Assets/OutfitToggleGenerator</code> installation is migrated automatically.',
      openHeading: '3. Open Avatar Wardrobe in Unity',
      openInstructions: 'Open the project in Unity. After compilation finishes, choose <strong>Tools → Avatar Wardrobe</strong>.',
      testProject: 'The VPM package still needs a fresh installation smoke test. Start with a separate test project before installing it in your main avatar project.',
      updateHeading: 'Update Avatar Wardrobe',
      updateInstructions: 'Close the project in Unity. In ALCOM or VCC, open the project’s <strong>Manage Packages</strong> page, refresh the list, and update <strong>Avatar Wardrobe</strong> to the latest version. Apply the changes, then reopen Unity and wait for compilation.',
      license: '<strong>View-only license.</strong> Installation and use require separate permission from gryphprime.',
      sourceLink: 'Source and license',
      guideLink: 'User guide',
      checks: 'VPM package: compilation and package checks passed; fresh Unity installation testing is pending.'
    },
    ja: {
      pageTitle: 'Avatar Wardrobe — VPM導入・使用ガイド',
      language: '言語',
      intro: 'Avatar Wardrobeの利用許可をお持ちの方のためのVPMリポジトリです。',
      openApp: 'VCC / ALCOMで開く',
      appHint: 'ブラウザーからアプリを開く許可を求められる場合があります。何も起こらない場合は、以下の手動での追加手順をご利用ください。',
      addHeading: '1. リポジトリを追加する',
      addIntro: '上の<strong>「VCC / ALCOMで開く」</strong>からアプリでリポジトリを確認するか、手動で追加してください：',
      addStep1: 'ALCOMを開き、<strong>Repositories → Add Repository</strong>（リポジトリ → リポジトリを追加）に進みます。',
      addStep2: '以下のリポジトリURLを貼り付け、リポジトリを確認して追加します。',
      copyUrl: 'リポジトリURLをコピー',
      copied: 'コピーしました。ALCOMの「Add Repository」ダイアログにURLを貼り付けてください。',
      copyFallback: '上のURLを選択してコピーし、ALCOMに貼り付けてください。',
      linkHelpHeading: 'アプリを開くリンクが動作しない場合',
      linkHelp: 'このリンクは<code>vcc:</code> URLスキームを使用します。インストール済みのアプリがこのスキームを登録し、ブラウザーがアプリを開くことを許可する必要があります。',
      linkSettings: 'ALCOMの設定に<strong>Use ALCOM for vcc: URL Scheme</strong>がある場合は有効にし、<strong>Register URL Scheme Handler Now</strong>をクリックしてください。これらの項目はプラットフォームによって異なります。項目がない場合は、上の手動での追加手順をご利用ください。',
      installHeading: '2. Avatar Wardrobeをインストールする',
      installStep1: '対象のプロジェクトをUnityで閉じます。',
      installStep2: 'ALCOMで対象プロジェクトの<strong>Manage Packages</strong>（パッケージ管理）を開きます。',
      installStep3: 'パッケージ一覧を更新し、<strong>Avatar Wardrobe</strong>を検索してインストールします。',
      installStep4: 'パッケージの変更内容を確認し、適用します。',
      regularReleases: '通常のリリースは、プレリリース版の表示を有効にしなくても一覧に表示されます。',
      dependencies: '必要なVRChat SDK AvatarsとModular AvatarのパッケージはVPMが解決します。Modular Avatarが見つからない場合は、<a href="https://modular-avatar.nadena.dev/docs/intro">Modular Avatarの導入ページ</a>からリポジトリを追加してください。',
      migration: '<code>Assets/OutfitToggleGenerator</code>に既存のインストールがある場合は、自動的に移行されます。',
      openHeading: '3. UnityでAvatar Wardrobeを開く',
      openInstructions: 'Unityでプロジェクトを開きます。コンパイルが完了したら、<strong>Tools → Avatar Wardrobe</strong>を選択してください。',
      testProject: 'VPMパッケージの新規インストール動作確認はまだ完了していません。メインのアバタープロジェクトに導入する前に、別のテスト用プロジェクトでお試しください。',
      updateHeading: 'Avatar Wardrobeを更新する',
      updateInstructions: 'Unityでプロジェクトを閉じます。ALCOMまたはVCCで対象プロジェクトの<strong>Manage Packages</strong>（パッケージ管理）を開き、一覧を更新して<strong>Avatar Wardrobe</strong>を最新版に更新します。変更を適用し、Unityを再度開いてコンパイルの完了を待ちます。',
      license: '<strong>閲覧専用ライセンス。</strong>インストールと使用には、gryphprimeから別途許可を得る必要があります。',
      sourceLink: 'ソースコードとライセンス',
      guideLink: '使用ガイド',
      checks: 'VPMパッケージ：コンパイルとパッケージチェックは合格しています。Unityでの新規インストールテストは未完了です。'
    },
    ko: {
      pageTitle: 'Avatar Wardrobe — VPM 설치 및 사용 안내',
      language: '언어',
      intro: 'Avatar Wardrobe 사용 허가를 받은 사용자를 위한 VPM 저장소입니다.',
      openApp: 'VCC / ALCOM에서 열기',
      appHint: '브라우저에서 앱을 열 수 있도록 허가를 요청할 수 있습니다. 아무 반응이 없다면 아래의 수동 추가 방법을 사용하세요.',
      addHeading: '1. 저장소 추가',
      addIntro: '위의 <strong>VCC / ALCOM에서 열기</strong>를 눌러 앱에서 저장소를 확인하거나, 다음 방법으로 직접 추가하세요:',
      addStep1: 'ALCOM을 열고 <strong>Repositories → Add Repository</strong>(저장소 → 저장소 추가)로 이동하세요.',
      addStep2: '아래 저장소 URL을 붙여 넣고 저장소를 확인하여 추가하세요.',
      copyUrl: '저장소 URL 복사',
      copied: '복사했습니다. ALCOM의 Add Repository 대화 상자에 URL을 붙여 넣으세요.',
      copyFallback: '위의 URL을 선택하여 복사한 다음 ALCOM에 붙여 넣으세요.',
      linkHelpHeading: '앱 열기 링크가 작동하지 않나요?',
      linkHelp: '이 링크는 <code>vcc:</code> URL 스킴을 사용합니다. 설치된 앱이 이 스킴을 등록해야 하며, 브라우저에서 앱을 열도록 허용해야 합니다.',
      linkSettings: 'ALCOM 설정에 <strong>Use ALCOM for vcc: URL Scheme</strong>이 있다면 활성화한 뒤 <strong>Register URL Scheme Handler Now</strong>를 누르세요. 이 항목들은 플랫폼에 따라 다릅니다. 항목이 없다면 위의 수동 추가 방법을 사용하세요.',
      installHeading: '2. Avatar Wardrobe 설치',
      installStep1: 'Unity에서 대상 프로젝트를 닫으세요.',
      installStep2: 'ALCOM에서 프로젝트의 <strong>Manage Packages</strong>(패키지 관리) 페이지를 여세요.',
      installStep3: '패키지 목록을 새로 고침하고 <strong>Avatar Wardrobe</strong>를 검색하여 설치하세요.',
      installStep4: '패키지 변경 사항을 확인하고 적용하세요.',
      regularReleases: '정식 릴리스는 사전 릴리스 패키지 표시를 활성화하지 않아도 목록에 나타납니다.',
      dependencies: 'VPM이 필요한 VRChat SDK Avatars 및 Modular Avatar 패키지의 의존성을 해결합니다. Modular Avatar를 찾을 수 없다면 <a href="https://modular-avatar.nadena.dev/docs/intro">Modular Avatar 설치 안내 페이지</a>에서 저장소를 추가하세요.',
      migration: '기존 <code>Assets/OutfitToggleGenerator</code> 설치는 자동으로 이전됩니다.',
      openHeading: '3. Unity에서 Avatar Wardrobe 열기',
      openInstructions: 'Unity에서 프로젝트를 여세요. 컴파일이 끝나면 <strong>Tools → Avatar Wardrobe</strong>를 선택하세요.',
      testProject: 'VPM 패키지의 신규 설치 동작 확인은 아직 완료되지 않았습니다. 기본 아바타 프로젝트에 설치하기 전에 별도의 테스트 프로젝트에서 먼저 시도하세요.',
      updateHeading: 'Avatar Wardrobe 업데이트',
      updateInstructions: 'Unity에서 프로젝트를 닫으세요. ALCOM 또는 VCC에서 프로젝트의 <strong>Manage Packages</strong>(패키지 관리) 페이지를 열고 목록을 새로 고침한 뒤 <strong>Avatar Wardrobe</strong>를 최신 버전으로 업데이트하세요. 변경 사항을 적용하고 Unity를 다시 열어 컴파일이 끝날 때까지 기다리세요.',
      license: '<strong>열람 전용 라이선스.</strong> 설치 및 사용에는 gryphprime의 별도 허가가 필요합니다.',
      sourceLink: '소스 코드 및 라이선스',
      guideLink: '사용 안내',
      checks: 'VPM 패키지: 컴파일 및 패키지 검사를 통과했습니다. Unity 신규 설치 테스트는 아직 완료되지 않았습니다.'
    },
    zh: {
      pageTitle: 'Avatar Wardrobe — VPM 安装与使用指南',
      language: '语言',
      intro: '供已获得 Avatar Wardrobe 使用授权的用户使用的 VPM 仓库。',
      openApp: '在 VCC / ALCOM 中打开',
      appHint: '浏览器可能会请求打开应用的权限。如果没有反应，请按下方步骤手动添加。',
      addHeading: '1. 添加仓库',
      addIntro: '点击上方的<strong>在 VCC / ALCOM 中打开</strong>，在应用中确认仓库，或按以下步骤手动添加：',
      addStep1: '打开 ALCOM，进入 <strong>Repositories → Add Repository</strong>（仓库 → 添加仓库）。',
      addStep2: '粘贴下方的仓库 URL，确认并添加仓库。',
      copyUrl: '复制仓库 URL',
      copied: '已复制。请将 URL 粘贴到 ALCOM 的 Add Repository 对话框中。',
      copyFallback: '请选中并复制上方的 URL，然后粘贴到 ALCOM 中。',
      linkHelpHeading: '为什么无法通过链接打开应用？',
      linkHelp: '此链接使用 <code>vcc:</code> URL 协议。已安装的应用必须注册该协议，浏览器也必须允许打开应用。',
      linkSettings: '如果 ALCOM 设置中有 <strong>Use ALCOM for vcc: URL Scheme</strong>，请启用它，然后点击 <strong>Register URL Scheme Handler Now</strong>。这些选项因平台而异；如果没有这些选项，请按上方步骤手动添加。',
      installHeading: '2. 安装 Avatar Wardrobe',
      installStep1: '在 Unity 中关闭目标项目。',
      installStep2: '在 ALCOM 中打开项目的 <strong>Manage Packages</strong>（管理软件包）页面。',
      installStep3: '刷新软件包列表，搜索 <strong>Avatar Wardrobe</strong> 并安装。',
      installStep4: '检查并应用软件包更改。',
      regularReleases: '无需启用预发布软件包，即可在列表中看到正式版本。',
      dependencies: 'VPM 会解析所需的 VRChat SDK Avatars 和 Modular Avatar 软件包依赖。如果找不到 Modular Avatar，请通过 <a href="https://modular-avatar.nadena.dev/docs/intro">Modular Avatar 安装指南页面</a>添加其仓库。',
      migration: '现有的 <code>Assets/OutfitToggleGenerator</code> 安装会自动迁移。',
      openHeading: '3. 在 Unity 中打开 Avatar Wardrobe',
      openInstructions: '在 Unity 中打开项目。编译完成后，选择 <strong>Tools → Avatar Wardrobe</strong>。',
      testProject: 'VPM 软件包的全新安装验证尚未完成。在安装到主要虚拟形象项目之前，请先在独立测试项目中尝试。',
      updateHeading: '更新 Avatar Wardrobe',
      updateInstructions: '在 Unity 中关闭项目。在 ALCOM 或 VCC 中打开项目的 <strong>Manage Packages</strong>（管理软件包）页面，刷新列表，将 <strong>Avatar Wardrobe</strong> 更新到最新版本。应用更改，然后重新打开 Unity，等待编译完成。',
      license: '<strong>仅供查阅的许可协议。</strong>安装和使用需要另行获得 gryphprime 的授权。',
      sourceLink: '源代码与许可协议',
      guideLink: '使用指南',
      checks: 'VPM 软件包：已通过编译和软件包检查；Unity 全新安装测试尚未完成。'
    }
  };

  const storageKey = 'avatar-wardrobe-vpm-language';
  const picker = document.getElementById('language');
  const url = document.getElementById('repository-url');
  const status = document.getElementById('copy-status');
  let currentLanguage = 'en';
  let copyMessage = '';

  function supportedLanguage(value) {
    const code = String(value || '').toLowerCase().split('-')[0];
    return Object.hasOwn(translations, code) ? code : null;
  }

  function initialLanguage() {
    const requested = supportedLanguage(new URLSearchParams(window.location.search).get('lang'));
    if (requested) return requested;
    try {
      const saved = supportedLanguage(localStorage.getItem(storageKey));
      if (saved) return saved;
    } catch { /* Language switching still works when browser storage is disabled. */ }
    for (const value of navigator.languages || [navigator.language]) {
      const code = supportedLanguage(value);
      if (code) return code;
    }
    return 'en';
  }

  function applyLanguage(code) {
    currentLanguage = code;
    document.documentElement.lang = code === 'zh' ? 'zh-Hans' : code;
    document.title = translations[code].pageTitle;
    document.querySelectorAll('[data-i18n]').forEach(element => {
      element.innerHTML = translations[code][element.dataset.i18n];
    });
    picker.value = code;
    status.textContent = copyMessage ? translations[code][copyMessage] : '';
  }

  picker.addEventListener('change', () => {
    const code = supportedLanguage(picker.value);
    if (!code) return;
    applyLanguage(code);
    try { localStorage.setItem(storageKey, code); } catch { /* Optional preference persistence. */ }
    const location = new URL(window.location.href);
    location.searchParams.set('lang', code);
    window.history.replaceState(null, '', location);
  });

  document.getElementById('copy-url').addEventListener('click', async () => {
    try {
      await navigator.clipboard.writeText(url.textContent.trim());
      copyMessage = 'copied';
    } catch {
      const range = document.createRange();
      range.selectNodeContents(url);
      const selection = window.getSelection();
      selection.removeAllRanges();
      selection.addRange(range);
      copyMessage = 'copyFallback';
    }
    status.textContent = translations[currentLanguage][copyMessage];
  });

  applyLanguage(initialLanguage());
  document.getElementById('language-control').hidden = false;
})();
