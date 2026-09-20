using FlowLens;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

internal static class UpdateInstallerTests
{
    public static void Run(Action<bool, string> check)
    {
        RunCase("guard paths, hashes, and read-only installations", check, fixture =>
        {
            check(Rejects(() => fixture.Prepare(currentPath: fixture.Source)),
                "An updater must reject a current path that does not match its running PID.");
            check(Rejects(() => fixture.Prepare(sourcePath: fixture.Target)),
                "An updater must reject using the running executable as its staged input.");
            check(Rejects(() => fixture.Prepare(expectedHash: new string('0', 64))),
                "The verified executable hash must survive staging unchanged.");
            check(!Directory.EnumerateFiles(fixture.DirectoryPath, ".FlowLens.update-*.exe").Any(),
                "A rejected candidate must not leave a partial staged executable in the install folder.");

            var invalid = Path.Combine(fixture.DirectoryPath, "invalid.exe");
            File.WriteAllText(invalid, "not a PE executable");
            check(Rejects(() => fixture.Prepare(sourcePath: invalid)),
                "The updater must reject an HTML/text file renamed to .exe.");

            var originalAttributes = File.GetAttributes(fixture.Target);
            try
            {
                File.SetAttributes(fixture.Target, originalAttributes | FileAttributes.ReadOnly);
                check(Rejects(() => fixture.Prepare()),
                    "A read-only installation must be rejected before shutting down the app.");
            }
            finally { File.SetAttributes(fixture.Target, originalAttributes); }
        });

        RunCase("successful atomic replacement", check, fixture =>
        {
            var prepared = fixture.Prepare();
            fixture.WriteTestScript(prepared);
            check(!File.ReadAllText(prepared.ScriptPath).Contains(fixture.DirectoryPath, StringComparison.Ordinal),
                "User-supplied paths must remain JSON data, not PowerShell source code.");
            using var helper = LaunchHelper(prepared);
            check(Hash(fixture.Target) == fixture.OriginalHash && !File.Exists(prepared.BackupPath),
                "Waiting for graceful shutdown must leave the running executable unchanged.");
            fixture.ExitNormally();
            check(WaitForHelper(helper) == 0, "A verified staged executable must install after the original process exits. " + File.ReadAllText(prepared.LogPath));
            check(Hash(fixture.Target) == fixture.NewHash && Hash(prepared.BackupPath) == fixture.OriginalHash,
                "Successful replacement must preserve the exact old executable in its backup.");
            check(File.ReadAllText(prepared.LogPath).Contains("Update completed successfully.", StringComparison.Ordinal),
                "A completed update must leave a readable result log.");
        });

        RunCase("modified candidate rejected after shutdown", check, fixture =>
        {
            var prepared = fixture.Prepare();
            fixture.WriteTestScript(prepared);
            using var helper = LaunchHelper(prepared);
            using (var changed = new FileStream(prepared.CandidatePath, FileMode.Append, FileAccess.Write)) { changed.WriteByte(1); }
            fixture.ExitNormally();
            check(WaitForHelper(helper) == 1 && Hash(fixture.Target) == fixture.OriginalHash && !File.Exists(prepared.BackupPath),
                "A candidate changed while the app was closing must be rejected without replacing the original.");
            check(File.ReadAllText(prepared.LogPath).Contains("Update file changed", StringComparison.Ordinal),
                "A candidate-integrity failure must explain the failure in the local log.");
        });

        RunCase("cancelled helper preserves running app", check, fixture =>
        {
            var prepared = fixture.Prepare();
            fixture.WriteTestScript(prepared);
            using var helper = LaunchHelper(prepared);
            File.WriteAllText(prepared.CancelPath, "cancelled");
            check(WaitForHelper(helper) == 1 && !fixture.RunningProcess.HasExited && Hash(fixture.Target) == fixture.OriginalHash,
                "Cancelling an update must not terminate FlowLens or replace its executable.");
        });

        RunCase("shutdown timeout preserves running app", check, fixture =>
        {
            var prepared = fixture.Prepare();
            fixture.WriteTestScript(prepared, exitTimeoutSeconds: 1);
            using var helper = LaunchHelper(prepared);
            check(WaitForHelper(helper) == 1 && !fixture.RunningProcess.HasExited && Hash(fixture.Target) == fixture.OriginalHash,
                "A shutdown timeout must leave the running process and original executable intact.");
            check(File.ReadAllText(prepared.LogPath).Contains("did not exit within", StringComparison.Ordinal),
                "A shutdown timeout must produce a useful failure log.");
        });

        RunCase("rollback after replacement", check, fixture =>
        {
            var prepared = fixture.Prepare();
            // Inject a harmless launch failure into the test-generated script only. No real app
            // is restarted or elevated; the production replacement and rollback code executes.
            fixture.WriteTestScript(prepared, simulateRestartFailure: true);
            using var helper = LaunchHelper(prepared);
            fixture.ExitNormally();
            check(WaitForHelper(helper) == 1 && Hash(fixture.Target) == fixture.OriginalHash,
                "Failure after replacement must atomically restore the original executable.");
            check(File.Exists(prepared.CandidatePath) && Hash(prepared.CandidatePath) == fixture.NewHash,
                "Rollback must retain the rejected executable separately from the restored app.");
            check(File.ReadAllText(prepared.LogPath).Contains("original executable was restored", StringComparison.Ordinal),
                "A successful rollback must explain how the user can resume using FlowLens.");
        });

        RunCase("competing instance blocks replacement", check, fixture =>
        {
            using var competing = fixture.StartFixtureProcess();
            var prepared = fixture.Prepare();
            fixture.WriteTestScript(prepared, competingProcessId: competing.Id);
            using var helper = LaunchHelper(prepared);
            fixture.ExitNormally();
            try
            {
                check(WaitForHelper(helper) == 1 && !competing.HasExited && Hash(fixture.Target) == fixture.OriginalHash,
                    "A second process running the target executable must block replacement without being terminated.");
                check(File.ReadAllText(prepared.LogPath).Contains("Another FlowLens instance is running", StringComparison.Ordinal),
                    "A competing-instance error must explain why no files were replaced.");
            }
            finally
            {
                competing.StandardInput.Close();
                competing.WaitForExit(5000);
            }
        });

        RunCase("cancel before launching", check, fixture =>
        {
            var prepared = fixture.Prepare();
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            var observedCancellation = false;
            try { using var helper = UpdateInstaller.LaunchAsync(prepared, cancelled.Token).GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { observedCancellation = true; }
            check(observedCancellation && !File.Exists(prepared.ReadyPath) && !File.Exists(prepared.CandidatePath),
                "Cancellation before launch must not start the helper and must clean the unused candidate.");
        });
    }

    private static void RunCase(string name, Action<bool, string> check, Action<Fixture> test)
    {
        try
        {
            using var fixture = new Fixture();
            test(fixture);
        }
        catch (Exception exception)
        {
            check(false, $"Installer regression ({name}) failed: {exception.Message}");
        }
    }

    private static bool Rejects(Action action)
    {
        try { action(); return false; }
        catch (InvalidOperationException) { return true; }
        catch (InvalidDataException) { return true; }
        catch (UnauthorizedAccessException) { return true; }
    }

    private static int WaitForHelper(Process helper)
    {
        if (!helper.WaitForExit(15000))
        {
            throw new TimeoutException("The fixture update helper did not exit within its bounded timeout.");
        }

        return helper.ExitCode;
    }

    private static Process LaunchHelper(PreparedUpdate prepared)
    {
        try { return UpdateInstaller.LaunchAsync(prepared).GetAwaiter().GetResult(); }
        catch (Exception exception)
        {
            var log = File.Exists(prepared.LogPath) ? File.ReadAllText(prepared.LogPath) : "No helper log was created.";
            throw new InvalidOperationException(exception.Message + Environment.NewLine + log, exception);
        }
    }

    private static string Hash(string path)
    {
        using var input = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(input));
    }

