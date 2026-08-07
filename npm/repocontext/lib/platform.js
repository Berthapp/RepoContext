"use strict";

// Resolution of the platform-specific RepoContext binary.
//
// The published `repocontext` package carries no binary itself: each supported
// platform ships as its own optional dependency (`repocontext-linux-x64`, ...)
// so `npm install` downloads exactly one payload instead of all six. This
// module maps the running platform onto that package and locates the
// executable inside it. It is pure resolution logic with no side effects, so
// it can be unit tested without any package being installed.

const path = require("node:path");

/**
 * Supported targets, keyed by `${process.platform}-${process.arch}`.
 * `rid` is the .NET runtime identifier the release workflow publishes; it is
 * carried here so the package layout and the build matrix cannot drift apart
 * silently.
 */
const TARGETS = {
  "linux-x64": { package: "repocontext-linux-x64", rid: "linux-x64", binary: "repoctx" },
  "linux-arm64": { package: "repocontext-linux-arm64", rid: "linux-arm64", binary: "repoctx" },
  "darwin-x64": { package: "repocontext-darwin-x64", rid: "osx-x64", binary: "repoctx" },
  "darwin-arm64": { package: "repocontext-darwin-arm64", rid: "osx-arm64", binary: "repoctx" },
  "win32-x64": { package: "repocontext-win32-x64", rid: "win-x64", binary: "repoctx.exe" },
  "win32-arm64": { package: "repocontext-win32-arm64", rid: "win-arm64", binary: "repoctx.exe" },
};

/** The directory inside a platform package that holds the published payload. */
const PAYLOAD_DIRECTORY = "bin";

/** Every supported target, ordered by key — used by the package builder. */
function targets() {
  return Object.keys(TARGETS)
    .sort()
    .map((key) => ({ key, ...TARGETS[key] }));
}

/**
 * Builds a resolver that tries several starting directories in order.
 *
 * One starting point is not enough. Node resolves this file through its real
 * path, so a symlinked wrapper — `npm link`, a `file:` dependency, some
 * monorepo layouts — looks for the platform package next to the *source*
 * directory rather than next to the installed one, where npm actually put it.
 * Falling back to the working directory recovers exactly those cases and can
 * never make a normal install worse: the first candidate already answers there.
 *
 * @param {string[]} paths starting directories, most specific first
 * @param {(request: string, options: {paths: string[]}) => string} requireResolve
 */
function createResolver(paths, requireResolve) {
  return (request) => {
    let lastError;
    for (const from of paths) {
      try {
        return requireResolve(request, { paths: [from] });
      } catch (error) {
        lastError = error;
      }
    }

    throw lastError ?? new Error(`Cannot find module '${request}'`);
  };
}

/**
 * The target for a platform/arch pair, or null when unsupported.
 * @param {string} platform `process.platform`
 * @param {string} arch `process.arch`
 */
function targetFor(platform, arch) {
  const target = TARGETS[`${platform}-${arch}`];
  return target ? { key: `${platform}-${arch}`, ...target } : null;
}

/**
 * Whether this Linux process runs against musl libc (Alpine and friends).
 *
 * The published binaries are self-contained .NET builds against glibc; on musl
 * they fail with a bare loader error that says nothing useful. Node reports the
 * runtime glibc version only when it linked against glibc, so an absent value
 * on Linux means musl. Anything unexpected is treated as glibc: a false
 * negative merely restores the old confusing failure, while a false positive
 * would refuse to run on a perfectly good machine.
 */
function isMuslLinux(processLike) {
  if (processLike.platform !== "linux") {
    return false;
  }

  try {
    const header = processLike.report.getReport().header;
    return !header.glibcVersionRuntime;
  } catch {
    return false;
  }
}

/**
 * Locates the `repoctx` executable for the running platform.
 *
 * @param {object} options
 * @param {NodeJS.Process} options.process the process to inspect
 * @param {(request: string) => string} options.resolve module resolver,
 *   expected to behave like `require.resolve` for `<package>/package.json`
 * @returns {{ok: true, binary: string, target: object}
 *          |{ok: false, reason: string, message: string}}
 */
function resolveBinary({ process: processLike, resolve }) {
  const target = targetFor(processLike.platform, processLike.arch);
  if (!target) {
    return {
      ok: false,
      reason: "unsupported-platform",
      message:
        `repoctx has no prebuilt binary for ${processLike.platform}-${processLike.arch}. ` +
        `Supported: ${targets()
          .map((t) => t.key)
          .join(", ")}. ` +
        "Install the .NET tool instead: dotnet tool install --global RepoContext.Tool",
    };
  }

  if (isMuslLinux(processLike)) {
    return {
      ok: false,
      reason: "musl",
      message:
        "repoctx ships self-contained binaries built against glibc, and this looks like a " +
        "musl-based Linux (Alpine). Install the .NET tool on a runtime that matches your libc " +
        "instead: dotnet tool install --global RepoContext.Tool",
    };
  }

  let manifest;
  try {
    manifest = resolve(`${target.package}/package.json`);
  } catch {
    return {
      ok: false,
      reason: "missing-package",
      message:
        `The platform package ${target.package} is not installed. This usually means npm skipped ` +
        "optional dependencies (--no-optional / --omit=optional) or the install was interrupted. " +
        `Reinstall with optional dependencies enabled, or run: npm install ${target.package}`,
    };
  }

  return {
    ok: true,
    target,
    binary: path.join(path.dirname(manifest), PAYLOAD_DIRECTORY, target.binary),
  };
}

module.exports = { PAYLOAD_DIRECTORY, createResolver, resolveBinary, targetFor, targets };
