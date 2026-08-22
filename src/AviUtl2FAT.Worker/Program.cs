using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using AviUtl2FAT.Core;

var pipeIndex = Array.FindIndex(args, value => value.Equals("--pipe", StringComparison.OrdinalIgnoreCase));
if (pipeIndex < 0 || pipeIndex + 1 >= args.Length)
{
    Console.Error.WriteLine("Usage: AviUtl2FAT.Worker --pipe <name>");
    return 2;
}

var pipeName = args[pipeIndex + 1];
try
{
    await using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
    await pipe.WaitForConnectionAsync();
    using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, leaveOpen: true);
    await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
    var line = await reader.ReadLineAsync();
    if (string.IsNullOrWhiteSpace(line)) return 0;
    var command = JsonSerializer.Deserialize<FatIpcMessage>(line, WorkerJson.Options) ?? throw new FatException("FAT_IPC_INVALID", "Worker request could not be parsed.");
    FatProtocols.Validate(command.Protocol, command.Version, FatProtocols.AppProtocol);
    if (!string.Equals(command.Type, "recognize", StringComparison.OrdinalIgnoreCase)) throw new FatException("FAT_IPC_UNSUPPORTED", $"Unsupported worker command: {command.Type}");
    var request = command.Payload is JsonElement payload
        ? payload.Deserialize<RecognitionRequest>(WorkerJson.Options)
        : null;
    if (request is null) throw new FatException("FAT_IPC_INVALID", "The recognize request has no payload.");
    using var cancellation = new CancellationTokenSource();
    try
    {
        await RunRecognitionAsync(request, command.RequestId, writer, cancellation.Token);
    }
    catch (FatException error)
    {
        await writer.WriteLineAsync(JsonSerializer.Serialize(new FatIpcMessage("error", command.RequestId, null, new FatError(error.Code, error.Message, error.InnerException?.Message)), WorkerJson.Options));
    }
    catch (Exception error)
    {
        await writer.WriteLineAsync(JsonSerializer.Serialize(new FatIpcMessage("error", command.RequestId, null, new FatError("FAT_WORKER_UNEXPECTED", "An unexpected worker error occurred.", error.Message)), WorkerJson.Options));
    }
}
catch (FatException error)
{
    Console.Error.WriteLine($"{error.Code}: {error.Message}");
    return 1;
}
catch (Exception error)
{
    Console.Error.WriteLine($"FAT_WORKER_UNEXPECTED: {error}");
    return 1;
}
return 0;

