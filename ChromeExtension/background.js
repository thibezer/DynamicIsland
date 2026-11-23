const WS_URL = "ws://127.0.0.1:49152/ws";
const KEEPALIVE_ALARM = "dynamic-island-pip-keepalive";

let socket = null;
let reconnectTimer = null;
let pingTimer = null;

function ensureConnected() {
  if (socket && (socket.readyState === WebSocket.OPEN || socket.readyState === WebSocket.CONNECTING)) {
    return;
  }

  try {
    socket = new WebSocket(WS_URL);

    socket.onopen = () => {
      if (reconnectTimer) {
        clearTimeout(reconnectTimer);
        reconnectTimer = null;
      }
      startPingLoop();
      safeSend({ type: "hello", client: "chrome-extension" });
    };

    socket.onmessage = async (event) => {
      try {
        const msg = JSON.parse(event.data);
        if (msg.action === "pip") {
          const result = await triggerYouTubePip();
          safeSend({ type: "pip_result", ...result });
        }
      } catch (err) {
        safeSend({ type: "pip_result", ok: false, error: String(err) });
      }
    };

    socket.onclose = () => {
      stopPingLoop();
      socket = null;
      scheduleReconnect();
    };

    socket.onerror = () => {
      // onclose será chamado em seguida
    };
  } catch {
    scheduleReconnect();
  }
}

function scheduleReconnect() {
  if (reconnectTimer) return;
  reconnectTimer = setTimeout(() => {
    reconnectTimer = null;
    ensureConnected();
  }, 3000);
}

function startPingLoop() {
  stopPingLoop();
  pingTimer = setInterval(() => {
    safeSend({ type: "ping" });
  }, 20000);
}

function stopPingLoop() {
  if (pingTimer) {
    clearInterval(pingTimer);
    pingTimer = null;
  }
}

function safeSend(payload) {
  if (socket && socket.readyState === WebSocket.OPEN) {
    socket.send(JSON.stringify(payload));
  }
}

async function findBestYouTubeTab() {
  const tabs = await chrome.tabs.query({ url: ["*://*.youtube.com/*"] });
  if (!tabs || tabs.length === 0) return null;

  const scoreTab = (tab) => {
    let score = 0;
    if (tab.audible) score += 1000;
    if (tab.url && (tab.url.includes("/watch") || tab.url.includes("/shorts/") || tab.url.includes("/live/"))) {
      score += 200;
    }
    if (tab.active) score += 50;
    return score;
  };

  tabs.sort((a, b) => {
    const diff = scoreTab(b) - scoreTab(a);
    if (diff !== 0) return diff;
    return (b.lastAccessed || 0) - (a.lastAccessed || 0);
  });

  return tabs[0];
}

async function triggerYouTubePip() {
  const tab = await findBestYouTubeTab();
  if (!tab || typeof tab.id !== "number") {
    return { ok: false, error: "no_youtube_tab" };
  }

  const target = { tabId: tab.id };
  const expression = `
    (async () => {
      const videos = Array.from(document.querySelectorAll('video'))
        .filter(v => v.readyState > 0 && v.videoWidth > 0 && v.videoHeight > 0);
      if (videos.length === 0) return { ok: false, error: 'no_video_element' };

      // Prioriza vídeo em reprodução; senão, o maior vídeo na página
      videos.sort((a, b) => {
        if (!a.paused && b.paused) return -1;
        if (a.paused && !b.paused) return 1;
        return (b.clientWidth * b.clientHeight) - (a.clientWidth * a.clientHeight);
      });

      const video = videos[0];
      if (document.pictureInPictureElement === video) {
        return { ok: true, status: 'already_in_pip' };
      }
      if (document.pictureInPictureElement) {
        await document.exitPictureInPicture();
      }
      if (video.disablePictureInPicture) {
        video.disablePictureInPicture = false;
      }
      await video.requestPictureInPicture();
      return { ok: true, status: 'entered' };
    })()
  `;

  try {
    await chrome.debugger.attach(target, "1.3");
    const evalResult = await chrome.debugger.sendCommand(target, "Runtime.evaluate", {
      expression,
      userGesture: true,
      awaitPromise: true,
      returnByValue: true
    });

    if (evalResult && evalResult.exceptionDetails) {
      return {
        ok: false,
        error: evalResult.exceptionDetails.text || "evaluate_exception"
      };
    }

    const value = evalResult?.result?.value;
    if (value && typeof value === "object") {
      return value;
    }
    return { ok: true, status: "entered" };
  } catch (err) {
    return { ok: false, error: err?.message || String(err) };
  } finally {
    try {
      await chrome.debugger.detach(target);
    } catch {
      // Ignora se já foi desanexado ou se a aba foi fechada
    }
  }
}

chrome.runtime.onInstalled.addListener(() => {
  chrome.alarms.create(KEEPALIVE_ALARM, { periodInMinutes: 0.5 });
  ensureConnected();
});

chrome.runtime.onStartup.addListener(() => {
  chrome.alarms.create(KEEPALIVE_ALARM, { periodInMinutes: 0.5 });
  ensureConnected();
});

chrome.alarms.onAlarm.addListener((alarm) => {
  if (alarm.name === KEEPALIVE_ALARM) {
    ensureConnected();
  }
});

// Garante conexão imediata quando o service worker acorda
ensureConnected();
