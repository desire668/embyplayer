using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;

namespace EmbyPlayer.Services;

public sealed class MpvLaunchOptions
{
    public required string MpvPath { get; init; }
    public required string Url { get; init; }
    public string? Title { get; init; }
    public double ResumeSeconds { get; init; }
    public string? SubtitleFile { get; init; }
}

/// <summary>
/// 启动并控制外部 mpv 进程，通过 --input-ipc-server 命名管道收发 JSON 命令。
/// 所有事件都在后台线程上触发，订阅方需自行切回 UI 线程。
/// </summary>
public sealed class MpvController : IDisposable
{
    /// <summary>播放位置变化（秒）。</summary>
    public event Action<double>? PositionChanged;

    /// <summary>时长解析完成（秒）。</summary>
    public event Action<double>? DurationChanged;

    /// <summary>当前文件自然播完（eof）。</summary>
    public event Action? FileEnded;

    /// <summary>mpv 进程退出（用户关闭窗口或被杀掉）。</summary>
    public event Action? PlayerClosed;

    private Process? _process;
    private NamedPipeClientStream? _pipe;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private CancellationTokenSource? _readCts;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private bool _closedRaised;

    public double LastKnownPosition { get; private set; }
    public double LastKnownDuration { get; private set; }
    public bool IsRunning => _process is { HasExited: false };

    public async Task StartAsync(MpvLaunchOptions options, CancellationToken ct = default)
    {
        Cleanup();

        LastKnownPosition = options.ResumeSeconds;
        LastKnownDuration = 0;
        _closedRaised = false;

        var pipeName = "embyplayer_" + Guid.NewGuid().ToString("N");
        var psi = new ProcessStartInfo
        {
            FileName = options.MpvPath,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("--no-terminal");
        psi.ArgumentList.Add("--idle=yes");
        psi.ArgumentList.Add("--force-window=yes");
        // 由本应用负责断点续播，禁用 mpv 自带的 watch_later
        psi.ArgumentList.Add("--no-resume-playback");
        psi.ArgumentList.Add($"--input-ipc-server=\\\\.\\pipe\\{pipeName}");
        if (!string.IsNullOrEmpty(options.Title))
        {
            psi.ArgumentList.Add($"--title={options.Title}");
        }
        if (options.ResumeSeconds > 1)
        {
            psi.ArgumentList.Add($"--start=+{(long)options.ResumeSeconds}");
        }
        if (!string.IsNullOrEmpty(options.SubtitleFile))
        {
            psi.ArgumentList.Add($"--sub-files={options.SubtitleFile}");
        }
        psi.ArgumentList.Add(options.Url);

        _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _process.Exited += OnProcessExited;
        if (!_process.Start())
        {
            throw new InvalidOperationException("无法启动 mpv。");
        }

        _pipe = await ConnectPipeAsync(pipeName, ct);
        _reader = new StreamReader(_pipe);
        _writer = new StreamWriter(_pipe) { AutoFlush = true };

        await SendCommandAsync(new object[] { "observe_property", 1, "playback-time" });
        await SendCommandAsync(new object[] { "observe_property", 2, "duration" });

        _readCts = new CancellationTokenSource();
        var token = _readCts.Token;
        _ = Task.Run(() => ReadLoopAsync(token), token);
    }

    public async Task LoadFileAsync(string url)
    {
        LastKnownPosition = 0;
        LastKnownDuration = 0;
        await SendCommandAsync(new object[] { "loadfile", url, "replace" });
    }

    public async Task AddSubtitleAsync(string url)
    {
        await SendCommandAsync(new object[] { "sub-add", url, "select" });
    }

    public async Task QuitAsync()
    {
        try
        {
            await SendCommandAsync(new object[] { "quit" });
        }
        catch
        {
            // 管道可能已断开，忽略
        }
    }

    private static async Task<NamedPipeClientStream> ConnectPipeAsync(string pipeName, CancellationToken ct)
    {
        for (var i = 0; i < 100; i++)
        {
            ct.ThrowIfCancellationRequested();
            var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await pipe.ConnectAsync(200, ct);
                return pipe;
            }
            catch (TimeoutException)
            {
                await pipe.DisposeAsync();
                await Task.Delay(100, ct);
            }
        }
        throw new TimeoutException("mpv 的 IPC 管道未能在预期时间内就绪。");
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var line = await _reader!.ReadLineAsync(ct);
                if (line is null)
                {
                    break;
                }
                HandleMessage(line);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            // 管道断开或读取失败，视为播放器退出
        }
        RaiseClosed();
    }

    private void HandleMessage(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;

            if (root.TryGetProperty("event", out var ev))
            {
                var eventName = ev.GetString();
                switch (eventName)
                {
                    case "property-change":
                        var name = root.GetProperty("name").GetString();
                        if (name == "playback-time" && root.TryGetProperty("data", out var time) &&
                            time.ValueKind == JsonValueKind.Number)
                        {
                            var seconds = time.GetDouble();
                            LastKnownPosition = seconds;
                            PositionChanged?.Invoke(seconds);
                        }
                        else if (name == "duration" && root.TryGetProperty("data", out var dur) &&
                                 dur.ValueKind == JsonValueKind.Number)
                        {
                            var duration = dur.GetDouble();
                            LastKnownDuration = duration;
                            DurationChanged?.Invoke(duration);
                        }
                        break;
                    case "end-file":
                        var reason = root.TryGetProperty("reason", out var r) ? r.GetString() : null;
                        if (reason == "eof")
                        {
                            FileEnded?.Invoke();
                        }
                        break;
                    case "shutdown":
                        RaiseClosed();
                        break;
                }
            }
        }
        catch (JsonException)
        {
            // 忽略无法解析的行
        }
    }

    private void OnProcessExited(object? sender, EventArgs e) => RaiseClosed();

    private void RaiseClosed()
    {
        if (_closedRaised)
        {
            return;
        }
        _closedRaised = true;
        PlayerClosed?.Invoke();
    }

    private async Task SendCommandAsync(object[] command)
    {
        if (_writer is null)
        {
            throw new InvalidOperationException("mpv 尚未连接。");
        }
        await _writeLock.WaitAsync();
        try
        {
            await _writer.WriteLineAsync(JsonSerializer.Serialize(command));
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private void Cleanup()
    {
        _readCts?.Cancel();
        try { _reader?.Dispose(); } catch { }
        try { _writer?.Dispose(); } catch { }
        try { _pipe?.Dispose(); } catch { }
        _reader = null;
        _writer = null;
        _pipe = null;
        if (_process is not null)
        {
            _process.Exited -= OnProcessExited;
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill();
                }
            }
            catch { }
            _process.Dispose();
            _process = null;
        }
        _readCts?.Dispose();
        _readCts = null;
    }

    public void Dispose()
    {
        _ = QuitAsync();
        Thread.Sleep(500);
        Cleanup();
        _writeLock.Dispose();
    }
}