    private sealed class Fixture : IDisposable
    {
        public string DirectoryPath { get; }
        public string Target { get; }
        public string Source { get; }
        public string OriginalHash { get; }
        public string NewHash { get; }
        public Process RunningProcess { get; }
        private readonly string _root;

        public Fixture()
        {
            _root = Path.Combine(Path.GetTempPath(), $"FlowLens-InstallerTests-{Guid.NewGuid():N}");
            DirectoryPath = Path.Combine(_root, "资料 空格' $() [fixture]");
            Directory.CreateDirectory(DirectoryPath);
            Target = Path.Combine(DirectoryPath, "FlowLens.exe");
            Source = Path.Combine(DirectoryPath, "staged.exe");
            var harmlessCommandProcessor = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
            File.Copy(harmlessCommandProcessor, Target);
            File.Copy(harmlessCommandProcessor, Source);
            // Harmless trailing bytes keep this a real PE while giving the two fixtures distinct hashes.
            using (var append = new FileStream(Source, FileMode.Append, FileAccess.Write))
            {
                append.Write(Encoding.UTF8.GetBytes("FlowLens updater fixture"));
            }

            OriginalHash = Hash(Target);
            NewHash = Hash(Source);
            RunningProcess = StartFixtureProcess();
        }

        public Process StartFixtureProcess()
        {
            var startInfo = new ProcessStartInfo(Target)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            startInfo.ArgumentList.Add("/D");
            startInfo.ArgumentList.Add("/Q");
            startInfo.ArgumentList.Add("/C");
            startInfo.ArgumentList.Add("set /p flowlens_update_fixture=");
            var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Cannot start the harmless fixture process.");
            if (process.WaitForExit(100)) { throw new InvalidOperationException("The fixture process unexpectedly exited."); }
            return process;
        }

