let currentSettings = {
  Skills: [],
  P1Skills: [],
  P2Skills: []
};

let detectedSkills = [];
let p1DetectedSkills = [];
let p2DetectedSkills = [];
let lastDetectedSig = '__init__';
let lastP2DetectedSig = '__init__';
let hasRenderedChips = false;
let detectedBuffs = [];
let lastBuffsSig = '__init__';
let detectedDebuffs = [];
let lastDebuffsSig = '__init__';
let liveActiveTotems = 0;
let liveActiveMinions = 0;
let liveActiveTotemNames = [];
let liveDeployedObjects = [];

// Gamepad button options for Co-op
const GAMEPAD_BUTTON_OPTIONS = [
  { value: "RightShoulder", num: 6, label: "RB (Right Bumper)" },
  { value: "RightTrigger", num: 8, label: "RT (Right Trigger)" },
  { value: "X", num: 3, label: "Button X" },
  { value: "Y", num: 4, label: "Button Y" },
  { value: "A", num: 1, label: "Button A" },
  { value: "B", num: 2, label: "Button B" },
  { value: "LeftShoulder", num: 5, label: "LB (Left Bumper)" },
  { value: "LeftTrigger", num: 7, label: "LT (Left Trigger)" }
];

function renderGamepadButtonOptions(selected) {
  return GAMEPAD_BUTTON_OPTIONS.map(b => {
    const isSel = (b.value === selected || b.num === selected || String(b.num) === String(selected) || (selected && b.value.toLowerCase() === String(selected).toLowerCase()));
    return `<option value="${b.value}" ${isSel ? 'selected' : ''}>${b.label}</option>`;
  }).join('');
}

function getGamepadButtonLabel(btn) {
  const match = GAMEPAD_BUTTON_OPTIONS.find(b => 
    b.value === btn || b.num === btn || String(b.num) === String(btn) || (btn && b.value.toLowerCase() === String(btn).toLowerCase())
  );
  if (match) {
    return match.label.split(' ')[0];
  }
  return btn || 'RB';
}

// Skill category definitions & presets (Ported from AutoExile 1)
const CATEGORY_PRESETS = {
  Attack: {
    role: 'EnemyTargeted', priority: 1, interval: 200, hold: 150, filter: 'Any',
    lowHp: false, lowHpThresh: 60, minEnemies: 0, maxRange: 0,
    label: '⚔️ Attack', color: '#38bdf8', bg: 'rgba(56,189,248,0.15)'
  },
  Buff: {
    role: 'SelfBuffGuard', priority: 5, interval: 8000, hold: 100, filter: 'Any',
    lowHp: false, lowHpThresh: 60, vitalCondition: 2, minEnemies: 0, maxRange: 0,
    onlyWhenBuffMissing: true,
    label: '✨ Buff', color: '#10b981', bg: 'rgba(16,185,129,0.15)'
  },
  Curse: {
    role: 'PackTargeted', priority: 4, interval: 5000, hold: 150, filter: 'MagicOrAbove',
    lowHp: false, lowHpThresh: 60, minEnemies: 1, maxRange: 70,
    onlyWhenBuffMissing: true,
    label: '🔮 Curse / Debuff', color: '#a855f7', bg: 'rgba(168,85,247,0.15)'
  },
  Totem: {
    role: 'TotemOrMinion', priority: 6, interval: 4000, hold: 150, filter: 'Any',
    lowHp: false, lowHpThresh: 60, minEnemies: 1, maxRange: 65,
    maxTotems: 1,
    label: '🗿 Totem', color: '#f59e0b', bg: 'rgba(245,158,11,0.15)'
  },
  Guard: {
    role: 'SelfBuffGuard', priority: 9, interval: 4000, hold: 100, filter: 'Any',
    lowHp: true, lowHpThresh: 60, vitalCondition: 2, minEnemies: 0, maxRange: 0,
    onlyWhenBuffMissing: true,
    label: '🛡️ Guard', color: '#ef4444', bg: 'rgba(239,68,68,0.15)'
  },
  Warcry: {
    role: 'SelfBuffGuard', priority: 4, interval: 4000, hold: 100, filter: 'Any',
    lowHp: false, lowHpThresh: 60, minEnemies: 1, maxRange: 0,
    label: '🗣️ Warcry', color: '#ec4899', bg: 'rgba(236,72,153,0.15)'
  },
  Minion: {
    role: 'TotemOrMinion', priority: 5, interval: 6000, hold: 150, filter: 'Any',
    lowHp: false, lowHpThresh: 60, minEnemies: 1, maxRange: 60,
    maxMinions: 3,
    label: '🧟 Minion', color: '#06b6d4', bg: 'rgba(6,182,212,0.15)'
  },
  Movement: {
    role: 'Disabled', priority: 0, interval: 500, hold: 80, filter: 'Any',
    lowHp: false, lowHpThresh: 60, minEnemies: 0, maxRange: 0,
    label: '⚡ Movement', color: '#6366f1', bg: 'rgba(99,102,241,0.15)'
  },
  Culler: {
    role: 'Culler', priority: 2, interval: 200, hold: 120, filter: 'Any',
    lowHp: false, lowHpThresh: 60, minEnemies: 0, maxRange: 0,
    cullerAimDist: 75, cullerStartDist: 35, cullerRequireMonsters: false,
    label: '🎯 Culler', color: '#f43f5e', bg: 'rgba(244,63,94,0.15)'
  },
  Custom: {
    role: 'EnemyTargeted', priority: 5, interval: 500, hold: 150, filter: 'Any',
    lowHp: false, lowHpThresh: 60, minEnemies: 0, maxRange: 0,
    label: '⚙️ Custom', color: '#94a3b8', bg: 'rgba(148,163,184,0.15)'
  }
};

// Dynamic System Metadata (Populated from C# /api/metadata)
let systemMetadata = {
  modes: [
    { id: "MapFarm", label: "Map Farm", desc: "Auto explore, clear & loot maps", icon: "🗺️" },
    { id: "Follower", label: "Couch Co-op Follower", desc: "Dual-Gamepad Co-op", icon: "🎮" },
    { id: "Boss", label: "Boss Encounter", desc: "Arena boss targeting & spacing", icon: "⚔️" },
    { id: "Idle", label: "Idle", desc: "Standby mode", icon: "💤" }
  ],
  roles: [
    { id: "EnemyTargeted", label: "Enemy Targeted (Direct / Single)", desc: "Aim cursor directly at target monster" },
    { id: "PackTargeted", label: "Pack Targeted (AoE / Cluster)", desc: "Aim cursor at pack center" },
    { id: "TotemOrMinion", label: "Totem / Minion Deploy", desc: "Deploy totem or minion toward enemies" },
    { id: "SelfBuffGuard", label: "Self Buff / Guard", desc: "Cast on self without moving cursor" },
    { id: "CorpseTargeted", label: "Corpse Targeted", desc: "Aim at nearest corpse" },
    { id: "Culler", label: "Culler (Focused Fire ahead of Host)", desc: "Aims in front of Leader when close to host" },
    { id: "Disabled", label: "Disabled", desc: "Do not cast this skill automatically" }
  ],
  targetFilters: [
    { id: "Any", label: "Any Hostile Monster" },
    { id: "NormalOnly", label: "Normal (White) Only" },
    { id: "MagicOrAbove", label: "Magic (Blue) or Above" },
    { id: "RareOrAbove", label: "Rare (Yellow) & Bosses Only" },
    { id: "UniqueOnly", label: "Unique (Bosses) Only" }
  ],
  inputTypes: [
    { id: "MouseRight", label: "Mouse Right Click (RMB)" },
    { id: "MouseLeft", label: "Mouse Left Click (LMB)" },
    { id: "KeyboardKey", label: "Keyboard Key" },
    { id: "MouseMiddle", label: "Mouse Middle Click (MMB)" }
  ]
};

async function loadMetadata() {
  try {
    const res = await fetch('/api/metadata');
    if (res.ok) {
      const data = await res.json();
      if (data && data.success) {
        systemMetadata = data;
        if (data.categoryPresets) {
          Object.assign(CATEGORY_PRESETS, data.categoryPresets);
        }
      }
    }
  } catch (e) {}
}


let previewDebounceTimer = null;
let lastPreviewPayload = null;

function previewRange(type, radius, unit, label, color, targetEntity = 'Leader') {
  lastPreviewPayload = {
    type: type,
    radius: parseFloat(radius) || 0,
    unit: unit || 'world',
    label: label || (type + ': ' + radius),
    color: color || '#38bdf8',
    targetEntity: targetEntity
  };

  if (!previewDebounceTimer) {
    previewDebounceTimer = setTimeout(async () => {
      previewDebounceTimer = null;
      if (!lastPreviewPayload) return;
      try {
        await fetch('/api/preview_range', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify(lastPreviewPayload)
        });
      } catch (e) {}
    }, 35);
  }
}

function renderModeSelectorOptions() {

  const sel = document.getElementById('Mode');
  if (!sel) return;
  const modes = (systemMetadata && systemMetadata.modes) ? systemMetadata.modes : [
    { id: "MapFarm", label: "Map Farm", desc: "Auto explore, clear & loot maps", icon: "🗺️" },
    { id: "Follower", label: "Couch Co-op Follower", desc: "Dual-Gamepad Co-op", icon: "🎮" }
  ];
  sel.innerHTML = modes.map(m => `<option value="${m.id}">${m.icon || '⚙️'} ${m.label} (${m.desc})</option>`).join('');
}

function renderTargetFilterOptions(selectedFilter) {
  const filters = [
    { id: 'Any', num: 0, label: 'Any Hostile Monster' },
    { id: 'NormalOnly', num: 1, label: 'Normal (White) Only' },
    { id: 'MagicOrAbove', num: 2, label: 'Magic (Blue) or Above' },
    { id: 'RareOrAbove', num: 3, label: 'Rare (Yellow) & Bosses Only' },
    { id: 'UniqueOnly', num: 4, label: 'Unique (Bosses) Only' }
  ];
  return filters.map(f => {
    const isSel = (f.id === selectedFilter || f.num === selectedFilter || String(f.num) === String(selectedFilter) || (selectedFilter && f.id.toLowerCase() === String(selectedFilter).toLowerCase()));
    return `<option value="${f.id}" ${isSel ? 'selected' : ''}>${f.label}</option>`;
  }).join('');
}

function renderInputTypeOptions(selectedInput) {
  const inputs = [
    { id: 'MouseRight', num: 0, label: 'Mouse Right Click (RMB)' },
    { id: 'MouseLeft', num: 1, label: 'Mouse Left Click (LMB)' },
    { id: 'KeyboardKey', num: 2, label: 'Keyboard Key' },
    { id: 'MouseMiddle', num: 3, label: 'Mouse Middle Click (MMB)' }
  ];
  return inputs.map(it => {
    const isSel = (it.id === selectedInput || it.num === selectedInput || String(it.num) === String(selectedInput) || (selectedInput && it.id.toLowerCase() === String(selectedInput).toLowerCase()));
    return `<option value="${it.id}" ${isSel ? 'selected' : ''}>${it.label}</option>`;
  }).join('');
}

