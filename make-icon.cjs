// Draws the app badge and writes icon/app.ico plus extension/icons/{16,32,48,128}.png.
// Usage: node make-icon.cjs   (no third-party dependencies)
const fs = require('fs');
const path = require('path');
const zlib = require('zlib');

const root = __dirname;
const MASTER = 1024;

const bgTop = [0x2b, 0x3a, 0x5c];
const bgBottom = [0x13, 0x1b, 0x2d];
const metalTop = [0xfb, 0xfc, 0xff];
const metalBottom = [0xd3, 0xdc, 0xed];
const ringColor = [0xf6, 0xf8, 0xfd];
const dotTop = [0xff, 0x64, 0x74];
const dotBottom = [0xe4, 0x2a, 0x45];

function mix(a, b, t) { return [a[0] + (b[0] - a[0]) * t, a[1] + (b[1] - a[1]) * t, a[2] + (b[2] - a[2]) * t]; }

function inRoundedSquare(u, v, inset, size, radius) {
  const lo = inset, hi = inset + size;
  if (u < lo || u > hi || v < lo || v > hi) return false;
  const cx = u < lo + radius ? lo + radius : (u > hi - radius ? hi - radius : u);
  const cy = v < lo + radius ? lo + radius : (v > hi - radius ? hi - radius : v);
  return (u - cx) * (u - cx) + (v - cy) * (v - cy) <= radius * radius;
}

function halfWidth(x) {
  if (x < 0.205 || x > 0.66) return 0;
  if (x < 0.3) return 0.235 * Math.sqrt(Math.max(0, 1 - Math.pow((0.3 - x) / 0.1, 2)));
  if (x < 0.58) return 0.235 + 0.02 * ((x - 0.3) / 0.28);
  return 0.255 + 0.035 * Math.pow((x - 0.58) / 0.08, 1.5);
}

function onBell(u, v) {
  const dx = u - 0.5;
  const w = halfWidth(v);
  if (w > 0 && Math.abs(dx) <= w) return true;
  if (v >= 0.655 && v <= 0.712 && Math.abs(dx) <= 0.3) return true;
  if (Math.pow(u - 0.5, 2) + Math.pow(v - 0.772, 2) <= 0.055 * 0.055) return true;
  return Math.pow(u - 0.5, 2) + Math.pow(v - 0.185, 2) <= 0.037 * 0.037;
}

function paint(u, v) {
  if (!inRoundedSquare(u, v, 0.03, 0.94, 0.22)) return [0, 0, 0, 0];
  const glow = Math.max(0, 1 - Math.hypot(u - 0.5, v - 0.34) / 0.62);
  let c = mix(bgTop, bgBottom, Math.min(1, Math.max(0, (v - 0.03) / 0.94)));
  c = [c[0] + 26 * glow, c[1] + 30 * glow, c[2] + 38 * glow];
  if (onBell(u, v)) c = mix(metalTop, metalBottom, Math.min(1, Math.max(0, (v - 0.15) / 0.64)));
  const badge = Math.hypot(u - 0.812, v - 0.218);
  if (badge <= 0.132) c = ringColor;
  if (badge <= 0.106) c = mix(dotTop, dotBottom, Math.min(1, Math.max(0, (v - 0.115) / 0.21)));
  return [Math.min(255, c[0]), Math.min(255, c[1]), Math.min(255, c[2]), 255];
}

function renderMaster() {
  const buf = Buffer.alloc(MASTER * MASTER * 4);
  for (let y = 0; y < MASTER; y++) {
    for (let x = 0; x < MASTER; x++) {
      const c = paint((x + 0.5) / MASTER, (y + 0.5) / MASTER);
      const o = (y * MASTER + x) * 4;
      buf[o] = c[0]; buf[o + 1] = c[1]; buf[o + 2] = c[2]; buf[o + 3] = c[3];
    }
  }
  return buf;
}

function weights(total, target) {
  const out = [];
  for (let i = 0; i < target; i++) {
    const lo = i * total / target, hi = (i + 1) * total / target;
    let from = Math.floor(lo), to = Math.ceil(hi);
    const list = [];
    for (let k = from; k < to; k++) {
      const a = Math.max(lo, k), b = Math.min(hi, k + 1);
      if (b > a) list.push([k, b - a]);
    }
    out.push(list);
  }
  return out;
}

