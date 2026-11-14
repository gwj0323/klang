// Klang · 汽水原生组件桥（隐藏 Node 子进程）
// 约束：只走 stdin/stdout 单行 JSON；不接受命令行参数里的凭据；不写日志。
// 依赖：官方 3.7.0 的 device.node / bdms.node / metasecml.dll（由调用方给出绝对路径）。
import { createRequire } from 'node:module';
import { readFileSync, writeFileSync } from 'node:fs';
import { gzipSync } from 'node:zlib';
import https from 'node:https';
import crypto from 'node:crypto';

const require = createRequire(import.meta.url);
const MAX = 128 * 1024;

function reply(ok, data, error) {
  process.stdout.write(JSON.stringify(ok ? { ok: true, data } : { ok: false, error: String(error || 'unknown') }) + '\n');
}

function post(url, headers, body, timeoutMs = 8000) {
  return new Promise((resolve, reject) => {
    const req = https.request(url, { method: 'POST', headers }, (res) => {
      const chunks = [];
      res.on('data', (c) => { chunks.push(c); if (Buffer.concat(chunks).length > MAX) req.destroy(new Error('too large')); });
      res.on('end', () => resolve({ status: res.statusCode, text: Buffer.concat(chunks).toString('utf8') }));
    });
    req.setTimeout(timeoutMs, () => req.destroy(new Error('timeout')));
    req.on('error', reject);
    req.end(body);
  });
}

function load(modPath) {
  if (!modPath) throw new Error('component path missing');
  return require(modPath);
}

function decorate(deviceNode, payload) {
  const mod = load(deviceNode);
  const fn = mod?.applogDecorated ?? mod?.default?.applogDecorated ?? mod?.decorate;
  if (typeof fn !== 'function') throw new Error('device.node 未暴露 applogDecorated');
  return fn(payload);
}

async function register(p) {
  const cdid = p.deviceId || crypto.randomUUID();
  const serial = (() => { try { return require('node:os').hostname(); } catch { return 'klang'; } })();
  const payload = {
    header: {
      device_id: '', install_id: '', os: 'Windows', device_platform: 'PC',
      sdk_version: '1.0', sdk_version_code: 1, aid: 386088, mc: '00:00:00:00:00:00',
      channel: 'official', package: 'com.soda.music', language: 'zh-CN',
      app_version: '3.7.0', os_version: require('node:os').release(), device_model: 'no detected',
      time_zone: Intl.DateTimeFormat().resolvedOptions().timeZone,
      tz_name: Intl.DateTimeFormat().resolvedOptions().timeZone, tz_offset: 0,
      resolution: 'no detected', app_region: 'cn', app_language: 'zh-CN',
      display_name: '汽水音乐', pc_uuid: cdid, pc_serial: serial,
    },
    gen_time: Date.now(), magic_tag: 'ss_app_log',
  };
  const body = decorate(p.deviceNode, gzipSync(Buffer.from(JSON.stringify(payload))));
  const query = new URLSearchParams({ aid: '386088', channel: 'official', os: 'Windows', device_platform: 'PC', app_version: '3.7.0' });
  const res = await post(`https://api.qishui.com/service/2/desktop/device_register/?${query}`, { 'content-type': 'application/octet-stream' }, body);
  let json = {};
  try { json = JSON.parse(res.text); } catch { throw new Error('device_register 返回非 JSON'); }
  if (!json.device_id_str) throw new Error(`device_register 未返回 device_id（device_id=${json.device_id ?? '?'}）`);
  return { device_id: String(json.device_id_str), install_id: String(json.install_id_str || '') };
}

function sign(p) {
  const mod = load(p.bdmsNode);
  if (typeof mod?.init === 'function' && p.deviceId) mod.init({ deviceId: String(p.deviceId) });
  const fn = mod?.generateHttpSignatureHeaders ?? mod?.default?.generateHttpSignatureHeaders;
  if (typeof fn !== 'function') throw new Error('bdms.node 未暴露 generateHttpSignatureHeaders');
  const raw = fn(p.url, p.headerLines);
  const lines = String(raw || '').split('\r\n').filter(Boolean);
  const headers = {};
  for (let i = 0; i + 1 < lines.length; i += 2) headers[lines[i]] = lines[i + 1];
  return { headers };
}

function decodeFile(p) {
  const decryptor = require(p.decryptorPath || './ThirdParty/qishui-api/audioDecryptor.js');
  const input = readFileSync(p.inPath);
  const hexKey = p.hexKey ? String(p.hexKey) : decryptor.resolveAudioKey({ spade_a: p.spade });
  const result = decryptor.decryptAudioBuffer(input, hexKey);
  writeFileSync(p.outPath, result.buffer);
  return { bytes: result.buffer.length, codec: result.codec, sample_count: result.sample_count, hex_key: hexKey ? 'ok' : '' };
}

(async () => {
  try {
    const chunks = [];
    for await (const chunk of process.stdin) { chunks.push(chunk); if (Buffer.concat(chunks).length > MAX) throw new Error('input too large'); }
    const req = JSON.parse(Buffer.concat(chunks).toString('utf8') || '{}');
    const p = req.payload || {};
    let data;
    if (req.command === 'register') data = await register(p);
    else if (req.command === 'sign') data = sign(p);
    else if (req.command === 'decode-file') data = decodeFile(p);
    else throw new Error(`unknown command ${req.command}`);
    reply(true, data);
  } catch (e) {
    reply(false, null, e?.message || e);
  }
})();
