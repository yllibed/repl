import assert from 'node:assert/strict';
import { spawn, spawnSync } from 'node:child_process';
import { mkdirSync, writeFileSync, mkdtempSync, unlinkSync, rmdirSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { setTimeout as delay } from 'node:timers/promises';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
const name = `repl-hosting-smoke-${process.pid}`;
const image = process.env.REPL_CONTAINER_IMAGE ?? name;
const artifacts = resolve(process.env.REPL_CONTAINER_ARTIFACTS ?? `${root}/artifacts/container-hosting`);
const containers = [];
const sockets = [];
const address = process.env.REPL_CONTAINER_HOST ?? '127.0.0.1';
const exec = ['dotnet', '/app/HostingRemoteSample.dll'];
let native;
let customDirectory;
let customImage;

function docker(args, expected = 0, timeout = 30_000) {
    const result = spawnSync('docker', args, { cwd: root, encoding: 'utf8', timeout });
    assert.equal(result.status, expected, `docker ${args.join(' ')}\n${result.stdout}\n${result.stderr}\n${result.error ?? ''}`);
    return result.stdout.trim();
}
async function until(predicate, description, timeout = 15_000) {
    const deadline = Date.now() + timeout;
    while (!predicate()) {
        assert(Date.now() < deadline, `Timed out: ${typeof description === "function" ? description() : description}`);
        await delay(50);
    }
}
function probe(args, code = 0, pipe = 'repl-hosting-sample', container = name) {
    return JSON.parse(docker(['exec', container, ...exec, '--remote-exec', pipe, ...args], code));
}
function run(container, tag) {
    containers.push(container);
    docker(['run', '-d', '--name', container, '-p', '127.0.0.1::8080', '--health-interval=1s', '--health-start-period=1s', tag]);
}
async function healthy(container) {
    await until(() => docker(['inspect', '-f', '{{.State.Health.Status}}', container]) === 'healthy', `${container} healthy`, 45_000);
}
async function terminal(url, signalR = false) {
    const socket = new WebSocket(url);
    sockets.push(socket);
    let text = '';
    let transcript = '';
    let closed = false;
    socket.addEventListener('close', () => { closed = true; });
    socket.addEventListener('error', () => {});
    let handshake = !signalR;
    let pending = '';
    socket.addEventListener('message', event => {
        if (!signalR) { text += event.data; transcript += event.data; return; }
        pending += event.data;
        const records = pending.split('\x1e');
        pending = records.pop();
        for (const record of records) {
            const message = JSON.parse(record);
            if (message.type === undefined) { assert(!message.error, message.error); handshake = true; }
            if (message.target === 'Output') { text += message.arguments[0]; transcript += message.arguments[0]; }
        }
    });
    await until(() => socket.readyState === WebSocket.OPEN, 'WebSocket open');
    if (signalR) { socket.send('{"protocol":"json","version":1}\x1e'); }
    await until(() => handshake, 'SignalR handshake');
    const send = input => socket.send(signalR ? JSON.stringify({ type: 1, target: 'OnInput', arguments: [input] }) + '\x1e' : input);
    async function expect(marker) {
        await until(() => text.includes(marker), () => `terminal output ${marker}; received ${JSON.stringify(text)}`);
        text = text.slice(text.indexOf(marker) + marker.length);
    }
    await expect('>');
    return { send, expect, closed: () => closed, transcript: () => transcript };
}

try {
    if (!process.env.REPL_CONTAINER_IMAGE) {
        docker(['build', '-f', 'samples/05-hosting-remote/Dockerfile', '-t', image, '.'], 0, 600_000);
    }
    assert(!['', '0', 'root'].includes(docker(['image', 'inspect', '-f', '{{.Config.User}}', image])), 'Image must use a non-root user');
    run(name, image);
    await healthy(name);
    assert.equal(probe(['health']).status, 'ready');
    probe(['settings', 'set', 'maintenance', 'on']);
    assert.equal(probe(['health'], 1).status, 'notReady');
    assert.equal(probe(['health', '--liveness']).status, 'alive');
    const maintenanceProbe = JSON.parse(docker(['inspect', '-f', '{{json .State.Health}}', name])).Log.at(-1).Start;
    await until(() => {
        const health = JSON.parse(docker(['inspect', '-f', '{{json .State.Health}}', name]));
        return health.Status === 'healthy' && health.Log.at(-1).Start !== maintenanceProbe && health.Log.at(-1).ExitCode === 0;
    }, 'fresh Docker liveness probe during maintenance');
    probe(['settings', 'set', 'maintenance', 'off']);
    const port = docker(['port', name, '8080/tcp']).split(':').at(-1);
    const ws = await terminal(`ws://${address}:${port}/ws/repl?ansi=false&cols=80&rows=24`);
    const negotiation = await fetch(`http://${address}:${port}/hub/repl/negotiate?negotiateVersion=1`, { method: 'POST' });
    assert(negotiation.ok);
    const { connectionToken } = await negotiation.json();
    const hub = await terminal(`ws://${address}:${port}/hub/repl?id=${encodeURIComponent(connectionToken)}&ansi=false`, true);
    ws.send('settings set greeting browser-shared\r');
    await ws.expect("updated to 'browser-shared'");
    assert(JSON.stringify(probe(['settings', 'show', 'greeting'])).includes('browser-shared'));
    hub.send('health\r');
    await hub.expect('ready');
    await hub.expect('>');
    ws.send('watch\r');
    await ws.expect('Watching...');
    hub.send('watch\r');
    await hub.expect('Watching...');
    probe(['send', 'bus-marker']);
    await ws.expect('bus-marker');
    await hub.expect('bus-marker');
    assert(JSON.stringify(probe(['log', 'host-only-marker'])).includes('Message written to host logs.'));
    assert(docker(['logs', name]).includes('Operator message: host-only-marker'));
    probe(['send', 'after-log-marker']);
    await ws.expect('after-log-marker');
    await hub.expect('after-log-marker');
    assert(!ws.transcript().includes('host-only-marker') && !hub.transcript().includes('host-only-marker'));

    native = spawn('python3', [resolve(root, 'eng/ci/container-console.py'), name], { stdio: ['pipe', 'pipe', 'pipe'] });
    let nativeOutput = '';
    let nativeError = '';
    native.stdout.on('data', data => { nativeOutput += data; });
    native.stderr.on('data', data => { nativeError += data; });
    const nativeExit = new Promise(resolveExit => {
        native.on('error', error => resolveExit({ code: -1, error }));
        native.on('exit', code => resolveExit({ code }));
    });
    await until(() => nativeOutput.includes('READY_FOR_STOP'), `native console ready: ${nativeError}`, 45_000);
    assert(JSON.stringify(probe(['settings', 'show', 'greeting'])).includes('native-shared'));
    docker(['stop', '--time', '20', name]);
    const state = JSON.parse(docker(['inspect', '-f', '{{json .State}}', name]));
    assert.equal(state.ExitCode, 0, JSON.stringify(state));
    assert.equal(state.OOMKilled, false);
    await until(() => ws.closed() && hub.closed(), 'browser sessions closed');
    native.stdin.end('stopped\n');
    const nativeResult = await Promise.race([nativeExit, delay(15_000).then(() => ({ code: -1 }))]);
    assert.equal(nativeResult.code, 0, nativeError + nativeOutput);
    assert(docker(['logs', name]).includes('Administration sessions stopped.'));

    // A pipe override needs a matching exec-form health check, not shell expansion.
    customDirectory = mkdtempSync(resolve(tmpdir(), 'repl-health-'));
    customImage = `${name}-custom`;
    writeFileSync(resolve(customDirectory, 'Dockerfile'), `FROM ${image}\nENV Repl__PipeName=custom-health\nHEALTHCHECK CMD ["dotnet", "/app/HostingRemoteSample.dll", "--remote-exec", "custom-health", "health", "--liveness"]\n`);
    docker(['build', '-t', customImage, customDirectory], 0, 60_000);
    run(`${name}-custom`, customImage);
    await healthy(`${name}-custom`);
    assert.equal(probe(['health'], 0, 'custom-health', `${name}-custom`).status, 'ready');
    console.log('Container health, maintenance, shared state, isolated output, native consoles and graceful shutdown passed.');
} catch (error) {
    console.error(error);
    process.exitCode = 1;
} finally {
    mkdirSync(artifacts, { recursive: true });
    for (const container of containers) {
        for (const command of ['logs', 'inspect']) {
            const result = spawnSync('docker', [command, container], { encoding: 'utf8', timeout: 10_000 });
            writeFileSync(resolve(artifacts, `${container}-${command}.txt`), `${result.stdout ?? ''}\n${result.stderr ?? ''}`);
        }
    }
    for (const socket of sockets) { socket.close(); }
    if (native && native.exitCode === null) { native.kill(); }
    for (const container of containers.reverse()) { spawnSync('docker', ['rm', '-f', container], { timeout: 30_000 }); }
    if (customImage) { spawnSync('docker', ['image', 'rm', customImage], { timeout: 30_000 }); }
    if (!process.env.REPL_CONTAINER_IMAGE) { spawnSync('docker', ['image', 'rm', image], { timeout: 30_000 }); }
    if (customDirectory) { unlinkSync(resolve(customDirectory, 'Dockerfile')); rmdirSync(customDirectory); }
}
