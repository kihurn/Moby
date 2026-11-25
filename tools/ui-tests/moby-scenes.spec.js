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
