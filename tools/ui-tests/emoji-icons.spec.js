// Emoji are drawn as Lucide icons: a mapped emoji on its own becomes an icon, a longer emoji sequence and an emoji with
// no icon stay text, and a page label with an icon still reads as text in the page-switch countdown.
const { test, expect } = require('@playwright/test');

test('an outline icon is horizontally centered on a shortcut key', async ({ page }) => {
  const config = {
    night: { enabled: false }, display: { idleReturnSec: 0 },
    shortcuts: [{ id: 'lock', label: 'Power Off', icon: '🔒', cmd: 'builtin:lock' }],
    pages: [{ id: 'keys', title: 'Keys', widgets: [{ id: 'deck', type: 'shortcuts', x: 0, y: 0, w: 12, h: 6 }] }],
  };
  await page.addInitScript(cfg => localStorage.setItem('pb-demo-config', JSON.stringify(cfg)), config);
  for (const [url, selector] of [['/panel/', '.deck-grid .key'], ['/settings/#shortcuts', '#deck .key']]) {
    await page.goto(url);
    const key = page.locator(selector).filter({ hasText: 'Power Off' });
    const icon = key.locator('svg[data-icon="lock"]');
    await expect(icon).toBeVisible();
    const [face, outline] = await Promise.all([key.boundingBox(), icon.boundingBox()]);
    expect(Math.abs((face.x + face.width / 2) - (outline.x + outline.width / 2)), url).toBeLessThan(1);
  }
});

test('rich() swaps a lone mapped emoji and leaves emoji sequences as typed', async ({ page }) => {
  await page.goto('/panel/');
  const out = await page.evaluate(async () => {
    const { rich } = await import('/panel/main.js');
    const probe = (text) => { const d = document.createElement('div'); d.append(rich(text)); return { icons: d.querySelectorAll('svg').length, text: d.textContent }; };
    return { bell: probe('\u{1F514} 3'), skin: probe('\u{1F44B}\u{1F3FD}'), family: probe('\u{1F468}\u200D\u{1F469}\u200D\u{1F467}'), unknown: probe('\u{1F984} x'), vs: probe('\u{1F3C3}\uFE0F\u200D\u{1F4BB}'), text: probe('\u2600\uFE0E'), sun: probe('\u2600\uFE0F') };
  });
  expect(out.bell).toEqual({ icons: 1, text: ' 3' });
  expect(out.skin).toEqual({ icons: 0, text: '\u{1F44B}\u{1F3FD}' });
  expect(out.family).toEqual({ icons: 0, text: '\u{1F468}\u200D\u{1F469}\u200D\u{1F467}' });
  expect(out.vs).toEqual({ icons: 0, text: '\u{1F3C3}\uFE0F\u200D\u{1F4BB}' }); // a variation selector before a ZWJ is not given back
  expect(out.text).toEqual({ icons: 0, text: '\u2600\uFE0E' }); // asked for text presentation: left alone
  expect(out.sun).toEqual({ icons: 1, text: '' }); // the emoji form is still an icon
  expect(out.unknown).toEqual({ icons: 0, text: '\u{1F984} x' });
});

test('settings show outline icons for Moby widgets and saved page icons', async ({ page }) => {
  await page.goto('/settings/');
  const capture = page.locator('.pal').filter({ hasText: 'Call capture' });
  await expect(capture.locator('svg[data-icon="video"] path').first()).toBeVisible();
  const field = page.locator('#peHead .icon-input');
  await field.fill('🎥');
  await field.blur();
  await expect(page.locator('#peHead .icon-field svg[data-icon="video"]')).toBeVisible();
  await expect(page.locator('.ptab.on svg[data-icon="video"]')).toBeVisible();
  await expect(field).toHaveValue('🎥');
});

test('shortcut preset options have contrasting colors and deletion is red', async ({ page }) => {
  await page.emulateMedia({ colorScheme: 'light' });
  await page.goto('/settings/#shortcuts');
  const preset = page.locator('#editor select').filter({ has: page.locator('option', { hasText: 'Windows Terminal' }) });
  const terminal = preset.locator('option', { hasText: 'Windows Terminal' });
  await expect(terminal).toHaveCSS('background-color', 'rgb(32, 32, 32)');
  await expect(terminal).toHaveCSS('color', 'rgb(244, 244, 244)');
  await preset.selectOption({ label: 'Windows Terminal' });
  await expect(page.locator('#editor input.technical-input').first()).toHaveValue('wt.exe');
  await expect(page.locator('#editor').getByRole('button', { name: 'Delete', exact: true })).toHaveCSS('background-color', 'rgb(73, 37, 37)');
});
