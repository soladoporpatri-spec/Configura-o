const { Client, LocalAuth, Poll } = require('whatsapp-web.js')
const axios = require('axios')
const axiosRetry = require('axios-retry').default
const qrcode = require('qrcode-terminal')
const QRCode = require('qrcode')
const express = require('express')
const fs = require('fs')
const path = require('path')
const os = require('os')
const crypto = require('crypto')

const app = express()

// Permite que o servidor identifique o IP real do cliente quando estiver atrás de um proxy (Ngrok, Nginx, etc)
app.set('trust proxy', 1)

const messageQueue = []
const sentPolls = new Map()
const handledPollVotes = new Set()
const lastPollIdPerChat = new Map() // Rastreia apenas a enquete ativa por chat
const startedAt = new Date()
// Cada loja tem sua própria sessão WhatsApp e arquivos de dados isolados.
// STORE_ID é definido pela bridge-factory como variável de ambiente (obrigatório no modo multi-loja).
const STORE_ID = process.env.STORE_ID;
if (!STORE_ID) {
    console.warn(
        '[Bridge] AVISO: STORE_ID não definido. ' +
        'Em modo multi-loja, a bridge-factory deve definir STORE_ID para cada instância. ' +
        'Usando "1" como fallback — todas as mensagens desta bridge serão atribuídas à Loja 1.'
    );
}
const STORE_ID_RESOLVED = STORE_ID || '1';
const botStatePath = path.join(__dirname, '..', 'data', `bot-state-${STORE_ID_RESOLVED}.json`)
let totalMessagesReceived = 0;
let totalMessagesSent = 0;
const queueFilePath = path.join(__dirname, '..', 'data', `whatsapp-queue-${STORE_ID_RESOLVED}.json`)
const pausedPendingPath = path.join(__dirname, '..', 'data', `whatsapp-paused-pending-${STORE_ID_RESOLVED}.json`)
// Pasta de sessão isolada por loja — impede que lojas compartilhem o mesmo número WhatsApp
const sessionPath = path.join(__dirname, `.wwebjs_auth_${STORE_ID_RESOLVED}`);
const queueMaxAgeMs = Number(process.env.QUEUE_MAX_AGE_MS || 15 * 60 * 1000)
const pausedPendingMaxAgeMs = Number(process.env.PAUSED_PENDING_MAX_AGE_MS || 24 * 60 * 60 * 1000)
const queueMaxAttempts = Number(process.env.QUEUE_MAX_ATTEMPTS || 10)
const queueMaxSize = Number(process.env.QUEUE_MAX_SIZE || 500)
const incomingDedupeTtlMs = Number(process.env.INCOMING_DEDUPE_TTL_MS || 90 * 1000)
const incomingRateWindowMs = Number(process.env.INCOMING_RATE_WINDOW_MS || 60 * 1000)
const incomingRateMax = Number(process.env.INCOMING_RATE_MAX || 12)
const pollCacheTtlMs = Number(process.env.POLL_CACHE_TTL_MS || 20 * 60 * 1000)
const qrDisplayTtlMs = Number(process.env.QR_DISPLAY_TTL_MS || 5 * 60 * 1000)
const qrRenewAfterMs = Number(process.env.QR_RENEW_AFTER_MS || 110 * 1000)
const pausedPending = new Map()
const incomingDedupe = new Map()
const incomingRate = new Map()
let botStartTime = null
let latestQr = null
let latestQrImage = null
let latestQrAt = null
let latestQrCreatedAtMs = 0
let connectionStatus = 'starting'
// Pareamento por código numérico (alternativa ao QR)
let latestPairingCode = null          // ex: 'ABCD-1234'
let latestPairingCodeAt = null        // ISO timestamp da geração
let latestPairingCodeMs = 0           // para calcular expiração
let pairingCodePendingPhone = null    // número aguardando o evento qr para gerar o código
const PAIRING_CODE_TTL_MS = 3 * 60 * 1000 // 3 min (WhatsApp expira o código neste prazo)
let reconnectTimer = null
let initWatchdogTimer = null
let isInitializing = false
let everConnected = false        // já ficou pronto ao menos uma vez (distingue 1º QR de reconexão)
let lastStatusAlertAt = 0        // rate-limit dos alertas proativos
let restartCount = 0
let client = null; // Instância dinâmica

app.use(express.json())
app.disable('x-powered-by')

// Middleware de Segurança: Validação de Chave de API e proteção contra acessos externos
if (!process.env.API_KEY) {
    console.error('API_KEY nao definido. Defina a variavel de ambiente API_KEY antes de iniciar o Bridge.');
    process.exit(1);
}
const BRIDGE_API_KEY = process.env.API_KEY;
const BACKEND_URL = process.env.BACKEND_URL || 'http://127.0.0.1:5000';
const BRIDGE_HOST = process.env.BRIDGE_HOST || '127.0.0.1';
const BRIDGE_PORT = Number(process.env.BRIDGE_PORT || process.env.PORT || 3000);

// Configuração do cliente Axios com Retentativas (SaaS Reliability)
const backendClient = axios.create({
    baseURL: BACKEND_URL,
    timeout: Number(process.env.BACKEND_WEBHOOK_TIMEOUT_MS || 45000),
    headers: {
        'X-API-KEY': BRIDGE_API_KEY,
        'Content-Type': 'application/json'
    }
});

axiosRetry(backendClient, {
    retries: 5, // Tenta até 5 vezes
    retryDelay: axiosRetry.exponentialDelay, // Espera 200ms, 400ms, 800ms...
    retryCondition: (error) => {
        // Retenta em erros de rede ou se o servidor retornar 5xx (Internal Server Error)
        return axiosRetry.isNetworkOrIdempotentRequestError(error) || 
               (error.response && error.response.status >= 500);
    },
    onRetry: (retryCount, error) => {
        console.warn(`[Bridge] Falha no Webhook. Retentativa #${retryCount}...`);
    }
});

app.use((req, res, next) => {
    // Endpoints públicos (Health/QR) podem ser necessários para o Dashboard ler o status
    const publicPaths = ['/health'];
    if (publicPaths.includes(req.path)) return next();

    const apiKey = req.headers['x-api-key'];
    if (apiKey !== BRIDGE_API_KEY) {
        console.warn(`[Security] Tentativa de acesso não autorizado bloqueada de: ${req.ip}`);
        return res.status(401).json({ error: 'Chave de API inválida ou ausente.' });
    }
    next();
});

function ensureDataPath() {
    const dataDir = path.dirname(queueFilePath)
    if (!fs.existsSync(dataDir)) {
        fs.mkdirSync(dataDir, { recursive: true })
    }
}