const TOTEM_KW = ["totem", "ballista", "ancestor", "ancestral", "earthbreaker", "siegeballista", "spelltotem", "holyflame"];
const CURSE_KW = ["curse", "hex", "mark", "despair", "flammability", "conductivity", "vulnerability", "punishment", "enfeeble", "temporalchains", "elementalweakness", "poachersmark", "warlordsmark", "assassinsmark", "snipersmark", "projectileweakness", "frostbite", "contagion", "bane", "wither", "frostbomb", "coldexposure", "exposure"];
const GUARD_KW = ["steelskin", "moltenshell", "immortalcall", "bonearmour", "arcanecloak", "frostshield", "defiancebanner"];
const WARCRY_KW = ["warcry", "shout", "enduringcry", "intimidatingcry", "rallyingcry", "seismiccry", "battlemagescry", "infernalcry", "generalcry", "ancestralcry"];
const MINION_KW = ["summon", "raise", "animate", "golem", "skeleton", "zombie", "spectre", "ragingspirit", "reaper", "absolution", "heraldofpurity", "minion", "dominatingblow"];
const MOVE_KW = ["frostblink", "flamedash", "dash", "leapslam", "shieldcharge", "whirlingblades", "lightningwarp", "blinkarrow", "flickerstrike", "smokemine", "bodyswap", "chargeddash", "blink", "roll"];
const BUFF_KW = ["bloodrage", "witheringstep", "berserk", "phaserun", "righteousfire", "tempestshield", "grace", "determination", "discipline", "hatred", "anger", "wrath", "zealotry", "malevolence", "pride", "haste", "purity", "vitality", "clarity", "precision", "aura", "herald", "manatempest", "tempest"];

function autoClassifySkill(name) {
  if (!name) return 'Attack';
  const clean = name.trim().toLowerCase().replace(/[\s_\-]+/g, '');
  if (clean === 'move' || clean === 'walk') return 'Movement';
  if (MOVE_KW.some(k => clean.includes(k))) return 'Movement';
  if (TOTEM_KW.some(k => clean.includes(k))) return 'Totem';
  if (GUARD_KW.some(k => clean.includes(k))) return 'Guard';
  if (WARCRY_KW.some(k => clean.includes(k))) return 'Warcry';
  if (CURSE_KW.some(k => clean.includes(k))) return 'Curse';
  if (MINION_KW.some(k => clean.includes(k))) return 'Minion';
  if (BUFF_KW.some(k => clean.includes(k))) return 'Buff';
  return 'Attack';
}

function onCombatStyleChange(style) {
  currentSettings.CombatStyle = style;
  const fSlider = document.getElementById('FightRange');
  if (fSlider) {
    if (style === 'Melee' && parseFloat(fSlider.value) > 25) {
      fSlider.value = 18;
      document.getElementById('fightRangeVal').innerText = 18;
      currentSettings.FightRange = 18;
    } else if (style === 'Ranged' && parseFloat(fSlider.value) < 35) {
      fSlider.value = 45;
      document.getElementById('fightRangeVal').innerText = 45;
      currentSettings.FightRange = 45;
    }
  }
}

function showTab(id) {
  document.querySelectorAll('.tab-pane').forEach(el => el.style.display = 'none');
  document.querySelectorAll('.tab').forEach(el => el.classList.remove('active'));
  document.getElementById(id).style.display = 'block';
  event.currentTarget.classList.add('active');
  if (id === 'tab-system') loadDumps();
}

function showToast(msg) {
  const t = document.getElementById('toast');
  t.innerText = msg;
  t.style.display = 'block';
  setTimeout(() => t.style.display = 'none', 2500);
}

// --- Key Dropdown List Engine ---
const DEFAULT_KEY_OPTIONS = [
  {
    group: "Movement & Common",
    options: [
      { value: "KEY_W", label: "W" },
      { value: "KEY_A", label: "A" },
      { value: "KEY_S", label: "S" },
      { value: "KEY_D", label: "D" },
      { value: "SPACE", label: "Space" }
    ]
  },
  {
    group: "Number Keys (0 - 9)",
    options: [
      { value: "KEY_1", label: "1" },
      { value: "KEY_2", label: "2" },
      { value: "KEY_3", label: "3" },
      { value: "KEY_4", label: "4" },
      { value: "KEY_5", label: "5" },
      { value: "KEY_6", label: "6" },
      { value: "KEY_7", label: "7" },
      { value: "KEY_8", label: "8" },
      { value: "KEY_9", label: "9" },
      { value: "KEY_0", label: "0" }
    ]
  },
  {
    group: "Letter Keys (A - Z)",
    options: [
      { value: "KEY_Q", label: "Q" },
      { value: "KEY_W", label: "W" },
      { value: "KEY_E", label: "E" },
      { value: "KEY_R", label: "R" },
      { value: "KEY_T", label: "T" },
      { value: "KEY_Y", label: "Y" },
      { value: "KEY_U", label: "U" },
      { value: "KEY_I", label: "I" },
      { value: "KEY_O", label: "O" },
      { value: "KEY_P", label: "P" },
      { value: "KEY_A", label: "A" },
      { value: "KEY_S", label: "S" },
      { value: "KEY_D", label: "D" },
      { value: "KEY_F", label: "F" },
      { value: "KEY_G", label: "G" },
      { value: "KEY_H", label: "H" },
      { value: "KEY_J", label: "J" },
      { value: "KEY_K", label: "K" },
      { value: "KEY_L", label: "L" },
      { value: "KEY_Z", label: "Z" },
      { value: "KEY_X", label: "X" },
      { value: "KEY_C", label: "C" },
      { value: "KEY_V", label: "V" },
      { value: "KEY_B", label: "B" },
      { value: "KEY_N", label: "N" },
      { value: "KEY_M", label: "M" }
    ]
  },
  {
    group: "Function Keys (F1 - F12)",
    options: [
      { value: "F1", label: "F1" },
      { value: "F2", label: "F2" },
      { value: "F3", label: "F3" },
      { value: "F4", label: "F4" },
      { value: "F5", label: "F5" },
      { value: "F6", label: "F6" },
      { value: "F7", label: "F7" },
      { value: "F8", label: "F8" },
      { value: "F9", label: "F9" },
      { value: "F10", label: "F10" },
      { value: "F11", label: "F11" },
      { value: "F12", label: "F12" }
    ]
  },
  {
    group: "Special & System Keys",
    options: [
      { value: "INSERT", label: "Insert" },
      { value: "SPACE", label: "Space" },
      { value: "TAB", label: "Tab" },
      { value: "ESCAPE", label: "Esc" },
      { value: "DELETE", label: "Delete" },
      { value: "HOME", label: "Home" },
      { value: "END", label: "End" },
      { value: "PRIOR", label: "Page Up" },
      { value: "NEXT", label: "Page Down" },
      { value: "OEM_3", label: "~" },
      { value: "LSHIFT", label: "Shift" },
      { value: "LCONTROL", label: "Ctrl" },
      { value: "LMENU", label: "Alt" }
    ]
  }
];

function normalizeVk(val) {
  if (!val) return '';
  let s = String(val).trim().toUpperCase();
  if (s.length === 1 && /[A-Z0-9]/.test(s)) return 'KEY_' + s;
  return s;
}

function renderKeyOptionsHtml(selectedVal) {
  const norm = normalizeVk(selectedVal);
  const groups = DEFAULT_KEY_OPTIONS;

  let found = false;
  let html = '';
  groups.forEach(g => {
    html += `<optgroup label="${esc(g.group)}">`;
    g.options.forEach(opt => {
      const isSel = (opt.value === norm);
      if (isSel) found = true;
      html += `<option value="${esc(opt.value)}" ${isSel ? 'selected' : ''}>${esc(opt.label)}</option>`;
    });
    html += `</optgroup>`;
  });

  if (!found && selectedVal) {
    html = `<option value="${esc(selectedVal)}" selected>⭐ Custom: ${esc(selectedVal)}</option>` + html;
  }
  return html;
}

function populateKeySelects() {
  const keyMap = [
    { id: 'MoveUp', def: 'KEY_W' },
    { id: 'MoveDown', def: 'KEY_S' },
    { id: 'MoveLeft', def: 'KEY_A' },
    { id: 'MoveRight', def: 'KEY_D' },
    { id: 'SprintKey', def: 'SPACE' },
    { id: 'ToggleKey', def: 'INSERT' },
    { id: 'DumpKey', def: 'F6' },
    { id: 'PortalKey', def: 'KEY_B' },
    { id: 'LifeFlaskKey', def: 'KEY_1' },
    { id: 'ManaFlaskKey', def: 'KEY_2' },
  ];

  keyMap.forEach(item => {
    const el = document.getElementById(item.id);
    if (!el) return;
    const curVal = currentSettings[item.id] || item.def;
    el.innerHTML = renderKeyOptionsHtml(curVal);
    el.value = normalizeVk(curVal);
  });
}

function getSlotHotkeyDisplay(slot) {
  if (slot.InputType === 'MouseRight') return 'Right Click (RMB)';
  if (slot.InputType === 'MouseLeft') return 'Left Click (LMB)';
  if (slot.InputType === 'MouseMiddle') return 'Middle Click (MMB)';
  let k = slot.Key || 'KEY_Q';
  if (k.startsWith('KEY_')) k = k.substring(4);
  return k;
}

function renderBuffBadgesForSlot(listName, slotIdx) {
  if (!detectedBuffs || detectedBuffs.length === 0) {
    return `<span style="font-size:11px; color:var(--text-dim); font-style:italic;">No active buffs detected</span>`;
  }
  return detectedBuffs.map(b => {
    const info = b.TimeLeft > 0 ? ` (${b.TimeLeft}s)` : (b.Charges > 1 ? ` (x${b.Charges})` : '');
    return `<span class="skill-chip" style="background:rgba(16,185,129,0.15); color:#10b981; border:1px solid #10b981; font-size:11px; padding:3px 8px; cursor:pointer;" onclick="selectBuffForSlot('${listName}', ${slotIdx}, '${esc(b.Name)}')" title="Click to set condition: ${esc(b.Name)}">✨ ${esc(b.Name)}${info}</span>`;
  }).join('');
}

function selectBuffForSlot(listName, slotIdx, buffName) {
  if (currentSettings[listName] && currentSettings[listName][slotIdx]) {
    currentSettings[listName][slotIdx].BuffDebuffName = buffName;
    currentSettings[listName][slotIdx].OnlyWhenBuffMissing = true;
        if (document.getElementById('ShowDistanceCircles')) {
      document.getElementById('ShowDistanceCircles').checked = currentSettings.ShowDistanceCircles !== undefined ? currentSettings.ShowDistanceCircles : true;
    }
    renderAllSkillLists();
    showToast(`Set condition: Buff "${buffName}" missing`);
  }
}

function renderDebuffBadgesForSlot(listName, slotIdx) {
  if (!detectedDebuffs || detectedDebuffs.length === 0) {
    return `<span style="font-size:11px; color:var(--text-dim); font-style:italic;">No monster debuffs detected</span>`;
  }
  return detectedDebuffs.map(d => {
    return `<span class="skill-chip" style="background:rgba(168,85,247,0.15); color:#a855f7; border:1px solid #a855f7; font-size:11px; padding:3px 8px; cursor:pointer;" onclick="selectDebuffForSlot('${listName}', ${slotIdx}, '${esc(d.Name)}')" title="Click to curse: ${esc(d.Name)}">🔮 ${esc(d.Name)}</span>`;
  }).join('');
}

function selectDebuffForSlot(listName, slotIdx, debuffName) {
  if (currentSettings[listName] && currentSettings[listName][slotIdx]) {
    currentSettings[listName][slotIdx].BuffDebuffName = debuffName;
    currentSettings[listName][slotIdx].OnlyWhenBuffMissing = true;
    renderAllSkillLists();
    showToast(`Set condition: Target lacking "${debuffName}"`);
  }
}

