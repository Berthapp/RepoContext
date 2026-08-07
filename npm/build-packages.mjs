// Assembles the publishable npm packages for RepoContext.
//
// The npm distribution mirrors the pattern esbuild and swc use: one small
// wrapper package (`repocontext-tool`) that carries the launcher and declares
// every platform build as an *optional* dependency, plus one package per
// platform carrying only that platform's self-contained binary. npm installs
// exactly the one that matches the machine, so a user downloads a single
// payload instead of all six.
//
// Package names are constrained by more than availability: npm rejects a new
// name that differs from an existing one only by punctuation or case. `repo-
// context` and `repo-context-cli` already exist, which rules out `repocontext`
// and `repocontext-cli` even though neither is registered. Check a candidate's
// punctuation-stripped form against the registry before adding a package here.
//
// Usage:
//   node npm/build-packages.mjs --artifacts <dir> [--out npm/dist] [--version X.Y.Z]
//   node npm/build-packages.mjs --dry-run          # manifests only, no payload
//
// `--artifacts` points at a directory holding one subdirectory per .NET runtime
// identifier (`linux-x64/`, `win-x64/`, ...), each containing the unpacked
// `dotnet publish --self-contained` output. `--dry-run` writes the manifests
// without any payload so CI can validate this generator without a .NET build.

import { createRequire } from "node:module";
import fs from "node:fs";
import path from "node:path";
import process from "node:process";
import { fileURLToPath } from "node:url";

const require = createRequire(import.meta.url);
const { PAYLOAD_DIRECTORY, targets } = require("./repocontext/lib/platform.js");

const HERE = path.dirname(fileURLToPath(import.meta.url));
const REPO_ROOT = path.resolve(HERE, "..");

// The wrapper's source directory keeps its original name; only the published
// package name changed. Reading the name from the manifest keeps that the one
// place it is written down.
const WRAPPER_SOURCE = path.join(HERE, "repocontext");
const WRAPPER_PACKAGE_NAME = JSON.parse(
  fs.readFileSync(path.join(WRAPPER_SOURCE, "package.json"), "utf8"),
).name;

/** Files copied verbatim from the checked-in wrapper into the built wrapper. */
const WRAPPER_FILES = [
  ["package.json"],
  ["README.md"],
  ["bin", "repoctx.js"],
  ["lib", "platform.js"],
];

function main(argv) {
  const options = parseArguments(argv);
  const version = options.version ?? readVersionPrefix();
  assertPublishableVersion(version);

  const outDir = path.resolve(REPO_ROOT, options.out ?? "npm/dist");
  fs.rmSync(outDir, { recursive: true, force: true });
  fs.mkdirSync(outDir, { recursive: true });

  const built = [];
  for (const target of targets()) {
    built.push(buildPlatformPackage(target, { version, outDir, options }));
  }

  built.push(buildWrapperPackage({ version, outDir }));

  process.stdout.write(
    `Built ${built.length} package(s) for version ${version} in ${path.relative(REPO_ROOT, outDir)}\n`,
  );
  for (const entry of built) {
    process.stdout.write(`  ${entry.name}  ${entry.detail}\n`);
  }

  return 0;
}

/**
 * One platform package: the self-contained payload under `bin/`, plus an
 * `os`/`cpu`-constrained manifest so npm skips it on every other machine.
 */
function buildPlatformPackage(target, { version, outDir, options }) {
  const packageDir = path.join(outDir, target.package);
  const payloadDir = path.join(packageDir, PAYLOAD_DIRECTORY);
  fs.mkdirSync(payloadDir, { recursive: true });

  let detail = "manifest only (dry run)";
  if (!options.dryRun) {
    const source = path.join(path.resolve(REPO_ROOT, options.artifacts), target.rid);
    if (!fs.existsSync(source)) {
      throw new Error(
        `Missing publish output for ${target.rid}: expected ${source}. ` +
          "Every runtime identifier in the release matrix must be present, otherwise the " +
          "published wrapper would advertise a platform package that does not exist.",
      );
    }

    fs.cpSync(source, payloadDir, { recursive: true });

    const binary = path.join(payloadDir, target.binary);
    if (!fs.existsSync(binary)) {
      throw new Error(`Publish output for ${target.rid} contains no ${target.binary}.`);
    }

    if (!target.binary.endsWith(".exe")) {
      // Restore the execute bit: it survives npm's tarball, but the artifact
      // round trip through GitHub's upload/download loses it.
      fs.chmodSync(binary, 0o755);
    }

    detail = `${target.rid}, ${formatSize(directorySize(payloadDir))}`;
  }

  const [os, cpu] = target.key.split("-");
  writeJson(path.join(packageDir, "package.json"), {
    name: target.package,
    version,
    description: `RepoContext (repoctx) binary for ${os}-${cpu}. Installed automatically by the '${WRAPPER_PACKAGE_NAME}' package.`,
    homepage: "https://berthapp.github.io/RepoContext/",
    repository: {
      type: "git",
      url: "git+https://github.com/Berthapp/RepoContext.git",
      directory: "npm/repocontext",
    },
    license: "Apache-2.0",
    author: "RepoContext",
    os: [os],
    cpu: [cpu],
    // No `bin`: the wrapper spawns the payload directly, so these packages need
    // no lifecycle scripts and no shim generation of their own.
    files: [PAYLOAD_DIRECTORY],
    preferUnplugged: true,
    publishConfig: { access: "public" },
  });

  copyIfPresent(path.join(REPO_ROOT, "LICENSE"), path.join(packageDir, "LICENSE"));
  copyIfPresent(path.join(REPO_ROOT, "NOTICE"), path.join(packageDir, "NOTICE"));

  return { name: target.package, detail };
}

