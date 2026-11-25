using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace AppDataRebuilder
{
    internal sealed class ProcessRunResult
    {
        public ProcessRunResult(int exitCode, IReadOnlyList<string> output)
        {
            ExitCode = exitCode;
            Output = output;
        }

        public int ExitCode { get; }
        public IReadOnlyList<string> Output { get; }

        public string GetErrorSummary()
        {
            var interesting = Output
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .TakeLast(6)
                .ToArray();

            return interesting.Length == 0
                ? "无输出"
                : string.Join(" | ", interesting);
        }
    }

    internal static class ProcessRunner
    {
        private const int MaxCapturedLines = 300;

        static ProcessRunner()
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        }

        public static async Task<ProcessRunResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            string? workingDirectory,
            Action<string>? onOutputLine,
            CancellationToken cancellationToken)
        {
            var startInfo = new ProcessStartInfo(fileName)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = GetConsoleEncoding(),
                StandardErrorEncoding = GetConsoleEncoding(),
            };

            if (!string.IsNullOrWhiteSpace(workingDirectory))
                startInfo.WorkingDirectory = workingDirectory;

            foreach (var argument in arguments)
                startInfo.ArgumentList.Add(argument);

            using var process = new Process { StartInfo = startInfo };
            if (!process.Start())
                throw new InvalidOperationException("无法启动进程：" + fileName);

            var output = new Queue<string>();
            var outputLock = new object();

            void HandleLine(string? line)
            {
                if (string.IsNullOrWhiteSpace(line))
                    return;

                var trimmed = line.Trim();
                lock (outputLock)
                {
                    output.Enqueue(trimmed);
                    while (output.Count > MaxCapturedLines)
                        output.Dequeue();
                }

                onOutputLine?.Invoke(trimmed);
            }

            var stdoutTask = PumpAsync(process.StandardOutput, HandleLine);
            var stderrTask = PumpAsync(process.StandardError, HandleLine);

            using var registration = cancellationToken.Register(() =>
            {
                try
                {
                    if (!process.HasExited)
                        process.Kill(entireProcessTree: true);
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
                {
                    // 进程可能已经退出。
                }
            });

            try
            {
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    if (!process.HasExited)
                        process.Kill(entireProcessTree: true);
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
                {
                    // 忽略终止失败。
                }

                throw;
            }

            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);

            string[] captured;
            lock (outputLock)
                captured = output.ToArray();

            return new ProcessRunResult(process.ExitCode, captured);
        }

        private static async Task PumpAsync(StreamReader reader, Action<string?> onLine)
        {
            try
            {
                while (true)
                {
                    var line = await reader.ReadLineAsync().ConfigureAwait(false);
                    if (line is null)
                        break;

                    onLine(line);
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
            {
                // 进程被终止时流可能关闭。
            }
        }

        private static Encoding GetConsoleEncoding()
        {
            try
            {
                return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
            {
                return Encoding.UTF8;
            }
        }
    }
}
