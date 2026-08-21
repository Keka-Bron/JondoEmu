const { contextBridge, ipcRenderer } = require('electron');

contextBridge.exposeInMainWorld('jondo', {
  preferences: () => ipcRenderer.invoke('preferences:get'),
  savePreferences: (preferences) => ipcRenderer.invoke('preferences:save', preferences),
  window: {
    minimize: () => ipcRenderer.invoke('window:minimize'),
    toggleMaximize: () => ipcRenderer.invoke('window:toggle-maximize'),
    close: () => ipcRenderer.invoke('window:close'),
    onMaximized: callback => ipcRenderer.on('window:maximized', (_, value) => callback(Boolean(value))),
  },
  account: {
    signIn: (credentials) => ipcRenderer.invoke('account:sign-in', credentials),
    register: (details) => ipcRenderer.invoke('account:register', details),
    remember: (session) => ipcRenderer.invoke('account:remember', session),
  },
  server: { status: () => ipcRenderer.invoke('server:status') },
  client: {
    choose: () => ipcRenderer.invoke('client:choose'),
    supportStatus: (file) => ipcRenderer.invoke('client:support-status', file),
    installSupport: (file) => ipcRenderer.invoke('client:install-support', file),
    launch: (payload) => ipcRenderer.invoke('client:launch', payload),
  },
  catalog: {
    chooseSnapshot: () => ipcRenderer.invoke('catalog:choose-snapshot'),
    open: (folder) => ipcRenderer.invoke('catalog:open', folder),
    rows: (request) => ipcRenderer.invoke('catalog:rows', request),
  }
});
