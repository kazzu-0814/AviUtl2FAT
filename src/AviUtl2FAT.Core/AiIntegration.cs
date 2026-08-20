using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace AviUtl2FAT.Core;

public enum AiProviderKind { RuleBased, LocalModel, ExternalRuntime, CloudAgent }
[Flags] public enum AiProviderCapability { None = 0, Naturalize = 1, Shorten = 2, SplitSuggestion = 4, BatchRewrite = 8, Offline = 16 }
public enum CaptionOperation { Naturalize, Shorten }
public enum CodexBackendPreference { Auto, AppServer, Cli }
public enum CodexConnectionState { NotInstalled, Available, NotAuthenticated, Connecting, Connected, Disconnected, Error }

public sealed record AiProviderDescriptor(string Id, string DisplayName, AiProviderKind Kind, AiProviderCapability Capabilities, bool RequiresCloudConfirmation, string PrivacyLabel);
public sealed record CaptionTransformRequest(IReadOnlyList<FATCaption> Captions, CaptionOperation Operation, string OutputLanguage, string? Previous = null, string? Following = null);
public sealed record CaptionTransformResponse(IReadOnlyList<FATCaption> Captions, string Provider, string? Model, string Backend);
public sealed record CaptionRevision(FATCaption Before, FATCaption After, CaptionOperation Operation, string Provider, string Backend, DateTimeOffset Timestamp);
public sealed record AiSelectionSettings(string PreferredAIProvider = "auto", string PreferredLocalModel = "gemma-4-e2b-it", string CodexBackend = "auto", string FailurePolicy = "rule", bool CodexIntegrationEnabled = false, bool ConfirmCodexEveryTime = true, string? CodexCliPath = null, string? ClaudeCliPath = null);

public static class AiSelectionSettingsStore
{
    public static async Task<AiSelectionSettings> LoadAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return new AiSelectionSettings();
        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<AiSelectionSettings>(stream, cancellationToken: cancellationToken) ?? new AiSelectionSettings();
        }
        // User settings must never prevent FAT itself from opening.  A malformed,
        // partially-written, or old settings file falls back to safe defaults.
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { return new AiSelectionSettings(); }
    }
    public static async Task SaveAsync(string path, AiSelectionSettings settings, CancellationToken cancellationToken)
    {
        await FatFiles.WriteUtf8AtomicAsync(path, JsonSerializer.Serialize(settings), cancellationToken);
    }
}

public interface ICaptionTransformProvider
{
    AiProviderDescriptor Descriptor { get; }
    Task<CaptionTransformResponse> TransformAsync(CaptionTransformRequest request, CancellationToken cancellationToken);
}

public sealed class RuleBasedProvider : ICaptionTransformProvider
{
    public AiProviderDescriptor Descriptor { get; } = new("rule", "AIなし（高速）", AiProviderKind.RuleBased, AiProviderCapability.Naturalize | AiProviderCapability.Shorten | AiProviderCapability.Offline, false, "Python ルール処理（外部送信なし）");
    public Task<CaptionTransformResponse> TransformAsync(CaptionTransformRequest request, CancellationToken cancellationToken)
    {
        var captions = request.Captions.Select(caption =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var text = request.Operation == CaptionOperation.Naturalize ? Naturalize(caption.Text) : Shorten(caption.Text);
            return caption with { Text = text, Provider = "rule", Model = "python-rule" };
        }).ToArray();
        return Task.FromResult(new CaptionTransformResponse(captions, "rule", "python-rule", "C#"));
    }
    private static string Naturalize(string text) => text.Trim().Replace("えー", "", StringComparison.Ordinal).Replace("えっと", "", StringComparison.Ordinal).Replace("ですね、", "", StringComparison.Ordinal).Trim();
    private static string Shorten(string text) => text.Length <= 36 ? text.Trim() : text.Trim()[..36].TrimEnd('、', '。', ' ');
}

public sealed record CodexRuntimeStatus(string? CliPath, bool CliDetected, bool CliRunnable, string? GuiPath, bool GuiDetected, string Detail)
{
    public bool IsAvailable => CliDetected && CliRunnable;
}