/**
 * The wrapper package, stamped with the release version. Its optional
 * dependencies are pinned exactly: a wrapper must never resolve a platform
 * payload from a different release than its own launcher.
 */
function buildWrapperPackage({ version, outDir }) {
  const packageDir = path.join(outDir, "repocontext");
  for (const parts of WRAPPER_FILES) {
    const destination = path.join(packageDir, ...parts);
    fs.mkdirSync(path.dirname(destination), { recursive: true });
    fs.copyFileSync(path.join(WRAPPER_SOURCE, ...parts), destination);
  }

  const manifestPath = path.join(packageDir, "package.json");
  const manifest = JSON.parse(fs.readFileSync(manifestPath, "utf8"));
  manifest.version = version;
  manifest.optionalDependencies = Object.fromEntries(
    targets().map((target) => [target.package, version]),
  );
  writeJson(manifestPath, manifest);

  copyIfPresent(path.join(REPO_ROOT, "LICENSE"), path.join(packageDir, "LICENSE"));
  copyIfPresent(path.join(REPO_ROOT, "NOTICE"), path.join(packageDir, "NOTICE"));

  const declared = Object.keys(manifest.optionalDependencies);
  return { name: manifest.name, detail: `wrapper, ${declared.length} optional deps` };
}

/**
 * The single source of truth for the product version is Directory.Build.props;
 * reading it here keeps the npm release from drifting away from the NuGet one.
 */
function readVersionPrefix() {
  const props = fs.readFileSync(path.join(REPO_ROOT, "Directory.Build.props"), "utf8");
  const match = /<VersionPrefix>([^<]+)<\/VersionPrefix>/.exec(props);
  if (!match) {
    throw new Error("Could not read <VersionPrefix> from Directory.Build.props.");
  }

  return match[1].trim();
}

/** npm accepts far more than this; the release only ever publishes semver. */
function assertPublishableVersion(version) {
  if (!/^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$/.test(version)) {
    throw new Error(`Refusing to build packages for a non-semver version '${version}'.`);
  }
}

function parseArguments(argv) {
  const options = { dryRun: false };
  for (let i = 0; i < argv.length; i++) {
    switch (argv[i]) {
      case "--dry-run":
        options.dryRun = true;
        break;
      case "--artifacts":
        options.artifacts = argv[++i];
        break;
      case "--out":
        options.out = argv[++i];
        break;
      case "--version":
        options.version = argv[++i];
        break;
      default:
        throw new Error(`Unknown argument '${argv[i]}'.`);
    }
  }

  if (!options.dryRun && !options.artifacts) {
    throw new Error("Pass --artifacts <dir> with the published runtime outputs, or --dry-run.");
  }

  return options;
}

function writeJson(file, value) {
  fs.writeFileSync(file, JSON.stringify(value, null, 2) + "\n");
}

function copyIfPresent(source, destination) {
  if (fs.existsSync(source)) {
    fs.copyFileSync(source, destination);
  }
}

function directorySize(dir) {
  let total = 0;
  for (const entry of fs.readdirSync(dir, { withFileTypes: true, recursive: true })) {
    if (entry.isFile()) {
      total += fs.statSync(path.join(entry.parentPath ?? entry.path, entry.name)).size;
    }
  }

  return total;
}

function formatSize(bytes) {
  return `${(bytes / 1024 / 1024).toFixed(1)} MB`;
}

try {
  process.exitCode = main(process.argv.slice(2));
} catch (error) {
  process.stderr.write(`npm/build-packages.mjs: ${error.message}\n`);
  process.exitCode = 1;
}
