"use strict";

const fs = require("node:fs");
const http = require("node:http");
const path = require("node:path");

const TAG = "[catpaw-bootstrap]";
const LOOPBACK = "127.0.0.1";

let runtime = null;
let stopping = false;

function fail(message, error) {
  const detail = error && (error.message || String(error));
  console.error(`${TAG} ${message}${detail ? `: ${detail}` : ""}`);
  process.exitCode = 1;
}

function resolveInput(value, fallback) {
  return path.resolve(value || path.join(process.cwd(), fallback));
}

function loadModule(file, label) {
  if (!fs.existsSync(file)) throw new Error(`${label} not found: ${file}`);
  try {
    return require(file);
  } catch (error) {
    throw new Error(`failed to load ${label}: ${error.message || error}`);
  }
}

function unwrapDefault(value) {
  if (value && typeof value === "object" && "default" in value) return value.default;
  return value;
}

function unwrapRuntime(value) {
  if (value && typeof value.start === "function") return value;
  const nested = value && value.default;
  return nested && typeof nested.start === "function" ? nested : value;
}

function tolerateDuplicateHeaders(server) {
  // Older CatPaw bundles can send a Fastify reply twice under newer Node versions.
  server.prependListener("request", (_request, response) => {
    const writeHead = response.writeHead;
    response.writeHead = function (...args) {
      if (response.headersSent) return response;
      return Reflect.apply(writeHead, response, args);
    };
  });
  return server;
}

function forceLoopback(server) {
  const listen = server.listen;
  server.listen = function (...args) {
    if (args[0] && typeof args[0] === "object") {
      args[0] = { ...args[0], host: LOOPBACK };
    } else if (typeof args[0] === "number") {
      if (typeof args[1] === "string") args[1] = LOOPBACK;
      else args.splice(1, 0, LOOPBACK);
    }
    return Reflect.apply(listen, server, args);
  };
  return server;
}

const nativeCreateServer = http.createServer.bind(http);
http.createServer = (...args) =>
  forceLoopback(tolerateDuplicateHeaders(nativeCreateServer(...args)));

globalThis.catServerFactory = (handler) => http.createServer(handler);
globalThis.catDartServerPort = () => 0;

async function stop(exitCode) {
  if (stopping) return;
  stopping = true;
  try {
    if (runtime && typeof runtime.stop === "function") await runtime.stop();
  } catch (error) {
    fail("stop failed", error);
    exitCode = 1;
  } finally {
    process.exit(exitCode);
  }
}

async function main() {
  const scriptPath = resolveInput(process.argv[2], "index.js");
  const configPath = resolveInput(process.argv[3], "index.config.js");
  const port = process.env.CATPAW_PORT || process.env.PORT || process.env.DEV_HTTP_PORT;

  if (port) {
    process.env.DEV_HTTP_PORT = port;
    process.env.PORT = port;
  }
  process.env.DEV_HTTP_HOST = LOOPBACK;
  process.env.HOST = LOOPBACK;
  process.env.CATVOD_DISABLE_AUTOSTART = "1";
  process.env.NODE_PATH =
    process.env.CATPAW_DATA_DIR || process.env.NODE_PATH || path.dirname(scriptPath);
  fs.mkdirSync(process.env.NODE_PATH, { recursive: true });

  const config = unwrapDefault(loadModule(configPath, "index.config.js"));
  runtime = unwrapRuntime(loadModule(scriptPath, "index.js"));
  if (!runtime || typeof runtime.start !== "function") {
    throw new Error("index.js does not export start(config)");
  }

  await runtime.start(config || {});
}

process.once("SIGINT", () => void stop(0));
process.once("SIGTERM", () => void stop(0));

main().catch((error) => {
  fail("startup failed", error);
  void stop(1);
});