function loadQueueFromDisk() {
    ensureDataPath()
    if (!fs.existsSync(queueFilePath)) return

    try {
        const content = fs.readFileSync(queueFilePath, 'utf8')
        const items = JSON.parse(content)
        if (Array.isArray(items)) {
            messageQueue.length = 0
            const validItems = items.filter(isQueueItemProcessable)
            validItems.forEach(item => messageQueue.push(item))
            const discarded = items.length - validItems.length
            console.log(`[Bridge] Fila recuperada de disco (${validItems.length} mensagens, ${discarded} descartadas).`)
            if (discarded > 0) saveQueueToDisk()
        }
    } catch (error) {
        console.error('[Bridge] Não foi possível recuperar fila do disco:', error.message)
    }
}

function saveQueueToDisk() {
    ensureDataPath()
    try {
        fs.writeFileSync(queueFilePath, JSON.stringify(messageQueue, null, 2), 'utf8')
    } catch (error) {
        console.error('[Bridge] Não foi possível persistir fila de mensagens:', error.message)
    }
}

function loadPausedPendingFromDisk() {
    ensureDataPath()
    if (!fs.existsSync(pausedPendingPath)) return

    try {
        const content = fs.readFileSync(pausedPendingPath, 'utf8')
        const items = JSON.parse(content)
        const list = Array.isArray(items) ? items : Object.values(items || {})
        pausedPending.clear()
        let discarded = 0

        for (const item of list) {
            if (isPausedPendingProcessable(item)) {
                pausedPending.set(getPendingKey(item.phone), item)
            } else {
                discarded++
            }
        }

        console.log(`[Bridge] Pendencias de bot pausado recuperadas (${pausedPending.size} contatos, ${discarded} descartadas).`)
        if (discarded > 0) savePausedPendingToDisk()
    } catch (error) {
        console.error('[Bridge] Nao foi possivel recuperar pendencias do bot pausado:', error.message)
    }
}

function savePausedPendingToDisk() {
    ensureDataPath()
    try {
        fs.writeFileSync(pausedPendingPath, JSON.stringify([...pausedPending.values()], null, 2), 'utf8')
    } catch (error) {
        console.error('[Bridge] Nao foi possivel persistir pendencias do bot pausado:', error.message)
    }
}

function enqueueMessage(message) {
    const text = String(message.text || '').trim()
    const phone = normalizeJid(message.phone)
    if (isDuplicateIncoming(phone, text, message.pollId)) {
        console.log(`[Bridge] Mensagem duplicada ignorada de [${phone}].`)
        return null
    }

    if (isRateLimitedIncoming(phone)) {
        console.warn(`[Bridge] Rate limit anti-spam aplicado para [${phone}].`)
        return null
    }

    const queuedMessage = {
        id: crypto.randomBytes(16).toString('hex'),
        attempts: 0,
        deliveredAt: null,
        ...message,
        phone,
        text
    }

    if (!isQueueItemProcessable(queuedMessage)) {
        console.warn(`[Bridge] Mensagem ignorada antes de entrar na fila: ${queuedMessage.phone || 'sem telefone'}`)
        return null
    }

    if (messageQueue.length >= queueMaxSize) {
        const removed = messageQueue.splice(0, messageQueue.length - queueMaxSize + 1)
        console.warn(`[Bridge] Fila acima do limite. ${removed.length} mensagens antigas removidas para proteger memoria.`)
    }

    messageQueue.push(queuedMessage)
    saveQueueToDisk();
    
    // Webhook: Notifica o backend C# imediatamente em vez de esperar o polling
    notifyBackend(queuedMessage);
    return queuedMessage
}

function isDuplicateIncoming(phone, text, pollId) {
    const now = Date.now()
    pruneMapByAge(incomingDedupe, now, incomingDedupeTtlMs)
    const key = `${phone}|${pollId || ''}|${text}`
    const previousAt = incomingDedupe.get(key)
    incomingDedupe.set(key, now)
    return previousAt && now - previousAt < incomingDedupeTtlMs
}

function isRateLimitedIncoming(phone) {
    const now = Date.now()
    const windowStart = now - incomingRateWindowMs
    const entries = (incomingRate.get(phone) || []).filter(ts => ts >= windowStart)
    if (entries.length >= incomingRateMax) {
        incomingRate.set(phone, entries)
        return true
    }
    entries.push(now)
    incomingRate.set(phone, entries)
    return false
}

function pruneMapByAge(map, now, ttlMs) {
    for (const [key, value] of map.entries()) {
        const ts = typeof value === 'number' ? value : value?.createdAt
        if (!ts || now - ts > ttlMs) map.delete(key)
    }
}

function pruneRuntimeCaches() {
    const now = Date.now()
    pruneMapByAge(incomingDedupe, now, incomingDedupeTtlMs)
    pruneMapByAge(sentPolls, now, pollCacheTtlMs)
    for (const [chat, pollId] of lastPollIdPerChat.entries()) {
        if (!sentPolls.has(pollId)) lastPollIdPerChat.delete(chat)
    }
    if (handledPollVotes.size > 1000) {
        for (const key of [...handledPollVotes].slice(0, handledPollVotes.size - 700)) {
            handledPollVotes.delete(key)
        }
    }
    for (const [phone, entries] of incomingRate.entries()) {
        const fresh = entries.filter(ts => now - ts <= incomingRateWindowMs)
        if (fresh.length) incomingRate.set(phone, fresh)
        else incomingRate.delete(phone)
    }
}

function handleIncomingMessage(message, options = {}) {
    if (!botEnabled && !options.forceQueue) {
        return storePausedPending(message, options.reason || 'bot-paused')
    }

    return enqueueMessage(message)
}

async function notifyBackend(message) {
    try {
        await backendClient.post('/api/webhook/whatsapp', message);
        removeQueuedMessage(message.id);
    } catch (error) {
        console.error('[Bridge] Falha definitiva no Webhook após retentativas. O Worker usará o fallback de polling.', error.message);
    }
}

// Alerta proativo de status do WhatsApp para a loja desta bridge (cai / precisa de QR / reconecta).
// Best-effort + rate-limit (não dispara mais de 1 a cada 30s) para não spammar o dashboard.
async function notifyBridgeStatus(title, message, priority = 'High') {
    const now = Date.now();
    if (now - lastStatusAlertAt < 30000) return;
    lastStatusAlertAt = now;
    try {
        await backendClient.post('/api/bridge/status-alert', {
            storeId: Number(STORE_ID_RESOLVED),
            title, message, priority
        });
    } catch (e) {
        console.warn('[Bridge] Falha ao enviar alerta de status (ignorado):', e.message);
    }
}

