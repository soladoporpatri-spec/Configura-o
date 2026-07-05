#!/usr/bin/env node
/**
 * WhatsApp Bridge Factory
 * Gerencia múltiplas instâncias da Bridge, uma por loja.
 * Porta por loja: BRIDGE_BASE_PORT + storeId  (ex: Store 1 → 3001, Store 2 → 3002)
 * Roda na porta FACTORY_PORT (padrão 2999).
 */

'use strict';

const { spawn } = require('child_process');
const path = require('path');
const fs = require('fs');
const http = require('http');

const BRIDGE_BASE_PORT = Number(process.env.BRIDGE_BASE_PORT || 3000);
const FACTORY_PORT     = Number(process.env.FACTORY_PORT     || 2999);
const BACKEND_URL      = process.env.BACKEND_URL || 'http://127.0.0.1:5000';
const API_KEY          = process.env.API_KEY     || '';

// ─── HTTP helper (sem dependência de fetch) ──────────────────────────────────

function httpGet(url, headers = {}, timeoutMs = 5000) {
    return new Promise((resolve, reject) => {
        const parsed = new URL(url);
        const req = http.request({
            hostname: parsed.hostname,
            port:     Number(parsed.port) || 80,
            path:     parsed.pathname + parsed.search,
            method:   'GET',
            headers,
            timeout:  timeoutMs
        }, res => {
            let body = '';
            res.on('data', d => { body += d; });
            res.on('end', () => {
                try { resolve({ status: res.statusCode, body: JSON.parse(body) }); }
                catch { resolve({ status: res.statusCode, body }); }
            });
        });
        req.on('timeout', () => { req.destroy(); reject(new Error('timeout')); });
        req.on('error', reject);
        req.end();
    });
}

function httpPost(url, data, headers = {}, timeoutMs = 8000) {
    return httpRequest('POST', url, data, headers, timeoutMs);
}

function httpPut(url, data, headers = {}, timeoutMs = 8000) {
    return httpRequest('PUT', url, data, headers, timeoutMs);
}

function httpRequest(method, url, data, headers = {}, timeoutMs = 8000) {
    return new Promise((resolve, reject) => {
        const parsed = new URL(url);
        const body = JSON.stringify(data);
        const req = http.request({
            hostname: parsed.hostname,
            port:     Number(parsed.port) || 80,
            path:     parsed.pathname + parsed.search,
            method,
            headers: {
                'Content-Type':   'application/json',
                'Content-Length': Buffer.byteLength(body),
                ...headers
            },
            timeout: timeoutMs
        }, res => {
            let buf = '';
            res.on('data', d => { buf += d; });
            res.on('end', () => {
                try { resolve({ status: res.statusCode, body: JSON.parse(buf) }); }
                catch { resolve({ status: res.statusCode, body: buf }); }
            });
        });
        req.on('timeout', () => { req.destroy(); reject(new Error('timeout')); });
        req.on('error', reject);
        req.write(body);
        req.end();
    });
}

// ─── BridgeInstance ──────────────────────────────────────────────────────────

class BridgeInstance {
    constructor(storeId, port) {
        this.storeId = storeId;
        this.port    = port;
        this.process = null;
        this.started = false;
        this.lastHealthCheck = null;
        this.restartCount = 0;
        this.healthFailures = 0; // healthchecks ativos consecutivos que falharam
    }

    // Bridge travada (processo vivo mas /health mudo): mata o processo.
    // O handler 'close' existente (não _stopping) faz o auto-restart com backoff.
    recycleStuck(reason) {
        console.warn(`[Factory] Bridge loja ${this.storeId} travada (${reason}). Forçando restart...`);
        this.healthFailures = 0;
        if (this.process) {
            try { this.process.kill('SIGKILL'); } catch (e) { console.error(`[Factory] Falha ao matar bridge ${this.storeId}: ${e.message}`); }
        }
    }

