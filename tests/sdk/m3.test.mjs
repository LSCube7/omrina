import assert from "node:assert/strict";
import test from "node:test";

import {
  OmrinaClient,
  OmrinaSdkError,
} from "../../packages/sdk/src/index.ts";

const grant = {
  grantId: "grant-1",
  token: "secret-token",
  expiresAt: new Date(Date.now() + 4 * 60 * 60 * 1000).toISOString(),
};

const template = {
  templateId: "template-default",
  schemaVersion: 1,
  title: "OMRINA 答题纸",
  questionCount: 10,
  optionsPerQuestion: 4,
  templateNumber: "AS1-10x4-AB12CD34",
  svg: "<svg xmlns=\"http://www.w3.org/2000/svg\"></svg>",
};

function makeTask(operation = "template", overrides = {}) {
  return {
    taskId: "task-123",
    operation,
    status: "queued",
    result: null,
    error: null,
    createdAt: "2026-10-02T00:00:00Z",
    updatedAt: "2026-10-02T00:00:00Z",
    ...overrides,
  };
}

function jsonResponse(payload, status = 200) {
  return {
    ok: status >= 200 && status < 300,
    status,
    json: async () => payload,
  };
}

function noContentResponse(status = 204) {
  return {
    ok: status >= 200 && status < 300,
    status,
    json: async () => {
      throw new Error("No response body");
    },
  };
}

test("M3 pairing stores credentials in memory and uses fixed authenticated routes", async () => {
  const requests = [];
  const client = new OmrinaClient({
    endpoint: "http://127.0.0.1:17843/untrusted/path?token=discarded",
    fetch: async (input, init) => {
      const url = new URL(input);
      const headers = new Headers(init.headers);
      requests.push({ url, init, headers });
      if (url.pathname === "/v1/pairing/requests") {
        return jsonResponse({
          requestId: "pair-1",
          expiresAt: new Date(Date.now() + 5 * 60 * 1000).toISOString(),
        }, 202);
      }
      if (url.pathname === "/v1/pairing/exchange") {
        return jsonResponse(grant);
      }
      if (url.pathname === "/v1/templates") {
        return jsonResponse([template]);
      }
      if (url.pathname === "/v1/devices") {
        return jsonResponse([{ deviceId: "scanner-1", name: "Scanner", driver: "mock" }]);
      }
      if (url.pathname === "/v1/grant") {
        return noContentResponse();
      }
      throw new Error(`Unexpected route ${url.pathname}`);
    },
  });

  const ticket = await client.requestPairing({ clientName: "Browser harness" });
  assert.equal(ticket.requestId, "pair-1");
  assert.equal(requests[0].url.href, "http://127.0.0.1:17843/v1/pairing/requests");
  assert.equal(requests[0].init.method, "POST");
  assert.equal(requests[0].headers.has("authorization"), false);
  assert.deepEqual(JSON.parse(requests[0].init.body), { clientName: "Browser harness" });

  const exchanged = await client.exchangePairing({ requestId: ticket.requestId, code: "one-time-code" });
  assert.equal(exchanged.grantId, grant.grantId);
  assert.equal(requests[1].url.search, "");
  assert.equal(requests[1].headers.has("authorization"), false);
  assert.deepEqual(JSON.parse(requests[1].init.body), {
    requestId: ticket.requestId,
    code: "one-time-code",
  });

  const templates = await client.getTemplates();
  assert.equal(templates[0].svg, template.svg);
  assert.deepEqual(await client.getDevices(), [{ deviceId: "scanner-1", name: "Scanner", driver: "mock" }]);
  assert.equal(requests[2].headers.get("authorization"), "Bearer secret-token");
  assert.equal(requests[3].headers.get("authorization"), "Bearer secret-token");
  assert.equal(requests[2].init.credentials, "omit");
  assert.equal(requests[2].init.redirect, "error");
  assert.equal(requests[2].url.origin, "http://127.0.0.1:17843");

  await client.revokeGrant();
  assert.equal(requests[4].init.method, "DELETE");
  assert.equal(requests[4].headers.get("authorization"), "Bearer secret-token");
  await assert.rejects(client.listTasks(), (error) => {
    return error instanceof OmrinaSdkError && error.code === "MISSING_CREDENTIAL";
  });
});

