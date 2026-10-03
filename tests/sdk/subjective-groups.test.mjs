import assert from "node:assert/strict";
import test from "node:test";
import { OmrinaClient, OmrinaSdkError } from "../../packages/sdk/src/index.ts";

const reviewId = "123e4567-e89b-12d3-a456-426614174000";
const captureId = "123e4567-e89b-12d3-a456-426614174001";
const png = new Uint8Array([137, 80, 78, 71, 13, 10, 26, 10, 0]);

async function pairedClient(fetch) {
  const client = new OmrinaClient({ fetch: async (url, init) => {
    if (String(url).endsWith("/v1/pairing/exchange")) {
      return new Response(JSON.stringify({ grantId: "grant", token: "token", expiresAt: "2099-01-01T00:00:00Z" }), {
        headers: { "Content-Type": "application/json" },
      });
    }
    return fetch(url, init);
  } });
  await client.exchangePairing({ requestId: "pair", code: "123456" });
  return client;
}

test("subjective group image methods use grant-authenticated PNG routes", async () => {
  const requests = [];
  const client = await pairedClient(async (url, init) => {
    requests.push({ url: String(url), headers: new Headers(init.headers) });
    return new Response(png, { headers: { "Content-Type": "image/png" } });
  });
  const fromReview = await client.getSubjectiveReviewGroupImage(reviewId, "g.1");
  const fromCapture = await client.getSubjectiveCaptureGroupImage(captureId, "g.1");
  assert.deepEqual([...fromReview], [...png]);
  assert.deepEqual([...fromCapture], [...png]);
  assert.deepEqual(requests.map((request) => new URL(request.url).pathname), [
    `/v1/subjective-reviews/${reviewId}/groups/g.1/image`,
    `/v1/captures/${captureId}/groups/g.1/image`,
  ]);
  assert.ok(requests.every((request) => request.headers.get("authorization") === "Bearer token"));
});

test("subjective group image validates identifiers and PNG bytes before returning them", async () => {
  let fetchCount = 0;
  const client = await pairedClient(async () => {
    fetchCount++;
    return new Response(new Uint8Array([1, 2, 3]), { headers: { "Content-Type": "image/jpeg" } });
  });
  assert.throws(() => client.getSubjectiveReviewGroupImage(reviewId, "../private"), (error) => error.code === "INVALID_ARGUMENT");
  assert.throws(() => client.getSubjectiveReviewGroupImage(reviewId, ".g"), (error) => error.code === "INVALID_ARGUMENT");
  assert.throws(() => client.getSubjectiveCaptureGroupImage(captureId, "-g"), (error) => error.code === "INVALID_ARGUMENT");
  assert.throws(() => client.getSubjectiveCaptureGroupImage("../../private", "written-group"), (error) => error.code === "INVALID_ARGUMENT");
  await assert.rejects(client.getSubjectiveReviewGroupImage(reviewId, "written-group"), (error) => error.code === "INVALID_RESPONSE");
  assert.equal(fetchCount, 1);
});

test("revoked group image authorization clears the SDK grant", async () => {
  let imageCalls = 0;
  const client = await pairedClient(async () => {
    imageCalls++;
    return new Response(JSON.stringify({ code: "UNAUTHORIZED", message: "Grant revoked." }), {
      status: 403,
      headers: { "Content-Type": "application/json" },
    });
  });
  await assert.rejects(client.getSubjectiveReviewGroupImage(reviewId, "written-group"), (error) => error.code === "UNAUTHORIZED");
  await assert.rejects(client.getSubjectiveReviewGroupImage(reviewId, "written-group"), (error) => error.code === "MISSING_CREDENTIAL");
  assert.equal(imageCalls, 1);
});
