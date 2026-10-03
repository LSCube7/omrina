import assert from "node:assert/strict";
import test from "node:test";
import { OmrinaClient, OmrinaSdkError } from "../../packages/sdk/src/index.ts";

const schoolDefinition = {
  examId: "exam", layoutDocumentId: "layout", version: 1, title: "学校考试",
  paper: "A3Landscape", columns: 3, bubbleShape: "Rectangle", labelPlacement: "Inside", bubbleWidthMm: 2,
  questions: [{ number: 7, type: "Choice", options: ["甲", "乙"] }, { number: 15, type: "Subjective", subjectiveHeightMm: 35 }],
};

test("school template task preserves typed definition without runtime dependency", async () => {
  let submitted;
  const client = new OmrinaClient({
    fetch: async (url, init) => {
      if (String(url).endsWith("/v1/pairing/exchange")) return new Response(JSON.stringify({ grantId: "grant", token: "token", expiresAt: "2099-01-01T00:00:00Z" }), { headers: { "Content-Type": "application/json" } });
      submitted = JSON.parse(init.body);
      return new Response(JSON.stringify({ taskId: "task", operation: "template", status: "queued", result: null, error: null, createdAt: "2026-10-03T00:00:00Z", updatedAt: "2026-10-03T00:00:00Z" }), { status: 202, headers: { "Content-Type": "application/json" } });
    },
  });
  await client.exchangePairing({ requestId: "pair", code: "123456" });
  for (const width of [2, 3, 4]) {
    const definition = { ...schoolDefinition, bubbleWidthMm: width, bubbleHeightMm: 2 };
    await client.createSchoolTemplateTask({ idempotencyKey: `school-task-${width}`, parameters: { schoolDefinition: definition } });
    assert.deepEqual(submitted.parameters.schoolDefinition, definition);
  }
});

test("school template rejects oversized bubbles, unknown/path fields and duplicate global question numbers before fetch", () => {
  const client = new OmrinaClient({ fetch: async () => { throw new Error("must not fetch"); } });
  for (const invalid of [
    { ...schoolDefinition, bubbleWidthMm: 2.01 },
    { ...schoolDefinition, bubbleWidthMm: 1.85 },
    { ...schoolDefinition, bubbleHeightMm: 2.1 },
    { ...schoolDefinition, bubbleHeightMm: 1.85 },
    { ...schoolDefinition, bubbleWidthMm: Infinity },
    { ...schoolDefinition, bubbleWidthMm: 0 },
    { ...schoolDefinition, bubbleShape: "Circle", bubbleWidthMm: 2.1 },
    { ...schoolDefinition, path: "C:/private" },
    { ...schoolDefinition, questions: [schoolDefinition.questions[0], schoolDefinition.questions[0]] },
    { ...schoolDefinition, candidateIdentity: { mode: "Barcode", digits: 4, candidateId: "12345" } },
    { ...schoolDefinition, questions: [{ number: 1, type: "Subjective", rectangleMm: { x: 0 } }] },
  ]) {
    assert.throws(() => client.createSchoolTemplateTask({ idempotencyKey: "school-invalid", parameters: { schoolDefinition: invalid } }), OmrinaSdkError);
  }
});

test("school template validates explicit homogeneous groups and preserves layout order", async () => {
  let submitted;
  const client = new OmrinaClient({
    fetch: async (url, init) => {
      if (String(url).endsWith("/v1/pairing/exchange")) return new Response(JSON.stringify({ grantId: "grant", token: "token", expiresAt: "2099-01-01T00:00:00Z" }), { headers: { "Content-Type": "application/json" } });
      submitted = JSON.parse(init.body);
      return new Response(JSON.stringify({ taskId: "task", operation: "template", status: "queued", result: null, error: null, createdAt: "2026-10-03T00:00:00Z", updatedAt: "2026-10-03T00:00:00Z" }), { status: 202, headers: { "Content-Type": "application/json" } });
    },
  });
  await client.exchangePairing({ requestId: "pair", code: "123456" });
  const definition = {
    ...schoolDefinition,
    layoutOrder: "Separated",
    groups: [
      { id: "subjective", title: "主观题", questionNumbers: [15] },
      { id: "objective", title: "客观题", questionNumbers: [7] },
    ],
  };
  await client.createSchoolTemplateTask({ idempotencyKey: "school-groups", parameters: { schoolDefinition: definition } });
  assert.deepEqual(submitted.parameters.schoolDefinition, definition);
});

test("school template rejects malformed groups and unsupported layout order before fetch", () => {
  const client = new OmrinaClient({ fetch: async () => { throw new Error("must not fetch"); } });
  const validGroups = [
    { id: "choice", title: "客观题", questionNumbers: [7] },
    { id: "essay", title: "主观题", questionNumbers: [15] },
  ];
  for (const invalid of [
    { ...schoolDefinition, layoutOrder: "separated" },
    { ...schoolDefinition, groups: [{ id: "bad id", title: "客观题", questionNumbers: [7, 15] }] },
    { ...schoolDefinition, groups: [{ id: "choice", title: "客观题", questionNumbers: [7], localPath: "C:/private" }, validGroups[1]] },
    { ...schoolDefinition, groups: [validGroups[0], { ...validGroups[1], id: "choice" }] },
    { ...schoolDefinition, groups: [validGroups[0]] },
    { ...schoolDefinition, groups: [{ ...validGroups[0], questionNumbers: [7, 7] }, validGroups[1]] },
    { ...schoolDefinition, groups: [{ id: "mixed", title: "混合题型", questionNumbers: [7, 15] }] },
    { ...schoolDefinition, groups: [{ ...validGroups[0], questionNumbers: [] }, validGroups[1]] },
    { ...schoolDefinition, groups: [{ ...validGroups[0], title: "主观\n题" }, validGroups[1]] },
  ]) {
    assert.throws(() => client.createSchoolTemplateTask({ idempotencyKey: "school-invalid-group", parameters: { schoolDefinition: invalid } }), OmrinaSdkError);
  }
});
