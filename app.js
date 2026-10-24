'use strict';

(function () {
  let ws = null;
  let hls = null;
  let osdTimeout = null;
  let controlsTimeout = null;

  // عناصر واجهة المستخدم
  const welcomeScreen = document.getElementById('welcome-screen');
  const connStatus = document.getElementById('conn-status');
  const tvIpEl = document.getElementById('tv-ip');
  const serverUrlEl = document.getElementById('server-url');
  const connectedDeviceEl = document.getElementById('connected-device');

  const video = document.getElementById('tv-player');
  const audioTrack = document.getElementById('tv-audio-track');
  const img = document.getElementById('tv-image');
  const osdBanner = document.getElementById('osd-banner');
  const osdMessage = document.getElementById('osd-message');
  const playerControls = document.getElementById('player-controls');
  const progressBar = document.getElementById('progress-bar');
  const currentTimeEl = document.getElementById('current-time');
  const totalDurationEl = document.getElementById('total-duration');

  function getWsTarget() {
    const params = new URLSearchParams(window.location.search);
    const ip = params.get('ip') || window.location.hostname || '127.0.0.1';
    const port = params.get('port') || '8088';
    return 'ws://' + ip + ':' + port + '/ws';
  }

  function displayLocalIp() {
    const host = window.location.hostname;
    if (host && host !== '' && host !== 'localhost' && host !== '127.0.0.1') {
      tvIpEl.textContent = host;
    } else {
      tvIpEl.textContent = '127.0.0.1 (محلي)';
    }
  }

  function setStatus(state, text) {
    connStatus.className = 'status-badge ' + state;
    connStatus.textContent = text;
  }

  function showOSD(text) {
    osdMessage.textContent = text;
    osdBanner.classList.remove('osd-hidden');
    clearTimeout(osdTimeout);
    osdTimeout = setTimeout(function () {
      osdBanner.classList.add('osd-hidden');
    }, 2500);
  }

  function showControls() {
    playerControls.classList.remove('controls-hidden');
    clearTimeout(controlsTimeout);
    controlsTimeout = setTimeout(function () {
      playerControls.classList.add('controls-hidden');
    }, 3500);
  }

  function formatTime(seconds) {
    if (isNaN(seconds) || seconds < 0) return '00:00:00';
    const h = Math.floor(seconds / 3600);
    const m = Math.floor((seconds % 3600) / 60);
    const s = Math.floor(seconds % 60);
    return [h, m, s].map(function (v) { return v < 10 ? '0' + v : v; }).join(':');
  }

  function initWebSocket() {
    const targetUrl = getWsTarget();
    serverUrlEl.textContent = targetUrl.replace('ws://', '');
    setStatus('connecting', 'جاري الاتصال...');

    try {
      ws = new WebSocket(targetUrl);
    } catch (e) {
      setTimeout(initWebSocket, 4000);
      return;
    }

    ws.onopen = function () {
      setStatus('connected', 'متصل بالسيرفر');
      ws.send(JSON.stringify({ type: 'REGISTER', client: 'TV_RECEIVER' }));
    };

    ws.onclose = function () {
      setStatus('disconnected', 'غير متصل');
      connectedDeviceEl.textContent = 'لا يوجد';
      setTimeout(initWebSocket, 3000);
    };

    ws.onerror = function () {
      ws.close();
    };

    ws.onmessage = function (event) {
      try {
        const cmd = JSON.parse(event.data);
        handleIncomingCommand(cmd);
      } catch (err) {
        // تجاهل أي رسالة تالفة
      }
    };
  }

  function handleIncomingCommand(cmd) {
    if (cmd.senderDevice) {
      connectedDeviceEl.textContent = cmd.senderDevice;
    }

    switch (cmd.action) {
      case 'PLAY_VIDEO':
        playVideo(cmd.url, cmd.audioUrl, cmd.title);
        break;

      case 'SHOW_IMAGE':
        displayImage(cmd.url);
        break;

      case 'PAUSE':
        video.pause();
        if (!audioTrack.paused) audioTrack.pause();
        showOSD('إيقاف مؤقت');
        break;

      case 'RESUME':
        video.play();
        if (audioTrack.src) audioTrack.play();
        showOSD('تشغيل');
        break;

      case 'SEEK':
        if (typeof cmd.position === 'number') {
          video.currentTime = cmd.position;
          if (audioTrack.src) audioTrack.currentTime = cmd.position;
          showControls();
        }
        break;

      case 'SEEK_RELATIVE':
        if (typeof cmd.seconds === 'number') {
          video.currentTime += cmd.seconds;
          if (audioTrack.src) audioTrack.currentTime += cmd.seconds;
          showOSD((cmd.seconds > 0 ? '+ ' : '') + cmd.seconds + ' ثانية');
          showControls();
        }
        break;

      case 'SET_VOLUME':
        if (typeof cmd.level === 'number') {
          const vol = Math.max(0, Math.min(1, cmd.level));
          video.volume = vol;
          audioTrack.volume = vol;
          showOSD('مستوى الصوت: ' + Math.round(vol * 100) + '%');
        }
        break;

      case 'VOLUME_UP':
        video.volume = Math.min(1, video.volume + 0.05);
        audioTrack.volume = video.volume;
        showOSD('الصوت: ' + Math.round(video.volume * 100) + '%');
        break;

      case 'VOLUME_DOWN':
        video.volume = Math.max(0, video.volume - 0.05);
        audioTrack.volume = video.volume;
        showOSD('الصوت: ' + Math.round(video.volume * 100) + '%');
        break;

      case 'MUTE_TOGGLE':
        video.muted = !video.muted;
        audioTrack.muted = video.muted;
        showOSD(video.muted ? 'كتم الصوت' : 'تشغيل الصوت');
        break;

      case 'ASPECT_RATIO':
        video.style.objectFit = cmd.fit || 'contain';
        showOSD('الأبعاد: ' + (cmd.fit || 'احتواء'));
        break;

      case 'STOP':
        stopAndReturnToWelcome();
        showOSD('تم الإيقاف');
        break;
    }
  }

  function playVideo(url, audioUrl, title) {
    stopMediaEngines();
    welcomeScreen.classList.add('hidden');
    img.style.display = 'none';
    video.style.display = 'block';

    if (title) showOSD(title);

    if (audioUrl) {
      audioTrack.src = audioUrl;
      audioTrack.load();
      video.onplay = function () { audioTrack.play(); };
      video.onpause = function () { audioTrack.pause(); };
      video.onseeking = function () { audioTrack.currentTime = video.currentTime; };
    }

    if (url.indexOf('.m3u8') !== -1 && window.Hls && window.Hls.isSupported()) {
      hls = new window.Hls({ enableWorker: true });
      hls.loadSource(url);
      hls.attachMedia(video);
      hls.on(window.Hls.Events.MANIFEST_PARSED, function () {
        video.play();
      });
    } else {
      video.src = url;
      video.play();
    }
  }

  function displayImage(url) {
    stopMediaEngines();
    welcomeScreen.classList.add('hidden');
    video.style.display = 'none';
    img.src = url;
    img.style.display = 'block';
    showOSD('عرض صورة');
  }

  function stopMediaEngines() {
    if (hls) {
      hls.destroy();
      hls = null;
    }
    video.pause();
    video.removeAttribute('src');
    video.load();
    audioTrack.pause();
    audioTrack.removeAttribute('src');
    img.removeAttribute('src');
  }

  function stopAndReturnToWelcome() {
    stopMediaEngines();
    video.style.display = 'none';
    img.style.display = 'none';
    playerControls.classList.add('controls-hidden');
    welcomeScreen.classList.remove('hidden');
  }

  // تحديث شريط الوقت والتقدم
  video.addEventListener('timeupdate', function () {
    if (video.duration) {
      const percentage = (video.currentTime / video.duration) * 100;
      progressBar.style.width = percentage + '%';
      currentTimeEl.textContent = formatTime(video.currentTime);
      totalDurationEl.textContent = formatTime(video.duration);
    }
  });

  // أوامر أزرار ريموت LG وسامسونج الرسمية
  window.addEventListener('keydown', function (e) {
    switch (e.keyCode) {
      case 415: // MediaPlay
      case 19:  // MediaPause
        if (video.paused) video.play(); else video.pause();
        break;
      case 413: // MediaStop
      case 461: // LG Back
      case 10009: // Tizen Return
        stopAndReturnToWelcome();
        break;
      case 417: // FastForward
      case 39:  // سهم يمين
        video.currentTime += 15;
        showControls();
        break;
      case 412: // Rewind
      case 37:  // سهم شمال
        video.currentTime -= 15;
        showControls();
        break;
    }
  });

  window.addEventListener('DOMContentLoaded', function () {
    displayLocalIp();
    initWebSocket();
  });
})();
