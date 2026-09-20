const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const source = fs.readFileSync(
  path.join(__dirname, '../../Plugins/AutoExile2/WebServer/wwwroot/settings.js'),
  'utf8');
const start = source.indexOf('function syncStashSettingsInputs()');
const end = source.indexOf('function updateFollowerLockUI()', start);
assert.ok(start >= 0 && end > start);

function makeElement() {
  return {
    value: '',
    textContent: '',
    className: '',
    style: {},
    children: [],
    replaceChildren() { this.children = []; },
    appendChild(child) {
      this.children.push(child);
      if (!this.value && child.value) this.value = child.value;
    },
  };
}

const elements = {
  WaystoneTab: makeElement(),
  MaxTier: makeElement(),
  DumpTab: makeElement(),
  stashOpenFlags: makeElement(),
  stashScanStatus: makeElement(),
  detectedStashTabSelect: makeElement(),
  stashTierCounts: makeElement(),
};
let saves = 0;
const context = vm.createContext({
  console,
  currentSettings: { WaystoneTab: '', MaxTier: 16, DumpTab: '' },
  lastDetectedStashTabs: [],
  lastDetectedStashTabsSignature: '',
  document: {
    getElementById: id => elements[id] || null,
    createElement: () => makeElement(),
  },
  saveSettings: () => { saves++; },
  showToast: () => {},
});
vm.runInContext(source.slice(start, end), context);

context.renderStashScanner({
  IsInventoryOpen: true,
  IsStashOpen: true,
  IsVendorOpen: false,
  StashState: 'Ready',
  CurrentStashTab: 'Map',
  CurrentStashPage: '1',
  IsSpecializedStashTab: true,
  DetectedStashTabs: ['Orb', 'Map', '5', '6'],
  StashTiers: [{ Name: 'XIV', Count: 6 }, { Name: 'XV', Count: 170 }],
});

assert.equal(elements.stashOpenFlags.textContent, 'Inventory=1 | Stash=1 | Vendor=0');
assert.deepEqual(
  elements.detectedStashTabSelect.children.map(option => option.value),
  ['Orb', 'Map', '5', '6']);
assert.equal(elements.detectedStashTabSelect.value, 'Map');
assert.match(elements.stashScanStatus.textContent, /Ready \| Map \| Page 1 \| Map stash/);
assert.deepEqual(
  elements.stashTierCounts.children.map(badge => badge.textContent),
  ['XIV: 6', 'XV: 170']);

elements.detectedStashTabSelect.value = '6';
context.renderStashScanner({
  IsInventoryOpen: false,
  IsStashOpen: false,
  IsVendorOpen: false,
  InteractionPhase: 'Idle',
  DetectedStashTabs: [],
  StashTiers: [],
});
assert.equal(elements.detectedStashTabSelect.value, '6');
assert.match(elements.stashScanStatus.textContent, /สแกนล่าสุด 4 แท็บ/);

context.renderStashScanner({
  IsInventoryOpen: false,
  IsStashOpen: false,
  IsVendorOpen: false,
  InteractionPhase: 'Navigating',
  InteractionStatus: 'Moving to personal stash (42g)',
  DetectedStashTabs: [],
  StashTiers: [],
});
assert.equal(
  elements.stashScanStatus.textContent,
  'Interaction Navigating: Moving to personal stash (42g)');

context.useDetectedStashTab('WaystoneTab');
assert.equal(elements.WaystoneTab.value, '6');
assert.equal(saves, 1);

console.log('PASS: stash scanner preserves detected tabs, exposes Tier counts, and assigns Waystone Tab safely.');