        public PreparedUpdate Prepare(string? sourcePath = null, string? currentPath = null, string? expectedHash = null) =>
            UpdateInstaller.Prepare(sourcePath ?? Source, currentPath ?? Target, RunningProcess.Id,
                Path.Combine(DirectoryPath, "update logs' [中文]"), expectedHash ?? NewHash);

        public void WriteTestScript(PreparedUpdate prepared, int exitTimeoutSeconds = 5, bool simulateRestartFailure = false, int? competingProcessId = null)
        {
            // The test token may not inspect the user's elevated real FlowLens. Restrict the
            // competing-instance scan to our known fixture PIDs; keep its production guard intact.
            var fixtureIds = RunningProcess.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (competingProcessId.HasValue) { fixtureIds += "," + competingProcessId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture); }
            var script = UpdateInstaller.BuildScript(restartAfterInstall: false, exitTimeoutSeconds)
                .Replace("foreach ($other in [System.Diagnostics.Process]::GetProcessesByName('FlowLens'))",
                    $"foreach ($other in @([System.Diagnostics.Process]::GetProcessesByName('FlowLens') | Where-Object {{ $_.Id -in @({fixtureIds}) }}))", StringComparison.Ordinal);
            if (simulateRestartFailure)
            {
                script = script.Replace("if ($restartAfterInstall) { Start-UpdatedApplication }",
                    "throw 'Simulated restart failure for the installer regression.'", StringComparison.Ordinal);
            }
            File.WriteAllText(prepared.ScriptPath, script, Encoding.UTF8);
        }

        public void ExitNormally()
        {
            if (RunningProcess.HasExited) { return; }
            RunningProcess.StandardInput.Close();
            if (!RunningProcess.WaitForExit(5000)) { throw new TimeoutException("The harmless fixture did not exit after stdin closed."); }
        }

        public void Dispose()
        {
            ExitNormally();
            RunningProcess.Dispose();
            // The path is the exact unique fixture root created above, never an installation path.
            try { Directory.Delete(_root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
