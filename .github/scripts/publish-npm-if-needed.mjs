import { execFileSync, spawnSync } from "node:child_process";
import { createHash } from "node:crypto";
import { existsSync, readFileSync } from "node:fs";
import { basename, dirname, join, resolve } from "node:path";

const [tarballArgument, expectedName, expectedVersion] = process.argv.slice(2);

if (!tarballArgument || !expectedName || !expectedVersion) {
  console.error(
    "Usage: node publish-npm-if-needed.mjs <package.tgz> <package-name> <version>",
  );
  process.exit(2);
}

if (!/^\d+\.\d+\.\d+$/.test(expectedVersion)) {
  throw new Error(`Refusing non-release semver: ${expectedVersion}`);
}

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

const tarball = resolve(tarballArgument);
const bytes = readFileSync(tarball);
const localIntegrity = `sha512-${createHash("sha512").update(bytes).digest("base64")}`;
const spec = `${expectedName}@${expectedVersion}`;

const manifestResult = spawnSync(
  "tar",
  ["-xOf", tarball, "package/package.json"],
  { encoding: "utf8" },
);
if (manifestResult.status !== 0) {
  throw new Error(
    `Unable to inspect ${basename(tarball)}: ${manifestResult.stderr}`,
  );
}
const manifest = JSON.parse(manifestResult.stdout);
if (manifest.name !== expectedName || manifest.version !== expectedVersion) {
  throw new Error(
    `Tarball identity mismatch: expected ${spec}, found ${manifest.name}@${manifest.version}.`,
  );
}

let remoteIntegrity;
try {
  remoteIntegrity = execFileSync(
    npmCommand,
    [...npmPrefix, "view", spec, "dist.integrity", "--json"],
    { encoding: "utf8", stdio: ["ignore", "pipe", "pipe"] },
  )
    .trim()
    .replace(/^"|"$/g, "");
} catch (error) {
  const stderr = error.stderr?.toString() ?? "";
  if (!stderr.includes("E404")) {
    throw error;
  }
}

if (remoteIntegrity) {
  if (remoteIntegrity !== localIntegrity) {
    throw new Error(
      `${spec} already exists with different integrity; refusing to pair it with ${basename(tarball)}.`,
    );
  }
  console.log(`${spec} already exists with matching integrity; publish is idempotently skipped.`);
  process.exit(0);
}

const publish = spawnSync(
  npmCommand,
  [
    ...npmPrefix,
    "publish",
    tarball,
    "--access",
    "public",
    "--provenance",
  ],
  { encoding: "utf8", stdio: "inherit" },
);

if (publish.status !== 0) {
  process.exit(publish.status ?? 1);
}
