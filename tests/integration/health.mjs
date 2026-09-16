import assert from "node:assert/strict";

import { DEFAULT_ENDPOINT, checkHealth } from "../../packages/sdk/src/index.ts";

const TIMEOUT_MS = 3_000;
const EXTERNAL_ORIGIN = "https://answersheet-integration.invalid";

async function main() {
  await verifySdkHealth();
  await verifyExternalOriginIsRejected();
  await verifyUnknownPathIsNotFound();
  await verifyHealthRejectsPost();

  console.log("PASS: local health integration checks completed.");
}

async function verifySdkHealth() {
  const health = await checkHealth({ timeoutMs: TIMEOUT_MS });
  assert.deepEqual(health, {
    service: "answersheet-local",
    protocolVersion: 1,
    status: "ready",
  });
  console.log("PASS: SDK health check returned the M0 ready response.");
}

async function verifyExternalOriginIsRejected() {
  const response = await request("/health", {
    headers: { Origin: EXTERNAL_ORIGIN },
  });
  assert.equal(response.status, 403, "an arbitrary external Origin must be rejected");
  console.log("PASS: arbitrary external Origin was rejected with HTTP 403.");
}

async function verifyUnknownPathIsNotFound() {
  const response = await request("/integration-path-that-does-not-exist");
  assert.equal(response.status, 404, "unknown paths must return HTTP 404");
  console.log("PASS: unknown path returned HTTP 404.");
}

async function verifyHealthRejectsPost() {
  const response = await request("/health", { method: "POST" });
  assert.equal(response.status, 405, "POST /health must return HTTP 405");
  console.log("PASS: POST /health returned HTTP 405.");
}

function request(path, init = {}) {
  return fetch(`${DEFAULT_ENDPOINT}${path}`, {
    ...init,
    signal: AbortSignal.timeout(TIMEOUT_MS),
    redirect: "error",
  });
}

main().catch((error) => {
  console.error("FAIL: local health integration check failed.");
  console.error(error instanceof Error ? error.message : error);
  console.error(`Start the desktop app, then run this script again. Each request is limited to ${TIMEOUT_MS} ms.`);
  process.exitCode = 1;
});
