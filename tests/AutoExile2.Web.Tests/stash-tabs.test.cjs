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
  CurrencyTab: makeElement(),
  MinTier: makeElement(),
  MaxTier: makeElement(),
  MinWaystoneItemRarity: makeElement(),
  MinWaystonePackSize: makeElement(),
  MinWaystoneMonsterRarity: makeElement(),
  MinWaystoneMonsterEffectiveness: makeElement(),
  MinWaystoneDropChance: makeElement(),
  MaxWaystoneMods: makeElement(),
  waystoneModFilterRows: makeElement(),
  DumpTab: makeElement(),
  stashOpenFlags: makeElement(),
  stashScanStatus: makeElement(),
  detectedStashTabSelect: makeElement(),
  stashTierCounts: makeElement(),
};
let saves = 0;
const context = vm.createContext({
  console,
  currentSettings: {
    WaystoneTab: '',
    CurrencyTab: 'Orb',
    MinTier: 3,
    MaxTier: 16,
    DumpTab: '',
    MinWaystonePackSize: 20,
    MaxWaystoneMods: 6,
    BlockedWaystoneMods: ['MapBurningGround'],
  },
  systemMetadata: {
    waystoneMods: [
      { Key: 'MapBurningGround', Label: 'Burning Ground' },
      { Key: 'MapChilledGround', Label: 'Chilled Ground' },
    ],
  },
  lastDetectedStashTabs: [],
  lastDetectedStashTabsSignature: '',
  document: {
    getElementById: id => elements[id] || null,
    createElement: () => makeElement(),
  },
  saveSettings: () => { saves++; },
  showToast: () => {},
  esc: value => String(value || ''),
});
vm.runInContext(source.slice(start, end), context);
context.syncStashSettingsInputs();
assert.equal(elements.MinTier.value, 3);
assert.equal(elements.MaxTier.value, 16);
assert.equal(elements.CurrencyTab.value, 'Orb');
assert.equal(elements.MinWaystoneItemRarity.value, '');
assert.equal(elements.MinWaystonePackSize.value, 20);
assert.equal(elements.MaxWaystoneMods.value, 6);
assert.match(elements.waystoneModFilterRows.innerHTML, /Burning Ground/);
assert.match(elements.waystoneModFilterRows.innerHTML, /checked/);
assert.match(source, /'WaystoneTab', 'CurrencyTab', 'MinTier', 'MaxTier', 'DumpTab'/);

context.renderStashScanner({
  IsInventoryOpen: true,
  IsStashOpen: true,
  IsVendorOpen: false,
  StashState: 'Ready',
  CurrentStashTab: 'Map',
  CurrentStashTier: 'XV',
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
assert.match(elements.stashScanStatus.textContent, /Ready \| Map \| Tier XV \| Page 1 \| Map stash/);
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
