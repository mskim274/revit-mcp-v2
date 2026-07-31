import { spawnSync } from "node:child_process";
import { createHash } from "node:crypto";
import {
  existsSync,
  readFileSync,
  readdirSync,
  statSync,
} from "node:fs";
import { dirname, join, resolve } from "node:path";

const [mode, tag, expectedCommit, artifactDirectory] = process.argv.slice(2);
if (
  !["prepare", "finalize"].includes(mode) ||
  !/^v\d+\.\d+\.\d+$/.test(tag ?? "") ||
  !/^[a-f0-9]{40}$/i.test(expectedCommit ?? "") ||
  !artifactDirectory
) {
  throw new Error(
    "Usage: release-state.mjs <prepare|finalize> <vX.Y.Z> <commit> <artifact-directory>",
  );
}

const repository = process.env.GITHUB_REPOSITORY;
if (!/^[A-Za-z0-9_.-]+\/[A-Za-z0-9_.-]+$/.test(repository ?? "")) {
  throw new Error("GITHUB_REPOSITORY is missing or invalid.");
}

const version = tag.slice(1);
const artifactRoot = resolve(artifactDirectory);
const pluginName = `RevitMCPPlugin-${version}-Revit2025.zip`;
const updaterName = `RevitMCPUpdater-${version}.zip`;
const checksumName = "SHA256SUMS.txt";
const expectedNames = new Set([pluginName, updaterName, checksumName]);

function sha256(value) {
  return createHash("sha256").update(value).digest("hex");
}

function run(command, args, { allowFailure = false, binary = false } = {}) {
  const result = spawnSync(command, args, {
    encoding: binary ? undefined : "utf8",
    env: process.env,
    maxBuffer: 100 * 1024 * 1024,
  });
  if (result.error) throw result.error;
  if (result.status !== 0 && !allowFailure) {
    throw new Error(
      `${command} ${args.join(" ")} failed (${result.status}): ${result.stderr}`,
    );
  }
  return result;
}

function ghJson(args, { allow404 = false } = {}) {
  const result = run("gh", args, { allowFailure: allow404 });
  if (result.status !== 0) {
    const stderr = result.stderr?.toString() ?? "";
    if (allow404 && /HTTP 404|release not found/i.test(stderr)) return null;
    throw new Error(`gh ${args.join(" ")} failed: ${stderr}`);
  }
  return JSON.parse(result.stdout);
}

function getRelease({ allow404 = false } = {}) {
  return ghJson(
    [
      "api",
      `repos/${repository}/releases/tags/${tag}`,
      "-H",
      "Accept: application/vnd.github+json",
    ],
    { allow404 },
  );
}

const checksumPath = join(artifactRoot, checksumName);
if (!existsSync(checksumPath)) {
  throw new Error(`Missing release checksum file: ${checksumPath}`);
}

const checksumLines = readFileSync(checksumPath, "ascii")
  .trim()
  .split(/\r?\n/);
const checksumMap = new Map();
for (const line of checksumLines) {
  const match = /^([a-f0-9]{64})  ([^\\/]+)$/i.exec(line);
  if (!match || checksumMap.has(match[2])) {
    throw new Error(`Malformed or duplicate checksum line: ${line}`);
  }
  checksumMap.set(match[2], match[1].toLowerCase());
}
if (
  checksumMap.size !== 2 ||
  !checksumMap.has(pluginName) ||
  !checksumMap.has(updaterName)
) {
  throw new Error(
    `SHA256SUMS.txt must contain exactly ${pluginName} and ${updaterName}.`,
  );
}

const localAssets = new Map();
for (const name of expectedNames) {
  const path = join(artifactRoot, name);
  if (!existsSync(path) || !statSync(path).isFile()) {
    throw new Error(`Missing release asset: ${path}`);
  }
  const bytes = readFileSync(path);
  const digest = sha256(bytes);
  if (name !== checksumName && checksumMap.get(name) !== digest) {
    throw new Error(`SHA256SUMS.txt mismatch for ${name}.`);
  }
  localAssets.set(name, {
    path,
    size: bytes.length,
    digest,
  });
}

const localZipNames = readdirSync(artifactRoot)
  .filter((name) => name.toLowerCase().endsWith(".zip"))
  .sort();
if (
  localZipNames.length !== 2 ||
  !localZipNames.every((name) => expectedNames.has(name))
) {
  throw new Error(
    `Unexpected release ZIP set: ${localZipNames.join(", ") || "(none)"}`,
  );
}

