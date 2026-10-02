import { readFile } from "node:fs/promises";
import { extname, resolve } from "node:path";
import { createInterface } from "node:readline/promises";
import { stdin, stdout } from "node:process";
import { randomUUID } from "node:crypto";

import { OmrinaClient } from "../../packages/sdk/dist/index.js";

const endpoint = process.argv[2] ?? "http://127.0.0.1:17845";
const fixturePath = process.argv[3];

if (!fixturePath) {
  console.error("用法: node tests/integration/m3-sdk-e2e.mjs <endpoint> <synthetic.png|synthetic.jpg>");
  process.exitCode = 2;
} else {
  await runEndToEnd(endpoint, resolve(fixturePath));
}

async function runEndToEnd(serviceEndpoint, imagePath) {
  const extension = extname(imagePath).toLowerCase();
  const contentType = extension === ".png"
    ? "image/png"
    : extension === ".jpg" || extension === ".jpeg"
      ? "image/jpeg"
      : undefined;
  if (!contentType) {
    throw new Error("Fixture must be a PNG or JPEG image.");
  }

  const origin = "http://localhost:3000";
  const client = new OmrinaClient({
    endpoint: serviceEndpoint,
    timeoutMs: 10_000,
    fetch: (input, init) => {
      const headers = new Headers(init.headers);
      headers.set("Origin", origin);
      return fetch(input, { ...init, headers });
    },
  });
  const terminal = createInterface({ input: stdin, output: stdout });
  let grantExchanged = false;

  try {
    const ticket = await client.requestPairing({ clientName: "OMRINA SDK real adapter E2E" });
    console.log(`配对请求已提交。请在 Desktop harness 输入 approve ${ticket.requestId} 后提供显示的一次性 code。`);
    const code = (await terminal.question("一次性 code: ")).trim();
    if (!code) {
      throw new Error("配对 code 不能为空。");
    }
    await client.exchangePairing({ requestId: ticket.requestId, code });
    grantExchanged = true;
    console.log("PASS pairing");

    const defaultTemplates = await client.getTemplates();
    assert(defaultTemplates.length > 0, "GET /v1/templates returned no static default template");
    console.log("PASS templates");

    const templateTask = await submitAndWait(client, client.createTemplateTask({
      idempotencyKey: randomUUID(),
      parameters: { title: "SDK E2E 答题纸", questionCount: 10, optionsPerQuestion: 4 },
    }));
    const template = requireRecord(templateTask.result, "template task result");
    const templateId = requireString(template.templateId, "templateId");
    console.log("PASS template task");

    const bytes = new Uint8Array(await readFile(imagePath));
    const uploadTask = await submitAndWait(client, client.uploadImage({
      idempotencyKey: randomUUID(),
      templateId,
      fileName: "答题纸.png",
      contentType,
      body: bytes,
    }));
    const uploadResult = requireRecord(uploadTask.result, "upload task result");
    const captureId = requireString(uploadResult.captureId, "captureId");
    console.log("PASS image upload");

    const recognitionTask = await submitAndWait(client, client.createRecognitionTask({
      idempotencyKey: randomUUID(),
      parameters: { captureId },
    }));
    const recognition = requireRecord(recognitionTask.result, "recognition task result");
    const resultId = requireString(recognition.resultId, "resultId");
    console.log("PASS recognition");

    const answerKey = Object.fromEntries(
      Array.from({ length: 10 }, (_, index) => [String(index + 1), "A"]),
    );
    const scoreTask = await submitAndWait(client, client.createScoreTask({
      idempotencyKey: randomUUID(),
      parameters: { resultId, answerKey, pointsPerQuestion: 1 },
    }));
    const score = requireRecord(scoreTask.result, "score task result");
    const scoreVersion = requireInteger(score.version, "score version");
    assert(scoreVersion === 2, `score version was ${scoreVersion}; expected 2`);
    const scoreSummary = requireRecord(score.result, "scored result");
    const scoring = requireRecord(scoreSummary.scoring, "score summary");
    assert(scoring.totalScore === 10, `score total was ${scoring.totalScore}; expected 10`);
    console.log("PASS scoring");

    const reviewTask = await submitAndWait(client, client.createReviewTask({
      idempotencyKey: randomUUID(),
      parameters: {
        resultId,
        expectedVersion: scoreVersion,
        reviewer: "SDK E2E",
        edits: [{ questionNumber: 1, answer: "B", reason: "E2E review" }],
      },
    }));
    const review = requireRecord(reviewTask.result, "review task result");
    const reviewVersion = requireInteger(review.version, "review version");
    assert(reviewVersion === 3, `review version was ${reviewVersion}; expected 3`);
    const reviewedResult = requireRecord(review.result, "reviewed result");
    const reviewedScoring = requireRecord(reviewedResult.scoring, "reviewed score summary");
    assert(reviewedScoring.totalScore === 9, `reviewed total was ${reviewedScoring.totalScore}; expected 9`);
    const reviewSummary = requireRecord(reviewedResult.review, "review summary");
    assert(
      Array.isArray(reviewSummary.history)
        && reviewSummary.history.some((entry) => entry.reason === "E2E review"),
      "reviewed result did not retain the submitted review history",
    );
    console.log("PASS review");

    const exportTask = await submitAndWait(client, client.createExportTask({
      idempotencyKey: randomUUID(),
      parameters: { resultId, format: "json" },
    }));
    const exported = requireRecord(exportTask.result, "export task result");
    assert(typeof exported.content === "string", "export content was not a string");
    const exportedDocument = JSON.parse(exported.content);
    const exportedScoring = requireRecord(exportedDocument.scoring, "exported scoring");
    assert(exportedScoring.totalScore === 9, "JSON export did not reflect the reviewed score");
    const exportedReview = requireRecord(exportedDocument.review, "exported review");
    assert(
      Array.isArray(exportedReview.history)
        && exportedReview.history.some((entry) => entry.reason === "E2E review"),
      "JSON export did not contain the submitted review history",
    );
    console.log("PASS export");

    const tasks = await client.listTasks();
    assert(tasks.length >= 6, "task recovery list did not contain the submitted pipeline");
    console.log("PASS task list recovery");
  } finally {
    terminal.close();
    if (grantExchanged) {
      try {
        await client.revokeGrant();
        console.log("PASS grant revoke");
      } catch (error) {
        console.error(`FAIL grant revoke: ${errorMessage(error)}`);
        throw error;
      }
    }
  }
}

