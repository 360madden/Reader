using System.Diagnostics;
using Reader.Core.Native;

namespace Reader.Core;

/// <summary>
/// Finds the RIFT game process and opens a read-only handle to it.
/// </summary>
public sealed class ProcessAttacher : IDisposable
{
    private static readonly string[] ProcessNames = ["rift", "rift_x64"];

    public nint Handle      { get; private set; }
    public int  ProcessId   { get; private set; }
    public bool IsAttached  => Handle != nint.Zero;

    private bool _disposed;

    private ProcessAttacher() { }

    /// <summary>
    /// Finds rift.exe and opens a read-only process handle.
    /// Returns null if RIFT is not running.
    /// </summary>
    public static ProcessAttacher? Attach()
    {
        Process? proc = null;
        foreach (string name in ProcessNames)
        {
            Process[] candidates = Process.GetProcessesByName(name);
            if (candidates.Length > 0)
            {
                proc = candidates[0];
                // Dispose extras we won't use
                for (int i = 1; i < candidates.Length; i++)
                    candidates[i].Dispose();
                break;
            }
        }

        if (proc is null) return null;

        using (proc)
        {
            nint handle = Kernel32.OpenProcess(
                Kernel32.ProcessVmRead | Kernel32.ProcessQueryInformation,
                false,
                proc.Id);

            if (handle == nint.Zero) return null;

            return new ProcessAttacher
            {
                Handle    = handle,
                ProcessId = proc.Id,
            };
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (Handle != nint.Zero)
        {
            Kernel32.CloseHandle(Handle);
            Handle = nint.Zero;
        }
    }
}
