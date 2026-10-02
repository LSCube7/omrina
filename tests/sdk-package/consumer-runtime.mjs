import assert from "node:assert/strict";

import { OmrinaClient, checkHealth } from "@omrina/local-sdk";

function response(payload, status = 200) {
  return { ok: status >= 200 && status < 300, status, json: async () => payload };
}

const health = await checkHealth({
  endpoint: "http://127.0.0.1:17843",
  fetch: async (input, init) => {
    assert.equal(input, "http://127.0.0.1:17843/health");
    assert.equal(init.method, "GET");
    return response({ service: "omrina-local", protocolVersion: 1, status: "ready" });
  },
});
assert.equal(health.status, "ready");

const client = new OmrinaClient({
  endpoint: "http://127.0.0.1:17843",
  fetch: async (input, init) => {
    assert.equal(new URL(input).pathname, "/v1/pairing/requests");
    assert.equal(init.method, "POST");
    assert.deepEqual(JSON.parse(init.body), { clientName: "Packed SDK smoke" });
    return response({ requestId: "consumer-pairing", expiresAt: "2027-01-01T00:00:00Z" }, 202);
  },
});
const ticket = await client.requestPairing({ clientName: "Packed SDK smoke" });
assert.equal(ticket.requestId, "consumer-pairing");
console.log("PASS actual package exports: checkHealth and OmrinaClient");
