// A loose script outside any project. Indexed because the scan covers the
// whole repository, not a fixed list of root directories.

/** Deploys one of the workspace projects. */
function deployProject(name) {
  if (name !== 'web' && name !== 'api') {
    throw new Error(`unknown project: ${name}`);
  }

  return `deployed ${name}`;
}

module.exports = { deployProject };
