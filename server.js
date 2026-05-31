const http = require("node:http");
const fs = require("node:fs");
const path = require("node:path");

const PORT = Number(process.env.PORT || 5058);
const HOST = process.env.HOST || "127.0.0.1";
const ROOT = __dirname;
const PUBLIC_DIR = path.join(ROOT, "public");
const STATUS_FILE = path.join(ROOT, "status.json");

const STATES = {
  waiting: {
    state: "waiting",
    label: "等待中",
    color: "red",
    message: "正在等待下一步指令"
  },
  working: {
    state: "working",
    label: "工作中",
    color: "yellow",
    message: "任务正在进行"
  },
  done: {
    state: "done",
    label: "已完成",
    color: "green",
    message: "工作已经结束"
  }
};

const ALIASES = {
  wait: "waiting",
  waiting: "waiting",
  red: "waiting",
  idle: "waiting",
  工作中: "working",
  working: "working",
  work: "working",
  yellow: "working",
  busy: "working",
  done: "done",
  complete: "done",
  completed: "done",
  green: "done",
  finish: "done",
  finished: "done",
  结束: "done",
  完成: "done"
};

let current = loadStatus();
let currentSnapshot = JSON.stringify(current);
const clients = new Set();

function loadStatus() {
  try {
    const parsed = JSON.parse(fs.readFileSync(STATUS_FILE, "utf8"));
    return normalizeStatus(parsed.state, parsed.message, parsed.updatedAt, parsed.source);
  } catch {
    return normalizeStatus("waiting");
  }
}

function normalizeStatus(value, message, updatedAt, source) {
  const state = ALIASES[String(value || "").trim().toLowerCase()] || "waiting";
  const base = STATES[state];
  return {
    ...base,
    message: typeof message === "string" && message.trim() ? message.trim() : base.message,
    updatedAt: updatedAt || new Date().toISOString(),
    source: typeof source === "string" && source.trim() ? source.trim() : "auto"
  };
}

function saveStatus(status) {
  currentSnapshot = JSON.stringify(status);
  fs.writeFileSync(STATUS_FILE, JSON.stringify(status, null, 2));
}

function sendJson(res, statusCode, payload) {
  const body = JSON.stringify(payload);
  res.writeHead(statusCode, {
    "Content-Type": "application/json; charset=utf-8",
    "Content-Length": Buffer.byteLength(body),
    "Access-Control-Allow-Origin": "*",
    "Access-Control-Allow-Headers": "content-type",
    "Access-Control-Allow-Methods": "GET,POST,OPTIONS"
  });
  res.end(body);
}

function sendEvent(res, event, payload) {
  res.write(`event: ${event}\n`);
  res.write(`data: ${JSON.stringify(payload)}\n\n`);
}

function broadcast(status) {
  for (const client of clients) {
    sendEvent(client, "status", status);
  }
}

function updateStatus(state, message, source) {
  current = normalizeStatus(state, message, new Date().toISOString(), source);
  saveStatus(current);
  broadcast(current);
  return current;
}

fs.watchFile(STATUS_FILE, { interval: 500 }, () => {
  const next = loadStatus();
  const nextSnapshot = JSON.stringify(next);
  if (nextSnapshot === currentSnapshot) {
    return;
  }

  current = next;
  currentSnapshot = nextSnapshot;
  broadcast(current);
});

function readBody(req) {
  return new Promise((resolve, reject) => {
    let body = "";
    req.on("data", chunk => {
      body += chunk;
      if (body.length > 1_000_000) {
        req.destroy();
        reject(new Error("Request body too large"));
      }
    });
    req.on("end", () => resolve(body));
    req.on("error", reject);
  });
}

function contentTypeFor(filePath) {
  const ext = path.extname(filePath).toLowerCase();
  return {
    ".html": "text/html; charset=utf-8",
    ".css": "text/css; charset=utf-8",
    ".js": "text/javascript; charset=utf-8",
    ".json": "application/json; charset=utf-8",
    ".svg": "image/svg+xml"
  }[ext] || "application/octet-stream";
}

function serveFile(res, requestPath) {
  const cleanPath = requestPath === "/" ? "/index.html" : requestPath;
  const decodedPath = decodeURIComponent(cleanPath.split("?")[0]);
  const filePath = path.normalize(path.join(PUBLIC_DIR, decodedPath));

  if (!filePath.startsWith(PUBLIC_DIR)) {
    sendJson(res, 403, { error: "Forbidden" });
    return;
  }

  fs.readFile(filePath, (error, data) => {
    if (error) {
      sendJson(res, 404, { error: "Not found" });
      return;
    }

    res.writeHead(200, {
      "Content-Type": contentTypeFor(filePath),
      "Cache-Control": "no-store"
    });
    res.end(data);
  });
}

const server = http.createServer(async (req, res) => {
  const url = new URL(req.url, `http://${req.headers.host}`);

  if (req.method === "OPTIONS") {
    sendJson(res, 204, {});
    return;
  }

  if (req.method === "GET" && url.pathname === "/api/status") {
    sendJson(res, 200, current);
    return;
  }

  if (req.method === "POST" && url.pathname === "/api/status") {
    try {
      const body = await readBody(req);
      const payload = body ? JSON.parse(body) : {};
      const next = updateStatus(payload.state, payload.message, payload.source || "external");
      sendJson(res, 200, next);
    } catch (error) {
      sendJson(res, 400, { error: error.message || "Bad request" });
    }
    return;
  }

  if (req.method === "GET" && url.pathname === "/events") {
    res.writeHead(200, {
      "Content-Type": "text/event-stream; charset=utf-8",
      "Cache-Control": "no-cache",
      "Connection": "keep-alive",
      "Access-Control-Allow-Origin": "*"
    });
    clients.add(res);
    sendEvent(res, "status", current);
    req.on("close", () => clients.delete(res));
    return;
  }

  if (req.method === "GET") {
    serveFile(res, url.pathname);
    return;
  }

  sendJson(res, 405, { error: "Method not allowed" });
});

server.listen(PORT, HOST, () => {
  console.log(`Work Status Light is running at http://${HOST}:${PORT}`);
  console.log("POST /api/status with {\"state\":\"waiting|working|done\"} to switch lights.");
});
