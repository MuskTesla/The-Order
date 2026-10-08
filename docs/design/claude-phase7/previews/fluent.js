// Revision 2 preview kit: Fluent-compatible tokens + Windows-accent EXAMPLES. CONCEPT MOCKUP ONLY.
// Accent values here are approximate examples of how Windows derives theme variants; the product reads
// the real values from WinUI theme resources (AccentFillColorDefault, AccentTextFillColorPrimary, ...).
'use strict';
const ACCENTS = {
  blue:   { name: '蓝色（Windows 默认，示例）', lightFill: '#005FB8', lightText: '#003E92', darkFill: '#60CDFF', darkText: '#99EBFF' },
  purple: { name: '紫色（示例）',              lightFill: '#6C46A3', lightText: '#50327F', darkFill: '#C0A6E8', darkText: '#D9C9F3' },
  orange: { name: '橙色（示例）',              lightFill: '#A13F0D', lightText: '#7A2F09', darkFill: '#F4A06D', darkText: '#F9C5A3' },
};
function fluentCSS(theme, accentKey) {
  const a = ACCENTS[accentKey] || ACCENTS.blue, L = theme !== 'dark';
  return `
:root {
 --mica:${L ? '#F3F3F3' : '#202020'}; --layer:${L ? '#F9F9F9' : '#272727'}; --card:${L ? '#FFFFFF' : '#2B2B2B'}; --card2:${L ? '#F6F6F6' : '#323232'};
 --stroke:${L ? 'rgba(0,0,0,.075)' : 'rgba(255,255,255,.08)'}; --divider:${L ? 'rgba(0,0,0,.08)' : 'rgba(255,255,255,.084)'};
 --ctl:${L ? '#FFFFFF' : '#2D2D2D'}; --ctlStroke:${L ? 'rgba(0,0,0,.06)' : 'rgba(255,255,255,.07)'}; --ctlBottom:${L ? 'rgba(0,0,0,.16)' : 'rgba(255,255,255,.1)'};
 --subtle:${L ? 'rgba(0,0,0,.037)' : 'rgba(255,255,255,.061)'}; --subtle2:${L ? 'rgba(0,0,0,.024)' : 'rgba(255,255,255,.042)'};
 --text:${L ? '#1A1A1A' : '#FFFFFF'}; --text2:${L ? '#5D5D5D' : '#C5C5C5'}; --text3:${L ? '#8A8A8A' : '#9A9A9A'};
 --accent:${L ? a.lightFill : a.darkFill}; --accentText:${L ? a.lightText : a.darkText}; --onAccent:${L ? '#FFFFFF' : '#000000'};
 --success:${L ? '#0F7B0F' : '#6CCB5F'}; --caution:${L ? '#9D5D00' : '#FCE100'}; --critical:${L ? '#C42B1C' : '#FF99A4'}; --neutral:${L ? '#8A8A8A' : '#9A9A9A'};
 --focusOuter:${L ? '#000000' : '#FFFFFF'}; --focusInner:${L ? '#FFFFFF' : '#000000'};
 --shadow:${L ? '0 2px 4px rgba(0,0,0,.04),0 8px 16px rgba(0,0,0,.08)' : '0 2px 4px rgba(0,0,0,.26),0 8px 16px rgba(0,0,0,.26)'};
 --acrylic:${L ? 'rgba(252,252,252,.85)' : 'rgba(44,44,44,.88)'};
}
* { box-sizing:border-box; margin:0; padding:0; }
html,body { width:100%; height:100%; }
body { font-family:'Segoe UI Variable Text','Segoe UI','Microsoft YaHei UI','Yu Gothic UI',sans-serif; font-size:14px; line-height:20px; color:var(--text); background:var(--mica); overflow:hidden; }
.ja { font-family:'Segoe UI Variable Text','Segoe UI','Yu Gothic UI','Microsoft YaHei UI',sans-serif; }
svg { display:block; width:100%; height:100%; }
button { font:inherit; color:inherit; }
.btn { height:32px; padding:0 12px; border-radius:4px; border:1px solid var(--ctlStroke); border-bottom-color:var(--ctlBottom); background:var(--ctl); display:inline-flex; align-items:center; gap:8px; white-space:nowrap; font-size:14px; }
.btn.accent { background:var(--accent); color:var(--onAccent); border-color:transparent; }
.btn.subtle { background:transparent; border-color:transparent; }
.btn.icon { width:32px; padding:0; justify-content:center; }
.btn.lg { height:36px; padding:0 16px; }
.btn.on { background:var(--subtle); }
.caption { font-size:12px; line-height:16px; color:var(--text2); }
.title { font-family:'Segoe UI Variable Display','Segoe UI',sans-serif; font-size:28px; line-height:36px; font-weight:600; }
.subtitle { font-size:20px; line-height:28px; font-weight:600; }
.dot { width:8px; height:8px; border-radius:50%; display:inline-block; flex:none; }
.clamp2 { display:-webkit-box; -webkit-line-clamp:2; -webkit-box-orient:vertical; overflow:hidden; }
.ellipsis { white-space:nowrap; overflow:hidden; text-overflow:ellipsis; }
.focus { outline:2px solid var(--focusOuter); outline-offset:1px; box-shadow:0 0 0 1px var(--focusInner) inset; }
.label-tag { position:fixed; right:10px; bottom:8px; font:11px 'Segoe UI'; color:var(--text3); background:var(--card); border:1px solid var(--stroke); padding:2px 8px; border-radius:10px; z-index:99; }
`;
}
const STATUS2 = { playing: ['在玩', 'var(--accent)'], completed: ['已完成', 'var(--success)'], paused: ['搁置', 'var(--caution)'], dropped: ['放弃', 'var(--neutral)'], planned: ['未开始', null] };
const status2 = s => { const [l, c] = STATUS2[s]; return `<span style="display:inline-flex;align-items:center;gap:6px">${c ? `<span class="dot" style="background:${c}"></span>` : '<span class="dot" style="border:1.5px solid var(--text3)"></span>'}${l}</span>`; };
