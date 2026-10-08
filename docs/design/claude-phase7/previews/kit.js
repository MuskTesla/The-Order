// The Order — design preview kit. CONCEPT MOCKUP ONLY: browser HTML/SVG, not WinUI runtime.
// Covers are procedurally generated originals (no third-party artwork).
'use strict';

const GAMES = [
  { t: '星海の約束', o: 'Hoshiumi no Yakusoku', s: 'playing', fav: true, r: 9, h: 42.5, last: '今天 21:40', inst: 2, tags: ['纯爱', '科幻'], dev: 'Synthetic Studio', p: 0 },
  { t: '夏日回信', o: '', s: 'completed', fav: false, r: 8.5, h: 18.2, last: '9 月 28 日', inst: 1, tags: ['日常', '书信'], dev: 'Paper Lantern', p: 1 },
  { t: 'The Last Train Beyond the Horizon — Director’s Cut', o: '地平线尽头的末班车', s: 'planned', fav: false, r: null, h: 0, last: '—', inst: 1, tags: ['悬疑'], dev: 'Northbound Works', p: 2 },
  { t: '雨音と、君のいない朝に。もう一度この街で出会えたなら', o: '雨声与没有你的清晨', s: 'paused', fav: false, r: 7, h: 9.6, last: '8 月 3 日', inst: 1, tags: ['群像', '雨'], dev: 'Amaoto', p: 3 },
  { t: '玻璃窗后的四季：写给未来的十二封信', o: '', s: 'completed', fav: true, r: 9.5, h: 31, last: '7 月 19 日', inst: 1, tags: ['书信', '治愈'], dev: 'Paper Lantern', p: 4 },
  { t: 'Promise of Stars — A Story Across Seasons', o: '星之约定', s: 'dropped', fav: false, r: 4, h: 2.3, last: '6 月 1 日', inst: 1, tags: ['奇幻'], dev: 'Synthetic Studio', p: 5 },
  { t: '白昼夢の標本室', o: '', s: 'playing', fav: false, r: null, h: 5.1, last: '昨天', inst: 1, tags: ['幻想', '短篇'], dev: 'Kotodama', p: 6 },
  { t: '灯塔与鲸鱼之歌', o: 'Song of the Lighthouse and the Whale', s: 'planned', fav: true, r: null, h: 0, last: '—', inst: 0, tags: ['海', '愿望单'], dev: 'Tidewater', p: 7 },
  { t: 'Lantern Night 花灯之夜', o: '', s: 'completed', fav: false, r: 8, h: 12.4, last: '5 月 2 日', inst: 1, tags: ['节日'], dev: 'Paper Lantern', p: 8 },
  { t: 'ゆめみる機械のための三つのワルツ', o: 'Three Waltzes for a Dreaming Machine', s: 'planned', fav: false, r: null, h: 0, last: '—', inst: 1, tags: ['SF'], dev: 'Kotodama', p: 9 },
  { t: '北纬五十度的告白', o: '', s: 'paused', fav: false, r: 6.5, h: 4.4, last: '4 月 11 日', inst: 1, tags: ['冬', '恋爱'], dev: 'Northbound Works', p: 0 },
  { t: 'Afterglow Overture', o: '余晖序曲', s: 'planned', fav: false, r: null, h: 0, last: '—', inst: 0, tags: ['音乐'], dev: 'Tidewater', p: 1 },
  { t: '月下の図書館と最後の司書', o: '', s: 'completed', fav: true, r: 9, h: 26, last: '3 月 9 日', inst: 1, tags: ['图书馆'], dev: 'Amaoto', p: 2 },
  { t: '纸飞机降落在第七站台', o: '', s: 'planned', fav: false, r: null, h: 0, last: '—', inst: 1, tags: ['青春'], dev: 'Synthetic Studio', p: 3 },
  { t: 'Snowglobe Hearts', o: '雪景球之心', s: 'completed', fav: false, r: 7.5, h: 14, last: '2 月 14 日', inst: 1, tags: ['冬'], dev: 'Tidewater', p: 4 },
  { t: 'きみと見た最後の流星群', o: '', s: 'playing', fav: false, r: null, h: 1.2, last: '10 月 5 日', inst: 1, tags: ['星空'], dev: 'Kotodama', p: 5 },
  { t: '旧城区的猫与咖啡馆', o: '', s: 'planned', fav: false, r: null, h: 0, last: '—', inst: 1, tags: ['日常'], dev: 'Paper Lantern', p: 6 },
  { t: 'Clockwork Garden', o: '发条庭园', s: 'paused', fav: false, r: 6, h: 3, last: '1 月 3 日', inst: 1, tags: ['蒸汽'], dev: 'Northbound Works', p: 7 },
];