public interface ICodexBackend
{
    Task<CodexRuntimeStatus> GetStatusAsync(CancellationToken cancellationToken);
    Task<string> GenerateAsync(string prompt, CancellationToken cancellationToken);
}

public interface IConnectableCodexBackend : ICodexBackend
{
    CodexConnectionState ConnectionState { get; }
    Task ConnectAsync(CancellationToken cancellationToken);
    Task DisconnectAsync(CancellationToken cancellationToken);
}

/// <summary>Official Codex App Server stdio client. It never automates the Codex desktop GUI.</summary>
public sealed class CodexAppServerBackend(string? executablePath = null, TimeSpan? timeout = null) : IConnectableCodexBackend, IDisposable
{
    private string? _executablePath = executablePath;
    private readonly TimeSpan _timeout = timeout ?? TimeSpan.FromMinutes(2);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _process;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private Task<string>? _stderr;
    private long _requestId;
    public CodexConnectionState ConnectionState { get; private set; } = CodexConnectionState.Disconnected;
    public void ConfigureExecutablePath(string? path)
    {
        if (ConnectionState == CodexConnectionState.Connected)
            throw new InvalidOperationException("Disconnect Codex App Server before changing the CLI path.");
        _executablePath = string.IsNullOrWhiteSpace(path) ? null : path;
    }

    // Keep the App Server away from the AviUtl2 project, video and FAT runtime.
    // Only the prompt sent below is available to the Codex turn.
    private static string WorkDirectory
    {
        get
        {
            var path = Path.Combine(Path.GetTempPath(), "AviUtl2FAT", "codex-app-server");
            Directory.CreateDirectory(path);
            return path;
        }
    }