function normalizeTimestampMs(timestamp) {
    const value = Number(timestamp)
    if (!Number.isFinite(value) || value <= 0) return Date.now()
    return value < 100000000000 ? value * 1000 : value
}

function isProcessablePhone(phone) {
    if (!phone || typeof phone !== 'string') return false
    if (phone === 'status@broadcast') return false
    if (phone.endsWith('@g.us') || phone.endsWith('@newsletter') || phone.endsWith('@broadcast')) return false
    return phone.endsWith('@c.us') || phone.endsWith('@lid')
}

function isQueueItemProcessable(item) {
    if (!item || !isProcessablePhone(item.phone)) return false
    if (typeof item.text !== 'string' || item.text.trim().length === 0) return false
    if ((item.attempts || 0) >= queueMaxAttempts) return false

    const ageMs = Date.now() - normalizeTimestampMs(item.timestamp)
    return ageMs <= queueMaxAgeMs
}

function isPausedPendingProcessable(item) {
    if (!item || !isProcessablePhone(item.phone)) return false
    if (typeof item.text !== 'string' || item.text.trim().length === 0) return false

    const ageMs = Date.now() - normalizeTimestampMs(item.timestamp || item.updatedAt)
    return ageMs <= pausedPendingMaxAgeMs
}

function getPendingKey(phone) {
    return normalizeJid(phone)
}

function storePausedPending(message, reason = 'bot-paused') {
    const phone = normalizeJid(message.phone)
    const key = getPendingKey(phone)
    const previous = pausedPending.get(key)
    const pending = {
        ...message,
        phone,
        text: String(message.text || '').trim(),
        timestamp: normalizeTimestampMs(message.timestamp),
        storeId: Number(message.storeId || STORE_ID_RESOLVED),
        source: message.source || 'paused-latest',
        pauseReason: reason,
        firstPausedAt: previous?.firstPausedAt || new Date().toISOString(),
        updatedAt: new Date().toISOString(),
        suppressedCount: previous ? (previous.suppressedCount || 0) + 1 : 0
    }

    if (!isPausedPendingProcessable(pending)) {
        console.warn(`[Bridge] Pendencia ignorada com bot pausado: ${phone || 'sem telefone'}`)
        return null
    }

    pausedPending.set(key, pending)
    savePausedPendingToDisk()
    console.log(`[Bridge] Bot pausado: guardada somente a ultima mensagem de [${phone}] (${pending.suppressedCount} substituidas).`)
    return pending
}

function prunePausedPending() {
    const before = pausedPending.size
    for (const [key, item] of pausedPending.entries()) {
        if (!isPausedPendingProcessable(item)) {
            pausedPending.delete(key)
        }
    }
    if (pausedPending.size !== before) {
        console.log(`[Bridge] Pendencias pausadas saneadas: ${before - pausedPending.size} removidas.`)
        savePausedPendingToDisk()
    }
}

function flushPausedPending(reason = 'bot-enabled') {
    prunePausedPending()
    const pending = [...pausedPending.values()]
    let queued = 0

    for (const item of pending) {
        const queuedMessage = enqueueMessage({
            phone: item.phone,
            text: item.text,
            timestamp: Date.now(),
            storeId: item.storeId,
            source: item.source === 'poll' ? 'poll' : 'paused-latest',
            pauseReason: reason,
            suppressedCount: item.suppressedCount || 0,
            firstPausedAt: item.firstPausedAt,
            resumedAt: new Date().toISOString()
        })

        if (queuedMessage) queued++
        pausedPending.delete(getPendingKey(item.phone))
    }

    if (pending.length > 0) {
        savePausedPendingToDisk()
        console.log(`[Bridge] Bot reativado: ${queued}/${pending.length} ultimas mensagens pendentes enviadas para processamento.`)
    }

    return queued
}

function pruneQueue() {
    const before = messageQueue.length
    for (let i = messageQueue.length - 1; i >= 0; i--) {
        if (!isQueueItemProcessable(messageQueue[i])) {
            messageQueue.splice(i, 1)
        }
    }
    if (messageQueue.length !== before) {
        console.log(`[Bridge] Fila saneada: ${before - messageQueue.length} mensagens antigas/inválidas removidas.`)
        saveQueueToDisk()
    }
}

function removeQueuedMessage(id) {
    if (!id) return
    const before = messageQueue.length
    for (let i = messageQueue.length - 1; i >= 0; i--) {
        if (String(messageQueue[i].id) === String(id)) {
            messageQueue.splice(i, 1)
        }
    }
    if (messageQueue.length !== before) saveQueueToDisk()
}

async function retryAsync(fn, retries = 3, delayMs = 800) {
    for (let attempt = 1; attempt <= retries; attempt++) {
        try {
            return await fn()
        } catch (err) {
            if (attempt === retries) throw err
            const wait = delayMs * attempt
            console.warn(`[Bridge] Tentativa ${attempt} falhou, aguardando ${wait}ms...`, err.message)
            await new Promise(resolve => setTimeout(resolve, wait))
        }
    }
}

loadQueueFromDisk()
loadPausedPendingFromDisk()
setInterval(saveQueueToDisk, 15000)
setInterval(savePausedPendingToDisk, 15000)
setInterval(prunePausedPending, 60000)
setInterval(pruneQueue, 60000)
setInterval(pruneRuntimeCaches, 60000)

let botEnabled = loadBotEnabled()
if (botEnabled) {
    setTimeout(() => flushPausedPending('startup-enabled'), 3000)
}

function loadBotEnabled() {
    try {
        if (!fs.existsSync(botStatePath)) {
            return true
        }

        const state = JSON.parse(fs.readFileSync(botStatePath, 'utf8'))
        return state.enabled !== false
    } catch (e) {
        console.error('Erro ao ler estado do bot:', e.message)
        return true
    }
}

function saveBotEnabled(enabled) {
    try {
        fs.mkdirSync(path.dirname(botStatePath), { recursive: true })
        fs.writeFileSync(botStatePath, JSON.stringify({
            enabled,
            updatedAt: new Date().toISOString()
        }, null, 2))
    } catch (e) {
        console.error('Erro ao salvar estado do bot:', e.message)
    }
}

function normalizeJid(jid) {
    if (!jid || !jid.includes('@')) return jid;
    // Remove sufixos de dispositivo (:1, :2) mas mantém o domínio (@c.us ou @lid)
    const parts = jid.split('@');
    return `${parts[0].split(':')[0]}@${parts[1]}`;
}

function normalizeBrazilPhone(phone) {
    const raw = String(phone || '').trim()
    if (raw.includes('@')) return normalizeJid(raw)

    let digits = raw.replace(/\D/g, '')
    if (!digits) return raw

    if ((digits.length === 10 || digits.length === 11) && !digits.startsWith('55')) {
        digits = `55${digits}`
    }

    return `${digits}@c.us`
}

