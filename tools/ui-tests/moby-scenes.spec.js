const { test, expect } = require('@playwright/test');

async function openPanel(page) {
  await page.setViewportSize({ width: 800, height: 400 });
  await page.addInitScript(config => localStorage.setItem('pb-demo-config', config), JSON.stringify({
    night: { enabled: false },
    display: { theme: 'liquid-glass', idleReturnSec: 0 },
    shortcuts: [{ id: 'calc', label: 'Calculator', cmd: 'calc.exe' }],
    pages: [
      { id: 'idle', title: 'Idle', widgets: [{ id: 'photos', type: 'photos', x: 0, y: 0, w: 12, h: 6, options: { clock: true } }] },
      { id: 'shortcuts', title: 'Shortcuts', widgets: [{ id: 'keys', type: 'shortcuts', x: 0, y: 0, w: 12, h: 6 }] },
    ],
  }));
  await page.goto('/panel/');
}

test('Liquid Glass uses the phone clock over the photo frame', async ({ page }) => {
  await openPanel(page);
  await expect(page.locator('body')).toHaveAttribute('data-theme', 'liquid-glass');
  await expect(page.locator('.pf-clock .hh')).toHaveText(/^\d{1,2}$/);
});

test('dragging across a key cancels its click, while a tap runs it', async ({ page }) => {
  await openPanel(page);
  await page.evaluate(() => window.onAndroidKey('next'));
  const key = page.getByRole('button', { name: /Calculator/ });
  await expect(key).toBeVisible();
  await page.evaluate(() => {
    window.mobyRuns = 0;
    const fetch = window.fetch;
    window.fetch = (input, options) => {
      if (new URL(input.url || input, location.href).pathname.endsWith('/api/run/calc')) window.mobyRuns++;
      return fetch(input, options);
    };
  });
  await key.evaluate(node => {
    node.dispatchEvent(new PointerEvent('pointerdown', { pointerId: 1, pointerType: 'touch', bubbles: true, clientX: 80, clientY: 70 }));
    node.dispatchEvent(new PointerEvent('pointermove', { pointerId: 1, pointerType: 'touch', bubbles: true, clientX: 140, clientY: 70 }));
    node.dispatchEvent(new PointerEvent('pointerup', { pointerId: 1, pointerType: 'touch', bubbles: true, clientX: 140, clientY: 70 }));
    node.dispatchEvent(new MouseEvent('click', { detail: 1, bubbles: true, cancelable: true }));
  });
  await expect(key).not.toHaveClass(/busy|ok|err/);
  expect(await page.evaluate(() => window.mobyRuns)).toBe(0);
  await key.click();
  await expect.poll(() => page.evaluate(() => window.mobyRuns)).toBe(1);
});

test('On air optionally toggles camera capture and follows the capture state', async ({ page }) => {
  await page.addInitScript(() => localStorage.setItem('pb-demo-config', JSON.stringify({
    night: { enabled: false },
    pages: [{ id: 'one', title: 'One', widgets: [
      { id: 'toggle', type: 'onair', x: 0, y: 0, w: 6, h: 3, options: { touchToggle: true, source: 'mic', text: 'VIVO MIC' } },
      { id: 'status', type: 'onair', x: 6, y: 0, w: 6, h: 3, options: {} },
    ] }],
  })));
  await page.goto('/panel/');
  await page.evaluate(async () => {
    const m = await import('/panel/main.js');
    const fetch = window.fetch;
    window.captureRequests = [];
    window.fetch = (input, init) => {
      const url = new URL(input, location.href);
      if (url.pathname.endsWith('/api/state') && m.D.state?.mobyCall) return Promise.resolve(Response.json(m.D.state));
      if (!url.pathname.includes('/api/moby/call/')) return fetch(input, init);
      window.captureRequests.push([init.method, url.pathname.split('/').pop(), url.search]);
      const camera = url.pathname.endsWith('/start');
      const state = { state: camera ? 'live' : 'off', camera };
      m.applyState({ ts: Date.now(), privacy: { mic: true }, phoneMic: { state: 'live', live: true }, mobyCall: state });
      return Promise.resolve(Response.json(state));
    };
    m.applyState({ ts: Date.now(), privacy: { mic: true }, phoneMic: { state: 'live', live: true }, mobyCall: { state: 'off', camera: false } });
  });
  const toggle = page.getByRole('button', { name: 'Toggle call capture' });
  await expect(toggle).toHaveAttribute('aria-pressed', 'false');
  await expect(toggle).toContainText('VIVO MIC');
  await expect(toggle).toContainText('Mic live');
  const status = page.locator('.w-onair').filter({ hasNot: page.getByRole('button') });
  await expect(status).toHaveClass(/\bon\b/);
  await toggle.click();
  await expect(toggle).toHaveAttribute('aria-pressed', 'true');
  await page.evaluate(async () => {
    const m = await import('/panel/main.js');
    m.applyState({ ...m.D.state, ts: Date.now(), phoneMic: { state: 'error', live: false },
      mobyCall: { state: 'starting', camera: true, mic: false, message: 'Mic unavailable' } });
  });
  await expect(toggle).toBeEnabled({ timeout: 1000 });
  await toggle.click();
  await expect(toggle).toHaveAttribute('aria-pressed', 'false');
  await expect(toggle).toContainText('Mic live');
  await page.evaluate(async () => {
    const m = await import('/panel/main.js');
    m.applyState({ ...m.D.state, ts: Date.now() + 1000, phoneMic: { state: 'live', live: false, muted: true } });
  });
  await expect(toggle).toContainText('Mic muted');
  await expect(toggle.locator('..')).not.toHaveClass(/\bon\b/);
  expect(await page.evaluate(() => window.captureRequests)).toEqual([
    ['POST', 'start', '?cameraOnly=true'], ['POST', 'stop', ''],
  ]);
});