    async start() {
        if (this.process) return; // já rodando

        console.log(`[Factory] Iniciando Bridge loja ${this.storeId} → porta ${this.port}`);

        const env = {
            ...process.env,
            STORE_ID:    this.storeId.toString(),
            BRIDGE_HOST: '127.0.0.1',
            BRIDGE_PORT: this.port.toString(),
            API_KEY:     API_KEY,
            BACKEND_URL: BACKEND_URL
        };

        const logFile = path.join(__dirname, '..', 'logs', `bridge-${this.storeId}.log`);
        const errFile = path.join(__dirname, '..', 'logs', `bridge-${this.storeId}.err.log`);

        this.process = spawn('node', ['index.js'], {
            cwd:         __dirname,
            env,
            stdio:       ['ignore', 'pipe', 'pipe'],
            windowsHide: true
        });

        this.process.stdout.on('data', data => {
            const line = `[${new Date().toISOString()}] ${data.toString().trimEnd()}\n`;
            process.stdout.write(`[Bridge-${this.storeId}] ${data.toString().trimEnd()}\n`);
            fs.appendFileSync(logFile, line);
        });

        this.process.stderr.on('data', data => {
            const line = `[${new Date().toISOString()}] ${data.toString().trimEnd()}\n`;
            process.stderr.write(`[Bridge-${this.storeId}] ${data.toString().trimEnd()}\n`);
            fs.appendFileSync(errFile, line);
        });

        this.process.on('close', code => {
            console.log(`[Factory] Bridge loja ${this.storeId} encerrou (code ${code})`);
            this.process = null;
            this.started = false;

            // Auto-restart se encerrou inesperadamente (não via stop())
            if (!this._stopping) {
                this.restartCount++;
                const delay = Math.min(5000 * this.restartCount, 60000);
                console.log(`[Factory] Reiniciando Bridge loja ${this.storeId} em ${delay}ms...`);
                setTimeout(() => {
                    if (!this._stopping) this.start().catch(e =>
                        console.error(`[Factory] Falha ao reiniciar Bridge loja ${this.storeId}: ${e.message}`)
                    );
                }, delay);
            }
        });

        await this.waitForReady();
        this.started = true;
        this.restartCount = 0;
        console.log(`[Factory] Bridge loja ${this.storeId} pronta na porta ${this.port}`);
    }

    async stop() {
        this._stopping = true;
        if (this.process) {
            console.log(`[Factory] Parando Bridge loja ${this.storeId}`);
            this.process.kill('SIGTERM');
            let attempts = 0;
            while (this.process && attempts++ < 20) {
                await new Promise(r => setTimeout(r, 500));
            }
            if (this.process) this.process.kill('SIGKILL');
        }
        this.started = false;
    }

    async waitForReady(maxMs = 60000) {
        const step = 500;
        const max  = maxMs / step;
        for (let i = 0; i < max; i++) {
            try {
                const res = await httpGet(`http://127.0.0.1:${this.port}/health`, {}, 2000);
                if (res.status === 200) return true;
            } catch {}
            await new Promise(r => setTimeout(r, step));
        }
        throw new Error(`Bridge loja ${this.storeId} não ficou pronta em ${maxMs}ms`);
    }

    async healthCheck() {
        try {
            const res = await httpGet(`http://127.0.0.1:${this.port}/health`, {}, 3000);
            this.lastHealthCheck = new Date().toISOString();
            return { healthy: true, port: this.port, ...res.body };
        } catch (e) {
            return { healthy: false, port: this.port, error: e.message };
        }
    }

    bridgeUrl() { return `http://127.0.0.1:${this.port}`; }
}

// ─── BridgeFactory ────────────────────────────────────────────────────────────

class BridgeFactory {
    constructor() { this.instances = new Map(); }

    portFor(storeId) { return BRIDGE_BASE_PORT + storeId; }

    async start(storeId) {
        storeId = Number(storeId);
        if (this.instances.has(storeId)) {
            const inst = this.instances.get(storeId);
            if (inst.started) return inst;
        }
        const inst = new BridgeInstance(storeId, this.portFor(storeId));
        this.instances.set(storeId, inst);
        try {
            await inst.start();
        } catch (e) {
            this.instances.delete(storeId);
            throw e;
        }
        return inst;
    }

