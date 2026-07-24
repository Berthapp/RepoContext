using System.Security.Cryptography;
using System.Text;

namespace RepoContext.Core;

/// <summary>
/// A process-wide, path-scoped lease for short local file transactions.
/// Named mutexes let independent CLI and MCP processes coordinate without a
/// lock file that can be left behind after a crash.
/// </summary>
internal sealed class PathScopedMutex : IDisposable
{
    private readonly Mutex _mutex;
    private bool _ownsMutex;

    private PathScopedMutex(Mutex mutex)
    {
        _mutex = mutex;
        _ownsMutex = true;
    }

    /// <summary>
    /// Attempts to acquire the mutex for <paramref name="path"/>. Returns
    /// <see langword="null"/> on timeout. An abandoned mutex is owned by this
    /// caller, so the protected store can inspect or repair its local file.
    /// </summary>
    public static PathScopedMutex? TryAcquire(
        string scope,
        string path,
        int timeoutMilliseconds)
    {
        var mutex = new Mutex(initiallyOwned: false, MutexName(scope, path));
        try
        {
            bool acquired;
            try
            {
                acquired = mutex.WaitOne(timeoutMilliseconds);
            }
            catch (AbandonedMutexException)
            {
                acquired = true;
            }

            if (!acquired)
            {
                mutex.Dispose();
                return null;
            }

            return new PathScopedMutex(mutex);
        }
        catch
        {
            mutex.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (!_ownsMutex)
        {
            return;
        }

        _ownsMutex = false;
        try
        {
            _mutex.ReleaseMutex();
        }
        finally
        {
            _mutex.Dispose();
        }
    }

    private static string MutexName(string scope, string path)
    {
        string fullPath = Path.GetFullPath(path);
        if (OperatingSystem.IsWindows())
        {
            fullPath = fullPath.ToUpperInvariant();
        }

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(fullPath));
        return "RepoContext." + scope + "." + Convert.ToHexString(hash);
    }
}