const STATUS = {
  playing: { label: '在玩', tone: 'accent' },
  completed: { label: '已完成', tone: 'success' },
  paused: { label: '搁置', tone: 'warning' },
  dropped: { label: '放弃', tone: 'muted' },
  planned: { label: '未开始', tone: 'outline' },
};

const PALETTES = [
  ['#1f3b5c', '#e6a87c', '#f4e3c3', '#2b2140'], ['#3d6b5e', '#f1c58f', '#fff3dd', '#1d2f2a'],
  ['#232946', '#b8c1ec', '#eebbc3', '#121629'], ['#5a6f8c', '#c9d6df', '#f7f3e9', '#2e3a4a'],
  ['#7b4b6a', '#f3b391', '#fde8d7', '#3a2233'], ['#2f2a4a', '#ff9e7a', '#ffd6a5', '#16132a'],
  ['#4a7c8c', '#f6e7c1', '#ffffff', '#20343b'], ['#0f3d56', '#5fb3c7', '#e8f6f7', '#08202d'],
  ['#8c3b2f', '#f2b94b', '#fbe7b5', '#3b1712'], ['#3a3f58', '#a8a4ce', '#f0e6ff', '#1c1e2c'],
];

function rng(seed) { let x = seed * 9301 + 49297; return () => ((x = (x * 9301 + 49297) % 233280) / 233280); }

// Original painterly scene: sky gradient, celestial body, layered silhouettes, light grain lines.
function coverSVG(i, label) {
  const g = GAMES[i % GAMES.length], [deep, warm, light, ink] = PALETTES[(g.p + i) % PALETTES.length];
  const r = rng(i + 3), id = 'c' + i + Math.floor(r() * 1e6);
  const sunX = 70 + r() * 160, sunY = 90 + r() * 90, sunR = 26 + r() * 30;
  const ridge = (base, amp, color, op) => {
    let d = `M0 ${base}`; for (let x = 0; x <= 300; x += 30) d += ` L${x} ${base - amp * (0.3 + r())}`;
    return `<path d="${d} L300 400 L0 400 Z" fill="${color}" opacity="${op}"/>`;
  };
  const kind = i % 3;
  let fg = '';
  if (kind === 0) fg = ridge(280, 70, deep, .75) + ridge(320, 50, ink, .9);
  else if (kind === 1) { for (let k = 0; k < 9; k++) { const w = 18 + r() * 26, h = 50 + r() * 120, x = k * 34 - 6; fg += `<rect x="${x}" y="${330 - h}" width="${w}" height="${h + 80}" fill="${ink}" opacity="${.65 + r() * .3}"/>`; } fg += `<rect y="320" width="300" height="80" fill="${deep}" opacity=".9"/>`; }
  else { for (let k = 0; k < 5; k++) fg += `<path d="M0 ${300 + k * 18} Q 75 ${285 + k * 18 - r() * 20} 150 ${300 + k * 18} T 300 ${300 + k * 18} L300 400 L0 400Z" fill="${k % 2 ? deep : ink}" opacity="${.35 + k * .12}"/>`; }
  const stars = Array.from({ length: 14 }, () => `<circle cx="${r() * 300}" cy="${r() * 160}" r="${r() * 1.4 + .3}" fill="${light}" opacity="${.4 + r() * .5}"/>`).join('');
  const title = (label ?? g.t).slice(0, 14);
  return `<svg viewBox="0 0 300 400" preserveAspectRatio="xMidYMid slice" xmlns="http://www.w3.org/2000/svg">
<defs><linearGradient id="${id}" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="${deep}"/><stop offset=".62" stop-color="${warm}"/><stop offset="1" stop-color="${light}"/></linearGradient>
<radialGradient id="${id}s"><stop offset="0" stop-color="${light}"/><stop offset="1" stop-color="${light}" stop-opacity="0"/></radialGradient></defs>
<rect width="300" height="400" fill="url(#${id})"/>${stars}
<circle cx="${sunX}" cy="${sunY}" r="${sunR * 2.4}" fill="url(#${id}s)" opacity=".45"/><circle cx="${sunX}" cy="${sunY}" r="${sunR}" fill="${light}" opacity=".92"/>
${fg}<text x="20" y="44" font-family="Georgia,'Yu Mincho',serif" font-size="19" fill="${light}" opacity=".92">${title}</text>
<text x="20" y="382" font-family="Segoe UI" font-size="9" letter-spacing="2" fill="${light}" opacity=".55">THE ORDER · TEST ${String(i).padStart(3, '0')}</text></svg>`;
}

