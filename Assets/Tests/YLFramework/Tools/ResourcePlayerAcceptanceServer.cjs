'use strict';
// 仅监听自有loopback HTTPS；所有模式和凭据均为非生产验收输入。
const fs = require('node:fs');
const path = require('node:path');
const https = require('node:https');
const crypto = require('node:crypto');
const zlib = require('node:zlib');
const root = path.resolve(process.argv[2]);
const location = JSON.parse(fs.readFileSync(path.join(root, 'carrier-location.json'), 'utf8'));
const owner = JSON.parse(fs.readFileSync(path.join(location.Carrier, 'r810-owner.json'), 'utf8'));
if (owner.Evidence !== root || owner.Purpose !== 'Resource-8.10-owned-carrier') throw new Error('Owned server root mismatch.');
const log = fs.createWriteStream(path.join(root, 'server-requests.jsonl'), { flags: 'a' });
log.write(JSON.stringify({ kind:'ServerStart', utc:new Date().toISOString(), pid:process.pid, endpoint:'https://localhost:18443/' }) + '\n');
let sequence = 0;
const hits = new Map();
const modePath = path.join(root, 'server-mode.json');
if (!fs.existsSync(modePath)) fs.writeFileSync(modePath, JSON.stringify({ mode: 'valid', run: 'initial' }), { flag: 'wx' });
function record(data) { log.write(JSON.stringify({ utc: new Date().toISOString(), ...data }) + '\n'); }
const server = https.createServer({ pfx: fs.readFileSync(path.join(root, 'loopback.pfx')), passphrase: 'r810-loopback-only', minVersion: 'TLSv1.2' }, (req, res) => {
  const policy = JSON.parse(fs.readFileSync(modePath, 'utf8'));
  const mode = policy.mode;
  const number = ++sequence;
  const event = { number, run: policy.run, mode, method: req.method, url: req.url, range: req.headers.range || null, ifRange: req.headers['if-range'] || null, status: 0, bytes: 0 };
  let recorded = false;
  const finish = () => { if (!recorded) { recorded = true; record(event); } };
  res.on('close', finish); res.on('finish', finish);
  if (req.method !== 'GET' || !/^\/r810-[abe]\/[a-zA-Z0-9_.\/-]+$/.test(req.url) || req.url.includes('..')) { event.status=400; res.writeHead(400); res.end(); return; }
  if (mode === 'redirect') { event.status=302; res.writeHead(302, { Location: 'https://localhost:18443/forbidden-target' }); res.end(); return; }
  if (mode === 'missing') { event.status=404; res.writeHead(404); res.end(); return; }
  const key = policy.run + req.url;
  const hit = (hits.get(key) || 0) + 1; hits.set(key, hit);
  if (mode === 'transient' && hit === 1) { event.status=503; res.writeHead(503, { 'Retry-After': '0' }); res.end(); return; }
  let relative = req.url.slice(1);
  if (relative.endsWith('/release.json') && (mode === 'old' || mode === 'conflict')) relative = relative.slice(0, -12) + (mode === 'old' ? 'release-v1.json' : 'release-conflict.json');
  const file = path.join(root, 'server', relative);
  if (!fs.existsSync(file) || !fs.statSync(file).isFile()) { event.status=404; res.writeHead(404); res.end(); return; }
  let bytes = fs.readFileSync(file);
  const originalSha = crypto.createHash('sha256').update(bytes).digest('hex');
  let etag = '"' + originalSha + '"';
  if (mode === 'weak') etag = 'W/' + etag;
  if (mode === 'changed-etag') etag = '"changed-' + originalSha + '"';
  if (mode === 'signature' && relative.endsWith('/release.json')) {
    const envelope = JSON.parse(bytes.toString('utf8'));
    const signature = Buffer.from(envelope.Signature, 'base64'); signature[0] ^= 1;
    envelope.Signature = signature.toString('base64'); bytes = Buffer.from(JSON.stringify(envelope));
  }
  if ((mode === 'corrupt' && bytes.length > 1024 * 1024) || (mode === 'catalog-hash' && relative.endsWith('/resource-catalog.json'))) {
    bytes = Buffer.from(bytes); bytes[bytes.length - 1] ^= 1;
  }
  if (mode === 'encoded') {
    event.contentEncoding = 'gzip'; event.decodedLength = bytes.length;
    bytes = zlib.gzipSync(bytes); event.wireLength = bytes.length;
  }
  let offset = 0;
  let status = 200;
  const range = /^bytes=(\d+)-$/.exec(req.headers.range || '');
  if (range && req.headers['if-range'] === etag && mode !== 'ignore-range') {
    offset = Number(range[1]); status = offset < bytes.length ? 206 : 416;
  }
  const headers = { 'Content-Length': bytes.length - offset, ETag: etag, 'Content-Type': 'application/octet-stream' };
  if (status === 206) headers['Content-Range'] = `bytes ${offset + (mode === 'bad-range' ? 1 : 0)}-${bytes.length - 1}/${bytes.length}`;
  if (mode === 'encoded') headers['Content-Encoding'] = 'gzip';
  if (status === 416) { headers['Content-Range'] = `bytes */${bytes.length}`; headers['Content-Length'] = 0; }
  event.status = status; event.etag = etag; event.offset = offset; event.total = bytes.length; event.contentRange = headers['Content-Range'] || null;
  res.writeHead(status, headers);
  if (status === 416) { res.end(); return; }
  if (mode === 'stall') return;
  let cursor = offset;
  const send = () => {
    if (res.destroyed) return;
    if (cursor >= bytes.length) { res.end(); return; }
    const next = Math.min(cursor + 32768, bytes.length);
    res.write(bytes.subarray(cursor, next)); event.bytes += next - cursor; cursor = next;
    if (mode === 'truncated' && cursor >= Math.min(bytes.length, offset + 65536)) { res.destroy(); return; }
    setTimeout(send, bytes.length > 1024 * 1024 ? 12 : 0);
  };
  send();
});
server.on('tlsClientError', error => record({ kind:'TLSRejected', error:error.code || error.name }));
server.listen(18443, '127.0.0.1', () => console.log('R810 HTTPS ready https://localhost:18443/'));
process.on('SIGINT', () => server.close(() => log.end()));
process.on('SIGTERM', () => server.close(() => log.end()));