function renderTotemDetailsHtml(slotIdx) {
  const count = liveActiveTotems || 0;
  const names = (liveActiveTotemNames && liveActiveTotemNames.length > 0) ? liveActiveTotemNames.join(', ') : 'None';
  return `
    <div style="display:flex; justify-content:space-between; align-items:center; font-size:11px; color:var(--text-dim);">
      <span>Active Totems in Area: <strong style="color:var(--yellow);">${count}</strong> (${esc(names)})</span>
    </div>
  `;
}

function renderDetectedSkillsChips() {
  const soloContainer = document.getElementById('detectedSkillsChips');
  const countBadge = document.getElementById('detectedCountBadge');
  const p1Container = document.getElementById('p1DetectedSkillsChips');
  const p2Container = document.getElementById('p2DetectedSkillsChips');

  if (!soloContainer && !p1Container && !p2Container) return;

  const p1List = (p1DetectedSkills && p1DetectedSkills.length > 0) ? p1DetectedSkills : detectedSkills;
  const p2List = (p2DetectedSkills && p2DetectedSkills.length > 0) ? p2DetectedSkills : [];

  if (countBadge) countBadge.innerText = `${p1List.length} detected`;

  // Solo chips
  if (soloContainer) {
    if (!p1List || p1List.length === 0) {
      soloContainer.innerHTML = '<span style="font-size:12px; color:var(--text-dim); font-style:italic;">No equipped skills detected yet.</span>';
    } else {
      let soloHtml = '';
      p1List.forEach(ds => {
        const cat = ds.Category || autoClassifySkill(ds.Name);
        const catDef = CATEGORY_PRESETS[cat] || CATEGORY_PRESETS.Attack;
        const isConfigured = currentSettings.Skills?.some(s => s.AssignedSkillName === ds.Name || s.Name === ds.Name);
        const checkmark = isConfigured ? '✓ ' : '+ ';
        const borderStyle = isConfigured ? `border: 1px solid ${catDef.color}; box-shadow: 0 0 6px ${catDef.color}40;` : 'border: 1px solid transparent;';
        const cdText = ds.CooldownMs > 0 ? ` (${(ds.CooldownMs/1000).toFixed(1)}s)` : '';

        soloHtml += `
          <span class="skill-chip" style="background:${catDef.bg}; color:${catDef.color}; ${borderStyle}" onclick="addSkillFromDetected('${esc(ds.Name)}', '${esc(cat)}', 'Skills')" title="Click to add: ${esc(ds.Name)}">
            ${checkmark}${catDef.label.split(' ')[0]} ${esc(ds.Name)}${cdText}
          </span>
        `;
      });
      soloContainer.innerHTML = soloHtml;
    }
  }

  // P1 Quick chips
  if (p1Container) {
    if (!p1List || p1List.length === 0) {
      p1Container.innerHTML = '<span style="font-size:11px; color:var(--text-dim); font-style:italic;">No P1 skills detected yet.</span>';
    } else {
      let p1Html = '';
      p1List.forEach(ds => {
        const cat = ds.Category || autoClassifySkill(ds.Name);
        const catDef = CATEGORY_PRESETS[cat] || CATEGORY_PRESETS.Attack;
        const isConfigured = currentSettings.P1Skills?.some(s => s.AssignedSkillName === ds.Name || s.Name === ds.Name);
        const checkmark = isConfigured ? '✓ ' : '+ ';
        p1Html += `
          <span class="skill-chip" style="background:${catDef.bg}; color:${catDef.color}; border:1px solid ${isConfigured ? catDef.color : 'rgba(255,255,255,0.1)'}; font-size:11px;" onclick="addSkillFromDetected('${esc(ds.Name)}', '${esc(cat)}', 'P1Skills')" title="Add to Player 1: ${esc(ds.Name)}">
            ${checkmark}P1: ${esc(ds.Name)}
          </span>
        `;
      });
      p1Container.innerHTML = p1Html;
    }
  }

  // P2 Quick chips (From Player 2 follower)
  if (p2Container) {
    if (!p2List || p2List.length === 0) {
      p2Container.innerHTML = '<span style="font-size:11px; color:var(--text-dim); font-style:italic;">Waiting for Player 2 skills in area...</span>';
    } else {
      let p2Html = '';
      p2List.forEach(ds => {
        const cat = ds.Category || autoClassifySkill(ds.Name);
        const catDef = CATEGORY_PRESETS[cat] || CATEGORY_PRESETS.Attack;
        const isConfigured = currentSettings.P2Skills?.some(s => s.AssignedSkillName === ds.Name || s.Name === ds.Name);
        const checkmark = isConfigured ? '✓ ' : '+ ';
        const cdText = ds.CooldownMs > 0 ? ` (${(ds.CooldownMs/1000).toFixed(1)}s)` : '';
        p2Html += `
          <span class="skill-chip" style="background:${catDef.bg}; color:${catDef.color}; border:1px solid ${isConfigured ? catDef.color : 'rgba(255,255,255,0.1)'}; font-size:11px;" onclick="addSkillFromDetected('${esc(ds.Name)}', '${esc(cat)}', 'P2Skills')" title="Add to Player 2: ${esc(ds.Name)}">
            ${checkmark}P2: ${esc(ds.Name)}${cdText}
          </span>
        `;
      });
      p2Container.innerHTML = p2Html;
    }
  }
}

// ═══════════════════════════════════════════════════════════════════════════════
// UNIFIED RICH SKILL SLOT RENDERER (Used by Solo, P1, and P2)
// ═══════════════════════════════════════════════════════════════════════════════