static async Task RunRecognitionAsync(RecognitionRequest request, string? requestId, StreamWriter writer, CancellationToken cancellationToken)
{
    if (!File.Exists(request.InputPath)) throw new FatException("FAT_INPUT_NOT_FOUND", $"Input file was not found: {request.InputPath}");
    var runtime = ResolveRuntimeRoot();
    var python = ExistingPath(request.Settings.PythonPath, ResolvePythonExecutable(runtime));
    var engine = Path.Combine(runtime, "python", "fat_worker.py");
    var legacyRecognizer = Path.Combine(runtime, "python", "recognize.py");
    var ffmpeg = ExistingPath(request.Settings.FfmpegPath, Path.Combine(runtime, "ffmpeg", "ffmpeg.exe"));
    var ffprobe = ExistingPath(request.Settings.FfprobePath, Path.Combine(runtime, "ffmpeg", "ffprobe.exe"));
    var models = request.Settings.ModelDirectory ?? Path.Combine(runtime, "models");
    if (!File.Exists(python) || !File.Exists(engine) || !File.Exists(legacyRecognizer)) throw new FatException("FAT_RUNTIME_MISSING", "Python FAT engine or recognition runtime was not found. Check the FAT runtime directory.");
    if (!File.Exists(ffmpeg) || !File.Exists(ffprobe)) throw new FatException("FAT_FFMPEG_MISSING", "FFmpeg or ffprobe was not found.");
    var session = Path.Combine(runtime, "sessions", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(session);
    var output = request.OutputPath;
    Directory.CreateDirectory(Path.GetDirectoryName(output) ?? ".");
    var startInfo = new ProcessStartInfo(python, $"\"{engine}\"")
    {
        UseShellExecute = false,
        RedirectStandardInput = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        StandardInputEncoding = new UTF8Encoding(false),
        StandardOutputEncoding = new UTF8Encoding(false),
        StandardErrorEncoding = new UTF8Encoding(false),
        CreateNoWindow = true,
        WorkingDirectory = runtime
    };
    // JSON Lines carries Japanese captions and progress messages.  Without this,
    // Python inherits the Windows ANSI code page (CP932) and can fail while
    // emitting a valid Unicode error/progress message.
    startInfo.Environment["PYTHONUTF8"] = "1";
    startInfo.Environment["PYTHONIOENCODING"] = "utf-8";
    var process = new Process { StartInfo = startInfo };
    process.Start();
    var pythonRequest = new
    {
        protocol = FatProtocols.PythonProtocol,
        version = FatProtocols.Version,
        id = requestId,
        type = "speech.recognize",
        payload = new
        {
            python, script = legacyRecognizer, input = request.InputPath, output,
            model = request.Settings.RecognitionModel, language = request.Settings.Language,
            device = request.Settings.Device, compute_type = request.Settings.ComputeType,
            profile = request.Settings.SpeechProfile, audio_enhancement = request.Settings.AudioEnhancement,
            filler_mode = request.Settings.FillerMode, speech_recovery = request.Settings.SpeechRecoveryMode, dictionary = string.Join(",", request.Settings.RecognitionDictionary),
            fps = request.Settings.Fps, ffmpeg, ffprobe, model_dir = models, session_dir = session
        }
    };
    await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(pythonRequest, WorkerJson.Options));
    process.StandardInput.Close();
    var errorTask = process.StandardError.ReadToEndAsync();
    while (!process.StandardOutput.EndOfStream)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var line = await process.StandardOutput.ReadLineAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(line)) continue;
        using var document = JsonDocument.Parse(line);
        var pythonMessage = document.RootElement;
        var protocol = pythonMessage.TryGetProperty("protocol", out var protocolElement) ? protocolElement.GetString() ?? string.Empty : string.Empty;
        var version = pythonMessage.TryGetProperty("version", out var versionElement) ? versionElement.GetInt32() : 0;
        FatProtocols.Validate(protocol, version, FatProtocols.PythonProtocol);
        var type = pythonMessage.GetProperty("type").GetString();
        if (type == "error")
        {
            var error = pythonMessage.GetProperty("error");
            throw new FatException(error.GetProperty("code").GetString() ?? "FAT_PYTHON_ERROR", error.GetProperty("message").GetString() ?? "Python FAT engine failed.");
        }
        if (type == "progress" && pythonMessage.TryGetProperty("payload", out var payload))
            await writer.WriteLineAsync(JsonSerializer.Serialize(new FatIpcMessage("progress", requestId, payload.Clone()), WorkerJson.Options));
    }
    await process.WaitForExitAsync(cancellationToken);
    var standardError = await errorTask;
    if (process.ExitCode != 0) throw new FatException("FAT_RECOGNITION_FAILED", "Speech recognition failed.", standardError.Length == 0 ? null : new Exception(standardError));
    await writer.WriteLineAsync(JsonSerializer.Serialize(new FatIpcMessage("completed", requestId, new { output }), WorkerJson.Options));
}

static string ExistingPath(string? preferred, string fallback) => !string.IsNullOrWhiteSpace(preferred) && File.Exists(preferred) ? preferred : fallback;

static string ResolveRuntimeRoot()
{
    // A deployed FAT installation keeps runtime beside the App. During Debug
    // development, however, App/bin and Worker/bin are separate directories.
    // Only accept a runtime that has the complete Python recognition contract;
    // do not let an old/incomplete copied runtime hide the repository runtime.
    var current = new DirectoryInfo(AppContext.BaseDirectory);
    for (var level = 0; current is not null && level < 8; level++, current = current.Parent)
    {
        var candidate = Path.Combine(current.FullName, "runtime");
        if (HasRecognitionRuntime(candidate)) return candidate;
    }

    // Preserve the normal location in the diagnostic path and let the existing
    // structured FAT_RUNTIME_MISSING response describe the missing assets.
    return Path.Combine(AppContext.BaseDirectory, "runtime");
}

static string ResolvePythonExecutable(string runtime)
{
    var portable = Path.Combine(runtime, "python-runtime", "python.exe");
    return File.Exists(portable) ? portable : Path.Combine(runtime, "python-env", "Scripts", "python.exe");
}

static bool HasRecognitionRuntime(string runtime) =>
    File.Exists(ResolvePythonExecutable(runtime)) &&
    File.Exists(Path.Combine(runtime, "python", "fat_worker.py")) &&
    File.Exists(Path.Combine(runtime, "python", "recognize.py"));

internal static class WorkerJson
{
    internal static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
}