test("business methods send only fixed operations and accurately parse task snapshots", async () => {
  const requests = [];
  const client = new OmrinaClient({
    grant,
    fetch: async (input, init) => {
      const url = new URL(input);
      requests.push({ url, init, headers: new Headers(init.headers) });
      if (url.pathname === "/v1/tasks/upload") {
        return jsonResponse(makeTask("upload"), 202);
      }
      if (url.pathname === "/v1/tasks" && init.method === "POST") {
        const body = JSON.parse(init.body);
        return jsonResponse(makeTask(body.operation, {
          taskId: `task-${body.operation}`,
          result: { preservedAsUnknown: true },
        }), 202);
      }
      if (url.pathname === "/v1/tasks" && init.method === "GET") {
        return jsonResponse([makeTask("recognize")]);
      }
      if (url.pathname === "/v1/tasks/task-recognize/cancel") {
        return jsonResponse(makeTask("recognize", { status: "cancelled" }));
      }
      if (url.pathname === "/v1/tasks/task-recognize") {
        return jsonResponse(makeTask("recognize", { result: { confidence: 0.95 } }));
      }
      throw new Error(`Unexpected route ${url.pathname}`);
    },
  });

  await client.createTemplateTask({
    idempotencyKey: "key-template",
    parameters: { title: "Quiz", questionCount: 10, optionsPerQuestion: 4 },
  });
  await client.uploadImage({
    idempotencyKey: "key-upload",
    templateId: "template-default",
    fileName: "answers.png",
    contentType: "image/png",
    body: new Uint8Array([137, 80, 78, 71]),
  });
  await client.createScanTask({
    idempotencyKey: "key-scan",
    parameters: { templateId: "template-default", deviceId: "scanner-1", dpi: 300 },
  });
  await client.createRecognitionTask({
    idempotencyKey: "key-recognize",
    parameters: { captureId: "capture-1" },
  });
  const scored = await client.createScoreTask({
    idempotencyKey: "key-score",
    parameters: { resultId: "result-1", answerKey: { "1": "A", "2": "B" }, pointsPerQuestion: 1 },
  });
  assert.deepEqual(scored.result, { preservedAsUnknown: true });
  await client.createReviewTask({
    idempotencyKey: "key-review",
    parameters: {
      resultId: "result-1",
      expectedVersion: 2,
      reviewer: "teacher",
      edits: [{ questionNumber: 1, answer: null, reason: "Ambiguous mark" }],
    },
  });
  await client.createExportTask({
    idempotencyKey: "key-export",
    parameters: { resultId: "result-1", format: "json" },
  });
  await client.createSubjectiveReviewTask({
    idempotencyKey: "key-subjective-create",
    parameters: {
      captureId: "capture-1",
      questions: [{
        questionId: "12345678-1234-1234-1234-1234567890ab",
        questionNumber: 1,
        maxScore: 10,
        region: { x: 0, y: 0, width: 20, height: 20 },
      }],
    },
  });
  await client.createSubjectiveReadTask({
    idempotencyKey: "key-subjective-read",
    parameters: { reviewId: "abcdefab-cdef-abcd-efab-cdefabcdefab" },
  });
  await client.createSubjectiveGradeTask({
    idempotencyKey: "key-subjective-grade",
    parameters: {
      reviewId: "abcdefab-cdef-abcd-efab-cdefabcdefab",
      expectedVersion: 1,
      reviewer: "teacher",
      edits: [{
        questionId: "12345678-1234-1234-1234-1234567890ab",
        status: "draft",
        score: 8,
        comment: "Checked",
      }],
    },
  });
  await client.createSubjectiveExportTask({
    idempotencyKey: "key-subjective-export",
    parameters: { reviewId: "abcdefab-cdef-abcd-efab-cdefabcdefab", format: "csv" },
  });

  assert.deepEqual(
    requests.filter(({ url }) => url.pathname === "/v1/tasks" && url.search === "")
      .filter(({ init }) => init.method === "POST")
      .map(({ init }) => JSON.parse(init.body).operation),
    [
      "template",
      "scan",
      "recognize",
      "score",
      "review",
      "export",
      "subjectiveCreate",
      "subjectiveRead",
      "subjectiveGrade",
      "subjectiveExport",
    ],
  );
  const uploadRequest = requests.find(({ url }) => url.pathname === "/v1/tasks/upload");
  assert.ok(uploadRequest);
  assert.equal(uploadRequest.init.method, "POST");
  assert.equal(uploadRequest.headers.get("x-idempotency-key"), "key-upload");
  assert.equal(uploadRequest.headers.get("content-type"), "image/png");
  assert.deepEqual(JSON.parse(uploadRequest.headers.get("x-omrina-parameters")), {
    templateId: "template-default",
    fileName: "answers.png",
  });
  assert.ok(uploadRequest.init.body instanceof Blob);

  const tasks = await client.listTasks();
  assert.equal(tasks[0].operation, "recognize");
  assert.equal((await client.getTask("task-recognize")).result.confidence, 0.95);
  assert.equal((await client.cancelTask("task-recognize")).status, "cancelled");
  assert.ok(requests.every(({ headers }) => headers.get("authorization") === "Bearer secret-token"));
  assert.ok(requests.every(({ url }) => !url.href.includes("secret-token")));
});