function renderSkillSlotList(listName, containerId, isGamepad) {
  const container = document.getElementById(containerId);
  if (!container) return;
  container.innerHTML = '';

  const slots = currentSettings[listName] || [];
  if (slots.length === 0) {
    const emptyMsg = isGamepad 
      ? (listName === 'P1Skills' ? 'No automated assist skills configured for Player 1.' : 'No autonomous combat skills configured for Player 2.')
      : 'No skill slots configured yet.';
    container.innerHTML = `
      <div style="text-align:center; padding:28px 16px; color:var(--text-dim); background:rgba(0,0,0,0.2); border-radius:8px; border:1px dashed var(--border);">
        <div style="font-size:22px; margin-bottom:6px;">${isGamepad ? '🎮' : '⚔️'}</div>
        <div style="font-weight:700; margin-bottom:4px;">${emptyMsg}</div>
        <div style="font-size:12px;">Click <strong>"+ Add Skill Slot"</strong> above or click any detected skill chip to configure one.</div>
      </div>
    `;
    return;
  }

  slots.forEach((slot, i) => {
    const card = document.createElement('div');
    card.className = 'skill-card';
    card.id = `${listName}_card_${i}`;

    const cat = slot.Category || autoClassifySkill(slot.AssignedSkillName || slot.Name);
    const catDef = CATEGORY_PRESETS[cat] || CATEGORY_PRESETS.Attack;

    // Hotkey / Gamepad badge
    let hotkeyBadgeHtml = '';
    if (isGamepad) {
      const btnLabel = getGamepadButtonLabel(slot.GamepadButton || 'RightShoulder');
      hotkeyBadgeHtml = `<span class="hotkey-badge" style="background:#1e3a5f; border-color:#3b82f6; color:#93c5fd;">🎮 [${esc(btnLabel)}]</span>`;
    } else {
      hotkeyBadgeHtml = `<span class="hotkey-badge">${esc(getSlotHotkeyDisplay(slot))}</span>`;
    }

    // Assigned skill options
    let skillOptionsHtml = `<option value="">-- Select Equipped Skill --</option>`;
    let foundCurrent = false;
    const relevantSkills = (listName === 'P2Skills')
      ? ((p2DetectedSkills && p2DetectedSkills.length > 0) ? p2DetectedSkills : ((p1DetectedSkills && p1DetectedSkills.length > 0) ? p1DetectedSkills : detectedSkills))
      : ((p1DetectedSkills && p1DetectedSkills.length > 0) ? p1DetectedSkills : detectedSkills);

    relevantSkills.forEach(ds => {
      const isSel = slot.AssignedSkillName === ds.Name;
      if (isSel) foundCurrent = true;
      const dCat = ds.Category || autoClassifySkill(ds.Name);
      const icon = (CATEGORY_PRESETS[dCat] || CATEGORY_PRESETS.Attack).label.split(' ')[0];
      skillOptionsHtml += `<option value="${esc(ds.Name)}" ${isSel ? 'selected' : ''}>${icon} ${esc(ds.Name)} [${dCat}]</option>`;
    });

    if (slot.AssignedSkillName && !foundCurrent) {
      skillOptionsHtml += `<option value="${esc(slot.AssignedSkillName)}" selected>⭐ ${esc(slot.AssignedSkillName)} [Custom]</option>`;
    }
    skillOptionsHtml += `<option value="__custom__">⚙️ Custom / Manual Name</option>`;

    // Category options
    let categoryOptionsHtml = '';
    Object.keys(CATEGORY_PRESETS).forEach(k => {
      if (k === 'Culler' && listName !== 'P2Skills') return;
      const cp = CATEGORY_PRESETS[k];
      categoryOptionsHtml += `<option value="${k}" ${k === cat ? 'selected' : ''}>${cp.label}</option>`;
    });

    // Category Specific Configuration Box
    let categorySpecificHtml = '';
    if (cat === 'Totem') {
      categorySpecificHtml = `
        <div class="category-box" style="border-color:rgba(245,158,11,0.3);">
          <div style="display:flex; justify-content:space-between; align-items:center; margin-bottom:10px;">
            <div style="font-weight:700; color:var(--yellow); font-size:12px;">🗿 Totem Deployment Controls</div>
            <span class="badge ${liveActiveTotems >= (slot.MaxTotemCount || 1) ? 'err' : 'ok'}">
              ${liveActiveTotems >= (slot.MaxTotemCount || 1) ? 'MAX REACHED (WAIT)' : 'READY TO RECAST'}
            </span>
          </div>
          <div class="grid3">
            <div class="form-group">
              <label>Max Active Totems: <span id="${listName}_maxTotemVal_${i}" class="slider-val">${slot.MaxTotemCount || 1}</span></label>
              <input type="range" min="1" max="8" value="${slot.MaxTotemCount || 1}" class="form-control" oninput="document.getElementById('${listName}_maxTotemVal_${i}').innerText=this.value; currentSettings['${listName}'][${i}].MaxTotemCount=parseInt(this.value);" />
            </div>
            <div class="form-group">
              <label>Target Monster Filter</label>
              <select class="form-control" onchange="currentSettings['${listName}'][${i}].TargetFilter = this.value;">
                ${renderTargetFilterOptions(slot.TargetFilter)}
              </select>
            </div>
            <div class="form-group">
              <label>Max Deploy Range: <span id="${listName}_totemRangeVal_${i}" class="slider-val">${slot.MaxTargetRange || 65}</span>g</label>
              <input type="range" min="20" max="100" value="${slot.MaxTargetRange || 65}" class="form-control" oninput="document.getElementById('${listName}_totemRangeVal_${i}').innerText=this.value; currentSettings['${listName}'][${i}].MaxTargetRange=parseFloat(this.value); previewRange('SkillRange', this.value, 'grid', 'Totem Range: ' + this.value + 'g', '#f59e0b', isGamepad ? 'Follower' : 'Player');" />
            </div>
          </div>
          <div style="margin-top:6px;">${renderTotemDetailsHtml(i)}</div>
        </div>
      `;
    } else if (cat === 'Buff' || cat === 'Guard' || cat === 'Warcry') {
      categorySpecificHtml = `
        <div class="category-box" style="border-color:${cat === 'Guard' ? 'rgba(239,68,68,0.3)' : cat === 'Warcry' ? 'rgba(236,72,153,0.3)' : 'rgba(16,185,129,0.3)'};">
          <div style="display:flex; justify-content:space-between; align-items:center; margin-bottom:8px;">
            <div style="font-weight:700; color:${catDef.color}; font-size:12px;">${catDef.label} Automatic Cast Conditions</div>
          </div>
          <div class="grid3">
            <div class="form-group">
              <label><input type="checkbox" ${slot.OnlyWhenBuffMissing ? 'checked' : ''} onchange="currentSettings['${listName}'][${i}].OnlyWhenBuffMissing = this.checked;" /> Only When Buff Missing</label>
              <input type="text" class="form-control" value="${esc(slot.BuffDebuffName || slot.AssignedSkillName || '')}" placeholder="Buff Name in BuffBar (e.g. Steelskin)" onchange="currentSettings['${listName}'][${i}].BuffDebuffName = this.value;" />
            </div>
            <div class="form-group">
              <div style="display:flex; justify-content:space-between; align-items:center;">
                <label style="margin-bottom:0;"><input type="checkbox" id="${listName}_onlyOnLowHp_${i}" ${slot.OnlyOnLowHp ? 'checked' : ''} onchange="currentSettings['${listName}'][${i}].OnlyOnLowHp = this.checked;" /> Only on Low Vital (<span id="${listName}_hpThresh_${i}">${slot.LowHpThresholdPercent || 60}%</span>)</label>
              </div>
              <div style="display:flex; gap:6px; align-items:center; margin-top:4px;">
                <select class="form-control" style="width:130px; padding:2px 6px; font-size:11px; height:28px;" onchange="currentSettings['${listName}'][${i}].VitalCondition = parseInt(this.value); currentSettings['${listName}'][${i}].OnlyOnLowHp = true; const cb = document.getElementById('${listName}_onlyOnLowHp_${i}'); if (cb) cb.checked = true;">
                  <option value="2" ${slot.VitalCondition === 2 || slot.VitalCondition === '2' || slot.VitalCondition === 'CombinedHpEs' || slot.VitalCondition === undefined ? 'selected' : ''}>🛡️ HP + ES</option>
                  <option value="0" ${slot.VitalCondition === 0 || slot.VitalCondition === '0' || slot.VitalCondition === 'HpOnly' ? 'selected' : ''}>❤️ HP Only</option>
                  <option value="1" ${slot.VitalCondition === 1 || slot.VitalCondition === '1' || slot.VitalCondition === 'EsOnly' ? 'selected' : ''}>⚡ ES Only</option>
                </select>
                <input type="range" min="15" max="90" value="${slot.LowHpThresholdPercent || 60}" class="form-control" style="flex:1;" oninput="document.getElementById('${listName}_hpThresh_${i}').innerText=this.value + '%'; currentSettings['${listName}'][${i}].LowHpThresholdPercent=parseFloat(this.value); currentSettings['${listName}'][${i}].OnlyOnLowHp = true; const cb = document.getElementById('${listName}_onlyOnLowHp_${i}'); if (cb) cb.checked = true;" />
              </div>
            </div>
            <div class="form-group">
              <label>Min Nearby Hostiles: <span id="${listName}_enemiesVal_${i}" class="slider-val">${slot.MinNearbyEnemies || 0}</span></label>
              <input type="range" min="0" max="8" value="${slot.MinNearbyEnemies || 0}" class="form-control" oninput="document.getElementById('${listName}_enemiesVal_${i}').innerText=this.value; currentSettings['${listName}'][${i}].MinNearbyEnemies=parseInt(this.value);" />
            </div>
          </div>
          <div style="display:flex; flex-wrap:wrap; gap:6px; margin-top:8px;">${renderBuffBadgesForSlot(listName, i)}</div>
        </div>
      `;
    } else if (cat === 'Curse') {
      categorySpecificHtml = `
        <div class="category-box" style="border-color:rgba(168,85,247,0.3);">
          <div style="font-weight:700; color:var(--purple); font-size:12px; margin-bottom:8px;">🔮 Curse / Debuff Casting Rules</div>
          <div class="grid3">
            <div class="form-group">
              <label>Target Monster Filter</label>
              <select class="form-control" onchange="currentSettings['${listName}'][${i}].TargetFilter = this.value;">
                ${renderTargetFilterOptions(slot.TargetFilter)}
              </select>
            </div>
            <div class="form-group">
              <label>Max Cast Range: <span id="${listName}_curseRangeVal_${i}" class="slider-val">${slot.MaxTargetRange || 70}</span>g</label>
              <input type="range" min="20" max="100" value="${slot.MaxTargetRange || 70}" class="form-control" oninput="document.getElementById('${listName}_curseRangeVal_${i}').innerText=this.value; currentSettings['${listName}'][${i}].MaxTargetRange=parseFloat(this.value); previewRange('SkillRange', this.value, 'grid', 'Curse Range: ' + this.value + 'g', '#a855f7', isGamepad ? 'Follower' : 'Player');" />
            </div>
            <div class="form-group">
              <label><input type="checkbox" ${slot.OnlyWhenBuffMissing ? 'checked' : ''} onchange="currentSettings['${listName}'][${i}].OnlyWhenBuffMissing = this.checked;" /> Target Lacking Curse</label>
              <input type="text" class="form-control" value="${esc(slot.BuffDebuffName || slot.AssignedSkillName || '')}" placeholder="Curse / Debuff Name" onchange="currentSettings['${listName}'][${i}].BuffDebuffName = this.value;" />
            </div>
          </div>
          <div style="display:flex; flex-wrap:wrap; gap:6px; margin-top:8px;">${renderDebuffBadgesForSlot(listName, i)}</div>
        </div>
      `;
    } else if (cat === 'Culler') {
      categorySpecificHtml = `
        <div class="category-box" style="border-color:rgba(244,63,94,0.3); background:rgba(244,63,94,0.05);">
          <div style="font-weight:700; color:#f43f5e; font-size:12px; margin-bottom:8px; display:flex; align-items:center; gap:6px;">
            🎯 Culler / Focused Fire (Co-op Follower Only)
          </div>
          <div class="grid2">
            <div class="form-group">
              <label>Distance from host to start attacking: <span id="${listName}_cullerStartVal_${i}" class="slider-val">${slot.CullerStartAttackDistance > 150 ? Math.round(slot.CullerStartAttackDistance/10.87) : (slot.CullerStartAttackDistance || 35)}</span>g</label>
              <input type="range" min="10" max="100" step="1" value="${slot.CullerStartAttackDistance > 150 ? Math.round(slot.CullerStartAttackDistance/10.87) : (slot.CullerStartAttackDistance || 35)}" class="form-control" oninput="document.getElementById('${listName}_cullerStartVal_${i}').innerText=this.value; currentSettings['${listName}'][${i}].CullerStartAttackDistance=parseFloat(this.value); previewRange('CullerStart', this.value, 'grid', '🎯 Culler Start: ' + this.value + 'g', '#f43f5e', 'Leader');" />
            </div>
            <div class="form-group" style="display:flex; flex-direction:column; justify-content:center;">
              <label style="display:flex; align-items:center; gap:8px; cursor:pointer; margin-top:14px;">
                <input type="checkbox" ${slot.CullerRequireMonsters ? 'checked' : ''} onchange="currentSettings['${listName}'][${i}].CullerRequireMonsters = this.checked;" />
                <span style="color:var(--text); font-weight:700;">Require Monsters Nearby</span>
              </label>
              <div style="font-size:11px; color:var(--text-dim); margin-top:3px;">
                ${slot.CullerRequireMonsters ? 'Attacks only when monsters are near host' : 'Attacks continuously when within attack distance'}
              </div>
            </div>
          </div>
        </div>
      `;
    } else {
      categorySpecificHtml = `
        <div class="category-box">
          <div class="grid3">
            <div class="form-group">
              <label>Target Monster Filter</label>
              <select class="form-control" onchange="currentSettings['${listName}'][${i}].TargetFilter = this.value;">
                ${renderTargetFilterOptions(slot.TargetFilter)}
              </select>
            </div>
            <div class="form-group">
              <label>Max Range: <span id="${listName}_rangeVal_${i}" class="slider-val">${slot.MaxTargetRange || 0}</span>g (0 = Auto)</label>
              <input type="range" min="0" max="120" value="${slot.MaxTargetRange || 0}" class="form-control" oninput="document.getElementById('${listName}_rangeVal_${i}').innerText=this.value; currentSettings['${listName}'][${i}].MaxTargetRange=parseFloat(this.value); if(parseFloat(this.value)>0) previewRange('SkillRange', this.value, 'grid', (slot.Name || 'Skill') + ' Range: ' + this.value + 'g', '#38bdf8', isGamepad ? 'Follower' : 'Player');" />
            </div>
            <div class="form-group">
              <label>Min Nearby Hostiles: <span id="${listName}_atkEnemiesVal_${i}" class="slider-val">${slot.MinNearbyEnemies || 0}</span></label>
              <input type="range" min="0" max="10" value="${slot.MinNearbyEnemies || 0}" class="form-control" oninput="document.getElementById('${listName}_atkEnemiesVal_${i}').innerText=this.value; currentSettings['${listName}'][${i}].MinNearbyEnemies=parseInt(this.value);" />
            </div>
          </div>
        </div>
      `;
    }

    // Row 2 Input / Gamepad controls
    let row2InputHtml = '';
    if (isGamepad) {
      row2InputHtml = `
        <div class="form-group">
          <label style="color:#60a5fa; font-weight:700;">🎮 Gamepad Button</label>
          <select class="form-control" style="font-weight:700; color:#93c5fd; border-color:#3b82f6;" onchange="currentSettings['${listName}'][${i}].GamepadButton = this.value; renderSkillSlotList('${listName}', '${containerId}', ${isGamepad});">
            ${renderGamepadButtonOptions(slot.GamepadButton || 'RightShoulder')}
          </select>
        </div>
        <div class="form-group">
          <label>Hold Duration: <span id="${listName}_holdVal_${i}" class="slider-val">${slot.HoldDurationMs || 150} ms</span></label>
          <input type="range" min="50" max="1000" step="25" value="${slot.HoldDurationMs || 150}" class="form-control" oninput="document.getElementById('${listName}_holdVal_${i}').innerText=this.value + ' ms'; currentSettings['${listName}'][${i}].HoldDurationMs=parseInt(this.value);" />
        </div>
      `;
    } else {
      row2InputHtml = `
        <div class="form-group">
          <label>Input Type</label>
          <select class="form-control" onchange="currentSettings['${listName}'][${i}].InputType = this.value; renderSkillSlotList('${listName}', '${containerId}', ${isGamepad});">
            ${renderInputTypeOptions(slot.InputType)}
          </select>
        </div>
        <div class="form-group" id="${listName}_keyGroup_${i}" style="${slot.InputType === 'KeyboardKey' ? '' : 'display:none;'}">
          <label>Keyboard Key</label>
          <select id="${listName}_skillKey_${i}" class="form-control key-select" onchange="currentSettings['${listName}'][${i}].Key = this.value; saveSettings(); renderSkillSlotList('${listName}', '${containerId}', ${isGamepad});">
            ${renderKeyOptionsHtml(slot.Key || 'KEY_Q')}
          </select>
        </div>
      `;
    }

    card.innerHTML = `
      <div class="skill-card-header">
        <div class="skill-card-title">
          <label class="toggle-switch">
            <input type="checkbox" ${slot.Enabled ? 'checked' : ''} onchange="currentSettings['${listName}'][${i}].Enabled = this.checked;" />
            <span class="slider"></span>
          </label>
          ${hotkeyBadgeHtml}
          <span id="${listName}_cardTitle_${i}" style="color:var(--text); font-weight:700;">${esc(slot.AssignedSkillName || slot.Name || 'Slot ' + (i + 1))}</span>
          <span class="cat-badge" style="background:${catDef.bg}; color:${catDef.color}; border:1px solid ${catDef.color};">${catDef.label}</span>
          <span class="pri-badge">Pri: ${slot.Priority}</span>
        </div>
        <div>
          <button class="btn danger" onclick="deleteSkillSlot('${listName}', ${i})">Delete</button>
        </div>
      </div>

      <!-- ROW 1: Skill Selection, Category, Label -->
      <div class="grid3" style="margin-bottom:12px;">
        <div class="form-group">
          <label style="color:${catDef.color};">Assigned Skill</label>
          <select class="form-control" style="border-color:${catDef.color};" onchange="onSelectAssignedSkill('${listName}', ${i}, this.value)">
            ${skillOptionsHtml}
          </select>
        </div>
        <div class="form-group">
          <label>Category</label>
          <select class="form-control" onchange="onChangeCategory('${listName}', ${i}, this.value)">
            ${categoryOptionsHtml}
          </select>
        </div>
        <div class="form-group">
          <label>Slot Label</label>
          <input type="text" class="form-control" value="${esc(slot.Name || '')}" onchange="currentSettings['${listName}'][${i}].Name = this.value; document.getElementById('${listName}_cardTitle_${i}').innerText = this.value;" />
        </div>
      </div>

      <!-- ROW 2: Input / Gamepad, Priority, Cooldown -->
      <div class="grid4" style="margin-bottom:10px;">
        ${row2InputHtml}
        <div class="form-group">
          <label>Priority (1-10): <span id="${listName}_priVal_${i}" class="slider-val">${slot.Priority}</span></label>
          <input type="range" min="1" max="10" value="${slot.Priority}" class="form-control" oninput="document.getElementById('${listName}_priVal_${i}').innerText=this.value; currentSettings['${listName}'][${i}].Priority=parseInt(this.value);" />
        </div>
        <div class="form-group">
          <label>Cooldown (ms): <span id="${listName}_cdVal_${i}" class="slider-val">${slot.MinCastIntervalMs} ms</span></label>
          <input type="range" min="0" max="10000" step="50" value="${slot.MinCastIntervalMs}" class="form-control" oninput="document.getElementById('${listName}_cdVal_${i}').innerText=this.value + ' ms'; currentSettings['${listName}'][${i}].MinCastIntervalMs=parseInt(this.value);" />
        </div>
      </div>

      <!-- ROW 3: Category-Specific Config Box -->
      ${categorySpecificHtml}
    `;

    container.appendChild(card);
  });
}