    public async Task<CodexRuntimeStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        if (ConnectionState == CodexConnectionState.Connected)
            return new(_executablePath ?? CodexCliBackend.FindCli(), true, true, CodexCliBackend.FindGui(), CodexCliBackend.FindGui() is not null, "Codex App Server に接続済みです。");
        var cli = _executablePath ?? CodexCliBackend.FindCli();
        if (cli is null) return new(null, false, false, CodexCliBackend.FindGui(), CodexCliBackend.FindGui() is not null, "Codex App Serverには実行可能な Codex CLI が必要です。");
        var cliBackend = new CodexCliBackend(_timeout);
        cliBackend.ConfigureExecutablePath(cli);
        var cliStatus = await cliBackend.GetStatusAsync(cancellationToken);
        return cliStatus with { Detail = cliStatus.IsAvailable ? "Codex App Server を開始できます。" : cliStatus.Detail };
    }

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (ConnectionState == CodexConnectionState.Connected) return;
            ConnectionState = CodexConnectionState.Connecting;
            var executable = _executablePath ?? CodexCliBackend.FindCli();
            if (executable is null) throw new FatException("CODEX_APP_SERVER_NOT_INSTALLED", "Codex App ServerにはCodex CLIが必要です。");
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            start.ArgumentList.Add("app-server"); // official Codex App Server stdio transport
            _process = Process.Start(start) ?? throw new FatException("CODEX_APP_SERVER_START_FAILED", "Codex App Serverを開始できませんでした。");
            _writer = _process.StandardInput; _writer.AutoFlush = true; _reader = _process.StandardOutput; _stderr = _process.StandardError.ReadToEndAsync();
            await RequestAsync("initialize", new { clientInfo = new { name = "aviutl2_fat", title = "AviUtl2 FAT", version = "1.0.0" } }, cancellationToken);
            await SendAsync(new { method = "initialized", @params = new { } }, cancellationToken);
            ConnectionState = CodexConnectionState.Connected;
        }
        catch (FatException) { Reset(CodexConnectionState.Error); throw; }
        catch (Exception error) { Reset(CodexConnectionState.Error); throw new FatException("CODEX_APP_SERVER_CONNECT_FAILED", error.Message, error); }
        finally { _gate.Release(); }
    }

    public async Task<string> GenerateAsync(string prompt, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (ConnectionState != CodexConnectionState.Connected) throw new FatException("CODEX_APP_SERVER_DISCONNECTED", "Codex App Serverは接続されていません。");
            var thread = await RequestAsync("thread/start", new { cwd = WorkDirectory, approvalPolicy = "never", sandbox = "readOnly", serviceName = "aviutl2_fat" }, cancellationToken);
            var threadId = thread.GetProperty("thread").GetProperty("id").GetString() ?? throw new FatException("CODEX_APP_SERVER_PROTOCOL", "Codex App Server did not return a thread id.");
            await RequestAsync("turn/start", new { threadId, input = new[] { new { type = "text", text = prompt } } }, cancellationToken);
            return await ReadTurnResultAsync(cancellationToken);
        }
        catch (FatException) { throw; }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw new FatException("CODEX_CANCELLED", "Codex処理をキャンセルしました。"); }
        catch (Exception error) { throw new FatException("CODEX_APP_SERVER_FAILED", error.Message, error); }
        finally { _gate.Release(); }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try { Reset(CodexConnectionState.Disconnected); }
        finally { _gate.Release(); }
    }
    private async Task<JsonElement> RequestAsync(string method, object parameters, CancellationToken cancellationToken)
    {
        var id = Interlocked.Increment(ref _requestId);
        await SendAsync(new { method, id, @params = parameters }, cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); timeout.CancelAfter(_timeout);
        while (await _reader!.ReadLineAsync(timeout.Token) is { } line)
        {
            using var document = JsonDocument.Parse(line); var root = document.RootElement;
            if (!root.TryGetProperty("id", out var responseId) || responseId.ValueKind != JsonValueKind.Number || responseId.GetInt64() != id) continue;
            if (root.TryGetProperty("error", out var error)) throw new FatException("CODEX_APP_SERVER_PROTOCOL", error.GetProperty("message").GetString() ?? "Codex App Server error.");
            return root.GetProperty("result").Clone();
        }
        throw new FatException("CODEX_APP_SERVER_DISCONNECTED", "Codex App Server disconnected before responding.");
    }
    private async Task<string> ReadTurnResultAsync(CancellationToken cancellationToken)
    {
        var result = new StringBuilder(); using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); timeout.CancelAfter(_timeout);
        while (await _reader!.ReadLineAsync(timeout.Token) is { } line)
        {
            using var document = JsonDocument.Parse(line); var root = document.RootElement;
            if (!root.TryGetProperty("method", out var method)) continue;
            var name = method.GetString(); var parameters = root.TryGetProperty("params", out var item) ? item : default;
            if (name == "item/agentMessage/delta" && parameters.TryGetProperty("delta", out var delta)) result.Append(delta.GetString());
            if (name == "item/completed" && parameters.TryGetProperty("item", out var completed) && completed.TryGetProperty("type", out var type) && type.GetString() == "agentMessage" && completed.TryGetProperty("text", out var text)) result.Clear().Append(text.GetString());
            if (name == "turn/completed")
            {
                if (parameters.TryGetProperty("turn", out var turn) && turn.TryGetProperty("status", out var status) && status.GetString() != "completed") throw new FatException("CODEX_APP_SERVER_FAILED", "Codex App Server turn did not complete.");
                return OutputValidator.ValidateText(result.ToString());
            }
        }
        throw new FatException("CODEX_APP_SERVER_DISCONNECTED", "Codex App Server disconnected during generation.");
    }
    private Task SendAsync(object value, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return _writer!.WriteLineAsync(JsonSerializer.Serialize(value));
    }
    private void Reset(CodexConnectionState state)
    {
        _writer?.Dispose(); _reader?.Dispose(); if (_process is { HasExited: false }) { try { _process.Kill(true); } catch { } }
        _process?.Dispose(); _process = null; _reader = null; _writer = null; _stderr = null; ConnectionState = state;
    }
    public void Dispose() { Reset(CodexConnectionState.Disconnected); _gate.Dispose(); }
}

