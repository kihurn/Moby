import { D, fmtMem, h } from '../main.js';
import { W } from '../registry.js';

W.phonemetrics = {
  needs: ['phoneMetrics'],
  create(box) {
    const rows = h('dl', 'moby-sensor-list');
    box.append(h('div', 'w-title', 'Phone metrics'), rows);
    const fields = ['CPU load', 'RAM used', 'CPU temp', 'Battery', 'Battery temp'].map(label => {
      const value = h('dd', '', '—');
      rows.append(h('dt', '', label), value);
      return value;
    });
    const message = h('dd', 'muted');
    message.style.gridColumn = '1 / -1';
    rows.append(message);
    const percent = n => n == null ? '—' : Math.round(n) + '%';
    const temperature = n => n == null ? '—' : n.toFixed(1) + ' °C';
    return { update() {
      const data = D.phoneMetrics;
      const ram = data?.ramUsedKb == null ? '—'
        : fmtMem(data.ramUsedKb / 1024) + ' / ' + fmtMem(data.ramTotalKb / 1024);
      const readings = [percent(data?.cpuPercent), ram, temperature(data?.cpuTempC),
        percent(data?.batteryPercent), temperature(data?.batteryTempC)];
      const missing = readings.every(value => value === '—');
      for (let i = 0; i < fields.length; i++) {
        fields[i].hidden = fields[i].previousElementSibling.hidden = missing;
        if (fields[i].textContent !== readings[i]) fields[i].textContent = readings[i];
      }
      message.hidden = !missing;
      message.textContent = data === undefined ? 'Reading phone metrics…' : 'Phone metrics unavailable · check USB';
    } };
  },
};