const fmtH = h => h === 0 ? '—' : h < 1 ? `${Math.round(h * 60)} 分钟` : `${Math.floor(h)} 小时 ${Math.round((h % 1) * 60)} 分`;
const rating = r => r == null ? '未评分' : `${r} 分`;
const esc = s => String(s).replace(/&/g, '&amp;').replace(/</g, '&lt;');

function themeCSS() {
  return `
:root, [data-theme=light] {
 --canvas:#F2F3EF; --mica:#E9ECE6; --content:#FAFBF8; --raised:#FFFFFF; --text:#1F2A28; --text2:#4E5E5A; --text3:#6B7A76;
 --stroke:#D9E1DC; --stroke2:#E6ECE8; --accent:#2F6F63; --accent-fg:#FFFFFF; --accent-soft:rgba(47,111,99,.12);
 --success:#256448; --warning:#805300; --muted:#6B7A76; --focus:#1F2A28; --shadow:0 1px 2px rgba(20,30,28,.08),0 6px 18px rgba(20,30,28,.08);
 --acrylic:rgba(250,252,246,.80); --scrim:rgba(242,243,239,.0);
}
[data-theme=dark] {
 --canvas:#1B2324; --mica:#151C1D; --content:#222B2C; --raised:#2A3435; --text:#E5EBE7; --text2:#B4C2BC; --text3:#8FA19B;
 --stroke:#364442; --stroke2:#2D3938; --accent:#7CC4B2; --accent-fg:#0E1C19; --accent-soft:rgba(124,196,178,.16);
 --success:#9BD1AF; --warning:#F0C77B; --muted:#8FA19B; --focus:#FFFFFF; --shadow:0 1px 2px rgba(0,0,0,.3),0 8px 24px rgba(0,0,0,.28);
 --acrylic:rgba(40,51,50,.82);
}
* { box-sizing:border-box; margin:0; padding:0; }
html,body { width:100%; height:100%; }
body { font-family:'Segoe UI Variable Text','Segoe UI','Microsoft YaHei UI','Yu Gothic UI',sans-serif; font-size:14px; line-height:20px; color:var(--text); background:var(--mica); overflow:hidden; -webkit-font-smoothing:antialiased; }
.ja { font-family:'Segoe UI Variable Text','Segoe UI','Yu Gothic UI','Microsoft YaHei UI',sans-serif; }
svg { display:block; width:100%; height:100%; }
button { font:inherit; color:inherit; }
.btn { height:32px; padding:0 12px; border-radius:4px; border:1px solid var(--stroke); background:var(--raised); display:inline-flex; align-items:center; gap:8px; font-size:14px; white-space:nowrap; }
.btn.accent { background:var(--accent); color:var(--accent-fg); border-color:transparent; font-weight:600; }
.btn.ghost { background:transparent; border-color:transparent; }
.btn.icon { width:32px; padding:0; justify-content:center; }
.btn.lg { height:40px; padding:0 18px; font-size:15px; }
.dot { width:8px; height:8px; border-radius:50%; display:inline-block; flex:none; }
.tone-accent { background:var(--accent); } .tone-success { background:var(--success); } .tone-warning { background:var(--warning); }
.tone-muted { background:var(--muted); } .tone-outline { border:1.5px solid var(--text3); background:transparent; }
.caption { font-size:12px; line-height:16px; color:var(--text2); }
.label-tag { position:fixed; right:10px; bottom:8px; font:11px 'Segoe UI'; color:var(--text3); background:var(--content); border:1px solid var(--stroke); padding:2px 8px; border-radius:10px; z-index:99; }
.clamp2 { display:-webkit-box; -webkit-line-clamp:2; -webkit-box-orient:vertical; overflow:hidden; }
.ellipsis { white-space:nowrap; overflow:hidden; text-overflow:ellipsis; }
.focus-ring { outline:2px solid var(--focus); outline-offset:2px; box-shadow:0 0 0 4px var(--content); }
/* C1-style glass approximation for mockups: clear refracting rim + frosted body (see 03 doc). */
.glass { position:relative; border-radius:20px; background:rgba(255,255,255,.06); backdrop-filter:blur(2px) saturate(1.4); }
.glass::before { content:''; position:absolute; inset:0; border-radius:inherit; padding:14px; background:linear-gradient(150deg,rgba(255,255,255,.75),rgba(255,255,255,.08) 30%,rgba(255,255,255,0) 60%,rgba(255,255,255,.35));
 -webkit-mask:linear-gradient(#000 0 0) content-box,linear-gradient(#000 0 0); -webkit-mask-composite:xor; mask-composite:exclude; opacity:.55; pointer-events:none; }
.glass::after { content:''; position:absolute; inset:0; border-radius:inherit; border:1px solid rgba(255,255,255,.55); box-shadow:inset 0 1px 0 rgba(255,255,255,.7), inset 0 -10px 18px rgba(0,0,0,.10), 0 10px 30px rgba(0,0,0,.25); pointer-events:none; }
.glass .body { position:absolute; inset:14px; border-radius:8px; backdrop-filter:blur(18px) saturate(1.2); }
[data-theme=light] .glass .body { background:rgba(250,251,248,.78); }
[data-theme=dark] .glass .body { background:rgba(27,35,36,.80); }
.glass .inner { position:relative; z-index:2; }
`;
}