    async stop(storeId) {
        storeId = Number(storeId);
        const inst = this.instances.get(storeId);
        if (inst) { await inst.stop(); this.instances.delete(storeId); }
    }

    async stopAll() {
        await Promise.all([...this.instances.values()].map(i => i.stop()));
        this.instances.clear();
    }

    async restart(storeId) {
        await this.stop(storeId);
        return this.start(storeId);
    }

    status() {
        const out = {};
        for (const [id, inst] of this.instances) {
            out[id] = { port: inst.port, started: inst.started, restartCount: inst.restartCount, lastHealthCheck: inst.lastHealthCheck };
        }
        return out;
    }

    async healthAll() {
        const out = {};
        for (const [id, inst] of this.instances) { out[id] = await inst.healthCheck(); }
        return out;
    }

    // Healthcheck ATIVO: a cada ciclo verifica as bridges que deveriam estar no ar.
    // 3 falhas consecutivas → recicla (mata + auto-restart). Fecha o ciclo de watchdog.
    async monitorHealth() {
        await Promise.all([...this.instances.values()].map(async inst => {
            if (!inst.started || !inst.process) return; // ignora paradas/intencionais
            const h = await inst.healthCheck();
            if (h.healthy) {
                inst.healthFailures = 0;
                return;
            }
            inst.healthFailures = (inst.healthFailures || 0) + 1;
            console.warn(`[Factory] Healthcheck falhou loja ${inst.storeId} (${inst.healthFailures}/3): ${h.error || 'sem resposta'}`);
            if (inst.healthFailures >= 3) inst.recycleStuck('healthcheck 3x consecutivas');
        }));
    }
}

const factory = new BridgeFactory();

// ─── Auto-start: lê lojas ativas do backend e inicia bridges ─────────────────

async function syncBridgesFromBackend() {
    if (!API_KEY) {
        console.warn('[Factory] API_KEY não definido — skipping auto-sync com backend.');
        return;
    }
    try {
        const res = await httpGet(`${BACKEND_URL}/api/superadmin/stores`, { 'X-API-KEY': API_KEY }, 8000);
        if (res.status !== 200 || !Array.isArray(res.body)) {
            console.warn(`[Factory] Não foi possível obter stores (HTTP ${res.status}) — bridges não iniciadas automaticamente.`);
            return;
        }

        const activeStores = res.body.filter(s => s.IsActive || s.isActive);
        console.log(`[Factory] ${activeStores.length} loja(s) ativa(s) encontrada(s). Iniciando bridges...`);

        for (const store of activeStores) {
            const storeId = store.Id || store.id;
            const expectedUrl = `http://127.0.0.1:${BRIDGE_BASE_PORT + storeId}`;
            try {
                await factory.start(storeId);
                // Atualiza BridgeUrl no backend se necessário
                const currentUrl = store.BridgeUrl || store.bridgeUrl || '';
                if (currentUrl !== expectedUrl) {
                    const upd = await httpPut(
                        `${BACKEND_URL}/api/superadmin/stores/${storeId}`,
                        { BridgeUrl: expectedUrl },
                        { 'X-API-KEY': API_KEY, 'Content-Type': 'application/json' }
                    );
                    if (upd.status >= 200 && upd.status < 300) {
                        console.log(`[Factory] BridgeUrl da loja ${storeId} atualizada → ${expectedUrl}`);
                    } else {
                        console.warn(`[Factory] Falha ao atualizar BridgeUrl da loja ${storeId}: HTTP ${upd.status}`);
                    }
                }
            } catch (e) {
                console.error(`[Factory] Falha ao iniciar Bridge loja ${storeId}: ${e.message}`);
            }
        }
    } catch (e) {
        console.error(`[Factory] Erro ao sincronizar bridges: ${e.message}`);
    }
}

// ─── HTTP API ─────────────────────────────────────────────────────────────────

const express = require('express');
const app     = express();
app.use(express.json());

// Autenticação
app.use((req, res, next) => {
    if (req.path === '/health') return next(); // health sem auth
    const key = req.headers['x-api-key'];
    if (!API_KEY || key === API_KEY) return next();
    res.status(401).json({ error: 'API key inválida' });
});

