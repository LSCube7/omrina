import { spawn } from "node:child_process";
import { randomUUID } from "node:crypto";
import { readFile, stat } from "node:fs/promises";
import { isAbsolute, relative, resolve, sep } from "node:path";
import { createInterface } from "node:readline";
import { fileURLToPath } from "node:url";
import { tmpdir } from "node:os";

import { OmrinaClient } from "../../packages/sdk/dist/index.js";

class E2eFailure extends Error {
  constructor(message) {
    super(message);
    this.name = "E2eFailure";
  }
}

const origin = "http://localhost:3000";
const requestOptions = { timeoutMs: 10_000 };
const taskTimeoutMs = 30_000;
const harnessReadyTimeoutMs = 15_000;
const approvalTimeoutMs = 15_000;
const harnessShutdownTimeoutMs = 5_000;
const repositoryRoot = resolve(fileURLToPath(new URL("../..", import.meta.url)));
const harnessAssemblyArgument = process.argv[2];

let stage = "arguments";
let harness;
let firstClient;
let staleFirstGrantClient;
let secondClient;
let firstGrantActive = false;
let secondGrantActive = false;
let failure;
let cleanupFailure;

try {
  if (!harnessAssemblyArgument) {
    throw new E2eFailure("usage: node tests/integration/m5-sdk-e2e.mjs <http-harness.dll>");
  }

  const harnessAssembly = resolve(repositoryRoot, harnessAssemblyArgument);
  const assemblyStats = await stat(harnessAssembly).catch(() => null);
  if (!assemblyStats?.isFile() || !harnessAssembly.toLowerCase().endsWith(".dll")) {
    throw new E2eFailure("harness assembly is missing");
  }

  stage = "start harness";
  harness = await startHarness(harnessAssembly);
  const fixtureBytes = await readSyntheticFixture(harness.fixturePath);
  assertPngDimensions(fixtureBytes, 40, 40, "harness fixture");
  console.log("PASS synthetic fixture");

  firstClient = createClient(harness.endpoint);
  stage = "pair first grant";
  const firstGrantCredentials = await pairClient(firstClient, harness, "OMRINA M5 subjective E2E");
  firstGrantActive = true;
  staleFirstGrantClient = createClient(harness.endpoint, firstGrantCredentials);
  console.log("PASS first grant pairing");

  stage = "create template";
  const templateTask = await submitAndWait(
    firstClient,
    firstClient.createTemplateTask({
      idempotencyKey: randomUUID(),
      parameters: { title: "M5 synthetic page", questionCount: 2, optionsPerQuestion: 4 },
    }, requestOptions),
    "template",
  );
  const templateResult = requireRecord(templateTask.result, "template result");
  const templateId = requireString(templateResult.templateId, "templateId");
  console.log("PASS template task");

  stage = "upload synthetic capture";
  const uploadTask = await submitAndWait(
    firstClient,
    firstClient.uploadImage({
      idempotencyKey: randomUUID(),
      templateId,
      fileName: "m5-synthetic.png",
      contentType: "image/png",
      body: fixtureBytes,
    }, requestOptions),
    "synthetic image upload",
  );
  const uploadResult = requireRecord(uploadTask.result, "upload result");
  const captureId = requireGuid(uploadResult.captureId, "captureId");
  console.log("PASS synthetic upload");

  const questionIds = [randomUUID(), randomUUID()];
  stage = "create subjective review";
  const createTask = await submitAndWait(
    firstClient,
    firstClient.createSubjectiveReviewTask({
      idempotencyKey: randomUUID(),
      parameters: {
        captureId,
        questions: [
          { questionId: questionIds[0], questionNumber: 1, maxScore: 10, region: { x: 0, y: 0, width: 10, height: 10 } },
          { questionId: questionIds[1], questionNumber: 2, maxScore: 20, region: { x: 10, y: 0, width: 20, height: 10 } },
        ],
      },
    }, requestOptions),
    "subjective review creation",
  );
  const createResult = requireRecord(createTask.result, "subjective review creation result");
  const reviewId = requireGuid(createResult.reviewId, "reviewId");
  const createdSnapshot = unwrapSnapshot(createResult);
  verifySnapshot(createdSnapshot, {
    reviewId,
    captureId,
    version: 1,
    expected: [
      { questionId: questionIds[0], questionNumber: 1, maxScore: 10, region: { x: 0, y: 0, width: 10, height: 10 }, status: "ungraded", score: null, comment: null },
      { questionId: questionIds[1], questionNumber: 2, maxScore: 20, region: { x: 10, y: 0, width: 20, height: 10 }, status: "ungraded", score: null, comment: null },
    ],
    isFinal: false,
    finalSubtotal: null,
  });
  console.log("PASS ungraded review snapshot");

  stage = "read first question image";
  const firstQuestionImage = await firstClient.getSubjectiveQuestionImage(reviewId, questionIds[0], requestOptions);
  assertPngDimensions(firstQuestionImage, 10, 10, "first subjective region");
  const secondQuestionImage = await firstClient.getSubjectiveQuestionImage(reviewId, questionIds[1], requestOptions);
  assertPngDimensions(secondQuestionImage, 20, 10, "second subjective region");
  console.log("PASS subjective PNG signature and dimensions");

  stage = "save draft batch";
  const draftTask = await submitAndWait(
    firstClient,
    firstClient.createSubjectiveGradeTask({
      idempotencyKey: randomUUID(),
      parameters: {
        reviewId,
        expectedVersion: 1,
        reviewer: "M5 E2E reviewer",
        edits: [
          { questionId: questionIds[0], status: "draft", score: 8, comment: "first draft" },
          { questionId: questionIds[1], status: "draft", score: 16, comment: "second draft" },
        ],
      },
    }, requestOptions),
    "draft batch",
  );
  const draftSnapshot = unwrapSnapshot(requireRecord(draftTask.result, "draft task result"));
  verifySnapshot(draftSnapshot, {
    reviewId,
    captureId,
    version: 2,
    expected: [
      { questionId: questionIds[0], questionNumber: 1, maxScore: 10, region: { x: 0, y: 0, width: 10, height: 10 }, status: "draft", score: 8, comment: "first draft" },
      { questionId: questionIds[1], questionNumber: 2, maxScore: 20, region: { x: 10, y: 0, width: 20, height: 10 }, status: "draft", score: 16, comment: "second draft" },
    ],
    isFinal: false,
    finalSubtotal: null,
  });
  assert(draftSnapshot.history.length === 1 && draftSnapshot.history[0].changes.length === 2, "one edit request should create one two-question history batch");
  console.log("PASS atomic two-question draft");

  stage = "confirm draft batch";
  const confirmTask = await submitAndWait(
    firstClient,
    firstClient.createSubjectiveGradeTask({
      idempotencyKey: randomUUID(),
      parameters: {
        reviewId,
        expectedVersion: 2,
        reviewer: "M5 E2E confirmer",
        edits: [
          { questionId: questionIds[0], status: "confirmed", score: 8, comment: "first draft" },
          { questionId: questionIds[1], status: "confirmed", score: 16, comment: "second draft" },
        ],
      },
    }, requestOptions),
    "confirmation batch",
  );
  const confirmedSnapshot = unwrapSnapshot(requireRecord(confirmTask.result, "confirmation task result"));
  verifySnapshot(confirmedSnapshot, {
    reviewId,
    captureId,
    version: 3,
    expected: [
      { questionId: questionIds[0], questionNumber: 1, maxScore: 10, region: { x: 0, y: 0, width: 10, height: 10 }, status: "confirmed", score: 8, comment: "first draft" },
      { questionId: questionIds[1], questionNumber: 2, maxScore: 20, region: { x: 10, y: 0, width: 20, height: 10 }, status: "confirmed", score: 16, comment: "second draft" },
    ],
    isFinal: true,
    finalSubtotal: 24,
  });
  console.log("PASS confirmed final subtotal");

  stage = "stale version rejection";
  const staleTask = await submitAndWait(
    firstClient,
    firstClient.createSubjectiveGradeTask({
      idempotencyKey: randomUUID(),
      parameters: {
        reviewId,
        expectedVersion: 2,
        reviewer: "M5 E2E reviewer",
        edits: [{ questionId: questionIds[0], status: "draft", score: 7, comment: "stale edit" }],
      },
    }, requestOptions),
    "stale grade batch",
    { expectFailure: true, expectedErrorCode: "VERSION_CONFLICT" },
  );
  assert(staleTask.status === "failed", "stale expectedVersion task should fail");
  const afterStaleSnapshot = await readSnapshot(firstClient, reviewId);
  verifySnapshot(afterStaleSnapshot, {
    reviewId,
    captureId,
    version: 3,
    expected: confirmedSnapshot.questions,
    isFinal: true,
    finalSubtotal: 24,
  });
  console.log("PASS stale version leaves review unchanged");

  stage = "reject confirmed score edit";
  const directConfirmedEdit = await submitAndWait(
    firstClient,
    firstClient.createSubjectiveGradeTask({
      idempotencyKey: randomUUID(),
      parameters: {
        reviewId,
        expectedVersion: 3,
        reviewer: "M5 E2E reviewer",
        edits: [{ questionId: questionIds[0], status: "confirmed", score: 7, comment: "direct confirmed edit" }],
      },
    }, requestOptions),
    "direct confirmed score edit",
    { expectFailure: true, expectedErrorCode: "INVALID_SUBJECTIVE_GRADE" },
  );
  assert(directConfirmedEdit.status === "failed", "a confirmed score must not be edited without returning to draft");
  const unchangedAfterConfirmedEdit = await readSnapshot(firstClient, reviewId);
  verifySnapshot(unchangedAfterConfirmedEdit, {
    reviewId,
    captureId,
    version: 3,
    expected: confirmedSnapshot.questions,
    isFinal: true,
    finalSubtotal: 24,
  });
  console.log("PASS confirmed score edit requires draft state");

  stage = "reopen confirmed question";
  const reopenTask = await submitAndWait(
    firstClient,
    firstClient.createSubjectiveGradeTask({
      idempotencyKey: randomUUID(),
      parameters: {
        reviewId,
        expectedVersion: 3,
        reviewer: "M5 E2E reviewer",
        edits: [{ questionId: questionIds[0], status: "draft", score: 5, comment: "revised score" }],
      },
    }, requestOptions),
    "reopen confirmed question",
  );
  const reopenedSnapshot = unwrapSnapshot(requireRecord(reopenTask.result, "reopen task result"));
  verifySnapshot(reopenedSnapshot, {
    reviewId,
    captureId,
    version: 4,
    expected: [
      { questionId: questionIds[0], questionNumber: 1, maxScore: 10, region: { x: 0, y: 0, width: 10, height: 10 }, status: "draft", score: 5, comment: "revised score" },
      { questionId: questionIds[1], questionNumber: 2, maxScore: 20, region: { x: 10, y: 0, width: 20, height: 10 }, status: "confirmed", score: 16, comment: "second draft" },
    ],
    isFinal: false,
    finalSubtotal: null,
  });
  console.log("PASS confirmed question must return to draft before editing");

  stage = "reconfirm revised score";
  const reconfirmTask = await submitAndWait(
    firstClient,
    firstClient.createSubjectiveGradeTask({
      idempotencyKey: randomUUID(),
      parameters: {
        reviewId,
        expectedVersion: 4,
        reviewer: "M5 E2E confirmer",
        edits: [{ questionId: questionIds[0], status: "confirmed", score: 5, comment: "revised score" }],
      },
    }, requestOptions),
    "reconfirm revised score",
  );
  const finalSnapshot = unwrapSnapshot(requireRecord(reconfirmTask.result, "reconfirm task result"));
  verifySnapshot(finalSnapshot, {
    reviewId,
    captureId,
    version: 5,
    expected: [
      { questionId: questionIds[0], questionNumber: 1, maxScore: 10, region: { x: 0, y: 0, width: 10, height: 10 }, status: "confirmed", score: 5, comment: "revised score" },
      { questionId: questionIds[1], questionNumber: 2, maxScore: 20, region: { x: 10, y: 0, width: 20, height: 10 }, status: "confirmed", score: 16, comment: "second draft" },
    ],
    isFinal: true,
    finalSubtotal: 21,
  });
  assertAuditHistory(finalSnapshot, questionIds);
  console.log("PASS revised score reconfirmation");

  stage = "JSON export";
  const jsonExportTask = await submitAndWait(
    firstClient,
    firstClient.createSubjectiveExportTask({
      idempotencyKey: randomUUID(),
      parameters: { reviewId, format: "json" },
    }, requestOptions),
    "JSON export",
  );
  const jsonExport = requireRecord(jsonExportTask.result, "JSON export result");
  assert(typeof jsonExport.content === "string", "JSON export content must be a string");
  const exportedSnapshot = JSON.parse(jsonExport.content);
  verifySnapshot(exportedSnapshot, {
    reviewId,
    captureId,
    version: 5,
    expected: finalSnapshot.questions,
    isFinal: true,
    finalSubtotal: 21,
  });
  assert(JSON.stringify(exportedSnapshot.history) === JSON.stringify(finalSnapshot.history), "JSON export must preserve the complete grading history");
  console.log("PASS JSON export");

  stage = "CSV export";
  const csvExportTask = await submitAndWait(
    firstClient,
    firstClient.createSubjectiveExportTask({
      idempotencyKey: randomUUID(),
      parameters: { reviewId, format: "csv" },
    }, requestOptions),
    "CSV export",
  );
  const csvExport = requireRecord(csvExportTask.result, "CSV export result");
  assert(typeof csvExport.content === "string" && csvExport.content.length > 0, "CSV export content must be a non-empty string");
  assert(csvExport.content.startsWith("questionId,questionNumber,maxScore,status,score,comment,reviewer,confirmedAtUtc"), "CSV export must contain its question grading columns");
  assert(csvExport.content.includes("revised score") && /\r?\n"[^"]+","1","10","confirmed","5","revised score",/.test(csvExport.content), "CSV export must contain the revised question score and comment");
  assert(/\r?\n"[^"]+","2","20","confirmed","16","second draft",/.test(csvExport.content), "CSV export must contain the second confirmed question score");
  console.log("PASS CSV export");

  stage = "pair second grant";
  secondClient = createClient(harness.endpoint);
  await pairClient(secondClient, harness, "OMRINA M5 same-origin isolation E2E");
  secondGrantActive = true;
  console.log("PASS same-origin second grant pairing");

  stage = "cross-grant read denial";
  const foreignRead = await submitAndWait(
    secondClient,
    secondClient.createSubjectiveReadTask({
      idempotencyKey: randomUUID(),
      parameters: { reviewId },
    }, requestOptions),
    "cross-grant read",
    { expectFailure: true, expectedErrorCode: "SUBJECTIVE_REVIEW_NOT_FOUND" },
  );
  assert(foreignRead.status === "failed", "second grant must not read the first grant's review");

  stage = "cross-grant edit denial";
  const foreignEdit = await submitAndWait(
    secondClient,
    secondClient.createSubjectiveGradeTask({
      idempotencyKey: randomUUID(),
      parameters: {
        reviewId,
        expectedVersion: 5,
        reviewer: "M5 E2E foreign reviewer",
        edits: [{ questionId: questionIds[0], status: "draft", score: 1, comment: "foreign edit" }],
      },
    }, requestOptions),
    "cross-grant grade",
    { expectFailure: true, expectedErrorCode: "SUBJECTIVE_REVIEW_NOT_FOUND" },
  );
  assert(foreignEdit.status === "failed", "second grant must not edit the first grant's review");

  stage = "cross-grant image denial";
  await assertSdkFailure(
    () => secondClient.getSubjectiveQuestionImage(reviewId, questionIds[0], requestOptions),
    { code: "REMOTE_ERROR", status: 404, remoteCode: "SUBJECTIVE_IMAGE_NOT_FOUND" },
    "second grant must not fetch the first grant's region image",
  );
  const unchangedAfterForeignAccess = await readSnapshot(firstClient, reviewId);
  verifySnapshot(unchangedAfterForeignAccess, {
    reviewId,
    captureId,
    version: 5,
    expected: finalSnapshot.questions,
    isFinal: true,
    finalSubtotal: 21,
  });
  console.log("PASS same-origin grants remain isolated");

  stage = "revoke first grant";
  await firstClient.revokeGrant(requestOptions);
  firstGrantActive = false;
  await assertSdkFailure(
    () => firstClient.getSubjectiveQuestionImage(reviewId, questionIds[0], requestOptions),
    { code: "MISSING_CREDENTIAL" },
    "revoked SDK client should clear its local grant",
  );
  await assertSdkFailure(
    () => staleFirstGrantClient.getSubjectiveQuestionImage(reviewId, questionIds[0], requestOptions),
    { code: "UNAUTHORIZED", status: 401, remoteCode: "UNAUTHORIZED" },
    "revoked grant must not fetch a region image",
  );
  console.log("PASS revoked grant cannot fetch image");
} catch (error) {
  failure = error;
} finally {
  if (firstGrantActive && firstClient) {
    try {
      await firstClient.revokeGrant(requestOptions);
      firstGrantActive = false;
    } catch (error) {
      cleanupFailure ??= error;
    }
  }

  if (secondGrantActive && secondClient) {
    try {
      await secondClient.revokeGrant(requestOptions);
      secondGrantActive = false;
    } catch (error) {
      cleanupFailure ??= error;
    }
  }

  if (harness) {
    try {
      await harness.stop();
    } catch (error) {
      cleanupFailure ??= error;
    }
  }
}

