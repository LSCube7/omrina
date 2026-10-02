import { createServer } from "node:http";
import { readFile } from "node:fs/promises";
import { resolve } from "node:path";
import { fileURLToPath } from "node:url";

const HOST = "127.0.0.1";
const DEFAULT_PORT = 17846;
const ROOT = resolve(fileURLToPath(new URL("../..", import.meta.url)));

const routes = new Map([
  ["/", { path: resolve(ROOT, "tests/integration/m3-browser.html"), type: "text/html; charset=utf-8" }],
  ["/m3-browser.html", { path: resolve(ROOT, "tests/integration/m3-browser.html"), type: "text/html; charset=utf-8" }],
  ["/sdk/index.js", { path: resolve(ROOT, "packages/sdk/dist/index.js"), type: "text/javascript; charset=utf-8" }],
  ["/sdk/client.js", { path: resolve(ROOT, "packages/sdk/dist/client.js"), type: "text/javascript; charset=utf-8" }],
  ["/sdk/errors.js", { path: resolve(ROOT, "packages/sdk/dist/errors.js"), type: "text/javascript; charset=utf-8" }],
  ["/sdk/health.js", { path: resolve(ROOT, "packages/sdk/dist/health.js"), type: "text/javascript; charset=utf-8" }],
]);

const requestedPort = Number(process.argv[2] ?? DEFAULT_PORT);
if (!Number.isInteger(requestedPort) || requestedPort < 1024 || requestedPort > 65535) {
  throw new Error("Port must be an integer between 1024 and 65535.");
}

const server = createServer(async (request, response) => {
  if (request.method !== "GET" && request.method !== "HEAD") {
    response.writeHead(405, { Allow: "GET, HEAD" });
    response.end();
    return;
  }

  let pathname;
  try {
    pathname = new URL(request.url ?? "/", `http://${HOST}`).pathname;
  } catch {
    response.writeHead(400);
    response.end("Bad request");
    return;
  }

  const route = routes.get(pathname);
  if (!route) {
    response.writeHead(404);
    response.end("Not found");
    return;
  }

  try {
    const content = await readFile(route.path);
    response.writeHead(200, {
      "Cache-Control": "no-store",
      "Content-Length": content.byteLength,
      "Content-Type": route.type,
      "X-Content-Type-Options": "nosniff",
      "Referrer-Policy": "no-referrer",
    });
    response.end(request.method === "HEAD" ? undefined : content);
  } catch {
    response.writeHead(503, { "Cache-Control": "no-store" });
    response.end("Build the SDK first with npm run build in packages/sdk.");
  }
});

server.on("error", (error) => {
  console.error(`M3 SDK static server failed: ${error.message}`);
  process.exitCode = 1;
});

server.listen(requestedPort, HOST, () => {
  console.log(`OMRINA M3 SDK browser harness: http://${HOST}:${requestedPort}/m3-browser.html`);
  console.log("Listening on 127.0.0.1 only. Press Ctrl+C to stop.");
});