test("subjective image retrieval uses its fixed authenticated PNG route", async () => {
  const requests = [];
  const pngBytes = new Uint8Array([137, 80, 78, 71, 13, 10, 26, 10, 1, 2, 3]);
  const client = new OmrinaClient({
    grant,
    fetch: async (input, init) => {
      requests.push({ url: new URL(input), init, headers: new Headers(init.headers) });
      return {
        ...jsonResponse(null),
        headers: new Headers({ "Content-Type": "image/png" }),
        arrayBuffer: async () => pngBytes.slice().buffer,
      };
    },
  });

  const image = await client.getSubjectiveQuestionImage(
    "abcdefab-cdef-abcd-efab-cdefabcdefab",
    "12345678-1234-1234-1234-1234567890ab",
  );
  assert.deepEqual([...image], [...pngBytes]);
  assert.equal(requests[0].url.pathname, "/v1/subjective-reviews/abcdefab-cdef-abcd-efab-cdefabcdefab/questions/12345678-1234-1234-1234-1234567890ab/image");
  assert.equal(requests[0].init.method, "GET");
  assert.equal(requests[0].headers.get("authorization"), "Bearer secret-token");
  assert.equal(requests[0].init.credentials, "omit");
  assert.equal(requests[0].init.cache, "no-store");

  assert.throws(
    () => client.getSubjectiveQuestionImage("../outside", "12345678-1234-1234-1234-1234567890ab"),
    (error) => error instanceof OmrinaSdkError && error.code === "INVALID_ARGUMENT",
  );
  assert.throws(
    () => client.createSubjectiveReviewTask({
      idempotencyKey: "key-invalid-region",
      parameters: {
        captureId: "capture-1",
        questions: [{
          questionId: "12345678-1234-1234-1234-1234567890ab",
          questionNumber: 1,
          maxScore: 10,
          region: { x: -1, y: 0, width: 20, height: 20 },
        }],
      },
    }),
    (error) => error instanceof OmrinaSdkError && error.code === "INVALID_ARGUMENT",
  );
});

test("subjective image retrieval validates MIME, signature, declared and streamed size", async () => {
  const pngSignature = new Uint8Array([137, 80, 78, 71, 13, 10, 26, 10]);
  const createClient = (response) => new OmrinaClient({
    grant,
    fetch: async () => response,
  });
  const imageArgs = [
    "abcdefab-cdef-abcd-efab-cdefabcdefab",
    "12345678-1234-1234-1234-1234567890ab",
  ];

  await assert.rejects(
    createClient({
      ...jsonResponse(null),
      headers: new Headers({ "Content-Type": "image/jpeg" }),
      arrayBuffer: async () => pngSignature.buffer,
    }).getSubjectiveQuestionImage(...imageArgs),
    (error) => error instanceof OmrinaSdkError && error.code === "INVALID_RESPONSE",
  );
  await assert.rejects(
    createClient({
      ...jsonResponse(null),
      headers: new Headers({ "Content-Type": "image/png" }),
      arrayBuffer: async () => new Uint8Array(8).buffer,
    }).getSubjectiveQuestionImage(...imageArgs),
    (error) => error instanceof OmrinaSdkError && error.code === "INVALID_RESPONSE",
  );

  let oversizedArrayBufferRead = false;
  await assert.rejects(
    createClient({
      ...jsonResponse(null),
      headers: new Headers({ "Content-Type": "image/png", "Content-Length": String(8 * 1024 * 1024 + 1) }),
      arrayBuffer: async () => {
        oversizedArrayBufferRead = true;
        return pngSignature.buffer;
      },
    }).getSubjectiveQuestionImage(...imageArgs),
    (error) => error instanceof OmrinaSdkError && error.code === "INVALID_RESPONSE",
  );
  assert.equal(oversizedArrayBufferRead, false);

  let streamCancelled = false;
  const oversizedStream = new ReadableStream({
    start(controller) {
      controller.enqueue(new Uint8Array(8 * 1024 * 1024 + 1));
    },
    cancel() {
      streamCancelled = true;
    },
  });
  await assert.rejects(
    createClient({
      ...jsonResponse(null),
      headers: new Headers({ "Content-Type": "image/png" }),
      body: oversizedStream,
    }).getSubjectiveQuestionImage(...imageArgs),
    (error) => error instanceof OmrinaSdkError && error.code === "INVALID_RESPONSE",
  );
  assert.equal(streamCancelled, true);
});

