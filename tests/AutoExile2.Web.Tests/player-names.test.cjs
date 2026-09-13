const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const path = require('node:path');
const source = fs.readFileSync(path.join(__dirname, '../../Plugins/AutoExile2/WebServer/wwwroot/settings.js'), 'utf8');
const start = source.indexOf("let lastPlayerNamesHash = '';");
const end = source.indexOf('async function fetchAndPopulatePlayerNames()', start);
assert.ok(start >= 0 && end > start);
const elements = {
  followerNameDatalist: { innerHTML: '' },
  followerPlayerSelect: { innerHTML: '', style: {}, value: '' },
  FollowerCharacterName: { value: '' }
};
const context = vm.createContext({ document: { getElementById: id => elements[id] } });
vm.runInContext(source.slice(start, end), context);
context.populatePlayerOptions([{ Name: 'Follower', ClassName: 'Ranger' }], ['Leader', 'Follower']);
assert.match(elements.followerPlayerSelect.innerHTML, /value="Leader"/);
assert.match(elements.followerPlayerSelect.innerHTML, /value="Follower"/);
context.populatePlayerOptions([{ Name: 'OtherPlayer' }], []);
assert.match(elements.followerPlayerSelect.innerHTML, /value="OtherPlayer"/);
context.populatePlayerOptions([{ Name: 'OtherPlayer' }], ['OtherPlayer', 'OtherPlayer']);
assert.equal((elements.followerPlayerSelect.innerHTML.match(/value="OtherPlayer"/g) || []).length, 1);
context.populatePlayerOptions([], []);
assert.equal(elements.followerPlayerSelect.style.display, 'none');
console.log('PASS: player picker retains all names with partial details, empty name arrays, duplicates and empty results.');
vm.runInContext(source.slice(end, source.indexOf('async function saveSettings()', end)), context);
let selected = '';
context.showToast = () => {};
context.selectFollowerName = name => { selected = name; };
elements.followerPlayerSelect.focus = () => {};
context.fetch = async () => ({ ok: true, json: async () => ({ leader: 'Leader', playerNames: ['Leader'], players: [] }) });
(async () => {
  await context.fetchAndPopulatePlayerNames();
  assert.equal(selected, '', 'Searching with only the local player must not auto-lock onto self.');
  context.fetch = async () => ({ ok: true, json: async () => ({ leader: 'Leader', playerNames: ['Follower'], players: [] }) });
  await context.fetchAndPopulatePlayerNames();
  assert.equal(selected, 'Follower');
  console.log('PASS: manual scan avoids auto-locking self and still selects one other player.');
})().catch(error => { console.error(error); process.exitCode = 1; });