public sealed class FakeCodexAppServerBackend(string result = "今回はAviUtl2 FATを紹介します。") : IConnectableCodexBackend
{
    public CodexConnectionState ConnectionState { get; private set; } = CodexConnectionState.Disconnected;
    public Task<CodexRuntimeStatus> GetStatusAsync(CancellationToken cancellationToken) => Task.FromResult(new CodexRuntimeStatus("fake-app-server", true, ConnectionState == CodexConnectionState.Connected, "fake-gui", true, ConnectionState.ToString()));
    public Task ConnectAsync(CancellationToken cancellationToken) { ConnectionState = CodexConnectionState.Connected; return Task.CompletedTask; }
    public Task DisconnectAsync(CancellationToken cancellationToken) { ConnectionState = CodexConnectionState.Disconnected; return Task.CompletedTask; }
    public Task<string> GenerateAsync(string prompt, CancellationToken cancellationToken) { if (ConnectionState != CodexConnectionState.Connected) throw new FatException("CODEX_APP_SERVER_DISCONNECTED", "Fake App Server is disconnected."); return Task.FromResult(result); }
}

public static class CodexBackendSelector
{
    public static async Task<(ICodexBackend Backend, string Name)> SelectAsync(CodexBackendPreference preference, IConnectableCodexBackend appServer, ICodexBackend cli, CancellationToken cancellationToken)
    {
        if (preference == CodexBackendPreference.AppServer)
        {
            if (appServer.ConnectionState != CodexConnectionState.Connected)
                throw new FatException("CODEX_APP_SERVER_DISCONNECTED", "Codex App Serverへ接続してから実行してください。");
            return (appServer, "Codex App Server");
        }
        if (preference == CodexBackendPreference.Auto && appServer.ConnectionState == CodexConnectionState.Connected)
            return (appServer, "Codex App Server");
        var cliStatus = await cli.GetStatusAsync(cancellationToken);
        if (cliStatus.IsAvailable) return (cli, "Codex CLI");
        throw new FatException("CODEX_UNAVAILABLE", "Codex App Serverは未接続で、Codex CLIも使用できません。");
    }
}

public sealed class FakeCodexBackend(string result = "今回はAviUtl2 FATを紹介します。") : ICodexBackend
{
    public Task<CodexRuntimeStatus> GetStatusAsync(CancellationToken cancellationToken) => Task.FromResult(new CodexRuntimeStatus("fake-codex", true, true, null, false, "Fake backend"));
    public Task<string> GenerateAsync(string prompt, CancellationToken cancellationToken) => Task.FromResult(result);
}

