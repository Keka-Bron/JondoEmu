const { contextBridge, ipcRenderer } = require('electron');
contextBridge.exposeInMainWorld('admin', {
  window: { minimize: () => ipcRenderer.invoke('window:minimize'), toggle: () => ipcRenderer.invoke('window:toggle'), close: () => ipcRenderer.invoke('window:close') },
  tools: { list: () => ipcRenderer.invoke('tools:list'), run: input => ipcRenderer.invoke('tools:run', input) },
  telemetry: { summary: () => ipcRenderer.invoke('telemetry:summary'), list: () => ipcRenderer.invoke('telemetry:list'), detail: id => ipcRenderer.invoke('telemetry:detail', id) },
  database: {
    list: () => ipcRenderer.invoke('database:list'),
    tables: database => ipcRenderer.invoke('database:tables', database),
    rows: input => ipcRenderer.invoke('database:rows', input)
  }
});