async function submitAndWait(client, submittedTask, timeoutMs = 60_000) {
  const initial = await submittedTask;
  const deadline = Date.now() + timeoutMs;
  let task = initial;
  while (task.status === "queued" || task.status === "running") {
    if (Date.now() >= deadline) {
      throw new Error(`Timed out waiting for task ${task.taskId}.`);
    }
    await new Promise((resolveDelay) => setTimeout(resolveDelay, 100));
    task = await client.getTask(task.taskId, { timeoutMs: 10_000 });
  }
  if (task.status !== "completed") {
    const detail = task.error ? `${task.error.code}: ${task.error.message}` : task.status;
    throw new Error(`Task ${task.taskId} did not complete (${detail}).`);
  }
  return task;
}

function requireRecord(value, label) {
  assert(value !== null && typeof value === "object" && !Array.isArray(value), `${label} was not an object`);
  return value;
}

function requireString(value, label) {
  assert(typeof value === "string" && value.length > 0, `${label} was missing`);
  return value;
}

function requireInteger(value, label) {
  assert(Number.isSafeInteger(value), `${label} was not an integer`);
  return value;
}

function assert(condition, message) {
  if (!condition) {
    throw new Error(message);
  }
}

function errorMessage(error) {
  return error instanceof Error ? error.message : "Unknown error";
}