function remoteAssetDigest(asset) {
  if (typeof asset.digest === "string" && asset.digest.startsWith("sha256:")) {
    return asset.digest.slice("sha256:".length).toLowerCase();
  }
  const result = run(
    "gh",
    [
      "api",
      `repos/${repository}/releases/assets/${asset.id}`,
      "-H",
      "Accept: application/octet-stream",
    ],
    { binary: true },
  );
  return sha256(result.stdout);
}

function verifyRemoteAssets(release, { allowMissing }) {
  const remoteByName = new Map(
    release.assets.map((asset) => [asset.name, asset]),
  );
  for (const asset of release.assets) {
    if (!expectedNames.has(asset.name)) {
      throw new Error(
        `Release ${tag} contains an unexpected asset: ${asset.name}`,
      );
    }
  }

  const missing = [];
  for (const [name, local] of localAssets) {
    const remote = remoteByName.get(name);
    if (!remote) {
      missing.push(name);
      continue;
    }
    if (remote.state !== "uploaded" || Number(remote.size) !== local.size) {
      throw new Error(`Release asset size/state mismatch for ${name}.`);
    }
    if (remoteAssetDigest(remote) !== local.digest) {
      throw new Error(`Release asset digest mismatch for ${name}.`);
    }
  }
  if (!allowMissing && missing.length > 0) {
    throw new Error(`Release ${tag} is missing assets: ${missing.join(", ")}`);
  }
  return missing;
}

let release = getRelease({ allow404: mode === "prepare" });
if (!release && mode === "prepare") {
  run("gh", [
    "release",
    "create",
    tag,
    "--repo",
    repository,
    "--draft",
    "--verify-tag",
    "--target",
    expectedCommit,
    "--generate-notes",
    "--title",
    tag,
  ]);
  release = getRelease();
}
if (!release) throw new Error(`Release ${tag} does not exist.`);
if (release.tag_name !== tag || release.prerelease) {
  throw new Error(`Release metadata for ${tag} is inconsistent.`);
}

if (mode === "prepare") {
  const missing = verifyRemoteAssets(release, {
    allowMissing: release.draft,
  });
  if (!release.draft && missing.length > 0) {
    throw new Error(`Published release ${tag} cannot be repaired in place.`);
  }
  for (const name of missing) {
    run("gh", [
      "release",
      "upload",
      tag,
      localAssets.get(name).path,
      "--repo",
      repository,
    ]);
  }
  release = getRelease();
  verifyRemoteAssets(release, { allowMissing: false });
  console.log(
    release.draft
      ? `Draft release ${tag} is ready with verified assets.`
      : `Published release ${tag} already has identical verified assets.`,
  );
  process.exit(0);
}

verifyRemoteAssets(release, { allowMissing: false });

const bundledNpmCli = join(
  dirname(process.execPath),
  "node_modules",
  "npm",
  "bin",
  "npm-cli.js",
);
const npmCli =
  process.env.npm_execpath ??
  (existsSync(bundledNpmCli) ? bundledNpmCli : undefined);
const npmCommand = npmCli ? process.execPath : "npm";
const npmPrefix = npmCli ? [npmCli] : [];

function npmManifest(name) {
  const result = run(npmCommand, [
    ...npmPrefix,
    "view",
    `${name}@${version}`,
    "--json",
  ]);
  return JSON.parse(result.stdout);
}

const core = npmManifest("@kimminsub/mcp-cad-core");
const revit = npmManifest("@kimminsub/revit-mcp");
if (
  core.name !== "@kimminsub/mcp-cad-core" ||
  core.version !== version ||
  !core.dist?.integrity ||
  revit.name !== "@kimminsub/revit-mcp" ||
  revit.version !== version ||
  !revit.dist?.integrity ||
  revit.dependencies?.["@kimminsub/mcp-cad-core"] !== version
) {
  throw new Error(
    `npm publication state does not exactly match release ${tag}.`,
  );
}

if (release.draft) {
  ghJson([
    "api",
    "--method",
    "PATCH",
    `repos/${repository}/releases/${release.id}`,
    "-F",
    "draft=false",
  ]);
}

release = getRelease();
if (release.draft) {
  throw new Error(`Release ${tag} is still a draft after finalization.`);
}
verifyRemoteAssets(release, { allowMissing: false });
console.log(
  `Release ${tag} is published and matches both npm packages and all assets.`,
);
