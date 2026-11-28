import { D, api, cancelDraggedClick, h, haptic, online, rateStr, tp, tpv } from '../main.js';
import { W } from '../registry.js';

function callStatus(state, phoneMic) {
  switch (state?.state) {
    case 'live':
      if (state.mic) return tp('Camera and mic live');
      if (phoneMic?.live && phoneMic.muted) return tp('Camera live · Mic muted');
      return tp('Camera live');
    case 'standby': return tp('Camera ready · waiting for a Windows app');
    case 'stopping': return tp('Stopping capture…');
    case 'starting': {
      const camera = state.camera ? tp('Camera ready') : tp('Camera waiting');
      const mic = state.mic ? tp('Mic ready') : tp('Mic waiting');
      return tpv('Starting · {camera} · {mic}', { camera, mic });
    }
    default: return tp('Capture is off');
  }
}

W.mobycall = {
  needs: ['state'],
  create(box) {
    const title = h('h2', '', tp('Call'));
    const status = h('p', 'moby-call-status', tp('Capture is off'));
    status.setAttribute('role', 'status');
    const start = h('button', 'key moby-call-start', tp('Start capture'));
    const camera = h('button', 'key', tp('Camera only'));
    const stop = h('button', 'key', tp('Stop capture'));
    const buttons = h('div', 'moby-call-actions', start, camera, stop);
    box.append(title, status, buttons);

    let state, busy = false, error = '', errorAt = 0;
    const show = () => {
      const active = state?.state === 'starting' || state?.state === 'live';
      const standby = state?.state === 'standby';
      const stopping = state?.state === 'stopping';
      const canAddMic = state?.state === 'live' && !state.automatic && state.camera && !D.state?.phoneMic?.live;
      start.textContent = canAddMic ? tp('Add mic') : tp('Start capture');
      start.disabled = busy || !online || standby || stopping || (active && !canAddMic);
      camera.disabled = busy || !online || active || stopping;
      stop.disabled = busy || !online || (!active && !standby && state?.state !== 'error');
      const message = error || (!online ? tp('Waiting for Moby') : state?.message || state?.micMessage)
        || callStatus(state, D.state?.phoneMic);
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
        error = tp('Moby did not respond. Check the USB connection.');
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
    box.append(h('div', 'w-title', tp('CPU, disk & fans')), rows);
    function reading(label, value) { return [h('dt', '', label), h('dd', '', value)]; }
    return { update() {
      const pc = D.state?.pc;
      const fans = pc?.fans || [];
      rows.replaceChildren(
        ...reading(tp('CPU clock'), pc?.cpuMhz > 0 ? `${Math.round(pc.cpuMhz)} MHz` : tp('Unavailable')),
        ...reading(tp('Disk read'), pc?.diskReadBps == null ? tp('Unavailable') : rateStr(pc.diskReadBps)),
        ...reading(tp('Disk write'), pc?.diskWriteBps == null ? tp('Unavailable') : rateStr(pc.diskWriteBps)),
        ...(fans.length ? fans.flatMap(fan => reading(fan.name, `${Math.round(fan.rpm)} rpm`)) : reading(tp('System fans'), tp('Unavailable'))),
      );
    } };
  },
};
