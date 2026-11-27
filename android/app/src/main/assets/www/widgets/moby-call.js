import { D, api, cancelDraggedClick, h, haptic, online, rateStr } from '../main.js';
import { W } from '../registry.js';

W.mobycall = {
  needs: ['state'],
  create(box) {
    const title = h('h2', '', 'Call');
    const status = h('p', 'moby-call-status', 'Capture is off');
    status.setAttribute('role', 'status');
    const start = h('button', 'key moby-call-start', 'Start capture');
    const camera = h('button', 'key', 'Camera only');
    const stop = h('button', 'key', 'Stop capture');
    const buttons = h('div', 'moby-call-actions', start, camera, stop);
    box.append(title, status, buttons);

    let state, busy = false, error = '', errorAt = 0;
    const show = () => {
      const active = state?.state === 'starting' || state?.state === 'live';
      const standby = state?.state === 'standby';
      const stopping = state?.state === 'stopping';
      const canAddMic = state?.state === 'live' && !state.automatic && state.camera && !D.state?.phoneMic?.live;
      start.textContent = canAddMic ? 'Add mic' : 'Start capture';
      start.disabled = busy || !online || standby || stopping || (active && !canAddMic);
      camera.disabled = busy || !online || active || stopping;
      stop.disabled = busy || !online || (!active && !standby && state?.state !== 'error');
      const message = error || (!online ? 'Waiting for Moby' : state?.message || state?.micMessage)
        || (state?.state === 'live' ? (state.mic ? 'Camera and mic live'
          : D.state?.phoneMic?.live && D.state.phoneMic.muted ? 'Camera live · Mic muted' : 'Camera live')
          : standby ? 'Camera ready · waiting for a Windows app'
          : stopping ? 'Stopping capture…'
          : active ? `Starting · Camera ${state?.camera ? 'ready' : 'waiting'} · Mic ${state?.mic ? 'ready' : 'waiting'}`
          : 'Capture is off');
      if (status.textContent !== message) status.textContent = message;
    };
    async function change(action, cameraOnly = false) {
      haptic();
      busy = true;
      error = '';
      show();
      try {
        state = await api(`/moby/call/${action}${cameraOnly ? '?cameraOnly=true' : ''}`, 'POST', action === 'start' ? 10000 : 5000);
      }
      catch (_) {
        error = 'Moby did not respond. Check the USB connection.';
        errorAt = D.state?.ts || 0;
      }
      finally { busy = false; show(); }
    }
    cancelDraggedClick(start);
    cancelDraggedClick(camera);
    cancelDraggedClick(stop);
    start.addEventListener('click', () => change('start'));
    camera.addEventListener('click', () => change('start', true));
    stop.addEventListener('click', () => change('stop'));
    show();
    return { update() {
      state = D.state?.mobyCall;
      if (state && D.state.ts > errorAt) error = '';
      show();
    } };
  },
};

W.mobysensors = {
  needs: ['state'],
  create(box) {
    const rows = h('dl', 'moby-sensor-list');
    box.append(h('div', 'w-title', 'CPU, disk & fans'), rows);
    function reading(label, value) { return [h('dt', '', label), h('dd', '', value)]; }
    return { update() {
      const pc = D.state?.pc;
      const fans = pc?.fans || [];
      rows.replaceChildren(
        ...reading('CPU clock', pc?.cpuMhz > 0 ? `${Math.round(pc.cpuMhz)} MHz` : 'Unavailable'),
        ...reading('Disk read', pc?.diskReadBps == null ? 'Unavailable' : rateStr(pc.diskReadBps)),
        ...reading('Disk write', pc?.diskWriteBps == null ? 'Unavailable' : rateStr(pc.diskWriteBps)),
        ...(fans.length ? fans.flatMap(fan => reading(fan.name, `${Math.round(fan.rpm)} rpm`)) : reading('System fans', 'Unavailable')),
      );
    } };
  },
};
