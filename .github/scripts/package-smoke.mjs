import { execFileSync } from "node:child_process";
import {
  existsSync,
  mkdtempSync,
  mkdirSync,
  readFileSync,
  rmSync,
  writeFileSync,
} from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const scriptDir = dirname(fileURLToPath(import.meta.url));
const repoRoot = resolve(scriptDir, "..", "..");
const nodeCommand = process.execPath;
const npmCli = process.env.npm_execpath;

if (!npmCli || !existsSync(npmCli)) {
  throw new Error(
    "npm CLI path is unavailable. Run this check through `npm run test:packages`.",
  );
}

const packages = [
  {
    workspace: "@kimminsub/mcp-cad-core",
    name: "@kimminsub/mcp-cad-core",
    distEntry: "packages/mcp-cad-core/dist/index.js",
  },
  {
    workspace: "server",
    name: "@kimminsub/revit-mcp",
    distEntry: "server/dist/index.js",
    bin: "revit-mcp",
  },
  {
    workspace: "@kimminsub/autocad-mcp",
    name: "@kimminsub/autocad-mcp",
    distEntry: "autocad/server/dist/index.js",
    bin: "autocad-mcp",
  },
];

function run(command, args, options = {}) {
  return execFileSync(command, args, {
    cwd: repoRoot,
    encoding: "utf8",
    stdio: ["ignore", "pipe", "inherit"],
    ...options,
  }).trim();
}

function runNpm(args, options = {}) {
  return run(nodeCommand, [npmCli, ...args], options);
}

function assert(condition, message) {
  if (!condition) {
    throw new Error(message);
  }
}

for (const pkg of packages) {
  const entry = join(repoRoot, pkg.distEntry);
  assert(
    existsSync(entry),
    `Missing ${pkg.distEntry}. Run the workspace build before package smoke tests.`,
  );
  run(nodeCommand, ["--check", entry]);
}

const tempRoot = mkdtempSync(join(tmpdir(), "revit-mcp-package-smoke-"));
const packDir = join(tempRoot, "packs");
const installDir = join(tempRoot, "consumer");

try {
  const tarballs = new Map();
  mkdirSync(packDir, { recursive: true });
  mkdirSync(installDir, { recursive: true });

  for (const pkg of packages) {
    const raw = runNpm([
      "pack",
      "--json",
      "--ignore-scripts",
      `--workspace=${pkg.workspace}`,
      `--pack-destination=${packDir}`,
    ]);
    const result = JSON.parse(raw)[0];
    const included = new Set(result.files.map((file) => file.path));

    for (const required of [
      "package.json",
      "dist/index.js",
      "README.md",
      "LICENSE",
    ]) {
      assert(
        included.has(required),
        `${pkg.name} tarball is missing required file: ${required}`,
      );
    }

    const tarball = join(packDir, result.filename);
    assert(existsSync(tarball), `npm pack did not create ${tarball}`);
    tarballs.set(pkg.name, tarball);
  }

  writeFileSync(
    join(installDir, "package.json"),
    `${JSON.stringify({ name: "package-smoke-consumer", private: true }, null, 2)}\n`,
    { encoding: "utf8", flag: "wx" },
  );

  runNpm(
    [
      "install",
      "--ignore-scripts",
      "--no-audit",
      "--no-fund",
      ...packages.map((pkg) => tarballs.get(pkg.name)),
    ],
    { cwd: installDir },
  );

  const corePackage = JSON.parse(
    readFileSync(
      join(installDir, "node_modules", "@kimminsub", "mcp-cad-core", "package.json"),
      "utf8",
    ),
  );
  const revitPackage = JSON.parse(
    readFileSync(
      join(installDir, "node_modules", "@kimminsub", "revit-mcp", "package.json"),
      "utf8",
    ),
  );
  const autocadPackage = JSON.parse(
    readFileSync(
      join(installDir, "node_modules", "@kimminsub", "autocad-mcp", "package.json"),
      "utf8",
    ),
  );

  for (const [name, packageJson] of [
    ["Revit", revitPackage],
    ["AutoCAD", autocadPackage],
  ]) {
    assert(
      packageJson.dependencies["@kimminsub/mcp-cad-core"] ===
        corePackage.version,
      `${name} package must depend on the exact packaged mcp-cad-core version.`,
    );
  }

  run(
    nodeCommand,
    [
      "--input-type=module",
      "-e",
      "const core = await import('@kimminsub/mcp-cad-core'); if (typeof core.CadWebSocketClient !== 'function') process.exit(1);",
    ],
    { cwd: installDir },
  );

  for (const pkg of packages.filter((item) => item.bin)) {
    const suffix = process.platform === "win32" ? ".cmd" : "";
    assert(
      existsSync(join(installDir, "node_modules", ".bin", `${pkg.bin}${suffix}`)),
      `Installed package is missing executable shim: ${pkg.bin}`,
    );
  }

  console.log("Package smoke test passed for core, Revit MCP, and AutoCAD MCP.");
} finally {
  rmSync(tempRoot, { recursive: true, force: true });
}
