using System.Runtime.InteropServices;
using AudioOrchestrator.Contracts;

namespace AudioOrchestrator.Services;

public interface IFifoService
{
    Task EnsureFifoAsync(string fifoPath);
    Task CleanupStaleFifosAsync(IEnumerable<string> validFifoPaths);
}

public class FifoService : IFifoService
{
    private readonly ILogger<FifoService> _logger;
    private const uint FifoMode = 0x1B6; // 0666 permissions (rw-rw-rw-)
    private const int DefaultOwnerUid = 1000;
    private const int DefaultOwnerGid = 1000;

    [DllImport("libc", SetLastError = true)]
    private static extern int mkfifo(string path, uint mode);

    [DllImport("libc", SetLastError = true)]
    private static extern int chown(string path, int owner, int group);

    [DllImport("libc", SetLastError = true)]
    private static extern int chmod(string path, uint mode);

    public FifoService(ILogger<FifoService> logger)
    {
        _logger = logger;
    }

    public Task EnsureFifoAsync(string fifoPath)
    {
        if (System.IO.Directory.Exists(fifoPath))
        {
            _logger.LogWarning("Path {FifoPath} is a directory. Removing to recreate as FIFO.", fifoPath);
            System.IO.Directory.Delete(fifoPath, true);
        }

        var directory = System.IO.Path.GetDirectoryName(fifoPath);
        if (!string.IsNullOrEmpty(directory) && !System.IO.Directory.Exists(directory))
        {
            _logger.LogInformation("Creating directory {Directory}", directory);
            System.IO.Directory.CreateDirectory(directory);
        }

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            _logger.LogWarning("FIFO creation is only supported on Linux. Skipping.");
            return Task.CompletedTask;
        }

        if (!string.IsNullOrEmpty(directory))
        {
            if (chown(directory, DefaultOwnerUid, DefaultOwnerGid) == -1)
            {
                _logger.LogWarning("Failed to chown directory {Directory}. Error: {Error}", directory, Marshal.GetLastPInvokeError());
            }
        }

        if (!System.IO.File.Exists(fifoPath))
        {
            if (mkfifo(fifoPath, FifoMode) == -1)
            {
                var error = Marshal.GetLastPInvokeError();
                if (error != 17) // 17 is EEXIST, which can happen if another thread created it between our Exists check and mkfifo call
                {
                    _logger.LogError("Failed to create FIFO at {FifoPath}. Error: {Error}", fifoPath, error);
                }
            }
        }

        if (chown(fifoPath, DefaultOwnerUid, DefaultOwnerGid) == -1)
        {
            _logger.LogWarning("Failed to chown FIFO {FifoPath}. Error: {Error}", fifoPath, Marshal.GetLastPInvokeError());
        }

        if (chmod(fifoPath, FifoMode) == -1)
        {
            _logger.LogWarning("Failed to chmod FIFO {FifoPath}. Error: {Error}", fifoPath, Marshal.GetLastPInvokeError());
        }

        return Task.CompletedTask;
    }

    public Task CleanupStaleFifosAsync(IEnumerable<string> validFifoPaths)
    {
        var fifoDir = OrchestratorConstants.FifoBaseDir;
        if (!System.IO.Directory.Exists(fifoDir)) return Task.CompletedTask;

        var validPathsSet = new HashSet<string>(validFifoPaths);
        var existingEntries = System.IO.Directory.GetFileSystemEntries(fifoDir);

        foreach (var entry in existingEntries)
        {
            if (!validPathsSet.Contains(entry))
            {
                _logger.LogInformation("Removing stale FIFO/directory: {Entry}", entry);
                try
                {
                    if (System.IO.Directory.Exists(entry)) System.IO.Directory.Delete(entry, true);
                    else System.IO.File.Delete(entry);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("Could not remove stale FIFO {Entry}: {Message}", entry, ex.Message);
                }
            }
        }

        return Task.CompletedTask;
    }
}
