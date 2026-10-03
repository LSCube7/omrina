import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { createInterface } from "node:readline";
import { readFile } from "node:fs/promises";
import { randomUUID } from "node:crypto";
import { OmrinaClient } from "../../packages/sdk/dist/index.js";

const host = spawn("dotnet", ["tests/m3-desktop/bin/Debug/net10.0/Omrina.M3.Desktop.Tests.dll", "--school-http-harness"], { stdio: ["pipe", "pipe", "inherit"], windowsHide: true });
const lines = createInterface({ input: host.stdout });
const queued = [];
const waiting = [];
lines.on("line", (line) => {
  const waiter = waiting.shift();
  if (waiter) waiter(line); else queued.push(line);
});
const nextLine = () => queued.length ? Promise.resolve(queued.shift()) : new Promise((resolve, reject) => {
  const timer = setTimeout(() => reject(new Error("HTTP harness timed out")), 10000);
  waiting.push((line) => { clearTimeout(timer); resolve(line); });
});
const waitTask = async (client, submission, expected = "completed") => {
  let task = await submission;
  for (let attempt = 0; attempt < 150; attempt++) {
    if (["completed", "failed", "cancelled"].includes(task.status)) {
      assert.equal(task.status, expected, JSON.stringify(task.error));
      return task;
    }
    await new Promise((resolve) => setTimeout(resolve, 50));
    task = await client.getTask(task.taskId);
  }
  throw new Error("School task did not complete");
};
try {
  const ready = await nextLine();
  assert.ok(ready.startsWith("READY "));
  const { endpoint, fixture, definition } = JSON.parse(ready.slice(6));
  const client = new OmrinaClient({ endpoint, fetch: (url, init) => {
    const headers = new Headers(init.headers); headers.set("Origin", "http://localhost:3000");
    return fetch(url, { ...init, headers });
  }});
  const ticket = await client.requestPairing({ clientName: "School SDK regression" });
  host.stdin.write(`approve ${ticket.requestId}\n`);
  const approval = await nextLine();
  await client.exchangePairing({ requestId: ticket.requestId, code: JSON.parse(approval.slice(9)).code });
  const template = (await waitTask(client, client.createSchoolTemplateTask({ idempotencyKey: randomUUID(), parameters: { schoolDefinition: definition } }))).result;
  assert.equal(template.examId, definition.examId);
  assert.equal(template.pages[0].widthMm, 420);
  assert.equal(template.pages[0].schoolMetadata.side, "Front");
  assert.deepEqual(template.pages[0].schoolGroups.map((group) => group.groupId), definition.groups.map((group) => group.id));
  assert.deepEqual(template.pages[0].schoolGroups.map((group) => group.questionNumbers), [[7], [15]]);
  assert.ok(template.pages[0].schoolGroups.every((group) => group.rectangleMm.width > 0 && group.rectangleMm.height > 0));
  const bytes = new Uint8Array(await readFile(fixture));
  const capture = (await waitTask(client, client.uploadImage({ idempotencyKey: randomUUID(), templateId: template.pages[0].templateId, fileName: "school.png", contentType: "image/png", body: bytes }))).result;
  assert.equal(capture.candidateId, "0123");
  assert.equal(capture.identityStatus, "Identified");
  const recognized = (await waitTask(client, client.createRecognitionTask({ idempotencyKey: randomUUID(), parameters: { captureId: capture.captureId } }))).result;
  assert.equal(recognized.schoolMetadata.examId, definition.examId);
  assert.equal(recognized.candidateId, "0123");
  const reviewed = await waitTask(client, client.createReviewTask({ idempotencyKey: randomUUID(), parameters: { resultId: recognized.resultId, expectedVersion: recognized.version, reviewer: "synthetic-teacher", edits: [{ questionNumber: 7, answer: "A", reason: "非连续题号复核" }], answerKey: { "7": "A" } } }));
  assert.ok(reviewed.result.version > recognized.version);
  const otherTemplate = (await waitTask(client, client.createSchoolTemplateTask({ idempotencyKey: randomUUID(), parameters: { schoolDefinition: { ...definition, examId: "other-exam" } } }))).result;
  const wrongUpload = await waitTask(client, client.uploadImage({ idempotencyKey: randomUUID(), templateId: otherTemplate.pages[0].templateId, fileName: "wrong.png", contentType: "image/png", body: bytes }), "failed");
  assert.equal(wrongUpload.error.code, "CAPTURE_SCHOOL_PAGE_MISMATCH");
  assert.equal((await client.getTemplates()).length, 1, "native/user school records must not leak into catalog");
  await client.revokeGrant();
  console.log("PASS school real HTTP SDK: pairing, A3 template, capture candidate, recognition, global-number review, wrong exam rejection, scoped catalog, revocation");
} finally {
  host.stdin.write("quit\n");
  host.stdin.end();
  lines.close();
  await new Promise((resolve) => { if (host.exitCode !== null) resolve(); else host.once("exit", resolve); });
}
