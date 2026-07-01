using System.Diagnostics;

namespace UEFNMapInstaller;

/// <summary>
/// Helper that launches uefn_downloader.py as a subprocess.
/// Streams stdout to the console in real time and returns the exit code.
/// </summary>
internal static class PythonRunner
{
    /// <summary>
    /// Runs a Python script with the given arguments.
    /// stdout / stderr are streamed to the parent process console in real time.
    /// </summary>
    /// <returns>Process exit code</returns>
    public static int Run(string scriptPath, string[] args, string workingDir, IDictionary<string, string>? env = null)
    {
        var pythonExe = FindPython();
        if (pythonExe is null)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("[ERROR] Python not found.");
            Console.WriteLine("  Make sure python.exe or python3.exe is on your PATH.");
            Console.ResetColor();
            return -1;
        }

        // Build command-line string
        var argList = new List<string> { $"\"{scriptPath}\"" };
        argList.AddRange(args.Select(QuoteArg));
        var argStr = string.Join(" ", argList);

        var psi = new ProcessStartInfo
        {
            FileName               = pythonExe,
            Arguments              = argStr,
            WorkingDirectory       = workingDir,
            UseShellExecute        = false,
            RedirectStandardOutput = false,  // let output go directly to console
            RedirectStandardError  = false,
            CreateNoWindow         = false,
        };

        if (env is not null)
            foreach (var (key, value) in env)
                psi.Environment[key] = value;

        using var proc = new Process { StartInfo = psi };
        proc.Start();
        proc.WaitForExit();
        return proc.ExitCode;
    }

    /// <summary>
    /// Runs a Python script and returns stdout as a string (for parsing).
    /// stderr is buffered and returned.
    /// </summary>
    public static (int exitCode, string stdout, string stderr) RunCapture(
        string scriptPath, string[] args, string workingDir)
    {
        var pythonExe = FindPython()
            ?? throw new FileNotFoundException("Python not found");

        var argList = new List<string> { $"\"{scriptPath}\"" };
        argList.AddRange(args.Select(QuoteArg));

        var psi = new ProcessStartInfo
        {
            FileName               = pythonExe,
            Arguments              = string.Join(" ", argList),
            WorkingDirectory       = workingDir,
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            CreateNoWindow         = true,
        };

        using var proc = new Process { StartInfo = psi };
        proc.Start();
        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        return (proc.ExitCode, stdout, stderr);
    }

    /// <summary>
    /// Runs a Python script and returns stdout as a string (for parsing).
    /// stderr is streamed to the console in real time (for progress logging).
    /// </summary>
    public static (int exitCode, string stdout) RunCaptureWithLiveStderr(
        string scriptPath, string[] args, string workingDir,
        ConsoleColor stderrColor = ConsoleColor.Cyan)
    {
        var pythonExe = FindPython()
            ?? throw new FileNotFoundException("Python not found");

        var argList = new List<string> { $"\"{scriptPath}\"" };
        argList.AddRange(args.Select(QuoteArg));

        var psi = new ProcessStartInfo
        {
            FileName               = pythonExe,
            Arguments              = string.Join(" ", argList),
            WorkingDirectory       = workingDir,
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            CreateNoWindow         = true,
        };

        var stdoutBuf = new System.Text.StringBuilder();

        using var proc = new Process { StartInfo = psi };

        // Display stderr in real time via events
        proc.EnableRaisingEvents = true;
        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            Console.ForegroundColor = stderrColor;
            Console.WriteLine(e.Data);
            Console.ResetColor();
        };

        proc.Start();
        proc.BeginErrorReadLine();

        // Read stdout asynchronously and return it all at the end
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        proc.WaitForExit();
        var stdout = stdoutTask.GetAwaiter().GetResult();

        return (proc.ExitCode, stdout);
    }

    // -- Locate Python executable --

    private static string? FindPython()
    {
        foreach (var name in new[] { "python", "python3", "py" })
        {
            var path = FindInPath(name + ".exe")
                    ?? FindInPath(name);      // Linux/Mac fallback
            if (path is not null) return path;
        }
        return null;
    }

    private static string? FindInPath(string exe)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathEnv.Split(Path.PathSeparator))
        {
            var full = Path.Combine(dir, exe);
            if (File.Exists(full)) return full;
        }
        return null;
    }

    private static string QuoteArg(string arg) =>
        arg.Contains(' ') || arg.Contains('"') ? $"\"{arg.Replace("\"", "\\\"")}\"" : arg;
}