if (failure) {
  console.error(`FAIL ${stage}: ${safeDiagnostic(failure)}`);
  process.exitCode = 1;
}

if (cleanupFailure) {
  console.error(`FAIL cleanup: ${safeDiagnostic(cleanupFailure)}`);
  process.exitCode = 1;
}

if (!failure && !cleanupFailure) {
  console.log("PASS M5 SDK-to-desktop subjective review E2E");
}

function createClient(endpoint, grant) {
  return new OmrinaClient({
    endpoint,
    grant,
    timeoutMs: requestOptions.timeoutMs,
    fetch: (input, init) => {
      const headers = new Headers(init?.headers);
      headers.set("Origin", origin);
      return fetch(input, { ...init, headers });
    },
  });
}

async function pairClient(client, processHarness, clientName) {
  const ticket = await client.requestPairing({ clientName }, requestOptions);
  const approval = processHarness.approve(ticket.requestId);
  const grantedCode = approval.then(({ code }) => code);
  return client.exchangePairing({ requestId: ticket.requestId, code: await grantedCode }, requestOptions);
}

async function startHarness(harnessAssembly) {
  const child = spawn("dotnet", [harnessAssembly, "--http-harness"], {
    cwd: repositoryRoot,
    stdio: ["pipe", "pipe", "pipe"],
    windowsHide: true,
  });
  const approvalWaiters = new Map();
  const pendingApprovals = new Map();
  let readyResolve;
  let readyReject;
  let exitInfo;
  let stderrBytes = 0;
  const readyPromise = new Promise((resolveReady, rejectReady) => {
    readyResolve = resolveReady;
    readyReject = rejectReady;
  });
  const stdout = createInterface({ input: child.stdout });

  stdout.on("line", (line) => {
    if (line.startsWith("READY ")) {
      const match = /^READY\s+(http:\/\/[^\s]+)\s+fixture=(.+)$/.exec(line);
      if (!match) {
        readyReject(new E2eFailure("harness READY line is invalid"));
        return;
      }

      const endpoint = new URL(match[1]);
      if (endpoint.hostname !== "127.0.0.1" || endpoint.port !== "17845") {
        readyReject(new E2eFailure("harness endpoint is unexpected"));
        return;
      }

      readyResolve({ endpoint: endpoint.href.replace(/\/$/, ""), fixturePath: match[2] });
      return;
    }

    if (line.startsWith("APPROVAL ")) {
      let parsed;
      try {
        parsed = JSON.parse(line.slice("APPROVAL ".length));
      } catch {
        failApprovalWaiters(new E2eFailure("harness approval response is invalid"));
        return;
      }

      if (typeof parsed?.requestId !== "string" || typeof parsed?.code !== "string" || parsed.code.length === 0) {
        failApprovalWaiters(new E2eFailure("harness approval response is incomplete"));
        return;
      }

      const waiter = approvalWaiters.get(parsed.requestId);
      if (waiter) {
        clearTimeout(waiter.timer);
        approvalWaiters.delete(parsed.requestId);
        waiter.resolve({ code: parsed.code });
      } else {
        pendingApprovals.set(parsed.requestId, { code: parsed.code });
      }
      return;
    }

    if (line.startsWith("APPROVAL_ERROR ")) {
      failApprovalWaiters(new E2eFailure("harness could not approve pairing"));
      return;
    }

    if (line.startsWith("HARNESS_ERROR ")) {
      readyReject(new E2eFailure("harness startup failed"));
      failApprovalWaiters(new E2eFailure("harness failed"));
    }
  });

  child.stderr.on("data", (chunk) => {
    stderrBytes += Buffer.byteLength(chunk);
  });
  child.on("error", () => {
    const error = new E2eFailure("harness process could not start");
    readyReject(error);
    failApprovalWaiters(error);
  });
  child.on("exit", (code, signal) => {
    exitInfo = { code, signal };
    const error = new E2eFailure(`harness exited early (${formatExit(exitInfo)}; stderrBytes=${stderrBytes})`);
    readyReject(error);
    failApprovalWaiters(error);
  });

  let ready;
  try {
    ready = await withTimeout(readyPromise, harnessReadyTimeoutMs, "harness READY timeout");
  } catch (error) {
    stdout.close();
    if (!exitInfo && child.stdin.writable) {
      child.stdin.write("quit\n");
      child.stdin.end();
      if (!(await waitForExit(child, harnessShutdownTimeoutMs))) {
        child.kill();
        await waitForExit(child, harnessShutdownTimeoutMs);
      }
    }
    throw error;
  }
  return {
    endpoint: ready.endpoint,
    fixturePath: ready.fixturePath,
    approve(requestId) {
      if (typeof requestId !== "string" || !/^[a-f0-9-]{32,36}$/i.test(requestId)) {
        throw new E2eFailure("pairing request ID is invalid");
      }

      if (pendingApprovals.has(requestId)) {
        const result = pendingApprovals.get(requestId);
        pendingApprovals.delete(requestId);
        return Promise.resolve(result);
      }

      const promise = new Promise((resolveApproval, rejectApproval) => {
        const timer = setTimeout(() => {
          approvalWaiters.delete(requestId);
          rejectApproval(new E2eFailure("harness approval timeout"));
        }, approvalTimeoutMs);
        approvalWaiters.set(requestId, { resolve: resolveApproval, reject: rejectApproval, timer });
      });
      if (!child.stdin.writable || exitInfo) {
        const waiter = approvalWaiters.get(requestId);
        if (waiter) clearTimeout(waiter.timer);
        approvalWaiters.delete(requestId);
        throw new E2eFailure("harness is not accepting commands");
      }

      child.stdin.write(`approve ${requestId}\n`);
      return promise;
    },
    async stop() {
      stdout.close();
      if (!exitInfo && child.stdin.writable) {
        child.stdin.write("quit\n");
        child.stdin.end();
      }

      if (!exitInfo) {
        const exited = await waitForExit(child, harnessShutdownTimeoutMs);
        if (!exited) {
          child.kill();
          const stopped = await waitForExit(child, harnessShutdownTimeoutMs);
          if (!stopped) {
            throw new E2eFailure("harness did not stop after quit and process termination");
          }
        }
      }

      failApprovalWaiters(new E2eFailure("harness stopped"));
    },
  };

  function failApprovalWaiters(error) {
    for (const [requestId, waiter] of approvalWaiters) {
      clearTimeout(waiter.timer);
      waiter.reject(error);
      approvalWaiters.delete(requestId);
    }
  }
}