function esc(s) {
  if (!s) return '';
  return String(s).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
}

function renderAllSkillLists() {
  renderSkillSlotList('Skills', 'skillsContainer', false);
  renderSkillSlotList('P1Skills', 'p1SkillsContainer', true);
  renderSkillSlotList('P2Skills', 'p2SkillsContainer', true);
}

function addSkillSlot(listName, defaultCat = 'Attack', defaultButton = 'RightShoulder') {
  if (!currentSettings[listName]) currentSettings[listName] = [];
  const isGamepad = (listName === 'P1Skills' || listName === 'P2Skills');
  const catDef = CATEGORY_PRESETS[defaultCat] || CATEGORY_PRESETS.Attack;

  currentSettings[listName].push({
    Enabled: true,
    Name: defaultCat + " Slot " + (currentSettings[listName].length + 1),
    AssignedSkillName: "",
    Category: defaultCat,
    Role: catDef.role || "EnemyTargeted",
    InputType: isGamepad ? "KeyboardKey" : "KeyboardKey",
    Key: "KEY_Q",
    GamepadButton: defaultButton,
    Priority: catDef.priority || 5,
    TargetFilter: catDef.filter || "Any",
    MinCastIntervalMs: catDef.interval || 500,
    HoldDurationMs: catDef.hold || 150,
    MaxTargetRange: catDef.maxRange || 0,
    MinNearbyEnemies: catDef.minEnemies || 0,
    OnlyOnLowHp: catDef.lowHp || false,
    VitalCondition: catDef.vitalCondition !== undefined ? catDef.vitalCondition : 2,
    LowHpThresholdPercent: catDef.lowHpThresh || 60,
    MinManaPercent: 0,
    IsChannel: false,
    OnlyWhenBuffMissing: catDef.onlyWhenBuffMissing || false,
    BuffDebuffName: "",
    MaxTotemCount: catDef.maxTotems || 1,
    MaxMinionCount: catDef.maxMinions || 3,
    CullerAimDistance: catDef.cullerAimDist || 75,
    CullerStartAttackDistance: catDef.cullerStartDist || 35,
    CullerRequireMonsters: catDef.cullerRequireMonsters !== undefined ? catDef.cullerRequireMonsters : false
  });
  renderAllSkillLists();
}

function addPresetSkill(listName, skillName, cat, button, cooldownMs, onlyBuffMissing) {
  if (!currentSettings[listName]) currentSettings[listName] = [];
  const catDef = CATEGORY_PRESETS[cat] || CATEGORY_PRESETS.Attack;
  currentSettings[listName].push({
    Enabled: true,
    Name: skillName,
    AssignedSkillName: skillName,
    Category: cat,
    Role: catDef.role || "SelfBuffGuard",
    InputType: "KeyboardKey",
    Key: "KEY_Q",
    GamepadButton: button,
    Priority: catDef.priority || 5,
    TargetFilter: catDef.filter || "Any",
    MinCastIntervalMs: cooldownMs || catDef.interval || 500,
    HoldDurationMs: catDef.hold || 100,
    MaxTargetRange: catDef.maxRange || 0,
    MinNearbyEnemies: catDef.minEnemies || 0,
    OnlyOnLowHp: catDef.lowHp || false,
    VitalCondition: catDef.vitalCondition !== undefined ? catDef.vitalCondition : 2,
    LowHpThresholdPercent: catDef.lowHpThresh || 60,
    MinManaPercent: 0,
    IsChannel: false,
    OnlyWhenBuffMissing: onlyBuffMissing !== undefined ? onlyBuffMissing : (catDef.onlyWhenBuffMissing || false),
    BuffDebuffName: onlyBuffMissing ? skillName : "",
    MaxTotemCount: catDef.maxTotems || 1,
    MaxMinionCount: catDef.maxMinions || 3,
    CullerAimDistance: catDef.cullerAimDist || 75,
    CullerStartAttackDistance: catDef.cullerStartDist || 35,
    CullerRequireMonsters: catDef.cullerRequireMonsters !== undefined ? catDef.cullerRequireMonsters : false
  });
  renderAllSkillLists();
  saveSettings();
  showToast(`Added ${skillName} to ${listName === 'P1Skills' ? 'Player 1' : 'Player 2'}`);
}

async function deleteSkillSlot(listName, index) {
  if (confirm("Delete this skill slot?")) {
    currentSettings[listName].splice(index, 1);
    renderAllSkillLists();
    await saveSettings();
  }
}

async function clearAllSkills(listName) {
  if (!currentSettings[listName] || currentSettings[listName].length === 0) return;
  const label = listName === 'P1Skills' ? 'Player 1' : listName === 'P2Skills' ? 'Player 2' : 'Solo';
  if (confirm(`Are you sure you want to clear all skill slots for ${label}?`)) {
    currentSettings[listName] = [];
    renderAllSkillLists();
    await saveSettings();
    showToast(`All ${label} skill slots cleared and saved.`);
  }
}

function addSkillFromDetected(skillName, cat, listName = 'Skills') {
  if (!currentSettings[listName]) currentSettings[listName] = [];
  const isGamepad = (listName === 'P1Skills' || listName === 'P2Skills');
  const catDef = CATEGORY_PRESETS[cat] || CATEGORY_PRESETS.Attack;
  const slot = {
    Enabled: true,
    Name: skillName,
    AssignedSkillName: skillName,
    Category: cat,
    Role: catDef.role || "EnemyTargeted",
    InputType: "KeyboardKey",
    Key: "KEY_Q",
    GamepadButton: isGamepad ? "RightShoulder" : "RightShoulder",
    Priority: catDef.priority || 5,
    TargetFilter: catDef.filter || "Any",
    MinCastIntervalMs: catDef.interval || 500,
    HoldDurationMs: catDef.hold || 150,
    MaxTargetRange: catDef.maxRange || 0,
    MinNearbyEnemies: catDef.minEnemies || 0,
    OnlyOnLowHp: catDef.lowHp || false,
    VitalCondition: catDef.vitalCondition !== undefined ? catDef.vitalCondition : 2,
    LowHpThresholdPercent: catDef.lowHpThresh || 60,
    MinManaPercent: 0,
    IsChannel: false,
    OnlyWhenBuffMissing: catDef.onlyWhenBuffMissing || false,
    BuffDebuffName: (cat === 'Buff' || cat === 'Guard') ? skillName : "",
    MaxTotemCount: catDef.maxTotems || 1,
    MaxMinionCount: catDef.maxMinions || 3,
    CullerAimDistance: catDef.cullerAimDist || 75,
    CullerStartAttackDistance: catDef.cullerStartDist || 35,
    CullerRequireMonsters: catDef.cullerRequireMonsters !== undefined ? catDef.cullerRequireMonsters : false
  };
  currentSettings[listName].push(slot);
  renderAllSkillLists();
  saveSettings();
  showToast(`Added ${skillName} [${cat}] to ${listName === 'P1Skills' ? 'Player 1' : listName === 'P2Skills' ? 'Player 2' : 'Skills'}`);
}

function onSelectAssignedSkill(listName, slotIndex, val) {
  if (!currentSettings[listName] || !currentSettings[listName][slotIndex]) return;
  if (val === '__custom__') {
    const customName = prompt("Enter skill name (exact name as in PoE 2):");
    if (customName && customName.trim()) {
      currentSettings[listName][slotIndex].AssignedSkillName = customName.trim();
      currentSettings[listName][slotIndex].Name = customName.trim();
    }
  } else {
    currentSettings[listName][slotIndex].AssignedSkillName = val;
    if (val) currentSettings[listName][slotIndex].Name = val;
  }
  renderAllSkillLists();
}

function onChangeCategory(listName, slotIndex, val) {
  if (!currentSettings[listName] || !currentSettings[listName][slotIndex]) return;
  currentSettings[listName][slotIndex].Category = val;
  const cp = CATEGORY_PRESETS[val];
  if (cp) {
    currentSettings[listName][slotIndex].Role = cp.role || 'EnemyTargeted';
    currentSettings[listName][slotIndex].Priority = cp.priority || 5;
    currentSettings[listName][slotIndex].MinCastIntervalMs = cp.interval || 500;
    currentSettings[listName][slotIndex].HoldDurationMs = cp.hold || 150;
    currentSettings[listName][slotIndex].TargetFilter = cp.filter || 'Any';
    if (cp.lowHp !== undefined) currentSettings[listName][slotIndex].OnlyOnLowHp = cp.lowHp;
    if (cp.vitalCondition !== undefined) currentSettings[listName][slotIndex].VitalCondition = cp.vitalCondition;
    if (cp.lowHpThresh !== undefined) currentSettings[listName][slotIndex].LowHpThresholdPercent = cp.lowHpThresh;
    if (cp.onlyWhenBuffMissing !== undefined) currentSettings[listName][slotIndex].OnlyWhenBuffMissing = cp.onlyWhenBuffMissing;
    if (cp.maxTotems !== undefined) currentSettings[listName][slotIndex].MaxTotemCount = cp.maxTotems;
    if (cp.maxMinions !== undefined) currentSettings[listName][slotIndex].MaxMinionCount = cp.maxMinions;
    if (cp.cullerAimDist !== undefined) currentSettings[listName][slotIndex].CullerAimDistance = cp.cullerAimDist;
    if (cp.cullerStartDist !== undefined) currentSettings[listName][slotIndex].CullerStartAttackDistance = cp.cullerStartDist;
    if (cp.cullerRequireMonsters !== undefined) currentSettings[listName][slotIndex].CullerRequireMonsters = cp.cullerRequireMonsters;
  }
  renderAllSkillLists();
}

