using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FlowLens;

internal sealed record PreparedUpdate(
    string ScriptPath,
    string ManifestPath,
    string LogPath,
    string BackupPath,
    string CandidatePath,
    string ReadyPath,
    string CancelPath);

internal static class UpdateInstaller
{
    internal static readonly string PowerShellPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System),
        "WindowsPowerShell", "v1.0", "powershell.exe");

    public static PreparedUpdate Prepare(
        string stagedExePath,
        string currentExePath,
        int currentProcessId,
        string updateDirectory,
        string? expectedExecutableSha256 = null)
    {
        var source = Path.GetFullPath(stagedExePath);
        var target = Path.GetFullPath(currentExePath);
        if (!string.Equals(Path.GetFileName(target), "FlowLens.exe", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The update must replace the current FlowLens.exe with a separately staged executable.");
        }

        EnsureOrdinaryFile(source);
        EnsureOrdinaryFile(target);
        if ((File.GetAttributes(target) & FileAttributes.ReadOnly) != 0)
        {
            throw new UnauthorizedAccessException("FlowLens.exe is read-only. Move FlowLens to a writable installation folder before updating.");
        }

        using var currentProcess = Process.GetProcessById(currentProcessId);
        if (currentProcess.HasExited || !string.Equals(
                Path.GetFullPath(currentProcess.MainModule?.FileName ?? string.Empty), target,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The specified process is not the running FlowLens executable.");
        }

        var processStartTicks = currentProcess.StartTime.ToUniversalTime().Ticks;
        var installDirectory = Path.GetDirectoryName(target)!;
        var id = Guid.NewGuid().ToString("N");
        var jobDirectory = Path.Combine(Path.GetFullPath(updateDirectory), $"install-{id}");
        Directory.CreateDirectory(jobDirectory);
        var prepared = new PreparedUpdate(
            Path.Combine(jobDirectory, "install.ps1"),
            Path.Combine(jobDirectory, "install.json"),
            Path.Combine(jobDirectory, "install.log"),
            Path.Combine(installDirectory, $".FlowLens.backup-{id}.exe"),
            Path.Combine(installDirectory, $".FlowLens.update-{id}.exe"),
            Path.Combine(jobDirectory, "ready"),
            Path.Combine(jobDirectory, "cancel"));

        try
        {
            // Copy beside the running executable: replacement must stay on the same volume.
            // Creating this file also checks folder write access before the app shuts down.
            using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var output = new FileStream(prepared.CandidatePath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                ValidateExecutable(input);
                input.Position = 0;
                input.CopyTo(output);
                output.Flush(flushToDisk: true);
            }

            var newHash = HashFile(prepared.CandidatePath);
            if (expectedExecutableSha256 is not null &&
                !string.Equals(newHash, expectedExecutableSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The verified update executable changed before installation.");
            }

            var plan = new
            {
                TargetPath = target,
                prepared.CandidatePath,
                prepared.BackupPath,
                prepared.LogPath,
                prepared.ReadyPath,
                prepared.CancelPath,
                CurrentPid = currentProcessId,
                ProcessStartTimeUtcTicks = processStartTicks,
                CurrentSha256 = HashFile(target),
                NewSha256 = newHash
            };
            File.WriteAllText(prepared.ManifestPath, JsonSerializer.Serialize(plan), new UTF8Encoding(false));
            File.WriteAllText(prepared.ScriptPath, BuildScript(), new UTF8Encoding(true));
            return prepared;
        }
        catch
        {
            TryDelete(prepared.CandidatePath);
            throw;
        }
    }

    public static async Task<Process> LaunchAsync(PreparedUpdate prepared, CancellationToken cancellationToken = default)
    {
        if (File.Exists(prepared.ReadyPath) || File.Exists(prepared.CancelPath))
        {
            throw new InvalidOperationException("This update installation has already been started or cancelled.");
        }

        var startInfo = new ProcessStartInfo(PowerShellPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Path.GetDirectoryName(prepared.ScriptPath)!
        };
        foreach (var argument in new[]
                 {
                     "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
                     "-File", prepared.ScriptPath, "-ManifestPath", prepared.ManifestPath
                 })
        {
            startInfo.ArgumentList.Add(argument);
        }

        Process? helper = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            helper = Process.Start(startInfo) ?? throw new InvalidOperationException("The update helper did not start.");
            var wait = Stopwatch.StartNew();
            while (wait.Elapsed < TimeSpan.FromSeconds(15))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (helper.HasExited)
                {
                    throw new InvalidOperationException($"The update helper exited before it was ready. See {prepared.LogPath}");
                }

                if (File.Exists(prepared.ReadyPath))
                {
                    return helper;
                }

                await Task.Delay(100, cancellationToken);
            }

            throw new TimeoutException($"The update helper did not become ready. See {prepared.LogPath}");
        }
        catch
        {
            // Do not terminate either process. The helper observes cancellation before replacing anything.
            File.WriteAllText(prepared.CancelPath, "cancelled", new UTF8Encoding(false));
            TryDelete(prepared.CandidatePath);
            helper?.Dispose();
            throw;
        }
    }

    internal static string BuildScript(bool restartAfterInstall = true, int exitTimeoutSeconds = 90)
    {
        if (exitTimeoutSeconds is < 1 or > 300)
        {
            throw new ArgumentOutOfRangeException(nameof(exitTimeoutSeconds));
        }

        // Paths are data in a separate JSON document, never interpolated into PowerShell code.
        return "param([Parameter(Mandatory = $true)][string]$ManifestPath)\r\n" +
               "$ErrorActionPreference = 'Stop'\r\nSet-StrictMode -Version Latest\r\n" +
               "$restartAfterInstall = " + (restartAfterInstall ? "$true" : "$false") + "\r\n" +
               "$exitTimeoutSeconds = " + exitTimeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\r\n" +
               """
               $plan = Get-Content -LiteralPath $ManifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
               $replaced = $false
               $utf8 = New-Object System.Text.UTF8Encoding($false)
               function Write-UpdateLog([string]$message) {
                   [System.IO.File]::AppendAllText($plan.LogPath, ('[' + [DateTime]::Now.ToString('s') + '] ' + $message + [Environment]::NewLine), $utf8)
               }
               function Assert-NotCancelled {
                   if ([System.IO.File]::Exists($plan.CancelPath)) { throw 'Update cancelled before installation.' }
               }
               function Assert-FileHash([string]$path, [string]$expected) {
                   $item = Get-Item -LiteralPath $path -Force
                   if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) { throw ('Refusing a linked update file: ' + $path) }
                   $input = [System.IO.File]::OpenRead($path)
                   $sha = [System.Security.Cryptography.SHA256]::Create()
                   try { $actual = [System.BitConverter]::ToString($sha.ComputeHash($input)).Replace('-', '') }
                   finally { $input.Dispose(); $sha.Dispose() }
                   if ($actual -ne $expected) { throw ('Update file changed: ' + $path) }
               }
               function Get-OriginalProcess {
                   try { $running = [System.Diagnostics.Process]::GetProcessById([int]$plan.CurrentPid) }
                   catch [System.ArgumentException] { return $null }
                   if ($running.StartTime.ToUniversalTime().Ticks -ne [long]$plan.ProcessStartTimeUtcTicks) {
                       $running.Dispose()
                       return $null
                   }
                   if (-not [string]::Equals($running.MainModule.FileName, $plan.TargetPath, [StringComparison]::OrdinalIgnoreCase)) {
                       $running.Dispose()
                       throw 'The waiting process path does not match this FlowLens installation.'
                   }
                   return $running
               }
               function Start-UpdatedApplication {
                   $startInfo = New-Object System.Diagnostics.ProcessStartInfo
                   $startInfo.FileName = $plan.TargetPath
                   $startInfo.WorkingDirectory = [System.IO.Path]::GetDirectoryName($plan.TargetPath)
                   $startInfo.UseShellExecute = $true
                   $startInfo.Verb = 'runas'
                   $started = [System.Diagnostics.Process]::Start($startInfo)
                   if ($null -eq $started) { throw 'The updated FlowLens application did not start.' }
                   try {
                       if ($started.WaitForExit(2000)) { throw 'The updated FlowLens application exited immediately after starting.' }
                   }
                   finally { $started.Dispose() }
               }
               try {
                   Assert-NotCancelled
                   $directory = [System.IO.Path]::GetDirectoryName($plan.TargetPath)
                   if ([System.IO.Path]::GetFileName($plan.TargetPath) -ne 'FlowLens.exe' -or
                       -not [string]::Equals($directory, [System.IO.Path]::GetDirectoryName($plan.CandidatePath), [StringComparison]::OrdinalIgnoreCase) -or
                       -not [string]::Equals($directory, [System.IO.Path]::GetDirectoryName($plan.BackupPath), [StringComparison]::OrdinalIgnoreCase)) {
                       throw 'Update files must be beside the current FlowLens.exe.'
                   }
                   if ([System.IO.File]::Exists($plan.BackupPath)) { throw 'The backup path already exists.' }
                   Assert-FileHash $plan.TargetPath $plan.CurrentSha256
                   Assert-FileHash $plan.CandidatePath $plan.NewSha256
                   $original = Get-OriginalProcess
                   Write-UpdateLog 'Ready. Waiting for FlowLens to save its history and exit.'
                   [System.IO.File]::WriteAllText($plan.ReadyPath, 'ready', $utf8)
                   if ($null -ne $original) {
                       try {
                           $watch = [System.Diagnostics.Stopwatch]::StartNew()
                           while (-not $original.WaitForExit(200)) {
                               Assert-NotCancelled
                               if ($watch.Elapsed.TotalSeconds -ge $exitTimeoutSeconds) { throw 'FlowLens did not exit within the update timeout. No files were replaced.' }
                           }
                       }
                       finally { $original.Dispose() }
                   }
                   Assert-NotCancelled
                   # Another launch must not be replaced while it is collecting traffic.
                   foreach ($other in [System.Diagnostics.Process]::GetProcessesByName('FlowLens')) {
                       try {
                           if ($other.HasExited) { continue }
                           $module = $other.MainModule
                           if ($null -eq $module) {
                               if ($other.HasExited) { continue }
                               throw 'Cannot verify another FlowLens process. Close other FlowLens instances and retry the update.'
                           }
                           if ([string]::Equals($module.FileName, $plan.TargetPath, [StringComparison]::OrdinalIgnoreCase)) {
                               throw 'Another FlowLens instance is running from this installation. No files were replaced.'
                           }
                       }
                       finally { $other.Dispose() }
                   }
                   Assert-FileHash $plan.TargetPath $plan.CurrentSha256
                   Assert-FileHash $plan.CandidatePath $plan.NewSha256
                   Assert-NotCancelled
                   [System.IO.File]::Replace($plan.CandidatePath, $plan.TargetPath, $plan.BackupPath, $false)
                   $replaced = $true
                   Assert-FileHash $plan.TargetPath $plan.NewSha256
                   Write-UpdateLog ('Installed update. Original executable retained at ' + $plan.BackupPath)
                   if ($restartAfterInstall) { Start-UpdatedApplication }
                   Write-UpdateLog 'Update completed successfully.'
                   exit 0
               }
               catch {
                   $failure = $_.Exception.Message
                   if ($replaced) {
                       try {
                           Assert-FileHash $plan.BackupPath $plan.CurrentSha256
                           [System.IO.File]::Replace($plan.BackupPath, $plan.TargetPath, $plan.CandidatePath, $false)
                           Write-UpdateLog 'Installation failed. The original executable was restored; start FlowLens again to continue.'
                       }
                       catch { Write-UpdateLog ('Rollback failed. Original executable is at ' + $plan.BackupPath + '. ' + $_.Exception.Message) }
                   }
                   Write-UpdateLog ('UPDATE FAILED: ' + $failure)
                   exit 1
               }
               """;
    }

    private static void EnsureOrdinaryFile(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
        {
            throw new InvalidOperationException($"The update path must be an ordinary file: {path}");
        }
    }

    private static void ValidateExecutable(FileStream input)
    {
        using var reader = new BinaryReader(input, Encoding.UTF8, leaveOpen: true);
        if (input.Length < 64 || reader.ReadUInt16() != 0x5a4d)
        {
            throw new InvalidDataException("The staged update is not a Windows executable.");
        }

        input.Position = 0x3c;
        var headerOffset = reader.ReadInt32();
        if (headerOffset < 64 || headerOffset > input.Length - 4)
        {
            throw new InvalidDataException("The staged update has an invalid executable header.");
        }

        input.Position = headerOffset;
        if (reader.ReadUInt32() != 0x00004550)
        {
            throw new InvalidDataException("The staged update has an invalid executable signature.");
        }
    }

    private static string HashFile(string path)
    {
        using var input = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(input));
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