async function readSyntheticFixture(fixturePath) {
  const resolvedFixture = resolve(fixturePath);
  const tempRoot = resolve(tmpdir());
  const relativePath = relative(tempRoot, resolvedFixture);
  const parts = relativePath.split(sep);
  if (isAbsolute(relativePath)
    || parts.length !== 2
    || !/^omrina-m3-http-[a-f0-9]{32}$/i.test(parts[0])
    || parts[1] !== "synthetic-answer-sheet.png") {
    throw new E2eFailure("harness fixture is outside its synthetic temporary directory");
  }

  return new Uint8Array(await readFile(resolvedFixture));
}

function assertPngDimensions(value, expectedWidth, expectedHeight, label) {
  const bytes = Buffer.from(value);
  const signature = [137, 80, 78, 71, 13, 10, 26, 10];
  assert(bytes.length >= 24, `${label} PNG is too small`);
  assert(signature.every((byte, index) => bytes[index] === byte), `${label} PNG signature is invalid`);
  const width = bytes.readUInt32BE(16);
  const height = bytes.readUInt32BE(20);
  assert(width === expectedWidth && height === expectedHeight, `${label} dimensions are unexpected`);
}

async function submitAndWait(client, submittedTask, label, { expectFailure = false, expectedErrorCode } = {}) {
  let task = await submittedTask;
  requireRecord(task, `${label} task`);
  const deadline = Date.now() + taskTimeoutMs;
  while (task.status === "queued" || task.status === "running") {
    if (Date.now() >= deadline) {
      throw new E2eFailure(`${label} task timed out`);
    }

    await delay(100);
    task = await client.getTask(task.taskId, requestOptions);
  }

  if (expectFailure) {
    assert(task.status === "failed", `${label} task should fail, got ${String(task.status)}`);
    if (expectedErrorCode) {
      assert(task.error?.code === expectedErrorCode, `${label} task should fail with ${expectedErrorCode}`);
    }
    return task;
  }

  if (task.status !== "completed") {
    const errorCode = typeof task.error?.code === "string" && /^[A-Z0-9_]{1,64}$/.test(task.error.code)
      ? task.error.code
      : "UNSAFE_OR_MISSING_ERROR_CODE";
    throw new E2eFailure(`${label} task did not complete (${errorCode})`);
  }

  return task;
}