// ═══════════════════════════════════════════════════════════════════════════════
// COOP TEST ACTION
// ═══════════════════════════════════════════════════════════════════════════════

async function connectGamepads() {
  try {
    const res = await fetch('/api/coop/connect', { method: 'POST' });
    const data = await res.json();
    if (data.success) {
      showToast(data.message || "🎮 Virtual Gamepads Connected!");
      const p1Badge = document.getElementById('p1PadBadge');
      const p2Badge = document.getElementById('p2PadBadge');
      if (p1Badge && data.leaderSlot >= 0) {
        p1Badge.className = 'badge ok';
        p1Badge.innerText = `P1: Virtual Pad #${data.leaderSlot} (Leader Passthrough)`;
      }
      if (p2Badge && data.followerSlot >= 0) {
        p2Badge.className = 'badge ok';
        p2Badge.innerText = `P2: Virtual Pad #${data.followerSlot} (Follower Bot)`;
      }
    } else {
      alert("Connection failed: " + (data.error || "Unknown error"));
    }
  } catch(e) {
    alert("Network error: " + e.message);
  }
}

async function testWobbleFollower() {
  try {
    const res = await fetch('/api/coop/test', { method: 'POST' });
    const data = await res.json();
    if (data.success) {
      showToast(data.message || "🎮 Follower Controller Nudged (Wobble Verified)!");
    } else {
      alert("Test failed: " + (data.error || "Unknown error"));
    }
  } catch(e) {
    alert("Network error: " + e.message);
  }
}

async function disconnectGamepads() {
  try {
    const res = await fetch('/api/coop/disconnect', { method: 'POST' });
    const data = await res.json();
    if (data.success) {
      showToast("🔌 Virtual Gamepads Disconnected!");
      const p1Badge = document.getElementById('p1PadBadge');
      const p2Badge = document.getElementById('p2PadBadge');
      if (p1Badge) { p1Badge.className = 'badge err'; p1Badge.innerText = 'P1: Disconnected'; }
      if (p2Badge) { p2Badge.className = 'badge err'; p2Badge.innerText = 'P2: Disconnected'; }
    }
  } catch(e) {
    alert("Network error: " + e.message);
  }
}

// ═══════════════════════════════════════════════════════════════════════════════
// MODE SWITCHING & VIEW TRANSFORM
// ═══════════════════════════════════════════════════════════════════════════════

function formatModeName(mode) {
  const m = String(mode || 'MapFarm').toLowerCase();
  if (m === 'mapfarm' || m === 'wavefarm' || m === '0') return 'MAP FARM';
  if (m === 'follower' || m === '1') return 'FOLLOWER (COUCH CO-OP)';
  if (m === 'boss' || m === '2') return 'BOSS';
  if (m === 'idle' || m === '3') return 'IDLE';
  return String(mode).toUpperCase();
}

function updateModeViews(mode) {
  const isFollower = (String(mode).toLowerCase() === 'follower' || mode === 1 || mode === '1');
  
  // Tab Header label
  const tabHeader = document.getElementById('tabHeaderCombat');
  if (tabHeader) {
    tabHeader.innerText = isFollower ? '🎮 Co-op Dual-Gamepad' : 'Combat & Movement';
  }

  // Live status follower bar & notice
  const liveBars = document.getElementById('coopLiveBars');
  if (liveBars) liveBars.style.display = isFollower ? 'grid' : 'none';

  const notice = document.getElementById('followerNotice');
  if (notice) notice.style.display = isFollower ? 'block' : 'none';

  // Combat Tab Views
  const soloView = document.getElementById('soloCombatView');
  const coopView = document.getElementById('coopCombatView');
  if (soloView) soloView.style.display = isFollower ? 'none' : 'block';
  if (coopView) coopView.style.display = isFollower ? 'block' : 'none';

  // Config Tab Flask Card vs Notice
  const soloFlask = document.getElementById('soloFlaskCard');
  const coopFlaskNotice = document.getElementById('coopFlaskNotice');
  if (soloFlask) soloFlask.style.display = isFollower ? 'none' : 'block';
  if (coopFlaskNotice) coopFlaskNotice.style.display = isFollower ? 'block' : 'none';
}

function onModeChange(newMode) {
  currentSettings.Mode = newMode;
  updateModeViews(newMode);

  const display = formatModeName(newMode);
  const badge = document.getElementById('statModeBadge');
  if (badge) badge.innerText = `MODE: ${display}`;
  const currBadge = document.getElementById('currentModeBadge');
  if (currBadge) currBadge.innerText = `ACTIVE: ${display}`;

  saveSettings();
  showToast(`Bot mode set to: ${display}`);
}

// ═══════════════════════════════════════════════════════════════════════════════
// PROFILES MANAGEMENT
// ═══════════════════════════════════════════════════════════════════════════════

let currentProfileName = "Default";

async function loadProfiles() {
  try {
    const res = await fetch('/api/profiles');
    if (!res.ok) return;
    const data = await res.json();
    currentProfileName = data.active || "Default";
    const sel = document.getElementById('profileSelect');
    if (!sel) return;
    sel.innerHTML = '';
    (data.profiles || ["Default"]).forEach(p => {
      const opt = document.createElement('option');
      opt.value = p;
      opt.innerText = p;
      if (p === currentProfileName) opt.selected = true;
      sel.appendChild(opt);
    });
  } catch(e) {
    console.error("Failed to load profiles:", e);
  }
}

async function switchProfile(name) {
  if (!name || name === currentProfileName) return;
  try {
    const res = await fetch('/api/profiles/switch', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ name })
    });
    const data = await res.json();
    if (data.success) {
      await loadProfiles();
      await loadSettings();
      showToast(`Switched to profile: ${name}`);
    } else {
      alert("Failed to switch profile: " + (data.error || "Unknown error"));
      await loadProfiles();
    }
  } catch(e) {
    alert("Error switching profile: " + e.message);
  }
}

async function promptNewProfile() {
  const name = prompt("Enter name for new profile (will clone current settings):");
  if (!name || !name.trim()) return;
  const clean = name.trim();
  try {
    const res = await fetch('/api/profiles/create', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ name: clean, switchTo: true })
    });
    const data = await res.json();
    if (data.success) {
      await loadProfiles();
      await loadSettings();
      showToast(`Created & switched to: ${clean}`);
    } else {
      alert("Failed to create profile: " + (data.error || "Name already exists"));
    }
  } catch(e) {
    alert("Error creating profile: " + e.message);
  }
}

async function promptRenameProfile() {
  if (currentProfileName.toLowerCase() === 'default') {
    alert("The 'Default' profile cannot be renamed.");
    return;
  }
  const to = prompt(`Rename profile "${currentProfileName}" to:`, currentProfileName);
  if (!to || !to.trim() || to.trim() === currentProfileName) return;
  const cleanTo = to.trim();
  try {
    const res = await fetch('/api/profiles/rename', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ from: currentProfileName, to: cleanTo })
    });
    const data = await res.json();
    if (data.success) {
      await loadProfiles();
      showToast(`Renamed profile to: ${cleanTo}`);
    } else {
      alert("Failed to rename: " + (data.error || "Name error"));
    }
  } catch(e) {
    alert("Error renaming profile: " + e.message);
  }
}

async function deleteCurrentProfile() {
  if (currentProfileName.toLowerCase() === 'default') {
    alert("The 'Default' profile cannot be deleted.");
    return;
  }
  if (!confirm(`Are you sure you want to delete profile "${currentProfileName}"?`)) return;
  try {
    const res = await fetch('/api/profiles/delete', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ name: currentProfileName })
    });
    const data = await res.json();
    if (data.success) {
      await loadProfiles();
      await loadSettings();
      showToast(`Deleted profile: ${currentProfileName}`);
    } else {
      alert("Failed to delete profile: " + (data.error || "Error"));
    }
  } catch(e) {
    alert("Error deleting profile: " + e.message);
  }
}

// ═══════════════════════════════════════════════════════════════════════════════
// SETTINGS LOAD & SAVE
// ═══════════════════════════════════════════════════════════════════════════════

async function loadSettings() {
  try {
    const res = await fetch('/api/settings');
    currentSettings = await res.json();

    // Ensure array initializations
    currentSettings.Skills = currentSettings.Skills || [];
    currentSettings.P1Skills = currentSettings.P1Skills || currentSettings.P1Buffs || [];
    currentSettings.P2Skills = currentSettings.P2Skills || [];

    for (let k in currentSettings) {
      const el = document.getElementById(k);
      if (!el) continue;
      if (el.type === 'checkbox') {
        el.checked = currentSettings[k];
      } else if (el.tagName === 'SELECT' && el.classList.contains('key-select')) {
        const norm = normalizeVk(currentSettings[k]);
        el.innerHTML = renderKeyOptionsHtml(norm);
        el.value = norm;
      } else {
        el.value = currentSettings[k];
      }
    }
    populateKeySelects();

    let modeVal = currentSettings.Mode || 'MapFarm';
    if (modeVal === 'WaveFarm' || modeVal === 0 || modeVal === '0') modeVal = 'MapFarm';
    renderModeSelectorOptions();
    const modeEl = document.getElementById('Mode');
    if (modeEl) modeEl.value = modeVal;

    updateModeViews(modeVal);

    const display = formatModeName(modeVal);
    const currBadge = document.getElementById('currentModeBadge');
    if (currBadge) currBadge.innerText = `ACTIVE: ${display}`;
    const statBadge = document.getElementById('statModeBadge');
    if (statBadge) statBadge.innerText = `MODE: ${display}`;

    // Update Slider Value Texts
    if (document.getElementById('CombatRange')) {
      if (document.getElementById('CombatStyle')) document.getElementById('CombatStyle').value = currentSettings.CombatStyle || 'Ranged';
      if (document.getElementById('FightRange')) {
        document.getElementById('fightRangeVal').innerText = currentSettings.FightRange || 45;
        document.getElementById('FightRange').value = currentSettings.FightRange || 45;
      }
      document.getElementById('combatRangeVal').innerText = currentSettings.CombatRange || 65;
      document.getElementById('sprintDistVal').innerText = currentSettings.SprintMinDistance || 40;
      document.getElementById('lifeThreshVal').innerText = (currentSettings.LifeFlaskThresholdPercent || 50) + '%';
      document.getElementById('manaThreshVal').innerText = (currentSettings.ManaFlaskThresholdPercent || 30) + '%';
    }

    // Co-op Sliders
    if (document.getElementById('p1LifeThreshVal')) {
      document.getElementById('p1LifeThreshVal').innerText = (currentSettings.P1LifeFlaskThresholdPercent || 50) + '%';
      document.getElementById('p1ManaThreshVal').innerText = (currentSettings.P1ManaFlaskThresholdPercent || 30) + '%';
      document.getElementById('p2LifeThreshVal').innerText = (currentSettings.P2LifeFlaskThresholdPercent || 50) + '%';
      document.getElementById('p2ManaThreshVal').innerText = (currentSettings.P2ManaFlaskThresholdPercent || 30) + '%';
      if (document.getElementById('coopStopDistVal')) document.getElementById('coopStopDistVal').innerText = (currentSettings.CoopStopDistance || 10) + 'g';
      document.getElementById('coopFollowDistVal').innerText = (currentSettings.CoopFollowDistance || 20) + 'g';
      document.getElementById('coopSprintDistVal').innerText = (currentSettings.CoopSprintDistance || 40) + 'g';
    }

    // Position Helper
    const posVal = currentSettings.FollowerPosition || 'Back';
    const posRadio = document.querySelector(`input[name="FollowerPosition"][value="${posVal}"]`);
    if (posRadio) {
      posRadio.checked = true;
    }
    updatePositionHelperVisuals(posVal);
    updateFollowerLockUI();

    renderAllSkillLists();
  } catch(e) {
    console.error("Failed to load settings:", e);
  }
}