async function resolveRecipientJid(phone) {
    const normalized = normalizeBrazilPhone(phone)
    if (!normalized || normalized.endsWith('@lid')) return normalized

    const number = normalized.replace('@c.us', '')
    try {
        const resolved = await client.getNumberId(number)
        if (resolved?._serialized) return normalizeJid(resolved._serialized)
    } catch (error) {
        console.warn(`[Bridge] Nao foi possivel resolver ${number} via getNumberId: ${error.message}`)
    }

    return normalizeJid(normalized)
}

function isNoLidError(error) {
    return /no lid for user/i.test(error?.message || '')
}

function resolveChromePath() {
    // 1. Variavel de ambiente explicita
    if (process.env.PUPPETEER_EXECUTABLE_PATH && fs.existsSync(process.env.PUPPETEER_EXECUTABLE_PATH)) {
        return process.env.PUPPETEER_EXECUTABLE_PATH
    }
    // 2. Arquivo gerado por scripts/instalar-chrome.js durante instalacao
    const chromePathFile = path.join(__dirname, '.chrome-path')
    try {
        const p = fs.readFileSync(chromePathFile, 'utf8').trim()
        if (p && fs.existsSync(p)) return p
    } catch { /* sem arquivo, continuar */ }
    // 3. Caminhos do sistema por plataforma
    const isWin = process.platform === 'win32'
    const systemPaths = isWin ? [
        path.join(os.homedir(), 'AppData', 'Local', 'Google', 'Chrome', 'Application', 'chrome.exe'),
        'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe',
        'C:\\Program Files (x86)\\Google\\Chrome\\Application\\chrome.exe',
        path.join(os.homedir(), 'AppData', 'Local', 'Chromium', 'Application', 'chrome.exe'),
    ] : [
        // Ubuntu/Debian (apt install chromium-browser / chromium)
        '/usr/bin/chromium-browser',
        '/usr/bin/chromium',
        // Google Chrome no Linux
        '/usr/bin/google-chrome',
        '/usr/bin/google-chrome-stable',
        // Snap (Ubuntu snap install chromium)
        '/snap/bin/chromium',
        // Arch/Fedora/outros
        '/usr/bin/chromium-browser-stable',
        '/usr/local/bin/chromium',
    ]
    for (const p of systemPaths) {
        try { if (fs.existsSync(p)) return p } catch { /* ignorar */ }
    }
    return null // puppeteer decide sozinho
}

function getPuppeteerOptions() {
    const options = {
        headless: true,
        userAgent: process.env.WHATSAPP_USER_AGENT || 'Mozilla/5.0 (X11; Linux aarch64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36',
        args: [
            '--no-sandbox',
            '--disable-setuid-sandbox',
            '--disable-dev-shm-usage',
            '--disable-accelerated-2d-canvas',
            '--no-first-run',
            '--no-zygote',
            '--disable-gpu',
            '--disable-extensions',
            '--disable-default-apps'
        ]
    }

    const chromePath = resolveChromePath()
    if (chromePath) {
        options.executablePath = chromePath
        console.log('[Bot] Chrome:', chromePath)
    }

    return options
}

function clearInitWatchdog() {
    if (initWatchdogTimer) {
        clearTimeout(initWatchdogTimer)
        initWatchdogTimer = null
    }
}

function scheduleInitWatchdog(reason, timeoutMs = Number(process.env.WHATSAPP_INIT_TIMEOUT_MS || 45000)) {
    clearInitWatchdog()
    initWatchdogTimer = setTimeout(() => {
        if (connectionStatus === 'connected' || connectionStatus === 'qr') return

        console.warn(`[Bot] Inicializacao travada em "${connectionStatus}" (${reason}). Limpando sessao local para gerar novo QR.`)
        cleanupAndRestart(true, { force: true }).catch(err => console.error('[Bot] Erro no watchdog de inicializacao:', err.message))
    }, timeoutMs)
}

function shouldClearSessionOnDisconnect(reason) {
    return /logout|unpaired|unlaunched|invalid|auth|not authorized|401/i.test(String(reason || ''))
}

function requestSessionReset(reason, details) {
    console.warn(`[Bot] Sessao invalida detectada (${reason}). ${details || ''}`.trim())
    connectionStatus = 'error'
    clearInitWatchdog()
    setTimeout(() => {
        cleanupAndRestart(true, { force: true }).catch(err => console.error('[Bot] Erro ao limpar sessao invalida:', err.message))
    }, 1000)
}

/**
 * Cria uma nova instância do cliente WhatsApp com todos os ouvintes de eventos
 */