test("401 clears the in-memory grant before a stalled error body times out", async () => {
  const client = new OmrinaClient({
    grant,
    timeoutMs: 20,
    fetch: async () => ({
      ok: false,
      status: 401,
      json: () => new Promise(() => {}),
    }),
  });
  const imageArgs = [
    "abcdefab-cdef-abcd-efab-cdefabcdefab",
    "12345678-1234-1234-1234-1234567890ab",
  ];

  await assert.rejects(
    client.getSubjectiveQuestionImage(...imageArgs),
    (error) => error instanceof OmrinaSdkError && error.code === "TIMEOUT",
  );
  await assert.rejects(
    client.getSubjectiveQuestionImage(...imageArgs),
    (error) => error instanceof OmrinaSdkError && error.code === "MISSING_CREDENTIAL",
  );
});

test("invalid endpoints, task paths, review versions, and path-like upload names are rejected", async () => {
  for (const endpoint of [
    "https://127.0.0.1:17843",
    "http://example.com:17843",
    "http://127.0.0.2:17843",
    "http://user:pass@localhost:17843",
    "not a url",
  ]) {
    assert.throws(
      () => new OmrinaClient({ endpoint, fetch: async () => jsonResponse({}) }),
      (error) => error instanceof OmrinaSdkError && error.code === "INVALID_ENDPOINT",
    );
  }

  const client = new OmrinaClient({ grant, fetch: async () => jsonResponse(makeTask()) });
  await assert.rejects(client.getTask("../outside"), (error) => {
    return error instanceof OmrinaSdkError && error.code === "INVALID_ARGUMENT";
  });
  assert.throws(() => client.createReviewTask({
    idempotencyKey: "key-review",
    parameters: { resultId: "result-1", expectedVersion: -1, reviewer: "teacher", edits: [] },
  }), (error) => error instanceof OmrinaSdkError && error.code === "INVALID_ARGUMENT");
  assert.throws(() => client.uploadImage({
    idempotencyKey: "key-upload",
    templateId: "template-default",
    fileName: "C:\\answers.png",
    contentType: "image/png",
    body: new Uint8Array([1]),
  }), (error) => error instanceof OmrinaSdkError && error.code === "INVALID_ARGUMENT");
});

test("malformed protocol responses fail instead of being cast to SDK types", async () => {
  const client = new OmrinaClient({
    grant,
    fetch: async () => jsonResponse([{ ...template, svg: 1 }]),
  });
  await assert.rejects(client.getTemplates(), (error) => {
    return error instanceof OmrinaSdkError && error.code === "INVALID_RESPONSE";
  });

  const malformedTaskClient = new OmrinaClient({
    grant,
    fetch: async () => jsonResponse(makeTask("unknown-operation")),
  });
  await assert.rejects(malformedTaskClient.listTasks(), (error) => {
    return error instanceof OmrinaSdkError && error.code === "INVALID_RESPONSE";
  });
});

test("task creation is not automatically retried after an uncertain network failure", async () => {
  let attempts = 0;
  const client = new OmrinaClient({
    grant,
    fetch: async () => {
      attempts += 1;
      throw new Error("connection lost after submit");
    },
  });

  await assert.rejects(client.createTemplateTask({
    idempotencyKey: "one-shot-key",
    parameters: { title: "Quiz", questionCount: 10, optionsPerQuestion: 4 },
  }), (error) => error instanceof OmrinaSdkError && error.code === "NETWORK_ERROR");
  assert.equal(attempts, 1);
});