/// <summary>Runs Codex CLI without invoking a command shell. Caption text is one ProcessStartInfo argument, never shell syntax.</summary>
public sealed class CodexCliBackend(TimeSpan? timeout = null) : ICodexBackend
{
    private readonly TimeSpan _timeout = timeout ?? TimeSpan.FromMinutes(2);
    private string? _executablePath;
    public string? ExecutablePath => _executablePath;
    public void ConfigureExecutablePath(string? path)
    {
        _executablePath = string.IsNullOrWhiteSpace(path) ? null : path;
    }
    public async Task<CodexRuntimeStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        var cli = _executablePath ?? FindCli(); var gui = FindGui();
        if (cli is null) return new(null, false, false, gui, gui is not null, "Codex CLI は PATH または標準のインストール先に見つかりません。");
        try
        {
            var result = await RunAsync(cli, ["--version"], null, TimeSpan.FromSeconds(8), cancellationToken);
            return new(cli, true, result.ExitCode == 0, gui, gui is not null, result.ExitCode == 0 ? "Codex CLI を利用できます。" : SafeDetail(result.StandardError));
        }
        catch (Exception error) { return new(cli, true, false, gui, gui is not null, SafeDetail(error.Message)); }
    }

    public async Task<string> GenerateAsync(string prompt, CancellationToken cancellationToken)
    {
        var status = await GetStatusAsync(cancellationToken);
        if (!status.IsAvailable || status.CliPath is null) throw new FatException("CODEX_UNAVAILABLE", status.Detail);
        var output = Path.Combine(Path.GetTempPath(), "AviUtl2FAT", $"codex-{Guid.NewGuid():N}.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        try
        {
            // Codex CLI's non-interactive command; ArgumentList preserves subtitle text as data.
            var result = await RunAsync(status.CliPath, ["exec", "--skip-git-repo-check", "--output-last-message", output, prompt], null, _timeout, cancellationToken);
            if (result.ExitCode != 0) throw new FatException("CODEX_CLI_FAILED", SafeDetail(result.StandardError));
            if (!File.Exists(output)) throw new FatException("CODEX_INVALID_OUTPUT", "Codex CLI did not return a text result.");
            return OutputValidator.ValidateText(await File.ReadAllTextAsync(output, Encoding.UTF8, cancellationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw new FatException("CODEX_CANCELLED", "Codex processing was cancelled."); }
        finally { try { File.Delete(output); } catch { } }
    }

    public static string? FindCli()
    {
        var names = new[] { "codex.exe", "codex.cmd", "codex" };
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            foreach (var name in names) { var path = Path.Combine(directory.Trim(), name); if (File.Exists(path)) return path; }
        return null;
    }
    public static string? FindGui()
    {
        // The Microsoft Store desktop build is installed below WindowsApps, which is
        // intentionally not enumerable by normal desktop apps.  The CLI alias found
        // on PATH identifies that package without probing its private files.
        var packagedAlias = FindCli();
        if (packagedAlias?.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase) == true &&
            packagedAlias.Contains("OpenAI.Codex", StringComparison.OrdinalIgnoreCase)) return packagedAlias;
        // A currently running GUI is also a valid, read-only availability signal.
        try
        {
            var process = Process.GetProcesses().FirstOrDefault(item =>
                item.ProcessName.Contains("codex", StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrWhiteSpace(item.MainWindowTitle) && item.MainWindowTitle.Contains("Codex", StringComparison.OrdinalIgnoreCase)));
            if (process is not null) return "running-codex-gui";
        }
        catch { /* Process metadata is optional and must not affect FAT. */ }
        // Do not recursively scan Program Files/WindowsApps during a WPF window open.
        // WindowsApps is intentionally access-restricted and a broad walk can fail or stall.
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var candidates = new[]
        {
            Path.Combine(local, "Programs", "Codex", "Codex.exe"),
            Path.Combine(local, "Programs", "OpenAI Codex", "Codex.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "OpenAI", "Codex.exe")
        };
        foreach (var path in candidates) if (File.Exists(path)) return path;
        return null;
    }
    public static void OpenGui(CodexRuntimeStatus status)
    {
        if (status.GuiPath is null) throw new FatException("CODEX_GUI_NOT_FOUND", "Codex GUI は検出されませんでした。");
        if (status.GuiPath == "running-codex-gui")
            throw new FatException("CODEX_GUI_RUNNING", "Codex GUI は既に起動しています。FATからGUIを自動操作することはありません。");
        if (status.GuiPath.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase))
            throw new FatException("CODEX_GUI_ALREADY_AVAILABLE", "Microsoft Store版 Codex GUI は検出されています。既に起動中の場合はそのウィンドウを使用してください。起動していない場合はスタートメニューから Codex を開いてください。");
        Process.Start(new ProcessStartInfo(status.GuiPath) { UseShellExecute = true });
    }
    private static async Task<(int ExitCode, string StandardOutput, string StandardError)> RunAsync(string file, IReadOnlyList<string> arguments, string? input, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(file) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = input is not null, CreateNoWindow = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new FatException("CODEX_START_FAILED", "Codex CLI を開始できませんでした。");
        if (input is not null) { await process.StandardInput.WriteAsync(input); process.StandardInput.Close(); }
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); linked.CancelAfter(timeout);
        try { await process.WaitForExitAsync(linked.Token); }
        catch (OperationCanceledException) { try { process.Kill(true); } catch { } if (cancellationToken.IsCancellationRequested) throw; throw new FatException("CODEX_TIMEOUT", "Codex CLI の応答がタイムアウトしました。"); }
        return (process.ExitCode, await stdout, await stderr);
    }
    private static string SafeDetail(string text) => string.IsNullOrWhiteSpace(text) ? "Codex CLI の実行に失敗しました。" : text.Trim()[..Math.Min(500, text.Trim().Length)];
}