function statusChip(s) { const st = STATUS[s]; return `<span style="display:inline-flex;align-items:center;gap:6px"><span class="dot tone-${st.tone}"></span>${st.label}</span>`; }
const isJa = t => /[぀-ヿ]/.test(t);
const ICON = {
  library: '<path d="M4 4h3v16H4zM9 4h3v16H9zM14.5 4.5l2.9-.8 4.1 15.4-2.9.8z" fill="none" stroke="currentColor" stroke-width="1.5"/>',
  globe: '<circle cx="12" cy="12" r="8.5" fill="none" stroke="currentColor" stroke-width="1.5"/><path d="M3.5 12h17M12 3.5c3 3.2 3 13.8 0 17M12 3.5c-3 3.2-3 13.8 0 17" fill="none" stroke="currentColor" stroke-width="1.5"/>',
  scan: '<circle cx="10.5" cy="10.5" r="6" fill="none" stroke="currentColor" stroke-width="1.5"/><path d="M15 15l5 5" stroke="currentColor" stroke-width="1.5"/>',
  gear: '<circle cx="12" cy="12" r="3" fill="none" stroke="currentColor" stroke-width="1.5"/><path d="M12 3v3M12 18v3M3 12h3M18 12h3M5.6 5.6l2.1 2.1M16.3 16.3l2.1 2.1M5.6 18.4l2.1-2.1M16.3 7.7l2.1-2.1" stroke="currentColor" stroke-width="1.5"/>',
  power: '<path d="M12 3v8" stroke="currentColor" stroke-width="1.5"/><path d="M7 6.5a7 7 0 1 0 10 0" fill="none" stroke="currentColor" stroke-width="1.5"/>',
  grid: '<path d="M4 4h7v7H4zM13 4h7v7h-7zM4 13h7v7H4zM13 13h7v7h-7z" fill="none" stroke="currentColor" stroke-width="1.5"/>',
  list: '<path d="M8 6h12M8 12h12M8 18h12M4 6h1M4 12h1M4 18h1" stroke="currentColor" stroke-width="1.6"/>',
  filter: '<path d="M4 5h16l-6 7v6l-4 2v-8z" fill="none" stroke="currentColor" stroke-width="1.5"/>',
  sort: '<path d="M7 4v16M4 17l3 3 3-3M17 20V4M14 7l3-3 3 3" fill="none" stroke="currentColor" stroke-width="1.5"/>',
  more: '<circle cx="6" cy="12" r="1.4" fill="currentColor"/><circle cx="12" cy="12" r="1.4" fill="currentColor"/><circle cx="18" cy="12" r="1.4" fill="currentColor"/>',
  play: '<path d="M8 5l11 7-11 7z" fill="currentColor"/>',
  pause: '<path d="M7 5h3v14H7zM14 5h3v14h-3z" fill="currentColor"/>',
  heart: '<path d="M12 20s-7-4.4-7-10a4 4 0 0 1 7-2.6A4 4 0 0 1 19 10c0 5.6-7 10-7 10z" fill="currentColor"/>',
  heartO: '<path d="M12 20s-7-4.4-7-10a4 4 0 0 1 7-2.6A4 4 0 0 1 19 10c0 5.6-7 10-7 10z" fill="none" stroke="currentColor" stroke-width="1.5"/>',
  plus: '<path d="M12 5v14M5 12h14" stroke="currentColor" stroke-width="1.6"/>',
  check: '<path d="M5 12.5l4.5 4.5L19 7.5" fill="none" stroke="currentColor" stroke-width="1.8"/>',
  select: '<rect x="4" y="4" width="16" height="16" rx="3" fill="none" stroke="currentColor" stroke-width="1.5"/><path d="M8 12l3 3 5-6" fill="none" stroke="currentColor" stroke-width="1.5"/>',
  pane: '<rect x="3.5" y="4.5" width="17" height="15" rx="2" fill="none" stroke="currentColor" stroke-width="1.5"/><path d="M14.5 4.5v15" stroke="currentColor" stroke-width="1.5"/>',
  chevron: '<path d="M9 6l6 6-6 6" fill="none" stroke="currentColor" stroke-width="1.6"/>',
  down: '<path d="M6 9l6 6 6-6" fill="none" stroke="currentColor" stroke-width="1.6"/>',
  back: '<path d="M15 6l-6 6 6 6" fill="none" stroke="currentColor" stroke-width="1.6"/>',
  close: '<path d="M6 6l12 12M18 6L6 18" stroke="currentColor" stroke-width="1.5"/>',
  folder: '<path d="M3.5 6.5h6l2 2h9v10h-17z" fill="none" stroke="currentColor" stroke-width="1.5"/>',
  clock: '<circle cx="12" cy="12" r="8.5" fill="none" stroke="currentColor" stroke-width="1.5"/><path d="M12 7v5l3 2" fill="none" stroke="currentColor" stroke-width="1.5"/>',
  star: '<path d="M12 4l2.4 5 5.4.6-4 3.7 1.1 5.4L12 16l-4.9 2.7 1.1-5.4-4-3.7 5.4-.6z" fill="currentColor"/>',
  edit: '<path d="M4 20h4l11-11-4-4L4 16z" fill="none" stroke="currentColor" stroke-width="1.5"/>',
  search: '<circle cx="10.5" cy="10.5" r="6" fill="none" stroke="currentColor" stroke-width="1.5"/><path d="M15 15l5 5" stroke="currentColor" stroke-width="1.5"/>',
  warn: '<path d="M12 4l9 16H3z" fill="none" stroke="currentColor" stroke-width="1.5"/><path d="M12 10v4M12 16.5v.5" stroke="currentColor" stroke-width="1.6"/>',
  info: '<circle cx="12" cy="12" r="8.5" fill="none" stroke="currentColor" stroke-width="1.5"/><path d="M12 11v6M12 7.5v.5" stroke="currentColor" stroke-width="1.6"/>',
};
const icon = (n, s = 16) => `<svg viewBox="0 0 24 24" style="width:${s}px;height:${s}px;display:inline-block;flex:none">${ICON[n]}</svg>`;
const BRAND = `<svg viewBox="0 0 24 24" style="width:20px;height:20px"><rect x="1" y="1" width="22" height="22" rx="5" fill="#E5EBE7"/><g fill="none" stroke="#2F6F63" stroke-width="1.4"><path d="M12 4v16M4 12h16M6.3 6.3l11.4 11.4M17.7 6.3L6.3 17.7"/><circle cx="12" cy="12" r="3.2"/></g></svg>`;

function params() { return Object.fromEntries(new URLSearchParams(location.search)); }