async function readSnapshot(client, reviewId) {
  const task = await submitAndWait(
    client,
    client.createSubjectiveReadTask({
      idempotencyKey: randomUUID(),
      parameters: { reviewId },
    }, requestOptions),
    "read subjective review",
  );
  return unwrapSnapshot(requireRecord(task.result, "read task result"));
}

function unwrapSnapshot(result) {
  let candidate = result.snapshot ?? result;
  if (typeof candidate === "string") {
    candidate = JSON.parse(candidate);
  }

  return requireRecord(candidate, "subjective snapshot");
}

function verifySnapshot(snapshot, expected) {
  assert(requireGuid(snapshot.reviewId, "reviewId") === expected.reviewId, "review ID mismatch");
  assert(requireGuid(snapshot.captureId, "captureId") === expected.captureId, "capture ID mismatch");
  assert(snapshot.version === expected.version, `snapshot version should be ${expected.version}`);
  assert(snapshot.isFinal === expected.isFinal, "snapshot final state mismatch");
  assert(snapshot.finalSubtotal === expected.finalSubtotal, "snapshot final subtotal mismatch");
  assert(Array.isArray(snapshot.questions) && snapshot.questions.length === expected.expected.length, "snapshot question count mismatch");

  for (let index = 0; index < expected.expected.length; index++) {
    const actual = requireRecord(snapshot.questions[index], "subjective question");
    const wanted = expected.expected[index];
    assert(requireGuid(actual.questionId, "questionId") === wanted.questionId, "subjective question ID mismatch");
    assert(actual.questionNumber === wanted.questionNumber, "subjective question number mismatch");
    assert(actual.maxScore === wanted.maxScore, "subjective question maximum score mismatch");
    assert(actual.status === wanted.status, "subjective question status mismatch");
    assert(actual.score === wanted.score, "subjective question score mismatch");
    assert(actual.comment === wanted.comment, "subjective question comment mismatch");
    assertRectangle(actual.region, wanted.region);
  }
}