public sealed class CodexProvider(ICodexBackend backend) : ICaptionTransformProvider
{
    public AiProviderDescriptor Descriptor { get; } = new("codex", "OpenAI Codex", AiProviderKind.CloudAgent, AiProviderCapability.Naturalize | AiProviderCapability.Shorten | AiProviderCapability.BatchRewrite, true, "OpenAI Codex（字幕本文を外部AIサービスへ送信）");
    public async Task<CaptionTransformResponse> TransformAsync(CaptionTransformRequest request, CancellationToken cancellationToken)
    {
        var result = new List<FATCaption>();
        for (var index = 0; index < request.Captions.Count; index++)
        {
            var caption = request.Captions[index];
            var before = index > 0 ? request.Captions[index - 1].Text : request.Previous ?? string.Empty;
            var after = index + 1 < request.Captions.Count ? request.Captions[index + 1].Text : request.Following ?? string.Empty;
            var prompt = CodexPromptTemplates.Render(request.Operation, request.OutputLanguage, before, caption.Text, after);
            var text = await backend.GenerateAsync(prompt, cancellationToken);
            result.Add(caption with { Text = OutputValidator.ValidateText(text), Provider = "codex", Model = "codex-cli" });
        }
        var backendName = backend is CodexAppServerBackend ? "App Server" : "CLI";
        return new CaptionTransformResponse(result, "codex", "codex", backendName);
    }
}

public static class OutputValidator
{
    public static string ValidateText(string text)
    {
        var value = text.Trim();
        if (string.IsNullOrWhiteSpace(value)) throw new FatException("AI_EMPTY_OUTPUT", "AI returned an empty caption.");
        if (value.Length > 1_000) throw new FatException("AI_OUTPUT_TOO_LONG", "AI returned an unexpectedly long caption.");
        if (value.Contains('\0')) throw new FatException("AI_INVALID_OUTPUT", "AI output contains an invalid character.");
        return value;
    }
}

public sealed record ExternalCliStatus(string? CliPath, bool Detected, bool Runnable, string Detail)
{
    public bool IsAvailable => Detected && Runnable;
}

/// <summary>Shared shell-free process runner for external AI CLIs.</summary>
public static class SafeExternalProcessRunner
{
    public static async Task<(int ExitCode, string StandardOutput, string StandardError)> RunAsync(string file, IReadOnlyList<string> arguments, string? input, string workingDirectory, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(file) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = input is not null, CreateNoWindow = true, WorkingDirectory = workingDirectory };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new FatException("EXTERNAL_AI_START_FAILED", "External AI CLI could not be started.");
        if (input is not null) { await process.StandardInput.WriteAsync(input); process.StandardInput.Close(); }
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); linked.CancelAfter(timeout);
        try { await process.WaitForExitAsync(linked.Token); }
        catch (OperationCanceledException)
        {
            try { process.Kill(true); } catch { }
            if (cancellationToken.IsCancellationRequested) throw;
            throw new FatException("CLAUDE_CODE_TIMEOUT", "Claude Code CLI の応答がタイムアウトしました。");
        }
        return (process.ExitCode, await stdout, await stderr);
    }
}

public interface IClaudeCodeBackend
{
    Task<ExternalCliStatus> GetStatusAsync(CancellationToken cancellationToken);
    Task<string> GenerateAsync(string prompt, CancellationToken cancellationToken);
}