function createClientInstance() {
    console.log('[Bot] Criando nova instância do cliente...');
    
    const newClient = new Client({
        authStrategy: new LocalAuth({ dataPath: sessionPath }),
        puppeteer: getPuppeteerOptions()
    });

    newClient.on('loading_screen', (percent, message) => {
        console.log(`[Bot] Carregando WhatsApp: ${percent}% - ${message}`);
    });

    newClient.on('authenticated', () => {
        console.log('[Bot] Autenticado com sucesso. Aguardando sincronização...');
        connectionStatus = 'authenticated';
        latestQr = null;
        latestQrImage = null;
        latestQrAt = null;
        latestQrCreatedAtMs = 0;
    });

    newClient.on('auth_failure', msg => {
        console.error('[Bot] Falha na autenticação:', msg);
        requestSessionReset('auth_failure', msg);
    });

    newClient.on('qr', async (qr) => {
        console.log('[Bot] QR Code gerado! Escaneie com o WhatsApp.');
        latestQr = qr;
        latestQrAt = new Date().toISOString();
        latestQrCreatedAtMs = Date.now();
        connectionStatus = 'qr';

        try {
            latestQrImage = await QRCode.toDataURL(qr);
        } catch (err) {
            console.error('[Bot] Não foi possível gerar imagem do QR:', err.message);
            latestQrImage = null;
        }

        // Garantir que o lock de inicialização seja liberado ao mostrar o QR
        isInitializing = false;
        clearInitWatchdog();

        // Se já havia conectado antes, a sessão caiu e exige RE-QR → alerta crítico proativo.
        if (everConnected) {
            notifyBridgeStatus('WhatsApp precisa de novo QR', 'A sessao caiu e nao pode ser restaurada. Reescaneie o QR Code no painel para reconectar o bot.', 'Critical');
        }

        // Se há um número aguardando pareamento por código, gera o código automaticamente.
        // O requestPairingCode DEVE ser chamado após o evento qr (quando o cliente está pronto para parear).
        if (pairingCodePendingPhone) {
            const phone = pairingCodePendingPhone;
            pairingCodePendingPhone = null;
            try {
                const rawCode = await newClient.requestPairingCode(phone);
                latestPairingCode = rawCode && rawCode.length === 8
                    ? `${rawCode.slice(0, 4)}-${rawCode.slice(4)}`
                    : (rawCode || null);
                latestPairingCodeAt = new Date().toISOString();
                latestPairingCodeMs = Date.now();
                console.log(`[Bot] Código de pareamento para ${phone}: ${latestPairingCode}`);
            } catch (e) {
                console.error(`[Bot] Erro ao gerar código de pareamento para ${phone}:`, e.message);
                latestPairingCode = null;
            }
        }
    });

    newClient.on('ready', () => {
        botStartTime = Math.floor(Date.now() / 1000);
        latestQr = null;
        latestQrImage = null;
        latestQrCreatedAtMs = 0;
        connectionStatus = 'connected';
        // Limpa o estado de pareamento — não é mais necessário após conexão
        latestPairingCode = null;
        latestPairingCodeAt = null;
        latestPairingCodeMs = 0;
        pairingCodePendingPhone = null;
        console.log('[Bot] WhatsApp conectado e pronto!');
        isInitializing = false;
        clearInitWatchdog();
        restartCount = 0;
        const wasConnectedBefore = everConnected;
        everConnected = true;
        // Se já tinha conectado antes, isto é uma RECONEXÃO bem-sucedida → avisa o dashboard.
        if (wasConnectedBefore) {
            lastStatusAlertAt = 0; // garante que o alerta de "reconectado" passe pelo rate-limit
            notifyBridgeStatus('WhatsApp reconectado', 'A conexao do bot foi restaurada automaticamente.', 'Medium');
        }
    });

    newClient.on('disconnected', async (reason) => {
        connectionStatus = 'disconnected';
        console.log('[Bot] Cliente desconectado:', reason);

        // Alerta proativo: só se já estava conectado (queda real, não setup inicial).
        if (everConnected) {
            notifyBridgeStatus('WhatsApp desconectado', `O bot caiu (${reason}). Tentando reconectar automaticamente...`, 'High');
        }

        if (shouldClearSessionOnDisconnect(reason)) {
            requestSessionReset('disconnected', reason);
            return;
        }

        // Prevenção de loop infinito: só tenta reiniciar se não estivermos no meio de um processo
        if (!isInitializing) {
            console.log('[Bot] Agendando reinício automático em 5s...');
            setTimeout(() => cleanupAndRestart(), 5000);
        }
    });

    // Alterado de 'message' para 'message_create' para capturar mensagens enviadas do próprio aparelho
    newClient.on('message', async (msg) => {
        // Ignora mensagens de grupos para evitar que o bot responda em chats coletivos
        if (msg.fromMe || !isProcessablePhone(msg.from)) return;
        
        // Se a mensagem for sua (fromMe), só processamos se você estiver enviando para você mesmo (Self-Chat)
        // Isso permite testar o bot digitando na sua própria conversa
        

        if (botStartTime && msg.timestamp < botStartTime) return;

        const text = (msg.body || '').trim() || `[${msg.type || 'midia'}]`;
        console.log(`[Bridge] Mensagem recebida de [${msg.from}]: ${text}`);
        totalMessagesReceived++;

        handleIncomingMessage({
            phone: msg.from,
            text,
            timestamp: Date.now(),
            storeId: Number(STORE_ID_RESOLVED)
        });
    });

    newClient.on('vote_update', (vote) => {
        const parentId = getPollMessageId(vote);
        const voter = normalizeJid(vote.voter);
        const selected = vote.selectedOptions?.[0];
        if (!parentId || !voter || !selected) return;

        // Global: ignora votos cuja origem nao seja conversa individual (grupos/listas).
        if (!isProcessablePhone(voter)) return;

        // Validação de Poll: Verifica se é a última poll ou se a poll existe no cache global
        const activePollId = lastPollIdPerChat.get(voter);
        const pollExists = sentPolls.has(parentId);

        if (parentId !== activePollId && !pollExists) {
            console.log(`[Bot] Voto ignorado (Poll expirada) de [${voter}]`);
            return;
        }

        const poll = sentPolls.get(parentId);
        if (!poll) return;

        // Resolve o valor do voto pelo NOME (label) da opção — robusto a divergências de índice.
        // Por que: em enquetes maiores (ex.: 10 horários + "Ver mais" + "Voltar"), o localId
        // reportado pelo whatsapp-web.js pode não bater com a posição no array, fazendo as
        // opções do FIM da lista caírem em undefined e o voto ser descartado em silêncio
        // ("não responde"). Casar por label elimina esse risco; o índice fica como fallback.
        const selectedName = selected.name ?? selected.localName ?? null;
        let selectedValue = null;
        if (selectedName != null) {
            const byLabel = poll.options.find(o => o.label === selectedName);
            if (byLabel) selectedValue = byLabel.value;
        }
        if (selectedValue == null && Number.isInteger(selected.localId)) {
            selectedValue = poll.options[selected.localId]?.value;
        }
        if (!selectedValue) {
            console.warn(`[Bot] Voto sem valor resolvido de [${voter}] (name=${selectedName ?? '-'}, localId=${selected.localId ?? '-'}). Ignorado.`);
            return;
        }

        // Dedup inclui o valor selecionado: permite que o cliente MUDE o voto na mesma enquete
        // (ex.: tocar "Ver mais" e depois um horário), mas bloqueia re-disparos idênticos do
        // Puppeteer para o mesmo par (enquete + opção).
        const voteKey = `${parentId}:${voter}:${selectedValue}`;
        if (handledPollVotes.has(voteKey)) return;

        handledPollVotes.add(voteKey);
        
        // Limpeza de cache para economizar memória (mantém apenas os últimos 500 votos)
        if (handledPollVotes.size > 500) {
            const firstItem = handledPollVotes.values().next().value;
            handledPollVotes.delete(firstItem);
        }

        handleIncomingMessage({
            phone: voter,
            text: selectedValue,
            timestamp: Date.now(),
            storeId: Number(STORE_ID_RESOLVED),
            source: 'poll',
            pollId: parentId // Enviamos o ID para o C# validar
        });
    });

    return newClient;
}

/**
 * Finaliza a instância atual de forma limpa e inicia uma nova
 */
