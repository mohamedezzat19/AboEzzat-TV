const { contextBridge, ipcRenderer } = require("electron");

contextBridge.exposeInMainWorld("aboezzatReceiver", {
  onEvent(callback) {
    if (typeof callback !== "function") return;
    ipcRenderer.on("receiver-event", (_event, payload) => callback(payload));
  }
});