function assertAuditHistory(snapshot, questionIds) {
  assert(Array.isArray(snapshot.history) && snapshot.history.length === 4, "review should retain all four grading batches");
  const expectedBatchSizes = [2, 2, 1, 1];
  for (let index = 0; index < expectedBatchSizes.length; index++) {
    const batch = requireRecord(snapshot.history[index], "grading history batch");
    assert(batch.version === index + 2, "grading history versions should be contiguous");
    assert(Array.isArray(batch.changes) && batch.changes.length === expectedBatchSizes[index], "grading history batch size mismatch");
  }

  const firstQuestionChanges = snapshot.history.flatMap(batch => batch.changes)
    .filter(change => change.questionId === questionIds[0]);
  assert(firstQuestionChanges.length === 4, "first question should have four audited changes");
  const expectedTransitions = [
    { before: "ungraded", after: "draft", beforeScore: null, afterScore: 8 },
    { before: "draft", after: "confirmed", beforeScore: 8, afterScore: 8 },
    { before: "confirmed", after: "draft", beforeScore: 8, afterScore: 5 },
    { before: "draft", after: "confirmed", beforeScore: 5, afterScore: 5 },
  ];
  for (let index = 0; index < expectedTransitions.length; index++) {
    const change = requireRecord(firstQuestionChanges[index], "first-question history change");
    const transition = expectedTransitions[index];
    assert(change.before?.status === transition.before && change.after?.status === transition.after, "grading history status transition mismatch");
    assert(change.before?.score === transition.beforeScore && change.after?.score === transition.afterScore, "grading history score transition mismatch");
  }
}

