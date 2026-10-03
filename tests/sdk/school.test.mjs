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
  await client.createSchoolTemplateTask({ idempotencyKey: "school-task", parameters: { schoolDefinition } });
  assert.deepEqual(submitted.parameters.schoolDefinition, schoolDefinition);
});

test("school template rejects oversized bubbles, unknown/path fields and duplicate global question numbers before fetch", () => {
  const client = new OmrinaClient({ fetch: async () => { throw new Error("must not fetch"); } });
  for (const invalid of [
    { ...schoolDefinition, bubbleWidthMm: 2.01 },
    { ...schoolDefinition, path: "C:/private" },
    { ...schoolDefinition, questions: [schoolDefinition.questions[0], schoolDefinition.questions[0]] },
    { ...schoolDefinition, candidateIdentity: { mode: "Barcode", digits: 4, candidateId: "12345" } },
    { ...schoolDefinition, questions: [{ number: 1, type: "Subjective", rectangleMm: { x: 0 } }] },
  ]) {
    assert.throws(() => client.createSchoolTemplateTask({ idempotencyKey: "school-invalid", parameters: { schoolDefinition: invalid } }), OmrinaSdkError);
  }
});
