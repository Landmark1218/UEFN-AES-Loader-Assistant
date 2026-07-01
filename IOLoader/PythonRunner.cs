using System.Diagnostics;

namespace UEFNMapInstaller;

/// <summary>
/// uefn_downloader.py をサブプロセスとして起動するヘルパー。
/// stdout をリアルタイムにコンソールへ転送し、終了コードを返します。
/// </summary>
internal static class PythonRunner
{
    /// <summary>
    /// Python スクリプトを引数付きで実行します。
    /// stdout / stderr はリアルタイムで親プロセスのコンソールに流れます。
    /// </summary>
    /// <returns>プロセスの終了コード</returns>
    public static int Run(string scriptPath, string[] args, string workingDir, IDictionary<string, string>? env = null)
    {
        var pythonExe = FindPython();
        if (pythonExe is null)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("[ERROR] Python が見つかりません。");
            Console.WriteLine("  python.exe または python3.exe にパスが通っているか確認してください。");
            Console.ResetColor();
            return -1;
        }

        // コマンドライン文字列を組み立て
        var argList = new List<string> { $"\"{scriptPath}\"" };
        argList.AddRange(args.Select(QuoteArg));
        var argStr = string.Join(" ", argList);

        var psi = new ProcessStartInfo
        {
            FileName               = pythonExe,
            Arguments              = argStr,
            WorkingDirectory       = workingDir,
            UseShellExecute        = false,
            RedirectStandardOutput = false,  // コンソールに直接出力させる
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
    /// Python スクリプトを実行し、stdout を文字列として返します（解析用）。
    /// stderr はバッファして返します。
    /// </summary>
    public static (int exitCode, string stdout, string stderr) RunCapture(
        string scriptPath, string[] args, string workingDir)
    {
        var pythonExe = FindPython()
            ?? throw new FileNotFoundException("Python が見つかりません");

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
    /// Python スクリプトを実行し、stdout を文字列として返します（解析用）。
    /// stderr はリアルタイムでコンソールに流します（進捗ログ用）。
    /// </summary>
    public static (int exitCode, string stdout) RunCaptureWithLiveStderr(
        string scriptPath, string[] args, string workingDir,
        ConsoleColor stderrColor = ConsoleColor.Cyan)
    {
        var pythonExe = FindPython()
            ?? throw new FileNotFoundException("Python が見つかりません");

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

        // stderr はイベントでリアルタイム表示
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

        // stdout は非同期で読みながら最後にまとめて返す
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        proc.WaitForExit();
        var stdout = stdoutTask.GetAwaiter().GetResult();

        return (proc.ExitCode, stdout);
    }

    // ── Python 実行ファイルを探す ──────────────────────────────────

    private static string? FindPython()
    {
        foreach (var name in new[] { "python", "python3", "py" })
        {
            var path = FindInPath(name + ".exe")
                    ?? FindInPath(name);      // Linux/Mac 互換
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