// Health do próprio factory
app.get('/health', async (_req, res) => {
    const health = await factory.healthAll();
    const allHealthy = Object.values(health).every(h => h.healthy);
    res.status(200).json({
        ok: true,
        service: 'bridge-factory',
        instances: Object.keys(factory.instances).length,
        allHealthy,
        health
    });
});

// Status geral
app.get('/status', async (_req, res) => {
    res.json({ status: factory.status(), health: await factory.healthAll(), timestamp: new Date().toISOString() });
});

// Iniciar bridge de uma loja
app.post('/bridge/:storeId/start', async (req, res) => {
    try {
        const inst = await factory.start(req.params.storeId);
        res.json({ ok: true, storeId: inst.storeId, port: inst.port, bridgeUrl: inst.bridgeUrl() });
    } catch (e) {
        res.status(500).json({ ok: false, error: e.message });
    }
});

// Parar bridge de uma loja
app.post('/bridge/:storeId/stop', async (req, res) => {
    try {
        await factory.stop(req.params.storeId);
        res.json({ ok: true, storeId: Number(req.params.storeId), status: 'stopped' });
    } catch (e) {
        res.status(500).json({ ok: false, error: e.message });
    }
});

// Reiniciar bridge de uma loja
app.post('/bridge/:storeId/restart', async (req, res) => {
    try {
        const inst = await factory.restart(req.params.storeId);
        res.json({ ok: true, storeId: inst.storeId, port: inst.port, bridgeUrl: inst.bridgeUrl() });
    } catch (e) {
        res.status(500).json({ ok: false, error: e.message });
    }
});

// Proxy GET para endpoint de uma loja específica (ex: /bridge/1/status, /bridge/1/qr)
app.get('/bridge/:storeId/:endpoint(*)', async (req, res) => {
    const storeId = Number(req.params.storeId);
    const inst = factory.instances.get(storeId);
    if (!inst || !inst.started) return res.status(503).json({ error: `Bridge loja ${storeId} não está rodando` });
    try {
        const result = await httpGet(`${inst.bridgeUrl()}/${req.params.endpoint}`, { 'X-API-KEY': API_KEY }, 8000);
        res.status(result.status).json(result.body);
    } catch (e) {
        res.status(500).json({ error: e.message });
    }
});

// Sincronizar todas as bridges com o backend (chamado pelo supervisor ou manualmente)
app.post('/sync', async (_req, res) => {
    try {
        await syncBridgesFromBackend();
        res.json({ ok: true, instances: Object.keys(factory.status()).length });
    } catch (e) {
        res.status(500).json({ ok: false, error: e.message });
    }
});

// ─── Inicialização ────────────────────────────────────────────────────────────

process.on('SIGINT',  async () => { await factory.stopAll(); process.exit(0); });
process.on('SIGTERM', async () => { await factory.stopAll(); process.exit(0); });

// Watchdog ativo: verifica a saúde de cada bridge a cada 20s e recicla as travadas.
const HEALTH_MONITOR_MS = Number(process.env.BRIDGE_HEALTH_MONITOR_MS || 20000);
setInterval(() => { factory.monitorHealth().catch(e => console.error('[Factory] Erro no monitor de saúde:', e.message)); }, HEALTH_MONITOR_MS);

app.listen(FACTORY_PORT, '127.0.0.1', async () => {
    console.log(`[Factory] Rodando na porta ${FACTORY_PORT}`);

    // Aguarda backend ficar pronto e sincroniza as bridges
    const maxWait = 90; // 90 tentativas × 2s = 3 min
    for (let i = 0; i < maxWait; i++) {
        try {
            const res = await httpGet(`${BACKEND_URL}/health`, {}, 3000);
            if (res.status === 200) {
                console.log('[Factory] Backend online — sincronizando bridges...');
                await syncBridgesFromBackend();
                return;
            }
        } catch {}
        await new Promise(r => setTimeout(r, 2000));
    }
    console.warn('[Factory] Backend não ficou online em 3min. Bridges não foram iniciadas automaticamente.');
});

module.exports = { BridgeFactory, BridgeInstance };