/// <summary>Claude Code is restricted to an empty FAT-owned workspace and receives subtitle prompts only.</summary>
public sealed class ClaudeCodeCliBackend(TimeSpan? timeout = null) : IClaudeCodeBackend
{
    private readonly TimeSpan _timeout = timeout ?? TimeSpan.FromMinutes(2);
    private string? _executablePath;
    public string? ExecutablePath => _executablePath;
    public void ConfigureExecutablePath(string? path) => _executablePath = string.IsNullOrWhiteSpace(path) ? null : path;
    private static string WorkDirectory
    {
        get { var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AviUtl2FAT", "claude-workspace"); Directory.CreateDirectory(path); return path; }
    }
    public async Task<ExternalCliStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        var cli = _executablePath ?? FindCli();
        if (cli is null) return new(null, false, false, "Claude Code CLI は PATH または標準のインストール先に見つかりません。");
        try
        {
            var result = await SafeExternalProcessRunner.RunAsync(cli, ["--version"], null, WorkDirectory, TimeSpan.FromSeconds(8), cancellationToken);
            return new(cli, true, result.ExitCode == 0, result.ExitCode == 0 ? "Claude Code CLI を利用できます。認証はClaude Codeの公式ログイン状態に従います。" : SafeDetail(result.StandardError));
        }
        catch (Exception error) { return new(cli, true, false, SafeDetail(error.Message)); }
    }
    public async Task<string> GenerateAsync(string prompt, CancellationToken cancellationToken)
    {
        var status = await GetStatusAsync(cancellationToken);
        if (!status.IsAvailable || status.CliPath is null) throw new FatException("CLAUDE_CODE_UNAVAILABLE", status.Detail);
        // --print/json is the documented non-interactive output. Do not pass a
        // project path, video, FAT source or a shell command to Claude Code.
        var result = await SafeExternalProcessRunner.RunAsync(status.CliPath,
            ["--print", "--output-format", "json", "--max-turns", "1", "--disallowedTools", "Bash,Read,Edit,Write,Glob,Grep,WebFetch,WebSearch", prompt], null, WorkDirectory, _timeout, cancellationToken);
        if (result.ExitCode != 0) throw new FatException("CLAUDE_CODE_FAILED", SafeDetail(result.StandardError));
        try
        {
            using var document = JsonDocument.Parse(result.StandardOutput);
            var root = document.RootElement;
            if (root.TryGetProperty("is_error", out var isError) && isError.GetBoolean()) throw new FatException("CLAUDE_CODE_FAILED", "Claude Code returned an error.");
            return OutputValidator.ValidateText(root.GetProperty("result").GetString() ?? string.Empty);
        }
        catch (JsonException error) { throw new FatException("CLAUDE_CODE_INVALID_OUTPUT", "Claude Code returned invalid JSON output.", error); }
    }
    public static string? FindCli()
    {
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            foreach (var name in new[] { "claude.exe", "claude.cmd", "claude" }) { var path = Path.Combine(directory.Trim(), name); if (File.Exists(path)) return path; }
        return null;
    }
    private static string SafeDetail(string value) => string.IsNullOrWhiteSpace(value) ? "Claude Code CLI の実行に失敗しました。" : value.Trim()[..Math.Min(500, value.Trim().Length)];
}

public sealed class FakeClaudeCodeBackend(string result = "今回はAviUtl2 FATについて紹介します。") : IClaudeCodeBackend
{
    public Task<ExternalCliStatus> GetStatusAsync(CancellationToken cancellationToken) => Task.FromResult(new ExternalCliStatus("fake-claude", true, true, "Fake Claude Code backend"));
    public Task<string> GenerateAsync(string prompt, CancellationToken cancellationToken) => Task.FromResult(result);
}

public sealed class ClaudeCodeProvider(IClaudeCodeBackend backend) : ICaptionTransformProvider
{
    public AiProviderDescriptor Descriptor { get; } = new("claude-code", "Anthropic Claude Code", AiProviderKind.CloudAgent, AiProviderCapability.Naturalize | AiProviderCapability.Shorten | AiProviderCapability.BatchRewrite, true, "Anthropic Claude Code（字幕本文を外部AIサービスへ送信）");
    public async Task<CaptionTransformResponse> TransformAsync(CaptionTransformRequest request, CancellationToken cancellationToken)
    {
        var output = new List<FATCaption>();
        foreach (var caption in request.Captions)
        {
            var prompt = $"Return only one revised subtitle with no explanation. Operation: {(request.Operation == CaptionOperation.Naturalize ? "naturalize" : "shorten")}. Output language: {(request.OutputLanguage == "en" ? "English" : "Japanese")}. Keep meaning and proper nouns.\nCurrent caption: {caption.Text}";
            output.Add(caption with { Text = OutputValidator.ValidateText(await backend.GenerateAsync(prompt, cancellationToken)), Provider = "claude-code", Model = "claude-code-cli" });
        }
        return new CaptionTransformResponse(output, "Anthropic Claude Code", "claude-code", "CLI");
    }
}

public static class CodexPromptTemplates
{
    public static string Render(CaptionOperation operation, string language, string previous, string current, string following) =>
        $"You are assisting a human subtitle editor. Return only one revised subtitle, with no explanation.\nOperation: {(operation == CaptionOperation.Naturalize ? "naturalize" : "shorten")}\nOutput language: {(language == "en" ? "English" : "Japanese")}\nKeep meaning and proper nouns. Do not add information.\nPrevious caption: {previous}\nCurrent caption: {current}\nNext caption: {following}";
}
