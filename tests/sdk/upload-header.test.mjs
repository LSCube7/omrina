import assert from "node:assert/strict";
import { createServer } from "node:http";
import test from "node:test";

import { OmrinaClient } from "../../packages/sdk/src/index.ts";

function snapshot() {
  return {
    taskId: "upload-task-1",
    operation: "upload",
    status: "queued",
    result: null,
    error: null,
    createdAt: "2026-10-02T00:00:00Z",
    updatedAt: "2026-10-02T00:00:00Z",
  };
}

test("中文图像文件名通过真实 Node fetch Header 编码并保持参数内容", async (t) => {
  let receivedResolve;
  const receivedRequest = new Promise((resolve) => {
    receivedResolve = resolve;
  });
  const server = createServer(async (request, response) => {
    const chunks = [];
    for await (const chunk of request) {
      chunks.push(chunk);
    }
    receivedResolve({
      method: request.method,
      url: request.url,
      origin: request.headers.origin,
      contentType: request.headers["content-type"],
      idempotencyKey: request.headers["x-idempotency-key"],
      parameters: request.headers["x-omrina-parameters"],
      body: Buffer.concat(chunks),
    });
    response.writeHead(202, { "Content-Type": "application/json" });
    response.end(JSON.stringify(snapshot()));
  });

  await new Promise((resolve, reject) => {
    server.once("error", reject);
    server.listen(0, "127.0.0.1", resolve);
  });
  t.after(() => new Promise((resolve, reject) => {
    server.close((error) => error ? reject(error) : resolve());
  }));

  const address = server.address();
  assert.ok(address && typeof address === "object");
  const client = new OmrinaClient({
    endpoint: `http://127.0.0.1:${address.port}`,
    grant: {
      grantId: "grant-1",
      token: "secret-token",
      expiresAt: new Date(Date.now() + 4 * 60 * 60 * 1000).toISOString(),
    },
    fetch: (input, init) => {
      const headers = new Headers(init.headers);
      headers.set("Origin", "http://localhost:3000");
      return fetch(input, { ...init, headers });
    },
  });
  const imageBytes = new Uint8Array([137, 80, 78, 71, 13, 10, 26, 10]);
  await client.uploadImage({
    idempotencyKey: "upload-key",
    templateId: "template-default",
    fileName: "答题纸-😀.png",
    contentType: "image/png",
    body: imageBytes,
  });

  const received = await receivedRequest;
  assert.equal(received.method, "POST");
  assert.equal(received.url, "/v1/tasks/upload");
  assert.equal(received.origin, "http://localhost:3000");
  assert.equal(received.contentType, "image/png");
  assert.equal(received.idempotencyKey, "upload-key");
  assert.match(received.parameters, /\\u/);
  assert.equal(Buffer.byteLength(received.parameters, "ascii"), Buffer.byteLength(received.parameters, "utf8"));
  assert.deepEqual(JSON.parse(received.parameters), {
    templateId: "template-default",
    fileName: "答题纸-😀.png",
  });
  assert.deepEqual(received.body, Buffer.from(imageBytes));
});
