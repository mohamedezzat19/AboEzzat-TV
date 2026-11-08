const { app, BrowserWindow } = require("electron");
const http = require("http");
const dgram = require("dgram");
const os = require("os");
const path = require("path");

const PORT = Number(process.env.ABOEZZAT_TV_PORT || 8081);
const DISCOVERY_PORT = 37020;
const DISCOVERY_GROUP = "239.255.42.99";

let windowRef;
let server;
let heartbeat;

function getLanIPv4() {
  const interfaces = os.networkInterfaces();
  for (const [name, addresses] of Object.entries(interfaces)) {
    if (/^(lo|tun|tap|vpn|wg|ppp)/i.test(name)) continue;
    for (const item of addresses || []) {
      if (item.family === "IPv4" && !item.internal && !item.address.startsWith("169.254.")) {
        return item.address;
      }
    }
  }
  return "0.0.0.0";
}

function sendToWindow(payload) {
  if (windowRef && !windowRef.isDestroyed()) {
    windowRef.webContents.send("receiver-event", payload);
  }
}

function jsonResponse(res, status, value) {
  const body = Buffer.from(JSON.stringify(value));
  res.writeHead(status, {
    "Content-Type": "application/json; charset=utf-8",
    "Access-Control-Allow-Origin": "*",
    "Access-Control-Allow-Methods": "GET, POST, OPTIONS",
    "Access-Control-Allow-Headers": "Content-Type",
    "Content-Length": body.length
  });
  res.end(body);
}

function startHttpReceiver() {
  server = http.createServer((req, res) => {
    if (req.method === "OPTIONS") {
      res.writeHead(204, {
        "Access-Control-Allow-Origin": "*",
        "Access-Control-Allow-Methods": "GET, POST, OPTIONS",
        "Access-Control-Allow-Headers": "Content-Type"
      });
      return res.end();
    }

    const parsed = new URL(req.url, "http://127.0.0.1");
    const command = parsed.searchParams.get("command");
    const url = parsed.searchParams.get("url") || parsed.searchParams.get("video");
    const audio = parsed.searchParams.get("audio");

    if (req.method === "POST" && parsed.pathname === "/play") {
      let raw = "";
      req.on("data", chunk => {
        raw += chunk;
        if (raw.length > 65536) {
          req.destroy();
        }
      });
      req.on("end", () => {
        try {
          const payload = JSON.parse(raw || "{}");
          if (!isHttpUrl(payload.url)) {
            return jsonResponse(res, 400, { ok: false, error: "Invalid media URL" });
          }
          sendToWindow({
            type: "play",
            url: payload.url,
            headers: payload.headers || {},
            receivedAt: Date.now()
          });
          jsonResponse(res, 200, { ok: true });
        } catch {
          jsonResponse(res, 400, { ok: false, error: "Invalid JSON body" });
        }
      });
      return;
    }

    if (command === "ping") return jsonResponse(res, 200, { ok: true, receiver: "AboEzzat TV" });
    if (command === "status") return jsonResponse(res, 200, { status: "ready", receiver: "AboEzzat TV" });

    if (command || url || audio) {
      sendToWindow({ type: "command", command, url, audio, receivedAt: Date.now() });
      return jsonResponse(res, 200, { ok: true });
    }

    res.writeHead(404, { "Content-Type": "text/plain; charset=utf-8" });
    res.end("AboEzzat TV receiver");
  });

  server.on("error", error => {
    sendToWindow({ type: "server-error", message: error.message });
  });

  server.listen(PORT, "0.0.0.0", () => {
    sendToWindow({ type: "server-status", ready: true, port: PORT, ip: getLanIPv4() });
  });
}

function isHttpUrl(value) {
  try {
    const parsed = new URL(String(value || ""));
    return (parsed.protocol === "http:" || parsed.protocol === "https:") && Boolean(parsed.hostname);
  } catch {
    return false;
  }
}

function startDiscovery() {
  const socket = dgram.createSocket({ type: "udp4", reuseAddr: true });
  socket.on("error", error => sendToWindow({ type: "discovery-error", message: error.message }));
  socket.bind(() => {
    try {
      socket.setMulticastTTL(1);
      socket.setMulticastLoopback(false);
    } catch {}
    heartbeat = setInterval(() => {
      const message = Buffer.from(`ABOEZZAT_RECEIVER|${getLanIPv4()}|${PORT}`);
      socket.send(message, DISCOVERY_PORT, DISCOVERY_GROUP, error => {
        if (error) sendToWindow({ type: "discovery-error", message: error.message });
      });
    }, 1200);
  });
  app.once("before-quit", () => {
    if (heartbeat) clearInterval(heartbeat);
    try { socket.close(); } catch {}
  });
}

function createWindow() {
  windowRef = new BrowserWindow({
    width: 1100,
    height: 700,
    minWidth: 760,
    minHeight: 480,
    backgroundColor: "#10131b",
    autoHideMenuBar: true,
    webPreferences: {
      preload: path.join(__dirname, "preload.js"),
      contextIsolation: true,
      nodeIntegration: false
    }
  });
  windowRef.loadFile(path.join(__dirname, "index.html"));
}

app.whenReady().then(() => {
  createWindow();
  startHttpReceiver();
  startDiscovery();
});

app.on("window-all-closed", () => {
  if (process.platform !== "darwin") app.quit();
});

app.on("before-quit", () => {
  if (server) server.close();
});