async function cleanupAndRestart(forceClearSession = false, options = {}) {
    if (isInitializing && !options.force) return;
    isInitializing = true;
    restartCount++;

    console.log(`[Bot] Tentativa de inicialização #${restartCount}...`);

    try {
        if (restartCount > 5 && !forceClearSession) {
            console.log('[Bot] Muitas falhas consecutivas. Forçando limpeza de sessão para tentar recuperar...');
            forceClearSession = true;
        }

        if (client) {
            client.removeAllListeners();
            await client.destroy().catch(e => console.log('[Bot] Erro ao destruir cliente:', e.message));
            client = null;
        }

        if (forceClearSession) {
            console.log('[Bot] Limpando pasta de sessão...');
            latestQr = null;
            latestQrImage = null;
            latestQrAt = null;
            if (fs.existsSync(sessionPath)) {
                fs.rmSync(sessionPath, { recursive: true, force: true });
            }
        }

        // Pequena pausa para garantir que os processos do SO foram liberados
        await new Promise(resolve => setTimeout(resolve, 2000));
        
        client = createClientInstance();
        console.log('[Bot] Inicializando navegador Chromium... (pode demorar alguns segundos)');
        connectionStatus = 'starting';
        scheduleInitWatchdog(forceClearSession ? 'forced-clean-session' : 'normal-start');
        await client.initialize();
        console.log('[Bot] Navegador inicializado com sucesso.');
    } catch (e) {
        console.error('[Bot] Falha crítica no reinício:', e.message);
        connectionStatus = 'error';
        clearInitWatchdog();
        // Em caso de erro, permite nova tentativa após 10s
        setTimeout(() => { isInitializing = false; }, 10000);
    }
}

/**
 * Inicialização controlada: 
 * Só inicia o bot no boot se houver uma sessão. 
 * Se não, espera o comando do usuário.
 */
async function initializeClient() {
    if (isInitializing) return;
    
    const hasSession = fs.existsSync(sessionPath);
    if (hasSession) {
        console.log('[Bot] Sessão encontrada. Iniciando automaticamente...');
        await cleanupAndRestart();

        // Se a sessão existir mas não produzir QR nem conectar, força limpeza depois de 30s.
        setTimeout(() => {
            if (connectionStatus !== 'connected' && connectionStatus !== 'authenticated' && connectionStatus !== 'qr') {
                console.log('[Bot] Sessão existente sem conexão. Tentando limpar sessão e gerar QR novamente.');
                cleanupAndRestart(true).catch(err => console.error('[Bot] Erro no reinício forçado:', err.message));
            }
        }, 30000);
    } else {
        console.log('[Bot] Nenhuma sessão ativa. Aguardando comando manual para gerar QR Code.');
        connectionStatus = 'disconnected';
    }
}

function getPollMessageId(vote) {
    if (vote.parentMessage?.id?._serialized) {
        return vote.parentMessage.id._serialized
    }

    const key = vote.parentMsgKey
    if (!key) {
        return null
    }

    if (key._serialized) {
        return key._serialized
    }

    if (key.id) {
        const fromMe = key.fromMe ? 'true' : 'false'
        const remote = key.remote || key.remoteJid || key.remoteJid?._serialized
        return remote ? `${fromMe}_${remote}_${key.id}` : key.id
    }

    return null
}

// Rota: C# busca mensagens pendentes
app.get('/messages', (req, res) => {
    pruneQueue()
    const now = Date.now()
    const msgs = messageQueue
        .filter(msg => isQueueItemProcessable(msg) && (!msg.deliveredAt || now - msg.deliveredAt > 30000))
        .slice(0, 50)
        .map(msg => {
            msg.deliveredAt = now
            msg.attempts = (msg.attempts || 0) + 1
            return msg
        })
    saveQueueToDisk()
    res.json(msgs)
})

app.post('/messages/ack', (req, res) => {
    const ids = Array.isArray(req.body?.ids) ? new Set(req.body.ids.map(String)) : new Set()
    if (ids.size === 0) {
        return res.status(400).json({ error: 'Informe ids para confirmar processamento.' })
    }

    const before = messageQueue.length
    for (let i = messageQueue.length - 1; i >= 0; i--) {
        if (ids.has(String(messageQueue[i].id))) {
            messageQueue.splice(i, 1)
        }
    }
    saveQueueToDisk()
    res.json({ ok: true, removed: before - messageQueue.length, remaining: messageQueue.length })
})

app.get('/messages/pending', (req, res) => {
    prunePausedPending()
    const items = [...pausedPending.values()].map(item => ({
        phone: item.phone,
        updatedAt: item.updatedAt,
        firstPausedAt: item.firstPausedAt,
        suppressedCount: item.suppressedCount || 0,
        source: item.source || 'paused',
        preview: item.text.length > 80 ? `${item.text.slice(0, 77)}...` : item.text
    }))

    res.json({
        count: items.length,
        maxAgeMs: pausedPendingMaxAgeMs,
        items
    })
})

app.post('/messages/pending/clear', (req, res) => {
    const before = pausedPending.size
    pausedPending.clear()
    savePausedPendingToDisk()
    res.json({ ok: true, removed: before, remaining: 0 })
})

// Rota para simular o recebimento de uma mensagem (útil para testes sem WhatsApp real)
app.post('/test/receive', (req, res) => {
    let { phone, text } = req.body
    phone = phone || req.body.from
    text = text || req.body.body
    if (!phone || !text) {
        return res.status(400).json({ error: 'Informe phone e text.' })
    }

    phone = phone.trim();
    const jid = normalizeJid(phone.includes('@') ? phone : `${phone}@c.us`);
    totalMessagesReceived++;

    const result = handleIncomingMessage({
        phone: jid,
        text: text,
        timestamp: Math.floor(Date.now() / 1000)
    })

    console.log(`[TESTE] Mensagem simulada de [${jid}]: ${text}`)
    res.json({
        ok: true,
        mode: botEnabled ? 'queued' : 'paused-latest',
        message: botEnabled ? 'Mensagem inserida na fila.' : 'Bot pausado: somente a ultima mensagem deste contato foi guardada.',
        id: result?.id || null,
        pendingCount: pausedPending.size
    })
})

