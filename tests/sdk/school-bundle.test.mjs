import assert from "node:assert/strict";
import test from "node:test";
import { OmrinaClient, OmrinaSdkError } from "../../packages/sdk/src/index.ts";

const schoolDefinition = {
  examId: "exam", layoutDocumentId: "layout", version: 2, title: "学校考试",
  paper: "A3Landscape", columns: 3,
  candidateIdentity: { mode: "Barcode", digits: 4, candidateId: "0123" },
  questions: [
    { number: 7, type: "Choice", options: ["甲", "乙"] },
    { number: 15, type: "Subjective", maximumScore: 5 },
  ],
  groups: [
    { id: "choice", title: "客观题", questionNumbers: [7] },
    { id: "written", title: "主观题", questionNumbers: [15] },
  ],
};
const templateId = "a".repeat(64);
const bundleDefinition = {
  ...schoolDefinition,
  groups: schoolDefinition.groups.map((group) => ({ ...group, choiceColumns: 1 })),
};
const bundle = {
  schemaVersion: 1,
  definition: bundleDefinition,
  pages: [{
    pageCode: `OM1${"A".repeat(26)}`,
    metadata: {
      examId: "exam", layoutDocumentId: "layout", version: 2, pageNumber: 1, side: "Front", templateId,
    },
  }],
  hash: "b".repeat(64),
};

function createClient(onTask) {
  return new OmrinaClient({
    fetch: async (url, init) => {
      if (String(url).endsWith("/v1/pairing/exchange")) {
        return new Response(JSON.stringify({ grantId: "grant", token: "token", expiresAt: "2099-01-01T00:00:00Z" }), {
          headers: { "Content-Type": "application/json" },
        });
      }
      onTask(JSON.parse(init.body));
      return new Response(JSON.stringify({ taskId: "task", operation: "schoolTemplateImport", status: "queued", result: null, error: null, createdAt: "2026-10-03T00:00:00Z", updatedAt: "2026-10-03T00:00:00Z" }), {
        status: 202,
        headers: { "Content-Type": "application/json" },
      });
    },
  });
}

test("school bundle import is a grant task and preserves its typed definition", async () => {
  let submitted;
  const client = createClient((body) => { submitted = body; });
  await client.exchangePairing({ requestId: "pair", code: "123456" });
  await client.createSchoolTemplateImportTask({ idempotencyKey: "school-import", parameters: { bundle } });
  assert.equal(submitted.operation, "schoolTemplateImport");
  assert.deepEqual(submitted.parameters.bundle, bundle);
  assert.equal(submitted.parameters.bundle.definition.candidateIdentity.candidateId, "0123");
});

test("school bundle import rejects malformed identities, unknown fields and oversized input before fetch", async () => {
  let fetchCount = 0;
  const client = new OmrinaClient({ fetch: async () => { fetchCount++; throw new Error("must not fetch"); } });
  const invalidBundles = [
    { ...bundle, path: "C:/private" },
    { ...bundle, hash: "not-a-hash" },
    { ...bundle, pages: [{ ...bundle.pages[0], metadata: { ...bundle.pages[0].metadata, side: "front" } }] },
    { ...bundle, pages: [{ ...bundle.pages[0], pageCode: "OM1bad" }] },
    { ...bundle, pages: [{ ...bundle.pages[0], metadata: { ...bundle.pages[0].metadata, templateId: "not-a-template-id" } }] },
    { ...bundle, pages: [bundle.pages[0], bundle.pages[0]] },
  ];
  for (const invalid of invalidBundles) {
    assert.throws(() => client.createSchoolTemplateImportTask({ idempotencyKey: "school-invalid", parameters: { bundle: invalid } }), OmrinaSdkError);
  }
  const tooLargeDefinition = {
    ...schoolDefinition,
    questions: Array.from({ length: 150 }, (_, index) => ({
      number: index + 1, type: "Subjective", body: "x".repeat(8000), maximumScore: 5,
    })),
    groups: Array.from({ length: 150 }, (_, index) => ({
      id: `written-${index + 1}`, title: `主观题组${index + 1}`, questionNumbers: [index + 1], choiceColumns: 1,
    })),
  };
  const tooLargeBundle = { ...bundle, definition: tooLargeDefinition };
  assert.throws(() => client.createSchoolTemplateImportTask({ idempotencyKey: "school-large", parameters: { bundle: tooLargeBundle } }), OmrinaSdkError);

  const requestLimitedDefinition = {
    ...schoolDefinition,
    questions: Array.from({ length: 10 }, (_, index) => ({
      number: index + 1, type: "Subjective", body: "x".repeat(8000), maximumScore: 5,
    })),
    groups: Array.from({ length: 10 }, (_, index) => ({
      id: `written-${index + 1}`, title: `主观题组${index + 1}`, questionNumbers: [index + 1], choiceColumns: 1,
    })),
  };
  const requestLimitedBundle = { ...bundle, definition: requestLimitedDefinition };
  const requestLimitedBytes = new TextEncoder().encode(JSON.stringify(requestLimitedBundle)).byteLength;
  assert.ok(requestLimitedBytes > 64 * 1024 && requestLimitedBytes < 1024 * 1024);
  assert.throws(() => client.createSchoolTemplateImportTask({ idempotencyKey: "school-request-limit", parameters: { bundle: requestLimitedBundle } }), OmrinaSdkError);
  assert.equal(fetchCount, 0);
});

test("school template export validates the grant-scoped page identity", () => {
  const client = new OmrinaClient({ fetch: async () => { throw new Error("must not fetch"); } });
  assert.throws(() => client.createSchoolTemplateExportTask({ idempotencyKey: "school-export", parameters: { templateId: "../outside" } }), OmrinaSdkError);
  assert.throws(() => client.createSchoolTemplateExportTask({ idempotencyKey: "school-export", parameters: { templateId, localPath: "C:/private" } }), OmrinaSdkError);
});
