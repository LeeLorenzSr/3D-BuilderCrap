using System.IO;

namespace Builder.Hosting;

/// <summary>Installed hosts coordinate with Setup. Development builds have no package marker.</summary>
internal sealed class InstallationLease : IDisposable
{
    internal const string ProductId = "7C2AC698-9A70-49C8-BF7D-A58F2CF1EB90";
    private readonly Mutex running;
    private InstallationLease(Mutex running) => this.running = running;
    public static InstallationLease? Acquire(string directory)
    {
        if (!File.Exists(Path.Combine(directory, ".builder-package.ini"))) return null;
        var marker = Path.Combine(directory, ".builder-install.ini");
        if (!File.Exists(marker)) throw new IOException("Installation is incomplete. Close this copy and run the installer again. Saved models and recovery copies are kept separately.");
        var values = File.ReadLines(marker).Where(line => line.Contains('=')).Select(line => line.Split('=', 2))
            .ToDictionary(pair => pair[0].Trim(), pair => pair[1].Trim(), StringComparer.OrdinalIgnoreCase);
        if (!values.TryGetValue("ProductId", out var product) || product != ProductId ||
            !values.TryGetValue("Id", out var id) || id.Length != 64 || !id.All(char.IsAsciiHexDigit))
            throw new IOException("The installation marker is invalid. Close this copy and run the installer again.");
        var prefix = @"Global\3DBuilderCrap-" + ProductId + "-" + id.ToUpperInvariant();
        using var gate = new Mutex(false, prefix + "-Maintenance");
        bool acquired;
        try { acquired = gate.WaitOne(0); } catch (AbandonedMutexException) { acquired = true; }
        if (!acquired) throw new IOException("3D BuilderCrap is being installed or removed. Wait for the installer to finish before opening it.");
        try { return new(new Mutex(false, prefix + "-Running")); }
        finally { gate.ReleaseMutex(); }
    }
    public void Dispose() => running.Dispose();
}
