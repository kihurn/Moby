const { test, expect } = require('@playwright/test');

test('presence state dims and restores the USB panel, and Call waits for native release', async ({ page }) => {
  await page.addInitScript(() => {
    window.__phoneboxKey = 'presence-test';
    window.presenceBridgeCalls = [];
    window.Android = {
      battery: () => '{}', version: () => 'test', transport: () => 'usb', is24Hour: () => true,
      pcs: () => '[]', setVolumeKeys() {},
      setBrightness(key, brightness) { window.presenceBridgeCalls.push([key, 'brightness', brightness]); },
      setDeskPresence(key, enabled, release) { window.presenceBridgeCalls.push([key, enabled, release]); },
    };
    localStorage.setItem('pb-demo-config', JSON.stringify({
      night: { enabled: false }, display: { autoBrightness: true, nightWhenDark: true },
    }));
  });
  await page.goto('/panel/');
  const state = (data) => page.evaluate(async (deskPresence) => {
    const panel = await import('/panel/main.js');
    window.presenceTick = (window.presenceTick || 9e12) + 1;
    panel.applyState({ ts: window.presenceTick, deskPresence });
  }, data);

  await expect.poll(() => page.evaluate(async () => (await import('/panel/main.js')).cfg.display?.nightWhenDark)).toBe(true);
  await page.evaluate(async () => {
    const panel = await import('/panel/main.js');
    const now = Date.now;
    window.onAndroidSensor('light', 1);
    try {
      Date.now = () => now() + 31_000;
      panel.tick();
    } finally { Date.now = now; }
  });
  await expect(page.locator('body')).toHaveClass(/\bnight\b/);
  await state({ enabled: true, sample: true, dim: false });
  await expect(page.locator('body')).not.toHaveClass(/\bnight\b/);
  expect(await page.evaluate(async () => (await import('/panel/main.js')).lastBrightness)).toBe(-2);

  await state({ enabled: true, sample: true, dim: true });
  await expect(page.locator('body')).toHaveClass(/desk-away/);
  expect(await page.evaluate(() => getComputedStyle(document.body, '::after').transitionDuration)).toBe('0.25s');
  await state({ enabled: true, sample: true, dim: true, monitorStandby: true });
  await page.evaluate(() => window.onAndroidSensor('light', 900));
  await expect(page.locator('body')).toHaveClass(/desk-away/);
  expect(await page.evaluate(async () => (await import('/panel/main.js')).lastBrightness)).toBe(0.05);
  expect(await page.evaluate(() => window.presenceBridgeCalls)).toContainEqual(['presence-test', 'brightness', 0.05]);
  expect(await page.evaluate(() => window.presenceBridgeCalls)).toContainEqual(['presence-test', true, 0]);

  await page.evaluate(() => {
    const fetch = window.fetch;
    window.fetch = (input, init) => {
      if (String(input).includes('/desk-presence?release=')) {
        window.presenceRelease = [String(input).split('release=')[1], init.method];
        return Promise.resolve(new Response(null, { status: 204 }));
      }
      return fetch(input, init);
    };
  });
  await state({ enabled: true, sample: false, dim: false, cameraRelease: 12 });
  await expect(page.locator('body')).not.toHaveClass(/desk-away/);
  expect(await page.evaluate(() => window.presenceBridgeCalls)).toContainEqual(['presence-test', 'brightness', -2]);
  expect(await page.evaluate(() => getComputedStyle(document.body, '::after').transitionDuration)).toBe('0.25s');
  expect(await page.evaluate(() => window.presenceBridgeCalls)).toContainEqual(['presence-test', false, 12]);
  await page.evaluate(() => window.onDeskPresenceSample('released:11'));
  expect(await page.evaluate(() => window.presenceRelease)).toBeUndefined();
  await page.evaluate(() => window.onDeskPresenceSample('released:12'));
  expect(await page.evaluate(() => window.presenceRelease)).toEqual(['12', 'POST']);
});