function resize(pixels, from, to) {
  const rows = weights(from, to), cols = weights(from, to);
  const out = Buffer.alloc(to * to * 4);
  for (let ty = 0; ty < to; ty++) {
    for (let tx = 0; tx < to; tx++) {
      let r = 0, g = 0, b = 0, a = 0, area = 0;
      for (const [sy, wy] of rows[ty]) {
        for (const [sx, wx] of cols[tx]) {
          const w = wy * wx, o = (sy * from + sx) * 4, alpha = pixels[o + 3] / 255;
          a += alpha * w; area += w;
          r += pixels[o] * alpha * w; g += pixels[o + 1] * alpha * w; b += pixels[o + 2] * alpha * w;
        }
      }
      const o = (ty * to + tx) * 4, alpha = a / area;
      out[o + 3] = Math.round(alpha * 255);
      if (alpha > 0) { out[o] = Math.round(r / a); out[o + 1] = Math.round(g / a); out[o + 2] = Math.round(b / a); }
    }
  }
  return out;
}

const crcTable = (() => { const t = new Int32Array(256); for (let i = 0; i < 256; i++) { let c = i; for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1; t[i] = c; } return t; })();
function crc32(buf) { let c = -1; for (let i = 0; i < buf.length; i++) c = crcTable[(c ^ buf[i]) & 0xff] ^ (c >>> 8); return (c ^ -1) >>> 0; }

function png(width, height, rgba) {
  const raw = Buffer.alloc((width * 4 + 1) * height);
  for (let y = 0; y < height; y++) { raw[y * (width * 4 + 1)] = 0; rgba.copy(raw, y * (width * 4 + 1) + 1, y * width * 4, (y + 1) * width * 4); }
  const chunk = (type, data) => {
    const head = Buffer.alloc(4); head.writeUInt32BE(data.length, 0);
    const name = Buffer.from(type, 'ascii');
    const tail = Buffer.alloc(4); tail.writeUInt32BE(crc32(Buffer.concat([name, data])), 0);
    return Buffer.concat([head, name, data, tail]);
  };
  const ihdr = Buffer.alloc(13); ihdr.writeUInt32BE(width, 0); ihdr.writeUInt32BE(height, 4); ihdr[8] = 8; ihdr[9] = 6;
  return Buffer.concat([Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]), chunk('IHDR', ihdr), chunk('IDAT', zlib.deflateSync(raw, { level: 9 })), chunk('IEND', Buffer.alloc(0))]);
}

function dib(size, rgba) {
  const header = Buffer.alloc(40);
  header.writeUInt32LE(40, 0); header.writeInt32LE(size, 4); header.writeInt32LE(size * 2, 8);
  header.writeUInt16LE(1, 12); header.writeUInt16LE(32, 14);
  const pixels = Buffer.alloc(size * size * 4);
  for (let y = 0; y < size; y++) {
    for (let x = 0; x < size; x++) {
      const s = ((size - 1 - y) * size + x) * 4, d = (y * size + x) * 4;
      pixels[d] = rgba[s + 2]; pixels[d + 1] = rgba[s + 1]; pixels[d + 2] = rgba[s]; pixels[d + 3] = rgba[s + 3];
    }
  }
  const mask = Buffer.alloc(size * Math.ceil(size / 32) * 4);
  return Buffer.concat([header, pixels, mask]);
}

function ico(entries) {
  const header = Buffer.alloc(6); header.writeUInt16LE(1, 2); header.writeUInt16LE(entries.length, 4);
  let offset = header.length + 16 * entries.length;
  const directory = Buffer.alloc(16 * entries.length);
  entries.forEach((entry, i) => {
    const at = i * 16;
    directory[at] = entry.size === 256 ? 0 : entry.size;
    directory[at + 1] = entry.size === 256 ? 0 : entry.size;
    directory.writeUInt16LE(1, at + 4);
    directory.writeUInt16LE(entry.bits, at + 6);
    directory.writeUInt32LE(entry.data.length, at + 8);
    directory.writeUInt32LE(offset, at + 12);
    offset += entry.data.length;
  });
  return Buffer.concat([header, directory].concat(entries.map(entry => entry.data)));
}

function write(relative, data) {
  const file = path.join(root, relative);
  fs.mkdirSync(path.dirname(file), { recursive: true });
  fs.writeFileSync(file, data);
  return relative + ' ' + data.length;
}

const master = renderMaster();
const cache = new Map([[MASTER, master]]);
for (const size of [16, 24, 32, 48, 64, 128, 256]) cache.set(size, resize(master, MASTER, size));

const entries = [16, 24, 32, 48, 64].map(size => ({ size, bits: 32, data: dib(size, cache.get(size)) }));
entries.push({ size: 256, bits: 32, data: png(256, 256, cache.get(256)) });

const report = [
  write(path.join('icon', 'app.ico'), ico(entries)),
  write(path.join('icon', 'app-preview.png'), png(256, 256, cache.get(256))),
].concat([16, 32, 48, 128].map(size => write(path.join('extension', 'icons', size + '.png'), png(size, size, cache.get(size)))));
console.log(report.join('\n'));