// Rota: C# envia resposta
app.post('/send', async (req, res) => {
    let { phone, text } = req.body
    
    if (!client || connectionStatus !== 'connected') {
        return res.status(503).json({ error: 'WhatsApp não está conectado.' })
    }

    if (!botEnabled) {
        return res.status(409).json({ error: 'Bot pausado pelo dashboard.' })
    }

    try {
        phone = phone.trim();
        const jid = await resolveRecipientJid(phone);

        // Global: o bot nunca envia para grupos, newsletters ou broadcast — apenas conversas individuais.
        if (!isProcessablePhone(jid)) {
            console.warn(`[Bridge] Envio bloqueado para destino nao-individual: ${jid}`)
            return res.status(400).json({ error: 'Envio para grupos/listas nao e permitido. O bot atende apenas conversas individuais.' })
        }

        await retryAsync(() => client.sendMessage(jid, text), 3, 600);
        totalMessagesSent++;
        console.log(`Enviado para [${phone}]: ${text.substring(0, 30)}`)
        res.json({ ok: true })
    } catch (e) {
        console.error('Erro ao enviar:', e.message)
        const message = isNoLidError(e)
            ? 'Nao foi possivel resolver o contato no WhatsApp. Use DDI+DDD+numero, exemplo 5562994014135, e confirme que o numero existe no WhatsApp.'
            : 'Falha no envio de mensagem'
        res.status(500).json({ error: message, details: e.message })
    }
})

// Rota: C# envia enquete com opcoes mapeadas para valores internos
app.post('/send-poll', async (req, res) => {
    let { phone, question, options, allowMultipleAnswers } = req.body

    if (!client || connectionStatus !== 'connected') {
        return res.status(503).json({ error: 'WhatsApp nao esta conectado.' })
    }

    if (!botEnabled) {
        return res.status(409).json({ error: 'Bot pausado pelo dashboard.' })
    }

    if (!phone || !question || !Array.isArray(options) || options.length < 2) {
        return res.status(400).json({ error: 'Informe phone, question e pelo menos 2 options.' })
    }

    try {
        phone = phone.trim();
        const jid = await resolveRecipientJid(phone);

        // Global: enquetes nunca vao para grupos/listas — apenas conversas individuais.
        if (!isProcessablePhone(jid)) {
            console.warn(`[Bridge] Envio de enquete bloqueado para destino nao-individual: ${jid}`)
            return res.status(400).json({ error: 'Envio para grupos/listas nao e permitido. O bot atende apenas conversas individuais.' })
        }

        const labels = options.map(option => option.label || option.Label)
        const values = options.map((option, index) => ({
            label: option.label || option.Label,
            value: String(option.value || option.Value || index + 1)
        }))

        const message = await retryAsync(() => client.sendMessage(
            jid,
            new Poll(question, labels, { allowMultipleAnswers: allowMultipleAnswers === true })
        ), 3, 600)

        totalMessagesSent++;
        sentPolls.set(message.id._serialized, {
            question,
            options: values,
            createdAt: Date.now()
        })
        lastPollIdPerChat.set(normalizeJid(jid), message.id._serialized);

        console.log(`Enquete enviada para [${jid}]: ${question}`)
        res.json({ ok: true, id: message.id._serialized })
    } catch (e) {
        console.error('Erro ao enviar enquete:', e.message)
        const message = isNoLidError(e)
            ? 'Nao foi possivel resolver o contato no WhatsApp. Use DDI+DDD+numero, exemplo 5562994014135, e confirme que o numero existe no WhatsApp.'
            : e.message
        res.status(500).json({ error: message, details: e.message })
    }
})

// Restrição Crítica: Ouve apenas em 127.0.0.1 (Localhost)
const server = app.listen(BRIDGE_PORT, BRIDGE_HOST, () => console.log(`Bridge rodando internamente em http://${BRIDGE_HOST}:${BRIDGE_PORT}`))
server.on('error', (error) => {
    console.error('Falha no servidor HTTP do Bridge:', error.message)
    process.exit(1)
})

initializeClient();
setInterval(() => {
    const hasSession = fs.existsSync(sessionPath)
    if (!hasSession || isInitializing || connectionStatus === 'connected' || connectionStatus === 'qr') return
    console.warn(`[Bot] Monitor detectou estado ${connectionStatus}. Agendando reconexao segura.`)
    scheduleReconnect()
}, Number(process.env.WHATSAPP_HEALTH_RECONNECT_MS || 60 * 1000))

function scheduleReconnect() {
    if (reconnectTimer) return

    reconnectTimer = setTimeout(async () => {
        reconnectTimer = null
        try {
            console.log('Tentando reconectar WhatsApp...')
            await initializeClient()
        } catch (e) {
            console.error('Reconexao falhou:', e.message)
            scheduleReconnect()
        }
    }, 10000)
}

function getWhatsAppStatus() {
    // BUGFIX: client pode ser null durante reinicialização — checar antes de acessar .info
    const isReady = client && client.info && client.info.wid
    const memoryUsage = process.memoryUsage();
    const normalizedState = isReady
        ? 'ONLINE'
        : connectionStatus === 'qr'
            ? 'QR_REQUIRED'
            : (isInitializing || connectionStatus === 'starting' || connectionStatus === 'authenticated')
                ? 'RECONNECTING'
                : 'OFFLINE'
    return {
        status: isReady ? 'connected' : connectionStatus,
        state: normalizedState,
        connectionState: normalizedState,
        whatsappConnected: !!isReady,
        botEnabled,
        hasQr: !!latestQrImage,
        qrUpdatedAt: latestQrAt,
        qrAgeMs: latestQrCreatedAtMs ? Date.now() - latestQrCreatedAtMs : null,
        qrDisplayTtlMs,
        qrRenewAfterMs,
        // Pareamento por código
        pairingCode: (latestPairingCodeMs && Date.now() - latestPairingCodeMs > PAIRING_CODE_TTL_MS) ? null : latestPairingCode,
        pairingCodeAt: latestPairingCodeAt,
        pairingCodeAgeMs: latestPairingCodeMs ? Date.now() - latestPairingCodeMs : null,
        pairingCodeTtlMs: PAIRING_CODE_TTL_MS,
        pairingCodePending: !!pairingCodePendingPhone,
        phone: isReady ? client.info.wid.user : null,
        pushname: isReady ? client.info.pushname : null,
        uptimeSeconds: Math.floor((Date.now() - startedAt.getTime()) / 1000),
        queueSize: messageQueue.length,
        pausedPendingCount: pausedPending.size,
        pausedPendingMaxAgeMs,
        antiSpam: {
            dedupeSize: incomingDedupe.size,
            trackedContacts: incomingRate.size,
            pollCacheSize: sentPolls.size
        },
        throughput: {
            received: totalMessagesReceived,
            sent: totalMessagesSent
        },
        timestamp: new Date().toISOString(),
        system: {
            platform: os.platform(),
            arch: os.arch(),
            freeMem: Math.round(os.freemem() / 1024 / 1024) + 'MB',
            cpuLoad: os.loadavg(),
            nodeVersion: process.version,
            memoryRss: Math.round(memoryUsage.rss / 1024 / 1024) + 'MB'
        }
    }
}

// Health check
app.get('/health', (req, res) => {
    const status = getWhatsAppStatus()
    res.json({
        ...status,
        status: status.whatsappConnected ? 'healthy' : 'unhealthy'
    })
})

