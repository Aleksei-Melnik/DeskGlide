import { build } from 'esbuild';
import { mkdir, writeFile, copyFile, cp } from 'node:fs/promises';
import { actions, uuid, keyImage } from '../src/presentation.mjs';
const root = `${uuid}.sdPlugin`;
await mkdir(`${root}/bin`, { recursive: true }); await mkdir(`${root}/imgs`, { recursive: true });
await build({ entryPoints: ['src/plugin.mjs'], outfile: `${root}/bin/plugin.js`, bundle: true, platform: 'node', format: 'esm', target: 'node24',
  banner: { js: "import { createRequire } from 'node:module'; const require = createRequire(import.meta.url);" } });
const manifest = {
  $schema: 'https://schemas.elgato.com/streamdeck/plugins/manifest.json', UUID: uuid, Name: 'DeskGlide', Category: 'DeskGlide',
  CategoryIcon: 'imgs/category', Author: 'Yami', Description: 'Save instant replays, open remote computers and control mouse transitions with live DeskGlide status.',
  Version: '1.1.1.0', CodePath: 'bin/plugin.js', Icon: 'imgs/plugin', URL: 'https://github.com/Aleksei-Melnik/DeskGlide',
  SDKVersion: 3, Nodejs: { Version: '24' }, Software: { MinimumVersion: '7.1' }, OS: [{ Platform: 'windows', MinimumVersion: '10' }],
  PropertyInspectorPath: 'ui/settings.html', Actions: Object.entries(actions).map(([kind, value]) => ({
    UUID: `${uuid}.${kind}`, Name: value.name, Tooltip: value.name, Icon: `imgs/${kind}-list`, Controllers: ['Keypad'],
    DisableAutomaticStates: true, SupportedInMultiActions: kind !== 'save-replay',
    States: [0, 1].map(() => ({ Image: `imgs/${kind}`, TitleAlignment: 'bottom', FontSize: 10 }))
  }))
};
await writeFile(`${root}/manifest.json`, JSON.stringify(manifest, null, 2) + '\n');
for (const [kind, value] of Object.entries(actions)) {
  await writeFile(`${root}/imgs/${kind}.svg`, keyImage(kind, { mode: kind === 'lock-mouse' ? 'locked' : 'ready', fill: 0 }));
  await writeFile(`${root}/imgs/${kind}-list.svg`, `<svg xmlns="http://www.w3.org/2000/svg" width="20" height="20" viewBox="0 0 88 88"><path d="${value.glyph}" fill="none" stroke="#FFFFFF" stroke-width="6" stroke-linejoin="round" stroke-linecap="round"/></svg>`);
}
await writeFile(`${root}/imgs/category.svg`, '<svg xmlns="http://www.w3.org/2000/svg" width="28" height="28" viewBox="0 0 28 28"><path d="M4 5h20v14H4z M10 24h8 M14 19v5" fill="none" stroke="#FFFFFF" stroke-width="2" stroke-linejoin="round"/></svg>');
await copyFile('../LICENSE', `${root}/LICENSE`);
await cp('licenses', `${root}/licenses`, { recursive: true });
await writeFile(`${root}/THIRD-PARTY-NOTICES.txt`, 'DeskGlide uses the official Elgato Stream Deck SDK 3.0.2, @elgato/utils and @elgato/schemas (Corsair Memory Inc., MIT), ws 8.22.0 (MIT), and Zod 3 (MIT). Full license texts are included in licenses. Stream Deck supplies its own Node runtime. No Elgato endorsement is implied.\n');