function updateFollowerLockUI() {
  const nameInput = document.getElementById('FollowerCharacterName');
  const badge = document.getElementById('followerLockBadge');
  if (!nameInput || !badge) return;
  const val = nameInput.value.trim();
  if (val) {
    badge.innerText = `🔒 Locked: [${val}]`;
    badge.style.borderColor = 'var(--green)';
    badge.style.color = 'var(--green)';
  } else {
    badge.innerText = 'Auto-Detect';
    badge.style.borderColor = '#38bdf8';
    badge.style.color = '#38bdf8';
  }
}

function clearFollowerName() {
  const input = document.getElementById('FollowerCharacterName');
  if (input) {
    input.value = '';
    saveSettings();
    updateFollowerLockUI();
    const sel = document.getElementById('followerPlayerSelect');
    if (sel) sel.value = '';
    showToast('ล้างชื่อตัวตามแล้ว (กลับสู่โหมด Auto-Detect)');
  }
}

function selectFollowerName(name) {
  const input = document.getElementById('FollowerCharacterName');
  if (input) {
    input.value = name;
    saveSettings();
    updateFollowerLockUI();
    const sel = document.getElementById('followerPlayerSelect');
    if (sel) sel.value = name;
    showToast(`ล็อกตัวตาม: ${name}`);
  }
}

function onSelectFollowerFromDropdown(name) {
  if (!name) return;
  selectFollowerName(name);
}

function onFollowerInputChange() {
  saveSettings();
  updateFollowerLockUI();
  const input = document.getElementById('FollowerCharacterName');
  const sel = document.getElementById('followerPlayerSelect');
  if (input && sel) {
    sel.value = input.value.trim();
  }
}

let lastPlayerNamesHash = '';
function populatePlayerOptions(players, playerNames) {
  const datalist = document.getElementById('followerNameDatalist');
  const select = document.getElementById('followerPlayerSelect');
  const names = playerNames || (players ? players.map(p => typeof p === 'string' ? p : p.Name) : []);
  const hash = names.slice().sort().join(',');

  if (datalist) {
    datalist.innerHTML = names.map(n => `<option value="${n}">`).join('');
  }

  if (select) {
    if (names.length > 0) {
      select.style.display = 'inline-block';
      if (hash !== lastPlayerNamesHash) {
        lastPlayerNamesHash = hash;
        let html = `<option value="">-- เลือกจากรายชื่อที่สแกนเจอ (${names.length} คน) --</option>`;
        if (players && players.length > 0 && typeof players[0] === 'object') {
          html += players.map(p => {
            const detail = [p.ClassName, p.Distance ? `${p.Distance}g` : ''].filter(Boolean).join(' | ');
            const label = detail ? `${p.Name} (${detail})` : p.Name;
            return `<option value="${p.Name}">👤 ${label}</option>`;
          }).join('');
        } else {
          html += names.map(n => `<option value="${n}">👤 ${n}</option>`).join('');
        }
        select.innerHTML = html;
      }
      const cur = document.getElementById('FollowerCharacterName')?.value.trim() || '';
      if (cur) select.value = cur;
    } else {
      select.style.display = 'none';
      select.innerHTML = '<option value="">-- ไม่พบผู้เล่นอื่น --</option>';
      lastPlayerNamesHash = '';
    }
  }
}

async function fetchAndPopulatePlayerNames() {
  const btn = document.getElementById('btnGetPlayerNames');
  const origText = btn ? btn.innerHTML : '';
  if (btn) btn.innerHTML = '⏳ Scanning...';

  try {
    const res = await fetch('/api/players/nearby');
    if (!res.ok) throw new Error(`HTTP ${res.status}`);
    const data = await res.json();
    const players = data.players || [];
    const playerNames = data.playerNames || [];

    populatePlayerOptions(players, playerNames);

    if (playerNames.length === 0) {
      showToast('⚠️ ไม่พบผู้เล่นอื่นในพื้นที่/แมพขณะนี้ (ต้องอยู่ในแมพหรือห้องเดียวกัน)');
    } else if (playerNames.length === 1) {
      const targetName = playerNames[0];
      selectFollowerName(targetName);
      showToast(`✅ ดึงชื่อตัวละครสำเร็จ: [${targetName}] ล็อกเป้าหมายเรียบร้อย`);
    } else {
      showToast(`👥 พบผู้เล่น ${playerNames.length} คน! เลือกชื่อจากช่องหรือรายการได้ทันที`);
      const select = document.getElementById('followerPlayerSelect');
      if (select) {
        select.style.display = 'inline-block';
        select.focus();
      }
    }
  } catch (err) {
    console.error('Failed to fetch nearby players:', err);
    try {
      const sRes = await fetch('/api/status');
      const sData = await sRes.json();
      const names = sData.NearbyPlayerNames || [];
      populatePlayerOptions(null, names);
      if (names.length === 1) {
        selectFollowerName(names[0]);
        showToast(`✅ ดึงชื่อตัวละครสำเร็จ: [${names[0]}] ล็อกเป้าหมายเรียบร้อย`);
      } else if (names.length > 1) {
        showToast(`👥 พบผู้เล่น ${names.length} คน! เลือกชื่อจากรายการได้ทันที`);
      } else {
        showToast('⚠️ ไม่พบผู้เล่นอื่นในพื้นที่/แมพขณะนี้');
      }
    } catch(e2) {
      showToast('❌ ไม่สามารถดึงรายชื่อผู้เล่นได้');
    }
  } finally {
    if (btn) btn.innerHTML = origText;
  }
}

async function saveSettings() {
  const payload = { ...currentSettings };
  const simpleFields = [
    'Mode', 'ToggleKey', 'DumpKey', 'PortalKey',
    'MoveUp', 'MoveDown', 'MoveLeft', 'MoveRight',
    'UseSprint', 'SprintKey', 'SprintMinDistance',
    'CombatStyle', 'FightRange', 'CombatRange',
    'AutoLifeFlask', 'LifeFlaskKey', 'LifeFlaskThresholdPercent',
    'AutoManaFlask', 'ManaFlaskKey', 'ManaFlaskThresholdPercent',
    'CheckFlaskActiveEffect', 'CheckFlaskCharges',
    'FollowerCharacterName', 'FollowerLeaderName', 'FollowDistance', 'FollowStopDistance', 'FollowerEnableCombat',
    'CoopPhysicalPadIndex', 'P1AutoLifeFlask', 'P1LifeFlaskThresholdPercent', 'P1AutoManaFlask', 'P1ManaFlaskThresholdPercent',
    'CoopFollowDistance', 'CoopStopDistance', 'CoopSprintDistance',
    'P2AutoLifeFlask', 'P2LifeFlaskThresholdPercent', 'P2AutoManaFlask', 'P2ManaFlaskThresholdPercent', 'P2EnableCombat',
    'ShowOverlay', 'ShowDistanceCircles', 'WebServerPort', 'WebServerNetworkAccess'
  ];

  simpleFields.forEach(f => {
    const el = document.getElementById(f);
    if (el) {
      if (el.type === 'checkbox') payload[f] = el.checked;
      else if (el.type === 'number' || el.type === 'range') payload[f] = parseFloat(el.value);
      else payload[f] = el.value;
    }
  });

  updateFollowerLockUI();

  // Position Helper radio
  const selectedPos = document.querySelector('input[name="FollowerPosition"]:checked');
  if (selectedPos) {
    payload.FollowerPosition = selectedPos.value;
  }

  // Ensure full skill slot lists are preserved
  payload.Skills = currentSettings.Skills || [];
  payload.P1Skills = currentSettings.P1Skills || [];
  payload.P2Skills = currentSettings.P2Skills || [];
  delete payload.P1Buffs;
  delete payload.p1buffs;

  try {
    const res = await fetch('/api/settings', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(payload)
    });
    if (res.ok) {
      const data = await res.json();
      if (data && data.settings) {
        currentSettings = data.settings;
        currentSettings.Skills = currentSettings.Skills || [];
        currentSettings.P1Skills = currentSettings.P1Skills || [];
        currentSettings.P2Skills = currentSettings.P2Skills || [];
        renderAllSkillLists();
        const posVal = currentSettings.FollowerPosition || 'Back';
        updatePositionHelperVisuals(posVal);
      }
      showToast("Settings Saved Successfully!");
    } else {
      alert("Failed to save settings: " + res.statusText);
    }
  } catch(e) {
    alert("Network error saving settings: " + e.message);
  }
}

function selectPositionHelper(val) {
  const radio = document.querySelector(`input[name="FollowerPosition"][value="${val}"]`);
  if (radio) {
    radio.checked = true;
  }
  updatePositionHelperVisuals(val);
  saveSettings();
}

function updatePositionHelperVisuals(val) {
  document.querySelectorAll('.pos-card').forEach(card => {
    const r = card.querySelector('input[type="radio"]');
    if (r && r.value === val) {
      card.classList.add('active');
    } else {
      card.classList.remove('active');
    }
  });
  const badge = document.getElementById('posHelperLabelBadge');
  if (badge) badge.innerText = val;
}

async function toggleBot() {
  try {
    const res = await fetch('/api/toggle', { method: 'POST' });
    const data = await res.json();
    updateRunStatus(data.isRunning);
    showToast(data.isRunning ? "Bot STARTED" : "Bot STOPPED");
  } catch(e) {
    alert("Failed to toggle bot: " + e.message);
  }
}

function updateRunStatus(isRunning) {
  const btn = document.getElementById('mainToggleBtn');
  if (!btn) return;
  if (isRunning) {
    btn.className = 'btn stop';
    btn.innerText = 'STOP BOT';
  } else {
    btn.className = 'btn start';
    btn.innerText = 'START BOT';
  }
}

