import { spawnSync } from "node:child_process";

const [tag, expectedCommit] = process.argv.slice(2);
if (
  !/^v\d+\.\d+\.\d+$/.test(tag ?? "") ||
  !/^[a-f0-9]{40}$/i.test(expectedCommit ?? "")
) {
  throw new Error(
    "Usage: verify-release-tag.mjs <vMAJOR.MINOR.PATCH> <40-character commit>",
  );
}

const repository = process.env.GITHUB_REPOSITORY;
if (!/^[A-Za-z0-9_.-]+\/[A-Za-z0-9_.-]+$/.test(repository ?? "")) {
  throw new Error("GITHUB_REPOSITORY is missing or invalid.");
}
if (!process.env.GH_TOKEN) {
  throw new Error("GH_TOKEN is required to verify the remote release tag.");
}

function ghJson(path) {
  const result = spawnSync(
    "gh",
    [
      "api",
      path,
      "-H",
      "Accept: application/vnd.github+json",
      "-H",
      "X-GitHub-Api-Version: 2022-11-28",
    ],
    {
      encoding: "utf8",
      env: process.env,
      maxBuffer: 10 * 1024 * 1024,
    },
  );
  if (result.error) throw result.error;
  if (result.status !== 0) {
    throw new Error(
      `GitHub API request failed (${result.status}): ${result.stderr}`,
    );
  }
  return JSON.parse(result.stdout);
}

const encodedTag = encodeURIComponent(tag);
const reference = ghJson(
  `repos/${repository}/git/ref/tags/${encodedTag}`,
);
let object = reference?.object;

for (let depth = 0; depth < 10; depth += 1) {
  if (
    object?.type === "commit" &&
    typeof object.sha === "string" &&
    /^[a-f0-9]{40}$/i.test(object.sha)
  ) {
    if (object.sha.toLowerCase() !== expectedCommit.toLowerCase()) {
      throw new Error(
        `Release tag ${tag} now targets ${object.sha}, not validated commit ${expectedCommit}.`,
      );
    }
    console.log(
      `Release tag ${tag} still targets validated commit ${expectedCommit}.`,
    );
    process.exit(0);
  }

  if (
    object?.type !== "tag" ||
    typeof object.sha !== "string" ||
    !/^[a-f0-9]{40}$/i.test(object.sha)
  ) {
    throw new Error(`Release tag ${tag} did not resolve to a commit.`);
  }

  const annotatedTag = ghJson(
    `repos/${repository}/git/tags/${object.sha}`,
  );
  object = annotatedTag?.object;
}

throw new Error(`Release tag ${tag} has too many nested tag objects.`);
