import assert from "node:assert/strict";
import test from "node:test";

import {
  OmrinaSdkError,
  checkHealth,
  DEFAULT_ENDPOINT,
} from "../../packages/sdk/src/index.ts";

const readyResponse = () => ({
  ok: true,
  status: 200,
  json: async () => ({
    service: "omrina-local",
    protocolVersion: 1,
    status: "ready",
  }),
});

test("默认向回环地址的 /health 发起 GET 请求", async () => {
  let request;
  const health = await checkHealth({
    fetch: async (input, init) => {
      request = { input, init };
      return readyResponse();
    },
  });

  assert.deepEqual(health, {
    service: "omrina-local",
    protocolVersion: 1,
    status: "ready",
  });
  assert.equal(request.input, `${DEFAULT_ENDPOINT}/health`);
  assert.equal(request.init.method, "GET");
  assert.ok(request.init.signal instanceof AbortSignal);
  assert.equal(request.init.redirect, "error");
  assert.equal(request.init.credentials, "omit");
});

test("只允许 HTTP 回环端点，并固定访问 /health", async () => {
  for (const endpoint of [
    "https://127.0.0.1:17843",
    "http://example.com",
    "http://192.168.1.8",
    "http://127.0.0.1:17843@evil.example",
    "not a url",
  ]) {
    await assert.rejects(
      checkHealth({ endpoint, fetch: async () => readyResponse() }),
      (error) => error instanceof OmrinaSdkError && error.code === "INVALID_ENDPOINT",
    );
  }

  let requestUrl;
  await checkHealth({
    endpoint: "http://127.2.3.4:17843/untrusted-path?value=1",
    fetch: async (input) => {
      requestUrl = input;
      return readyResponse();
    },
  });
  assert.equal(requestUrl, "http://127.2.3.4:17843/health");
});

test("超时会中止请求并返回 TIMEOUT", async () => {
  await assert.rejects(
    checkHealth({
      timeoutMs: 15,
      fetch: async (_input, init) => new Promise((_resolve, reject) => {
        init.signal.addEventListener("abort", () => reject(init.signal.reason), { once: true });
      }),
    }),
    (error) => error instanceof OmrinaSdkError && error.code === "TIMEOUT",
  );
});

test("读取响应体时超时仍返回 TIMEOUT", async () => {
  await assert.rejects(
    checkHealth({
      timeoutMs: 15,
      fetch: async (_input, init) => ({
        ok: true,
        status: 200,
        json: async () => new Promise((_resolve, reject) => {
          init.signal.addEventListener("abort", () => reject(init.signal.reason), { once: true });
        }),
      }),
    }),
    (error) => error instanceof OmrinaSdkError && error.code === "TIMEOUT",
  );
});

test("调用方取消会中止请求并返回 ABORTED", async () => {
  const controller = new AbortController();
  const promise = checkHealth({
    signal: controller.signal,
    fetch: async (_input, init) => new Promise((_resolve, reject) => {
      init.signal.addEventListener("abort", () => reject(init.signal.reason), { once: true });
    }),
  });

  controller.abort(new Error("caller stopped"));
  await assert.rejects(
    promise,
    (error) => error instanceof OmrinaSdkError && error.code === "ABORTED",
  );
});

test("拒绝 HTTP 错误和不符合 M0 协议的响应", async () => {
  await assert.rejects(
    checkHealth({ fetch: async () => ({ ok: false, status: 503, json: async () => ({}) }) }),
    (error) => error instanceof OmrinaSdkError && error.code === "HTTP_ERROR" && error.status === 503,
  );

  await assert.rejects(
    checkHealth({
      fetch: async () => ({
        ok: true,
        status: 200,
        json: async () => ({ service: "omrina-local", protocolVersion: 2, status: "ready" }),
      }),
    }),
    (error) => error instanceof OmrinaSdkError && error.code === "INVALID_RESPONSE",
  );
});

test("拒绝会溢出 JavaScript 定时器的超时值", async () => {
  await assert.rejects(
    checkHealth({ timeoutMs: 2_147_483_648, fetch: async () => readyResponse() }),
    (error) => error instanceof OmrinaSdkError && error.code === "INVALID_TIMEOUT",
  );
});
