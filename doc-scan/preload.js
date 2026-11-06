const { contextBridge, ipcRenderer } = require('electron');

const call = (channel) => (...args) => ipcRenderer.invoke(channel, ...args);

contextBridge.exposeInMainWorld('api', {
  defaults: call('app:defaults'),
  discover: call('scanners:discover'),
  addManual: call('scanners:addManual'),
  chooseFolder: call('dialog:folder'),
  openTable: call('dialog:table'),
  parseTable: call('table:parse'),
  buildJobs: call('jobs:build'),
  scan: call('scan:run'),
  openFolder: call('shell:openFolder'),
});
