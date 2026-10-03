import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { createInterface } from "node:readline";
import { readFile } from "node:fs/promises";
import { randomUUID } from "node:crypto";
import { OmrinaClient } from "../../packages/sdk/dist/index.js";

const origin = "http://localhost:3000";
const requestOptions = { timeoutMs: 10_000 };
const host = spawn("dotnet", ["tests/m3-desktop/bin/Debug/net10.0/Omrina.M3.Desktop.Tests.dll", "--school-http-harness"], {
  stdio: ["pipe", "pipe", "inherit"],
  windowsHide: true,
});
const lines = createInterface({ input: host.stdout });
const queued = [];
const waiting = [];
let grantA;
let grantB;
let clientA;
let clientB;
let staleClientA;

lines.on("line", (line) => {
  const waiter = waiting.shift();
  if (waiter) waiter(line); else queued.push(line);
});

try {
  const ready = await nextLine();
  assert.ok(ready.startsWith("READY "));
  const { endpoint, fixture, definition } = JSON.parse(ready.slice(6));
  const bytes = new Uint8Array(await readFile(fixture));
  clientA = createClient(endpoint);
  grantA = await pair(clientA, "School SDK group regression A");
  staleClientA = createClient(endpoint, grantA);

  const template = (await waitTask(clientA, clientA.createSchoolTemplateTask({
    idempotencyKey: randomUUID(),
    parameters: { schoolDefinition: definition },
  }))).result;
  assert.equal(template.examId, definition.examId);
  assert.equal(template.pages[0].widthMm, 420);
  assert.equal(template.pages[0].schoolMetadata.side, "Front");
  assert.match(template.pages[0].pageCode, /^OM1[A-Z2-7]{26}$/);
  assert.deepEqual(template.pages[0].schoolGroups.map((group) => group.groupId), definition.groups.map((group) => group.id));
  assert.deepEqual(template.pages[0].schoolGroups.map((group) => group.questionNumbers), [[7], [15, 16]]);
  assert.deepEqual(template.pages[0].schoolGroups.map((group) => group.choiceColumns), [2, 1]);
  assert.ok(template.pages[0].schoolGroups.every((group) => group.rectangleMm.width > 0 && group.rectangleMm.height > 0));

  const bundle = (await waitTask(clientA, clientA.createSchoolTemplateExportTask({
    idempotencyKey: randomUUID(),
    parameters: { templateId: template.pages[0].templateId },
  }))).result;
  assert.equal(bundle.schemaVersion, 1);
  assert.equal(bundle.definition.candidateIdentity.candidateId, "0123");
  assert.deepEqual(bundle.pages.map((page) => page.pageCode), [template.pages[0].pageCode]);
  assert.equal(bundle.pages[0].metadata.templateId, template.pages[0].templateId);
  assert.equal(bundle.definition.groups[0].choiceColumns, 2);

  const capture = (await waitTask(clientA, clientA.uploadImage({
    idempotencyKey: randomUUID(),
    templateId: template.pages[0].templateId,
    fileName: "school.png",
    contentType: "image/png",
    body: bytes,
  }))).result;
  assert.equal(capture.candidateId, "0123");
  assert.equal(capture.identityStatus, "Identified");
  const recognized = (await waitTask(clientA, clientA.createRecognitionTask({
    idempotencyKey: randomUUID(),
    parameters: { captureId: capture.captureId },
  }))).result;
  assert.equal(recognized.schoolMetadata.examId, definition.examId);
  assert.equal(recognized.candidateId, "0123");
  const reviewed = await waitTask(clientA, clientA.createReviewTask({
    idempotencyKey: randomUUID(),
    parameters: {
      resultId: recognized.resultId,
      expectedVersion: recognized.version,
      reviewer: "synthetic-teacher",
      edits: [{ questionNumber: 7, answer: "A", reason: "非连续题号复核" }],
      answerKey: { "7": "A" },
    },
  }));
  assert.ok(reviewed.result.version > recognized.version);

  clientB = createClient(endpoint);
  grantB = await pair(clientB, "School SDK group regression B");
  const foreignExport = await waitTask(clientB, clientB.createSchoolTemplateExportTask({
    idempotencyKey: randomUUID(),
    parameters: { templateId: template.pages[0].templateId },
  }), "failed");
  assert.equal(foreignExport.error.code, "TEMPLATE_NOT_FOUND");
  await assertImageDenied(
    () => clientB.getSubjectiveCaptureGroupImage(capture.captureId, "g.1", requestOptions),
    "second grant cannot read the first grant's group image by capture ID",
  );

  const damaged = { ...bundle, hash: bundle.hash[0] === "0" ? `1${bundle.hash.slice(1)}` : `0${bundle.hash.slice(1)}` };
  const damagedImport = await waitTask(clientB, clientB.createSchoolTemplateImportTask({
    idempotencyKey: randomUUID(),
    parameters: { bundle: damaged },
  }), "failed");
  assert.equal(damagedImport.error.code, "INVALID_SCHOOL_BUNDLE");
  await assertTemplateNotRegistered(clientB, bundle.pages[0].metadata.templateId);

  const unknownBundle = { ...bundle, unexpected: true };
  const unknownImport = await submitRawImport(clientB, grantB, endpoint, JSON.stringify(unknownBundle));
  assert.equal(unknownImport.error.code, "INVALID_SCHOOL_BUNDLE");
  await assertTemplateNotRegistered(clientB, bundle.pages[0].metadata.templateId);

  const duplicateBundleJson = JSON.stringify(bundle).replace(/^\{/, '{"schemaVersion":1,');
  const duplicateImport = await submitRawImport(clientB, grantB, endpoint, duplicateBundleJson);
  assert.equal(duplicateImport.error.code, "INVALID_SCHOOL_BUNDLE");
  await assertTemplateNotRegistered(clientB, bundle.pages[0].metadata.templateId);

  const imported = (await waitTask(clientB, clientB.createSchoolTemplateImportTask({
    idempotencyKey: randomUUID(),
    parameters: { bundle },
  }))).result;
  assert.equal(imported.examId, definition.examId);
  assert.equal(imported.pages.length, 1);
  assert.equal(imported.pages[0].templateId, template.pages[0].templateId);
  assert.equal(imported.pages[0].pageCode, template.pages[0].pageCode);
  assert.equal(imported.pages[0].schoolGroups[0].choiceColumns, 2);
  const bundleAgain = (await waitTask(clientB, clientB.createSchoolTemplateExportTask({
    idempotencyKey: randomUUID(),
    parameters: { templateId: imported.pages[0].templateId },
  }))).result;
  assert.equal(bundleAgain.hash, bundle.hash);

  const captureB = (await waitTask(clientB, clientB.uploadImage({
    idempotencyKey: randomUUID(),
    templateId: imported.pages[0].templateId,
    fileName: "school-copy.png",
    contentType: "image/png",
    body: bytes,
  }))).result;
  assert.equal(captureB.candidateId, "0123");
  assert.equal(captureB.identityStatus, "Identified");

  const reviewCreatedA = await waitTask(clientA, clientA.createSubjectiveReviewTask({
    idempotencyKey: randomUUID(),
    parameters: { captureId: capture.captureId },
  }));
  const reviewA = unwrapSnapshot(reviewCreatedA.result);
  assert.equal(reviewA.captureId, capture.captureId);
  assert.equal(reviewA.identityStatus, "Identified");
  assert.equal(reviewA.candidateId, "0123");
  assert.equal(reviewA.groups.length, 1);
  const groupA = reviewA.groups[0];
  assert.equal(groupA.groupId, "g.1");
  assert.deepEqual(groupA.questionNumbers, [15, 16]);
  assert.equal(groupA.maxScore, 11);
  assert.equal(groupA.status, "provisional");
  assert.equal(groupA.finalSubtotal, null);
  assert.equal(reviewA.questions.length, 2);
  assert.ok(reviewA.questions.every((question) => question.imageGroupId === groupA.groupId));

  const captureGroupImage = await clientA.getSubjectiveCaptureGroupImage(capture.captureId, groupA.groupId, requestOptions);
  const reviewGroupImage = await clientA.getSubjectiveReviewGroupImage(reviewA.reviewId, groupA.groupId, requestOptions);
  const memberQuestionImage = await clientA.getSubjectiveQuestionImage(reviewA.reviewId, reviewA.questions[1].questionId, requestOptions);
  assertPng(captureGroupImage, "capture group image");
  assert.deepEqual(reviewGroupImage, captureGroupImage, "review group and capture group share the same rectified image");
  assert.deepEqual(memberQuestionImage, captureGroupImage, "each member question uses the shared group image");
  await assertImageDenied(
    () => clientB.getSubjectiveReviewGroupImage(reviewA.reviewId, groupA.groupId, requestOptions),
    "second grant cannot read the first grant's group image by review ID",
  );
  await assertTaskFailure(clientB, clientB.createSubjectiveReadTask({
    idempotencyKey: randomUUID(),
    parameters: { reviewId: reviewA.reviewId },
  }), "SUBJECTIVE_REVIEW_NOT_FOUND", "cross-grant review read");

  const reviewCreatedB = await waitTask(clientB, clientB.createSubjectiveReviewTask({
    idempotencyKey: randomUUID(),
    parameters: { captureId: captureB.captureId },
  }));
  const reviewB = unwrapSnapshot(reviewCreatedB.result);
  const ownGroupImageB = await clientB.getSubjectiveReviewGroupImage(reviewB.reviewId, "g.1", requestOptions);
  assertPng(ownGroupImageB, "imported-template review group image");

  const questionIds = reviewA.questions.map((question) => question.questionId);
  const firstDraft = await waitTask(clientA, clientA.createSubjectiveGradeTask({
    idempotencyKey: randomUUID(),
    parameters: {
      reviewId: reviewA.reviewId,
      expectedVersion: reviewA.version,
      reviewer: "synthetic-teacher",
      edits: [{ questionId: questionIds[0], status: "draft", score: 3, comment: "第一题草稿" },
        { questionId: questionIds[1], status: "draft", score: 4, comment: "第二题草稿" }],
    },
  }));
  const draftDocument = unwrapSnapshot(firstDraft.result);
  assert.equal(draftDocument.groups[0].provisionalSubtotal, 7);
  assert.equal(draftDocument.groups[0].finalSubtotal, null);
  assert.equal(draftDocument.groups[0].status, "provisional");

  const firstConfirmed = await waitTask(clientA, clientA.createSubjectiveGradeTask({
    idempotencyKey: randomUUID(),
    parameters: {
      reviewId: reviewA.reviewId,
      expectedVersion: draftDocument.version,
      reviewer: "synthetic-teacher",
      edits: [{ questionId: questionIds[0], status: "confirmed", score: 3, comment: "第一题草稿" }],
    },
  }));
  const mixedDocument = unwrapSnapshot(firstConfirmed.result);
  assert.equal(mixedDocument.questions[0].status, "confirmed");
  assert.equal(mixedDocument.questions[1].status, "draft");
  assert.equal(mixedDocument.groups[0].provisionalSubtotal, 7);
  assert.equal(mixedDocument.groups[0].finalSubtotal, null, "one confirmed member must not finalize the group");
  const mixedExport = await waitTask(clientA, clientA.createSubjectiveExportTask({
    idempotencyKey: randomUUID(),
    parameters: { reviewId: reviewA.reviewId, format: "json" },
  }));
  const mixedExportDocument = JSON.parse(mixedExport.result.content);
  assert.equal(mixedExportDocument.questions[0].status, "confirmed");
  assert.equal(mixedExportDocument.questions[1].status, "draft");
  assert.ok(mixedExportDocument.questions.every((question) => question.imageGroupId === "g.1"));
  assert.equal(mixedExportDocument.groups[0].finalSubtotal, null);

  const secondConfirmed = await waitTask(clientA, clientA.createSubjectiveGradeTask({
    idempotencyKey: randomUUID(),
    parameters: {
      reviewId: reviewA.reviewId,
      expectedVersion: mixedDocument.version,
      reviewer: "synthetic-teacher",
      edits: [{ questionId: questionIds[1], status: "confirmed", score: 4, comment: "第二题草稿" }],
    },
  }));
  const finalDocument = unwrapSnapshot(secondConfirmed.result);
  assert.equal(finalDocument.groups[0].status, "final");
  assert.equal(finalDocument.groups[0].provisionalSubtotal, 7);
  assert.equal(finalDocument.groups[0].finalSubtotal, 7);
  assert.equal(finalDocument.finalSubtotal, 7);
  assert.ok(finalDocument.questions.every((question) => question.status === "confirmed"));
  const finalExport = await waitTask(clientA, clientA.createSubjectiveExportTask({
    idempotencyKey: randomUUID(),
    parameters: { reviewId: reviewA.reviewId, format: "json" },
  }));
  const finalExportDocument = JSON.parse(finalExport.result.content);
  assert.equal(finalExportDocument.groups[0].finalSubtotal, 7);
  assert.deepEqual(finalExportDocument.questions.map((question) => question.questionNumber), [15, 16]);
  assert.ok(finalExportDocument.questions.every((question) => question.imageGroupId === "g.1"));

  const otherTemplate = (await waitTask(clientA, clientA.createSchoolTemplateTask({
    idempotencyKey: randomUUID(),
    parameters: { schoolDefinition: { ...definition, examId: "other-exam" } },
  }))).result;
  const wrongUpload = await waitTask(clientA, clientA.uploadImage({
    idempotencyKey: randomUUID(),
    templateId: otherTemplate.pages[0].templateId,
    fileName: "wrong.png",
    contentType: "image/png",
    body: bytes,
  }), "failed");
  assert.equal(wrongUpload.error.code, "CAPTURE_SCHOOL_PAGE_MISMATCH");
  assert.equal((await clientA.getTemplates()).length, 1, "native/user school records must not leak into catalog");

  await clientA.revokeGrant(requestOptions);
  await assert.rejects(
    () => staleClientA.getSubjectiveReviewGroupImage(reviewA.reviewId, groupA.groupId, requestOptions),
    (error) => error?.status === 401 && error?.remoteCode === "UNAUTHORIZED",
    "a revoked grant cannot retain access to the group image",
  );
  console.log("PASS school real HTTP SDK: grant-scoped bundle transfer, page codes, group crops, independent question grading, cross-grant isolation, identity-aware subtotals, mismatch rejection and revocation");
} finally {
  if (clientA && grantA) await clientA.revokeGrant(requestOptions).catch(() => {});
  if (clientB && grantB) await clientB.revokeGrant(requestOptions).catch(() => {});
  if (host.exitCode === null && host.stdin.writable) {
    host.stdin.write("quit\n");
    host.stdin.end();
  }
  lines.close();
  await new Promise((resolve) => { if (host.exitCode !== null) resolve(); else host.once("exit", resolve); });
}

function createClient(endpoint, grant) {
  return new OmrinaClient({
    endpoint,
    ...(grant ? { grant } : {}),
    fetch: (url, init) => {
      const headers = new Headers(init?.headers);
      headers.set("Origin", origin);
      return fetch(url, { ...init, headers });
    },
  });
}

async function pair(client, clientName) {
  const ticket = await client.requestPairing({ clientName }, requestOptions);
  host.stdin.write(`approve ${ticket.requestId}\n`);
  const approval = await nextLine();
  assert.ok(approval.startsWith("APPROVAL "));
  return client.exchangePairing({ requestId: ticket.requestId, code: JSON.parse(approval.slice(9)).code }, requestOptions);
}

function nextLine() {
  if (queued.length) return Promise.resolve(queued.shift());
  return new Promise((resolve, reject) => {
    const timer = setTimeout(() => reject(new Error("HTTP harness timed out")), 10_000);
    waiting.push((line) => { clearTimeout(timer); resolve(line); });
  });
}

async function waitTask(client, submission, expected = "completed") {
  let task = await submission;
  for (let attempt = 0; attempt < 150; attempt++) {
    if (["completed", "failed", "cancelled"].includes(task.status)) {
      assert.equal(task.status, expected, JSON.stringify(task.error));
      return task;
    }
    await new Promise((resolve) => setTimeout(resolve, 50));
    task = await client.getTask(task.taskId, requestOptions);
  }
  throw new Error("School task did not complete");
}

async function submitRawImport(client, grant, endpoint, bundleJson) {
  const body = `{"idempotencyKey":"${randomUUID()}","operation":"schoolTemplateImport","parameters":{"bundle":${bundleJson}}}`;
  const response = await fetch(`${endpoint}/v1/tasks`, {
    method: "POST",
    headers: {
      Origin: origin,
      Authorization: `Bearer ${grant.token}`,
      "Content-Type": "application/json",
    },
    body,
  });
  assert.equal(response.status, 202, "raw strictness probe should be accepted as a task");
  const initial = await response.json();
  return waitTask(client, Promise.resolve(initial), "failed");
}

async function assertTemplateNotRegistered(client, templateId) {
  const failure = await waitTask(client, client.createSchoolTemplateExportTask({
    idempotencyKey: randomUUID(),
    parameters: { templateId },
  }), "failed");
  assert.equal(failure.error.code, "TEMPLATE_NOT_FOUND", "a rejected bundle must leave no template behind");
}

async function assertTaskFailure(client, submission, code, label) {
  const failure = await waitTask(client, submission, "failed");
  assert.equal(failure.error.code, code, `${label} should fail with ${code}`);
}

async function assertImageDenied(request, message) {
  await assert.rejects(request, (error) => error?.status === 404 && error?.remoteCode === "SUBJECTIVE_IMAGE_NOT_FOUND", message);
}

function unwrapSnapshot(result) {
  const snapshot = result.snapshot ?? result;
  return typeof snapshot === "string" ? JSON.parse(snapshot) : snapshot;
}

function assertPng(value, label) {
  const bytes = Buffer.from(value);
  assert(bytes.length > 8, `${label} is empty`);
  assert.deepEqual([...bytes.subarray(0, 8)], [137, 80, 78, 71, 13, 10, 26, 10], `${label} has invalid PNG signature`);
}