test("WebSocket 1013 reconnects and recovers tasks while 1008 clears credentials", async () => {
  const sockets = [];
  let taskReads = 0;
  class FakeSocket extends EventTarget {
    readyState = 0;
    sent = [];

    constructor(sequence, authenticate = true) {
      super();
      this.sequence = sequence;
      this.authenticate = authenticate;
      queueMicrotask(() => {
        this.readyState = 1;
        this.dispatchEvent(new Event("open"));
      });
    }

    send(message) {
      this.sent.push(message);
      if (!this.authenticate) {
        queueMicrotask(() => this.remoteClose(1008));
        return;
      }
      queueMicrotask(() => this.emit({ type: "authenticated", sequence: this.sequence }));
    }

    close(code = 1000) {
      if (this.readyState === 3) {
        return;
      }
      this.readyState = 3;
      this.closeWithCode(code);
    }

    emit(payload) {
      this.dispatchEvent(new MessageEvent("message", { data: JSON.stringify(payload) }));
    }

    remoteClose(code = 1006) {
      this.readyState = 3;
      this.closeWithCode(code);
    }

    closeWithCode(code) {
      const event = new Event("close");
      Object.defineProperty(event, "code", { value: code });
      this.dispatchEvent(event);
    }
  }

  const client = new OmrinaClient({
    grant,
    fetch: async (input, init) => {
      assert.equal(new URL(input).pathname, "/v1/tasks");
      assert.equal(init.method, "GET");
      taskReads += 1;
      return jsonResponse([]);
    },
    webSocketFactory: (url) => {
      assert.equal(url, "ws://127.0.0.1:17843/v1/events");
      const socket = new FakeSocket(sockets.length === 0 ? 0 : 2);
      sockets.push(socket);
      return socket;
    },
  });
  const controller = new AbortController();
  let firstRecoveryResolve;
  let reconnectRecoveryResolve;
  const firstRecovery = new Promise((resolve) => {
    firstRecoveryResolve = resolve;
  });
  const reconnectRecovery = new Promise((resolve) => {
    reconnectRecoveryResolve = resolve;
  });
  const events = [];
  const watcher = client.watchEvents({
    signal: controller.signal,
    timeoutMs: 50,
    onEvent: (event) => events.push(event),
    onRecovered: (recovery) => {
      if (recovery.reason === "connected") {
        firstRecoveryResolve(recovery);
      }
      if (recovery.reason === "reconnected") {
        reconnectRecoveryResolve(recovery);
      }
    },
  });

  await firstRecovery;
  assert.deepEqual(JSON.parse(sockets[0].sent[0]), { type: "authenticate", token: "secret-token" });
  await new Promise((resolve) => setTimeout(resolve, 75));
  assert.equal(sockets[0].readyState, 1, "authenticated event connections should outlive the handshake timeout");
  sockets[0].emit({ type: "task", sequence: 1, task: makeTask("scan") });
  await new Promise((resolve) => setTimeout(resolve, 0));
  assert.equal(events[0].sequence, 1);
  sockets[0].remoteClose(1013);
  const recovered = await reconnectRecovery;
  assert.equal(recovered.sequence, 2);
  assert.equal(sockets[1].readyState, 1, "1013 capacity closes should reconnect with the existing grant");
  sockets[1].remoteClose(1008);
  await assert.rejects(watcher, (error) => error instanceof OmrinaSdkError && error.code === "UNAUTHORIZED");
  await assert.rejects(client.listTasks(), (error) => {
    return error instanceof OmrinaSdkError && error.code === "MISSING_CREDENTIAL";
  });
  assert.equal(taskReads, 2);
  assert.equal(sockets.length, 2);
  assert.deepEqual(JSON.parse(sockets[1].sent[0]), { type: "authenticate", token: "secret-token" });

  const unauthorizedSocket = new FakeSocket(0, false);
  const unauthenticatedClient = new OmrinaClient({
    grant,
    fetch: async () => {
      throw new Error("task recovery must not run before event authentication");
    },
    webSocketFactory: () => unauthorizedSocket,
  });
  const unauthorizedWatcher = unauthenticatedClient.watchEvents({
    signal: new AbortController().signal,
    timeoutMs: 50,
    onEvent: () => undefined,
  });
  await assert.rejects(unauthorizedWatcher, (error) => {
    return error instanceof OmrinaSdkError && error.code === "UNAUTHORIZED";
  });
  await assert.rejects(unauthenticatedClient.listTasks(), (error) => {
    return error instanceof OmrinaSdkError && error.code === "MISSING_CREDENTIAL";
  });
});