function updateDashboardSnapshot(snap) {
  if (!snap) return;
  document.getElementById('statState').innerText = snap.State || 'IDLE';
  document.getElementById('statDuration').innerText = snap.ActiveDuration || '00:00:00';
  document.getElementById('statArea').innerText = snap.AreaName || 'Unknown';
  document.getElementById('statHostiles').innerText = snap.HostileCount || 0;
  const activeMode = snap.Mode || currentSettings.Mode || 'MapFarm';
  const display = formatModeName(activeMode);
  const statBadge = document.getElementById('statModeBadge');
  if (statBadge) statBadge.innerText = "MODE: " + display;
  const currBadge = document.getElementById('currentModeBadge');
  if (currBadge) currBadge.innerText = "ACTIVE: " + display;

  const cov = Math.round(snap.ExplorationCoverage || 0);
  document.getElementById('coverageText').innerText = cov + '%';
  document.getElementById('coverageBar').style.width = cov + '%';

  // Player 1 HP / Mana & ES (Smart Dual Bar)
  const hp = snap.PlayerHpPercent !== undefined ? snap.PlayerHpPercent : 100;
  const es = snap.PlayerEsPercent || 0;
  const esTot = snap.PlayerEsTotal || 0;
  const hpTot = snap.PlayerHpTotal || 0;
  const hpEl = document.getElementById('hpText');
  const hpBar = document.getElementById('hpBar');
  const p1EsWrap = document.getElementById('p1EsWrap');
  const esBar = document.getElementById('esBar');
  const p1HpWrap = document.getElementById('p1HpWrap');

  if (hpBar) hpBar.style.width = hp + '%';

  if (p1EsWrap && esBar) {
    if (esTot > 0) {
      p1EsWrap.style.display = 'block';
      if (p1HpWrap) p1HpWrap.style.marginTop = '4px';
      esBar.style.width = Math.min(100, Math.max(0, es)) + '%';
      if (hpTot <= 1) {
        // Chaos Inoculation (CI)
        if (hpEl) hpEl.innerHTML = `<span style="color:#38bdf8; font-weight:700;">ES ${es}%</span> <span style="font-size:10px; color:var(--text-dim);">(CI)</span>`;
      } else {
        // Hybrid HP + ES
        if (hpEl) hpEl.innerHTML = `${hp}% <span style="color:var(--border);">|</span> <span style="color:#38bdf8; font-weight:600;">ES ${es}%</span>`;
      }
    } else {
      // Pure Life
      p1EsWrap.style.display = 'none';
      if (p1HpWrap) p1HpWrap.style.marginTop = '8px';
      if (hpEl) hpEl.innerText = hp + '%';
    }
    if (hpEl && hpTot > 0) {
      hpEl.title = esTot > 0 ? `HP: ${snap.PlayerHpCurrent}/${hpTot} | ES: ${snap.PlayerEsCurrent}/${esTot}` : `HP: ${snap.PlayerHpCurrent}/${hpTot}`;
    }
  } else if (hpEl) {
    hpEl.innerText = hp + '%';
  }

  const mana = snap.PlayerManaPercent || 100;
  document.getElementById('manaText').innerText = mana + '%';
  document.getElementById('manaBar').style.width = mana + '%';

  // Player 2 HP / Mana & ES (Co-op Smart Dual Bar)
  if (snap.FollowerHpPercent !== undefined) {
    const fHp = snap.FollowerHpPercent;
    const fEs = snap.FollowerEsPercent || 0;
    const fEsTot = snap.FollowerEsTotal || 0;
    const fHpTot = snap.FollowerHpTotal || 0;
    const p2HpEl = document.getElementById('p2HpText');
    const p2HpBar = document.getElementById('p2HpBar');
    const p2EsWrap = document.getElementById('p2EsWrap');
    const p2EsBar = document.getElementById('p2EsBar');
    const p2HpWrap = document.getElementById('p2HpWrap');
    const p2ManaEl = document.getElementById('p2ManaText');
    const p2ManaBar = document.getElementById('p2ManaBar');

    if (p2HpBar) p2HpBar.style.width = fHp + '%';

    if (p2EsWrap && p2EsBar) {
      if (fEsTot > 0) {
        p2EsWrap.style.display = 'block';
        if (p2HpWrap) p2HpWrap.style.marginTop = '4px';
        p2EsBar.style.width = Math.min(100, Math.max(0, fEs)) + '%';
        if (fHpTot <= 1) {
          // CI
          if (p2HpEl) p2HpEl.innerHTML = `<span style="color:#38bdf8; font-weight:700;">ES ${fEs}%</span> <span style="font-size:10px; color:var(--text-dim);">(CI)</span>`;
        } else {
          // Hybrid
          if (p2HpEl) p2HpEl.innerHTML = `${fHp}% <span style="color:var(--border);">|</span> <span style="color:#38bdf8; font-weight:600;">ES ${fEs}%</span>`;
        }
      } else {
        // Pure Life
        p2EsWrap.style.display = 'none';
        if (p2HpWrap) p2HpWrap.style.marginTop = '8px';
        if (p2HpEl) p2HpEl.innerText = fHp + '%';
      }
      if (p2HpEl && fHpTot > 0) {
        p2HpEl.title = fEsTot > 0 ? `HP: ${snap.FollowerHpCurrent}/${fHpTot} | ES: ${snap.FollowerEsCurrent}/${fEsTot}` : `HP: ${snap.FollowerHpCurrent}/${fHpTot}`;
      }
    } else if (p2HpEl) {
      p2HpEl.innerText = fHp + '%';
    }

    const fMana = snap.FollowerManaPercent || 100;
    if (p2ManaEl) p2ManaEl.innerText = fMana + '%';
    if (p2ManaBar) p2ManaBar.style.width = fMana + '%';
  }

  // Controller badges
  if (snap.LeaderSlotIndex !== undefined) {
    const p1Badge = document.getElementById('p1PadBadge');
    if (p1Badge) {
      const isConn = snap.LeaderSlotIndex >= 0;
      p1Badge.className = isConn ? 'badge ok' : 'badge err';
      p1Badge.innerText = isConn ? `P1: Virtual Pad #${snap.LeaderSlotIndex} (Leader Passthrough)` : 'P1: Disconnected';
    }
  }
  if (snap.FollowerSlotIndex !== undefined) {
    const p2Badge = document.getElementById('p2PadBadge');
    if (p2Badge) {
      const isConn = snap.FollowerSlotIndex >= 0;
      p2Badge.className = isConn ? 'badge ok' : 'badge err';
      p2Badge.innerText = isConn ? `P2: Virtual Pad #${snap.FollowerSlotIndex} (Follower Bot)` : 'P2: Disconnected';
    }
  }

  // Live Totems & Minions
  if (snap.ActiveTotems !== undefined) liveActiveTotems = snap.ActiveTotems;
  if (snap.ActiveMinions !== undefined) liveActiveMinions = snap.ActiveMinions;
  if (snap.ActiveTotemNames) liveActiveTotemNames = snap.ActiveTotemNames;

  // Active Buffs scan
  if (snap.DetectedBuffs) {
    const buffSig = snap.DetectedBuffs.map(b => b.Name).sort().join(',');
    if (buffSig !== lastBuffsSig) {
      lastBuffsSig = buffSig;
      detectedBuffs = snap.DetectedBuffs;
    }
  }

  // Active Debuffs scan
  if (snap.DetectedDebuffs) {
    const debuffSig = snap.DetectedDebuffs.map(d => d.Name).sort().join(',');
    if (debuffSig !== lastDebuffsSig) {
      lastDebuffsSig = debuffSig;
      detectedDebuffs = snap.DetectedDebuffs;
    }
  }

  updateRunStatus(snap.IsRunning);

  // Update character name badges
  if (snap.LeaderPlayerName && document.getElementById('p1CharNameBadge')) {
    document.getElementById('p1CharNameBadge').innerText = `[${snap.LeaderPlayerName}]`;
  }
  if (snap.FollowerPlayerName && document.getElementById('p2CharNameBadge')) {
    document.getElementById('p2CharNameBadge').innerText = `[${snap.FollowerPlayerName}]`;
  }

  // Update Nearby Players chips for one-click follower locking
  if (snap.NearbyPlayers || snap.NearbyPlayerNames) {
    populatePlayerOptions(snap.NearbyPlayers, snap.NearbyPlayerNames);
  }

  if (snap.NearbyPlayerNames && document.getElementById('nearbyPlayerChips')) {
    const chipBox = document.getElementById('nearbyPlayerChips');
    if (snap.NearbyPlayerNames.length === 0) {
      chipBox.innerHTML = '<span style="font-size:11px; color:var(--text-dim); font-style:italic;">ไม่พบผู้เล่นอื่นในขณะนี้</span>';
    } else {
      const curLock = (document.getElementById('FollowerCharacterName')?.value.trim().toLowerCase() || '');
      chipBox.innerHTML = snap.NearbyPlayerNames.map(name => {
        const isCur = (curLock === name.toLowerCase());
        const bg = isCur ? 'rgba(16,185,129,0.2)' : 'rgba(56,189,248,0.15)';
        const col = isCur ? '#10b981' : '#38bdf8';
        const border = isCur ? '#10b981' : 'rgba(56,189,248,0.4)';
        return `<button type="button" class="skill-chip" style="background:${bg}; color:${col}; border:1px solid ${border}; font-size:11px;" onclick="selectFollowerName('${name}')">👤 ${name}</button>`;
      }).join('');
    }
  }

  // Update detected skills ONLY when changed to prevent wiping DOM during user clicks
  let skillsChanged = false;
  if (snap.DetectedSkills) {
    const newSig1 = snap.DetectedSkills.map(s => s.Name).sort().join(',');
    if (newSig1 !== lastDetectedSig || !hasRenderedChips) {
      lastDetectedSig = newSig1;
      p1DetectedSkills = snap.DetectedSkills;
      detectedSkills = snap.DetectedSkills;
      skillsChanged = true;
    }
  }
  if (snap.P2DetectedSkills) {
    const newSig2 = snap.P2DetectedSkills.map(s => s.Name).sort().join(',');
    if (newSig2 !== lastP2DetectedSig) {
      lastP2DetectedSig = newSig2;
      p2DetectedSkills = snap.P2DetectedSkills;
      skillsChanged = true;
    }
  }
  if (skillsChanged || !hasRenderedChips) {
    hasRenderedChips = true;
    renderDetectedSkillsChips();
  }
}

let ws = null;
function connectWebSocket() {
  const protocol = window.location.protocol === 'https:' ? 'wss:' : 'ws:';
  const wsUrl = `${protocol}//${window.location.host}/ws`;
  try {
    ws = new WebSocket(wsUrl);
    ws.onopen = () => {
      const b = document.getElementById('connBadge');
      if (b) { b.className = 'badge ok'; b.innerText = 'CONNECTED'; }
    };
    ws.onmessage = (evt) => {
      try {
        const snap = JSON.parse(evt.data);
        updateDashboardSnapshot(snap);
      } catch(e) {}
    };
    ws.onclose = () => {
      const b = document.getElementById('connBadge');
      if (b) { b.className = 'badge err'; b.innerText = 'DISCONNECTED'; }
      setTimeout(connectWebSocket, 2000);
    };
    ws.onerror = () => ws.close();
  } catch(e) {
    setTimeout(connectWebSocket, 2000);
  }
}

async function loadDumps() {
  try {
    const res = await fetch('/api/dumps');
    if (!res.ok) return;
    const data = await res.json();
    const list = document.getElementById('dumpsList');
    if (!list) return;
    if (!data.dumps || data.dumps.length === 0) {
      list.innerHTML = '<div style="color:var(--text-dim); font-size:12px;">No dumps recorded yet. Press F6 or click Capture Debug Dump.</div>';
      return;
    }
    list.innerHTML = data.dumps.map(d => `
      <div style="display:flex; justify-content:space-between; align-items:center; padding:8px 0; border-bottom:1px solid var(--border);">
        <span style="font-size:12px; font-weight:600;">${esc(d.name)}</span>
        <a href="${esc(d.url)}" target="_blank" class="btn secondary" style="font-size:11px; padding:3px 8px;">View / Download</a>
      </div>
    `).join('');
  } catch(e) {}
}

async function dumpState() {
  try {
    const res = await fetch('/api/dump', { method: 'POST' });
    const data = await res.json();
    showToast(data.message || "Dump captured!");
    loadDumps();
  } catch(e) {
    alert("Dump error: " + e.message);
  }
}

window.onload = async () => {
  await loadMetadata();
  await loadProfiles();
  await loadSettings();
  connectWebSocket();
  setInterval(async () => {
    try {
      const res = await fetch('/api/status');
      const snap = await res.json();
      updateDashboardSnapshot(snap);
    } catch(e) {}
  }, 1500);
};