app.get('/status', (req, res) => {
    res.json(getWhatsAppStatus())
})

app.get('/qr', (req, res) => {
    const qrIsStale = latestQrCreatedAtMs && Date.now() - latestQrCreatedAtMs > qrRenewAfterMs
    const qrTooOldToDisplay = latestQrCreatedAtMs && Date.now() - latestQrCreatedAtMs > qrDisplayTtlMs
    if (qrTooOldToDisplay) {
        latestQr = null
        latestQrImage = null
        latestQrAt = null
        latestQrCreatedAtMs = 0
    }

    if ((!latestQrImage || qrIsStale) && !isInitializing && connectionStatus !== 'connected') {
        cleanupAndRestart(false).catch(err => console.error('[Bot] Erro ao iniciar geracao de QR:', err.message))
    }

    res.json({
        ...getWhatsAppStatus(),
        qr: latestQr,
        qrImage: latestQrImage
    })
})

// ── Pareamento por código numérico ───────────────────────────────────────────
// POST /pairing-code  { phone: "5511999999999" }
// Gera um código de 8 caracteres que o usuário digita no WhatsApp em vez de escanear QR.
app.post('/pairing-code', async (req, res) => {
    const raw = String(req.body?.phone || '').replace(/\D/g, '');
    if (!raw || raw.length < 7 || raw.length > 15) {
        return res.status(400).json({ error: 'Informe um número válido no formato internacional sem + ou espaços. Ex: 5511999999999' });
    }

    if (connectionStatus === 'connected') {
        return res.status(400).json({ error: 'WhatsApp já está conectado. Desconecte primeiro para parear novamente.' });
    }

    try {
        if (connectionStatus === 'qr' && client) {
            // Cliente já está na fase QR — podemos solicitar o código agora
            const rawCode = await client.requestPairingCode(raw);
            latestPairingCode = rawCode && rawCode.length === 8
                ? `${rawCode.slice(0, 4)}-${rawCode.slice(4)}`
                : (rawCode || null);
            latestPairingCodeAt = new Date().toISOString();
            latestPairingCodeMs = Date.now();
            console.log(`[Bot] Código de pareamento gerado para ${raw}: ${latestPairingCode}`);
            return res.json({ code: latestPairingCode, phone: raw, requestedAt: latestPairingCodeAt, pending: false });
        } else {
            // Ainda não estamos na fase QR — armazena o número e inicia a conexão
            pairingCodePendingPhone = raw;
            latestPairingCode = null;
            latestPairingCodeAt = null;
            latestPairingCodeMs = 0;
            if (!isInitializing) {
                cleanupAndRestart(false).catch(e => console.error('[Bot] Erro ao iniciar para pareamento:', e.message));
            }
            return res.json({ code: null, phone: raw, pending: true, message: 'Iniciando conexão. O código será gerado em instantes. Consulte GET /pairing-code/status.' });
        }
    } catch (e) {
        console.error('[Bot] Erro ao gerar código de pareamento:', e.message);
        return res.status(500).json({ error: `Erro ao gerar código: ${e.message}` });
    }
});

// GET /pairing-code/status — retorna o código atual e metadados (para polling do dashboard)
app.get('/pairing-code/status', (req, res) => {
    const ageMs = latestPairingCodeMs ? Date.now() - latestPairingCodeMs : null;
    const expired = ageMs !== null && ageMs > PAIRING_CODE_TTL_MS;
    res.json({
        code: expired ? null : latestPairingCode,
        requestedAt: latestPairingCodeAt,
        codeAgeMs: ageMs,
        expired,
        pending: !!pairingCodePendingPhone,
        pendingPhone: pairingCodePendingPhone,
        connectionStatus
    });
});

app.post('/bot/toggle', (req, res) => {
    if (typeof req.body.enabled !== 'boolean') {
        return res.status(400).json({ error: 'Informe enabled como true ou false.' })
    }

    const wasEnabled = botEnabled
    botEnabled = req.body.enabled
    saveBotEnabled(botEnabled)
    const flushedPausedPending = !wasEnabled && botEnabled
        ? flushPausedPending('bot-enabled')
        : 0
    if (botEnabled && !client && !isInitializing && connectionStatus !== 'connected') {
        cleanupAndRestart(false).catch(err => console.error('[Bot] Erro ao iniciar WhatsApp apos ativar bot:', err.message))
    }
    console.log(`Bot ${botEnabled ? 'ativado' : 'pausado'} pelo dashboard.`)
    res.json({
        ...getWhatsAppStatus(),
        flushedPausedPending
    })
})

app.post('/logout', async (req, res) => {
    try {
        console.log('[Bot] Logout solicitado pelo dashboard.');
        await cleanupAndRestart(true); // Logout força limpeza de sessão
        res.json({ ok: true, status: 'restarting' });
    } catch (e) {
        console.error('Erro ao desconectar WhatsApp:', e.message)
        res.status(500).json({ error: e.message })
    }
})

process.on('unhandledRejection', (reason) => {
    console.error('Unhandled rejection no Bridge:', reason)
})

process.on('uncaughtException', (error) => {
    console.error('Uncaught exception no Bridge:', error)
    process.exit(1)
})

async function shutdown(signal) {
    console.log(`${signal} recebido. Encerrando Bridge...`)
    try {
        clearTimeout(reconnectTimer)
        if (client) {
            // Tenta destruir graciosamente com timeout de 2s
            await Promise.race([
                client.destroy(),
                new Promise(resolve => setTimeout(resolve, 2000))
            ]);
        }
    } catch (e) {
        console.error('Erro ao encerrar cliente WhatsApp:', e.message)
    } finally {
        console.log('[Bot] Encerrando processo Node.');
        server.close();
        // Força a saída em 1s caso o Puppeteer ainda esteja prendendo o processo
        setTimeout(() => process.exit(0), 1000).unref();
    }
}

process.on('SIGINT', () => shutdown('SIGINT'))
process.on('SIGTERM', () => shutdown('SIGTERM'))

// Permite simular mensagens via console diretamente (ex: digitar 5511999999999:oi no terminal do Bridge)
process.stdin.on('data', (data) => {
    const input = data.toString().trim()
    if (input.includes(':')) {
        const [phone, ...textParts] = input.split(':')
        const text = textParts.join(':').trim()
        const jid = phone.trim().includes('@') ? phone.trim() : `${phone.trim()}@c.us`
        messageQueue.push({
            phone: jid,
            text: text,
            timestamp: Math.floor(Date.now() / 1000)
        })
        console.log(`[CONSOLE] Simulação enviada: ${jid} -> ${text}`)
    }
})