function assertRectangle(value, expected) {
  const actual = requireRecord(value, "subjective question region");
  assert(actual.x === expected.x, "region x mismatch");
  assert(actual.y === expected.y, "region y mismatch");
  assert(actual.width === expected.width, "region width mismatch");
  assert(actual.height === expected.height, "region height mismatch");
}

function requireRecord(value, label) {
  assert(value !== null && typeof value === "object" && !Array.isArray(value), `${label} is not an object`);
  return value;
}

function requireString(value, label) {
  assert(typeof value === "string" && value.length > 0, `${label} is missing`);
  return value;
}

function requireGuid(value, label) {
  const text = requireString(value, label);
  assert(/^[0-9a-f]{8}-[0-9a-f]{4}-[1-8][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i.test(text)
    || /^[0-9a-f]{32}$/i.test(text), `${label} is not a GUID`);
  return text.toLowerCase();
}

async function assertSdkFailure(action, expected, message) {
  try {
    await action();
  } catch (error) {
    assert(error !== null && typeof error === "object", `${message}: error type mismatch`);
    assert(error.code === expected.code, `${message}: SDK error code mismatch`);
    if (Object.hasOwn(expected, "status")) {
      assert(error.status === expected.status, `${message}: HTTP status mismatch`);
    }
    if (Object.hasOwn(expected, "remoteCode")) {
      assert(error.remoteCode === expected.remoteCode, `${message}: remote error code mismatch`);
    }
    return;
  }

  throw new E2eFailure(message);
}

function assert(condition, message) {
  if (!condition) {
    throw new E2eFailure(message);
  }
}

function safeDiagnostic(error) {
  if (error instanceof E2eFailure) {
    return error.message;
  }

  const name = error && typeof error.name === "string" && /^[A-Za-z0-9_.-]{1,64}$/.test(error.name)
    ? error.name
    : "Error";
  const fields = [];
  if (error && typeof error.code === "string" && /^[A-Z0-9_]{1,64}$/.test(error.code)) {
    fields.push(`code=${error.code}`);
  }
  if (error && Number.isInteger(error.status) && error.status >= 100 && error.status <= 599) {
    fields.push(`status=${error.status}`);
  }
  if (error && typeof error.remoteCode === "string" && /^[A-Z0-9_]{1,64}$/.test(error.remoteCode)) {
    fields.push(`remoteCode=${error.remoteCode}`);
  }
  if (error instanceof Error) {
    const missingIdentifier = /^([A-Za-z_$][A-Za-z0-9_$]{0,63}) is not defined$/.exec(error.message);
    if (missingIdentifier) {
      fields.push(`${missingIdentifier[1]} is not defined`);
    }
  }
  return fields.length === 0 ? name : `${name} (${fields.join(", ")})`;
}

function formatExit(exitInfo) {
  if (Number.isInteger(exitInfo?.code)) {
    return `exit=${exitInfo.code}`;
  }

  return `signal=${/^[A-Z0-9]+$/i.test(exitInfo?.signal ?? "") ? exitInfo.signal : "unknown"}`;
}

function waitForExit(child, timeoutMs) {
  if (child.exitCode !== null || child.signalCode !== null) {
    return Promise.resolve(true);
  }

  return new Promise((resolveExit) => {
    const timer = setTimeout(() => {
      child.removeListener("exit", onExit);
      resolveExit(false);
    }, timeoutMs);
    const onExit = () => {
      clearTimeout(timer);
      resolveExit(true);
    };
    child.once("exit", onExit);
  });
}

function withTimeout(promise, timeoutMs, message) {
  let timer;
  return Promise.race([
    promise,
    new Promise((_, reject) => {
      timer = setTimeout(() => reject(new E2eFailure(message)), timeoutMs);
    }),
  ]).finally(() => clearTimeout(timer));
}

function delay(timeoutMs) {
  return new Promise((resolveDelay) => setTimeout(resolveDelay, timeoutMs));
}
