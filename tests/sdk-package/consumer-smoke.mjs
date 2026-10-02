import assert from "node:assert/strict";
import { existsSync } from "node:fs";
import { spawnSync } from "node:child_process";
import { cp, mkdir, mkdtemp, writeFile } from "node:fs/promises";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const repoRoot = resolve(dirname(fileURLToPath(import.meta.url)), "../..");
const sdkRoot = join(repoRoot, "packages", "sdk");
const fixtureRoot = join(repoRoot, "tests", "sdk-package");
const artifactsRoot = join(repoRoot, "artifacts");
await mkdir(artifactsRoot, { recursive: true });
const outputRoot = await mkdtemp(join(artifactsRoot, "sdk-package-consumer-"));
const packDirectory = join(outputRoot, "pack");
const cacheDirectory = join(outputRoot, "npm-cache");
const extractionDirectory = join(outputRoot, "extracted");
const consumerDirectory = join(outputRoot, "consumer");
await Promise.all([
  mkdir(packDirectory),
  mkdir(cacheDirectory),
  mkdir(extractionDirectory),
  mkdir(consumerDirectory),
]);

runNpm(["run", "build"]);
console.log("PASS TypeScript 5.9.3 package build");

const packOutput = runNpm([
  "pack",
  "--json",
  "--pack-destination",
  packDirectory,
  "--cache",
  cacheDirectory,
], sdkRoot);
const packMetadata = JSON.parse(packOutput.stdout);
assert(Array.isArray(packMetadata) && packMetadata.length === 1, "npm pack did not report one package");
const tarballPath = join(packDirectory, packMetadata[0].filename);
const tarList = run("tar", ["-tzf", tarballPath], repoRoot).stdout
  .split(/\r?\n/)
  .filter((entry) => entry.length > 0 && !entry.endsWith("/"))
  .map((entry) => entry.replace(/^\.\//, ""))
  .sort();
const expectedTarEntries = [
  "package/README.md",
  "package/dist/client.d.ts",
  "package/dist/client.js",
  "package/dist/errors.d.ts",
  "package/dist/errors.js",
  "package/dist/health.d.ts",
  "package/dist/health.js",
  "package/dist/index.d.ts",
  "package/dist/index.js",
  "package/package.json",
].sort();
assert.deepEqual(tarList, expectedTarEntries, "npm tarball contained an unexpected or missing file");
console.log("PASS tarball contains only README, package manifest, and dist files");

run("tar", ["-xzf", tarballPath, "-C", extractionDirectory], repoRoot);
const installedPackageDirectory = join(consumerDirectory, "node_modules", "@omrina", "local-sdk");
await mkdir(dirname(installedPackageDirectory), { recursive: true });
await cp(join(extractionDirectory, "package"), installedPackageDirectory, { recursive: true, errorOnExist: true });
await writeFile(join(consumerDirectory, "package.json"), JSON.stringify({ type: "module" }, null, 2));
await writeFile(join(consumerDirectory, "tsconfig.json"), JSON.stringify({
  compilerOptions: {
    target: "ES2022",
    module: "NodeNext",
    moduleResolution: "NodeNext",
    strict: true,
    noEmit: true,
    lib: ["ES2022", "DOM", "DOM.Iterable"],
    types: [],
  },
  files: ["consumer.mts"],
}, null, 2));
await Promise.all([
  cp(join(fixtureRoot, "consumer.mts"), join(consumerDirectory, "consumer.mts")),
  cp(join(fixtureRoot, "consumer-runtime.mjs"), join(consumerDirectory, "consumer-runtime.mjs")),
]);

const compiler = join(sdkRoot, "node_modules", "typescript", "bin", "tsc");
run(process.execPath, [compiler, "--project", join(consumerDirectory, "tsconfig.json")], consumerDirectory);
console.log("PASS strict TypeScript consumer resolves tarball declarations via package exports");
const runtimeResult = run(process.execPath, [join(consumerDirectory, "consumer-runtime.mjs")], consumerDirectory);
process.stdout.write(runtimeResult.stdout);
console.log(`Artifacts: ${outputRoot}`);

function runNpm(args) {
  const adjacentNpmCli = join(dirname(process.execPath), "node_modules", "npm", "bin", "npm-cli.js");
  if (existsSync(adjacentNpmCli)) {
    return run(process.execPath, [adjacentNpmCli, ...args], sdkRoot);
  }
  if (process.platform === "win32") {
    throw new Error("The npm CLI was not found beside the active Node.js installation; no install was attempted.");
  }
  return run("npm", args, sdkRoot);
}

function run(command, args, cwd) {
  const result = spawnSync(command, args, {
    cwd,
    encoding: "utf8",
  });
  if (result.error) {
    throw result.error;
  }
  if (result.status !== 0) {
    const detail = [result.stdout, result.stderr].filter(Boolean).join("\n").trim();
    throw new Error(`${command} ${args.join(" ")} failed with status ${result.status}.\n${detail}`);
  }
  return result;
}