test('custom HTML data stays with the original widget document', async ({ page }) => {
  await page.route('**/panel/next-widget-document', route => route.fulfill({
    contentType: 'text/html',
    body: '<p id="received">Waiting for check</p><script>let received = false; addEventListener("message", e => { if (e.data?.phonebox === "data") received = true; if (e.data?.test === "flush") document.querySelector("p").textContent = received ? "Received widget data" : "No widget data"; });</script>',
  }));
  await page.addInitScript(() => {
    if (window.top !== window) return;
    localStorage.setItem('pb-demo-config', JSON.stringify({
      night: { enabled: false },
      shortcuts: [{ id: 'check', label: 'Check', cmd: 'example' }],
      pages: [{ id: 'one', title: 'One', widgets: [
        { id: 'custom', type: 'html', x: 0, y: 0, w: 8, h: 4, options: {
          html: '<p id="received">Waiting</p><button onclick="phonebox.run(\'check\')">Check</button><a id="next" href="/panel/next-widget-document">Next</a><script>phonebox.on("state", () => document.querySelector("p").textContent = "Original widget ready");</script>',
        } },
      ] }],
    }));
  });
  await page.goto('/panel/');
  const frame = page.frameLocator('.html-frame');
  await expect(frame.locator('#received')).toHaveText('Original widget ready');
  await page.evaluate(() => {
    const fetch = window.fetch;
    window.customRuns = 0;
    window.fetch = (input, init) => {
      if (new URL(input.url || input, location.href).pathname.endsWith('/api/run/check')) window.customRuns++;
      return fetch(input, init);
    };
  });
  await frame.getByRole('button', { name: 'Check' }).click();
  await expect.poll(() => page.evaluate(() => window.customRuns)).toBe(1);
  await frame.locator('#next').click();
  await expect(frame.locator('#received')).toHaveText('Waiting for check');
  await page.evaluate(async () => {
    const m = await import('/panel/main.js');
    m.emit('state');
    m.emit('push');
    document.querySelector('.html-frame').contentWindow.postMessage({ test: 'flush' }, '*');
  });
  await expect(frame.locator('#received')).toHaveText('No widget data');
});

test('settings expose control labels and webhook column names', async ({ page }) => {
  await page.goto('/settings/#general');
  await expect(page.getByRole('textbox', { name: 'Search for a city' })).toHaveAttribute('id', 'citySearch');
  await expect(page.getByRole('spinbutton', { name: 'Latitude' })).toBeVisible();
  await expect(page.getByRole('spinbutton', { name: 'Longitude' })).toBeVisible();
  await expect(page.getByRole('checkbox', { name: 'Let the phone connect without the USB cable' })).toHaveCount(1);
  await page.goto('/settings/#webhooks');
  await expect(page.getByRole('columnheader', { name: 'Key' })).toBeVisible();
  await expect(page.getByRole('columnheader', { name: 'Value' })).toBeVisible();
});
