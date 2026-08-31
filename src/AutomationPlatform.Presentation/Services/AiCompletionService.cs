using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AutomationPlatform.Presentation.Services;

public sealed record AiCompletionResult(
    bool Success,
    string Content,
    string ProviderName,
    string UserMessage)
{
    public static AiCompletionResult Succeeded(string content, string providerName) =>
        new(true, content, providerName, string.Empty);

    public static AiCompletionResult Failed(string userMessage) =>
        new(false, string.Empty, string.Empty, userMessage);
}

public sealed class AiCompletionService
{
    private static readonly TimeSpan CodexTimeout = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan AgentRouterRequestTimeout = TimeSpan.FromSeconds(150);
    private static readonly TimeSpan CodexReaderDrainTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan[] AgentRouterRetryDelays =
    {
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(3)
    };
    private static readonly SemaphoreSlim AgentRouterRequestGate = new(1, 1);
    private static readonly Regex TerminalAnsiSequence = new(
        @"\x1B\[[0-?]*[ -/]*[@-~]",
        RegexOptions.Compiled);
    private static readonly Regex SensitiveDiagnosticValue = new(
        """(?i)(['"]?(?:api[_-]?key|authorization|token|password|secret)['"]?\s*[:=]\s*)(?:"(?:\\.|[^"])*"|'(?:\\.|[^'])*'|[^\s,;}\]]+)""",
        RegexOptions.Compiled);
    private static readonly Regex BearerDiagnosticValue = new(
        """(?i)\bbearer\s+(?:"(?:\\.|[^"])*"|'(?:\\.|[^'])*'|[^\s,;}\]]+)""",
        RegexOptions.Compiled);
    private const int MaxAgentRouterAttempts = 3;
    private const int MaxCodexStandardOutputChars = 2_000_000;
    private const int MaxCodexStandardErrorChars = 256_000;

    public async Task<AiCompletionResult> CompleteAsync(
        string systemPrompt,
        string userPrompt,
        double temperature,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        _ = temperature;

        var provider = GetConfiguredAgentRouter();
        if (provider is null)
        {
            return AiCompletionResult.Failed(
                "Chưa cấu hình AgentRouter. Hãy đặt AGENTROUTER_API_KEY rồi thử lại.");
        }

        var gateAcquired = false;
        ProviderAttemptResult? finalAttempt = null;
        using var requestTimeoutSource = new CancellationTokenSource(AgentRouterRequestTimeout);
        using var requestCancellationSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            requestTimeoutSource.Token);
        var requestCancellationToken = requestCancellationSource.Token;
        try
        {
            if (AgentRouterRequestGate.CurrentCount == 0)
            {
                progress?.Report("🤖 AgentRouter đang xử lý yêu cầu trước; đang chờ lượt...");
            }

            await AgentRouterRequestGate.WaitAsync(requestCancellationToken);
            gateAcquired = true;

            for (var attemptNumber = 1; attemptNumber <= MaxAgentRouterAttempts; attemptNumber++)
            {
                progress?.Report(attemptNumber == 1
                    ? $"🤖 Đang hỏi AgentRouter ({provider.Model})..."
                    : $"🤖 AgentRouter đang thử lại lần {attemptNumber}/{MaxAgentRouterAttempts}...");

                finalAttempt = await TryCompleteWithCodexAsync(
                    provider,
                    systemPrompt,
                    userPrompt,
                    requestCancellationToken);
                if (finalAttempt.Success)
                {
                    return AiCompletionResult.Succeeded(finalAttempt.Content, provider.Name);
                }

                if (attemptNumber == MaxAgentRouterAttempts ||
                    !ShouldRetryAgentRouterAttempt(finalAttempt))
                {
                    break;
                }

                await Task.Delay(
                    AgentRouterRetryDelays[attemptNumber - 1],
                    requestCancellationToken);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested &&
                                                 requestTimeoutSource.IsCancellationRequested)
        {
            return AiCompletionResult.Failed(
                "AgentRouter quá thời gian chờ; vui lòng thử lại sau ít phút.");
        }
        finally
        {
            if (gateAcquired)
            {
                AgentRouterRequestGate.Release();
            }
        }

        return AiCompletionResult.Failed(
            $"AgentRouter: {finalAttempt?.UserMessage ?? "không nhận được kết quả"}");
    }

    private static bool ShouldRetryAgentRouterAttempt(ProviderAttemptResult attempt)
    {
        if (attempt.DisableForSession)
        {
            return false;
        }

        return !attempt.UserMessage.Contains("vượt giới hạn", StringComparison.OrdinalIgnoreCase) &&
            !attempt.UserMessage.Contains("từ chối nội dung yêu cầu", StringComparison.OrdinalIgnoreCase);
    }

    private static AiProvider? GetConfiguredAgentRouter()
    {
        var apiKey = GetEnvironmentValue("AGENTROUTER_API_KEY");
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            return new AiProvider(
                "AgentRouter",
                "https://agentrouter.org/v1",
                // The former gpt-5.6-sol pool is exhausted. This model was
                // verified through the same AgentRouter key and endpoint.
                GetEnvironmentValue("AGENTROUTER_MODEL") ?? "deepseek-v4-flash",
                apiKey,
                ResolveCodexExecutablePath());
        }

        return null;
    }

    private static string? GetEnvironmentValue(string name)
    {
        var value = Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Process)
            ?? Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User)
            ?? Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Machine);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static string? ResolveCodexExecutablePath()
    {
        var configuredPath = GetEnvironmentValue("AGENTROUTER_CODEX_PATH");
        var trustedConfiguredPath = GetTrustedCodexExecutablePath(configuredPath);
        if (trustedConfiguredPath is not null)
        {
            return trustedConfiguredPath;
        }

        var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var npmCodexPath = Path.Combine(
            appDataPath,
            "npm",
            "node_modules",
            "@openai",
            "codex",
            "node_modules",
            "@openai",
            "codex-win32-x64",
            "vendor",
            "x86_64-pc-windows-msvc",
            "bin",
            "codex.exe");

        return GetTrustedCodexExecutablePath(npmCodexPath);
    }

    private static string? GetTrustedCodexExecutablePath(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath)
            || !Path.IsPathFullyQualified(executablePath)
            || !string.Equals(Path.GetExtension(executablePath), ".exe", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            var fullPath = Path.GetFullPath(executablePath);
            return File.Exists(fullPath) &&
                   string.Equals(
                       Path.GetFileName(fullPath),
                       "codex.exe",
                       StringComparison.OrdinalIgnoreCase)
                ? fullPath
                : null;
        }
        catch (Exception exception) when (exception is ArgumentException
            or NotSupportedException
            or PathTooLongException)
        {
            return null;
        }
    }

    private static async Task<ProviderAttemptResult> TryCompleteWithCodexAsync(
        AiProvider provider,
        string systemPrompt,
        string userPrompt,
        CancellationToken cancellationToken)
    {
        var executablePath = GetTrustedCodexExecutablePath(provider.ExecutablePath);
        if (executablePath is null)
        {
            return ProviderAttemptResult.Failed(
                "không tìm thấy Codex CLI tin cậy (hãy đặt AGENTROUTER_CODEX_PATH tới codex.exe)",
                disableForSession: true);
        }

        string? temporaryRoot = null;
        Task<string>? standardOutputTask = null;
        Task<string>? standardErrorTask = null;
        var processStarted = false;
        using var process = new Process();
        try
        {
            temporaryRoot = Directory
                .CreateTempSubdirectory("AutomationPlatform-AgentRouter-")
                .FullName;
            var workingDirectory = Directory.CreateDirectory(
                Path.Combine(temporaryRoot, "workspace")).FullName;
            var codexHome = Directory.CreateDirectory(
                Path.Combine(temporaryRoot, "codex-home")).FullName;

            var startInfo = CreateCodexStartInfo(
                executablePath,
                workingDirectory,
                codexHome,
                provider);
            process.StartInfo = startInfo;

            if (!process.Start())
            {
                return ProviderAttemptResult.Failed(
                    "không khởi động được Codex CLI",
                    disableForSession: true);
            }
            processStarted = true;

            standardOutputTask = ReadBoundedAsync(
                process.StandardOutput,
                MaxCodexStandardOutputChars);
            standardErrorTask = ReadBoundedAsync(
                process.StandardError,
                MaxCodexStandardErrorChars);
            using var timeoutSource = new CancellationTokenSource(CodexTimeout);
            using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                timeoutSource.Token);

            await process.StandardInput.WriteAsync(
                BuildCodexPrompt(systemPrompt, userPrompt).AsMemory(),
                linkedSource.Token);
            await process.StandardInput.FlushAsync(linkedSource.Token);
            process.StandardInput.Close();

            await process.WaitForExitAsync(linkedSource.Token);

            // A descendant can keep an inherited pipe handle open after the CLI
            // exits.  Never await the stream readers indefinitely in that case;
            // the finally block below will perform bounded cleanup instead.
            var standardOutputResultTask = ReadWithDeadlineAsync(
                standardOutputTask,
                cancellationToken);
            var standardErrorResultTask = ReadWithDeadlineAsync(
                standardErrorTask,
                cancellationToken);
            var standardOutputResult = await standardOutputResultTask;
            var standardErrorResult = await standardErrorResultTask;
            if (!standardOutputResult.Completed || !standardErrorResult.Completed)
            {
                return ProviderAttemptResult.Failed(
                    "Codex CLI không đóng luồng phản hồi đúng hạn",
                    disableForSession: true);
            }

            var standardOutput = standardOutputResult.Content;
            var standardError = standardErrorResult.Content;
            if (process.ExitCode != 0)
            {
                return CreateCodexFailure(process.ExitCode, standardOutput, standardError);
            }

            var content = ExtractCodexAnswer(standardOutput);
            return string.IsNullOrWhiteSpace(content)
                ? ProviderAttemptResult.Failed(
                    "Codex CLI không trả về nội dung",
                    disableForSession: false)
                : ProviderAttemptResult.Succeeded(content);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ProviderAttemptResult.Failed(
                "Codex CLI quá thời gian chờ",
                disableForSession: false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Win32Exception)
        {
            return ProviderAttemptResult.Failed(
                "không tìm thấy Codex CLI (hãy đặt AGENTROUTER_CODEX_PATH)",
                disableForSession: true);
        }
        catch (IOException)
        {
            return ProviderAttemptResult.Failed(
                "lỗi giao tiếp với Codex CLI",
                disableForSession: false);
        }
        catch (InvalidOperationException)
        {
            return ProviderAttemptResult.Failed(
                "Codex CLI không thể khởi động",
                disableForSession: true);
        }
        finally
        {
            await StopAndDrainProcessAsync(
                process,
                processStarted,
                standardOutputTask,
                standardErrorTask);
            TryDeleteTemporaryDirectory(temporaryRoot);
        }
    }

    private static ProcessStartInfo CreateCodexStartInfo(
        string executablePath,
        string workingDirectory,
        string codexHome,
        AiProvider provider)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // Codex reads the '-' prompt strictly as UTF-8.  The Windows ANSI
            // code page can corrupt Vietnamese text (and other Unicode content)
            // before it reaches the child process, causing "invalid UTF-8".
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardOutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardErrorEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
        };

        AddCodexArgument(startInfo, "exec");
        AddCodexArgument(startInfo, "--ephemeral");
        AddCodexArgument(startInfo, "--ignore-user-config");
        AddCodexArgument(startInfo, "--ignore-rules");
        AddCodexArgument(startInfo, "--skip-git-repo-check");
        AddCodexArgument(startInfo, "--sandbox", "read-only");
        AddCodexArgument(startInfo, "--model", provider.Model);
        AddCodexConfig(startInfo, "approval_policy=\"never\"");
        AddCodexConfig(startInfo, "history.persistence=\"none\"");
        AddCodexConfig(startInfo, "feedback.enabled=false");
        AddCodexConfig(startInfo, "project_root_markers=[]");
        AddCodexConfig(startInfo, "shell_environment_policy.inherit=\"none\"");
        AddCodexConfig(startInfo, "model_provider=\"agentrouter\"");
        AddCodexConfig(startInfo, "model_providers.agentrouter.name=\"AgentRouter\"");
        AddCodexConfig(startInfo, $"model_providers.agentrouter.base_url=\"{provider.ApiUrl}\"");
        AddCodexConfig(startInfo, "model_providers.agentrouter.env_key=\"AGENTROUTER_API_KEY\"");
        AddCodexConfig(startInfo, "model_providers.agentrouter.wire_api=\"responses\"");
        // Let the Codex transport absorb one short upstream glitch.  The outer
        // attempt loop below provides the final bounded retry if the child exits.
        AddCodexConfig(startInfo, "model_providers.agentrouter.request_max_retries=1");
        AddCodexConfig(startInfo, "model_providers.agentrouter.stream_max_retries=1");
        AddCodexConfig(startInfo, "features.apps=false");
        AddCodexConfig(startInfo, "features.auth_elicitation=false");
        AddCodexConfig(startInfo, "features.browser_use=false");
        AddCodexConfig(startInfo, "features.browser_use_external=false");
        AddCodexConfig(startInfo, "features.computer_use=false");
        AddCodexConfig(startInfo, "features.goals=false");
        AddCodexConfig(startInfo, "features.hooks=false");
        AddCodexConfig(startInfo, "features.image_generation=false");
        AddCodexConfig(startInfo, "features.in_app_browser=false");
        AddCodexConfig(startInfo, "features.multi_agent=false");
        AddCodexConfig(startInfo, "features.plugins=false");
        AddCodexConfig(startInfo, "features.remote_plugin=false");
        AddCodexConfig(startInfo, "features.shell_snapshot=false");
        AddCodexConfig(startInfo, "features.shell_tool=false");
        AddCodexConfig(startInfo, "features.skill_mcp_dependency_install=false");
        AddCodexConfig(startInfo, "features.skill_search=false");
        AddCodexConfig(startInfo, "features.tool_call_mcp_elicitation=false");
        AddCodexConfig(startInfo, "features.workspace_dependencies=false");
        AddCodexConfig(startInfo, "tools.view_image=false");
        AddCodexConfig(startInfo, "web_search=\"disabled\"");
        AddCodexArgument(startInfo, "--json");
        AddCodexArgument(startInfo, "-");

        ConfigureCodexEnvironment(startInfo, codexHome, provider.ApiKey);
        return startInfo;
    }

    private static void ConfigureCodexEnvironment(
        ProcessStartInfo startInfo,
        string codexHome,
        string apiKey)
    {
        var allowedEnvironmentNames = new[]
        {
            "APPDATA",
            "COMSPEC",
            "HOMEDRIVE",
            "HOMEPATH",
            "LOCALAPPDATA",
            "PATH",
            "PATHEXT",
            "PROGRAMDATA",
            "SYSTEMDRIVE",
            "SYSTEMROOT",
            "TEMP",
            "TMP",
            "USERPROFILE",
            "WINDIR"
        };

        var allowedValues = allowedEnvironmentNames
            .Select(name => (Name: name, Value: Environment.GetEnvironmentVariable(name)))
            .Where(item => !string.IsNullOrWhiteSpace(item.Value))
            .ToArray();

        startInfo.Environment.Clear();
        foreach (var (name, value) in allowedValues)
        {
            startInfo.Environment[name] = value!;
        }

        startInfo.Environment["AGENTROUTER_API_KEY"] = apiKey;
        startInfo.Environment["CODEX_HOME"] = codexHome;
        startInfo.Environment["NO_COLOR"] = "1";
    }

    private static void AddCodexArgument(ProcessStartInfo startInfo, params string[] arguments)
    {
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
    }

    private static void AddCodexConfig(ProcessStartInfo startInfo, string configValue)
    {
        AddCodexArgument(startInfo, "--config", configValue);
    }

    private static async Task<string> ReadBoundedAsync(
        TextReader reader,
        int maximumCharacters)
    {
        var result = new StringBuilder(Math.Min(maximumCharacters, 16_384));
        var buffer = new char[8_192];
        while (await reader.ReadAsync(buffer.AsMemory()) is var charactersRead
            && charactersRead > 0)
        {
            var remainingCapacity = maximumCharacters - result.Length;
            if (remainingCapacity > 0)
            {
                result.Append(buffer, 0, Math.Min(charactersRead, remainingCapacity));
            }
        }

        return result.ToString();
    }

    private static async Task<(bool Completed, string Content)> ReadWithDeadlineAsync(
        Task<string> readTask,
        CancellationToken cancellationToken)
    {
        var deadlineTask = Task.Delay(CodexReaderDrainTimeout, cancellationToken);
        var completedTask = await Task.WhenAny(readTask, deadlineTask);
        if (completedTask == readTask)
        {
            return (true, await readTask);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return (false, string.Empty);
    }

    private static string BuildCodexPrompt(string systemPrompt, string userPrompt)
    {
        return $$"""
            Act only as a text-completion backend for another application.
            Do not use tools, inspect files, browse, or run commands.
            Follow the system instructions and answer the user request directly.

            <system_instructions>
            {{systemPrompt}}
            </system_instructions>

            <user_request>
            {{userPrompt}}
            </user_request>
            """;
    }

    private static string? ExtractCodexAnswer(string jsonLines)
    {
        string? lastAnswer = null;
        using var reader = new StringReader(jsonLines);
        while (reader.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (root.ValueKind == JsonValueKind.Object
                    && root.TryGetProperty("type", out var eventType)
                    && eventType.ValueKind == JsonValueKind.String
                    && eventType.GetString() == "item.completed"
                    && root.TryGetProperty("item", out var item)
                    && item.ValueKind == JsonValueKind.Object
                    && item.TryGetProperty("type", out var itemType)
                    && itemType.ValueKind == JsonValueKind.String
                    && itemType.GetString() == "agent_message"
                    && item.TryGetProperty("text", out var textElement)
                    && textElement.ValueKind == JsonValueKind.String)
                {
                    var answer = textElement.GetString()?.Trim();
                    if (!string.IsNullOrWhiteSpace(answer))
                    {
                        lastAnswer = answer;
                    }
                }
            }
            catch (Exception exception) when (exception is JsonException or InvalidOperationException)
            {
                // Codex may emit diagnostics outside the JSONL event stream; ignore those lines.
            }
        }

        return lastAnswer;
    }

    private static ProviderAttemptResult CreateCodexFailure(
        int exitCode,
        string standardOutput,
        string standardError)
    {
        var diagnostics = $"{standardOutput}\n{standardError}";
        var detail = ExtractStructuredCodexFailureDetail(standardOutput, standardError)
            ?? ExtractSafePlainTextDiagnostic(standardError);
        if (diagnostics.Contains("401", StringComparison.OrdinalIgnoreCase)
            || diagnostics.Contains("unauthorized", StringComparison.OrdinalIgnoreCase)
            || diagnostics.Contains("unauthenticated", StringComparison.OrdinalIgnoreCase))
        {
            return ProviderAttemptResult.Failed(
                "khóa AgentRouter không hợp lệ hoặc chưa được cấp quyền (HTTP 401)",
                disableForSession: true);
        }

        if (diagnostics.Contains("402", StringComparison.OrdinalIgnoreCase)
            || diagnostics.Contains("429", StringComparison.OrdinalIgnoreCase)
            || diagnostics.Contains("insufficient", StringComparison.OrdinalIgnoreCase)
            || diagnostics.Contains("quota", StringComparison.OrdinalIgnoreCase))
        {
            return ProviderAttemptResult.Failed(
                "AgentRouter hết quota hoặc đang giới hạn truy cập",
                disableForSession: true);
        }

        if (diagnostics.Contains("context_length", StringComparison.OrdinalIgnoreCase)
            || diagnostics.Contains("too long", StringComparison.OrdinalIgnoreCase)
            || diagnostics.Contains("maximum context", StringComparison.OrdinalIgnoreCase))
        {
            return ProviderAttemptResult.Failed(
                "yêu cầu vượt giới hạn nội dung của AgentRouter",
                disableForSession: false);
        }

        if (diagnostics.Contains("content-blocked", StringComparison.OrdinalIgnoreCase))
        {
            return ProviderAttemptResult.Failed(
                "AgentRouter từ chối nội dung yêu cầu; không thử lại tự động",
                disableForSession: false);
        }

        if (!string.IsNullOrWhiteSpace(detail))
        {
            return ProviderAttemptResult.Failed(
                $"AgentRouter báo: {detail}",
                disableForSession: false);
        }

        return ProviderAttemptResult.Failed(
            $"Codex CLI kết thúc với mã lỗi {exitCode} (không có chẩn đoán an toàn)",
            disableForSession: false);
    }

    private static string? ExtractStructuredCodexFailureDetail(
        params string[] diagnosticStreams)
    {
        string? lastDetail = null;
        foreach (var diagnosticStream in diagnosticStreams)
        {
            using var reader = new StringReader(diagnosticStream);
            while (reader.ReadLine() is { } line)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                try
                {
                    using var document = JsonDocument.Parse(line);
                    var root = document.RootElement;
                    if (root.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    var type = root.TryGetProperty("type", out var typeElement)
                        && typeElement.ValueKind == JsonValueKind.String
                        ? typeElement.GetString()
                        : null;
                    if (string.Equals(type, "turn.failed", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(type, "error", StringComparison.OrdinalIgnoreCase))
                    {
                        lastDetail = ReadCodexErrorMessage(root) ?? lastDetail;
                    }

                    if (root.TryGetProperty("item", out var item)
                        && item.ValueKind == JsonValueKind.Object
                        && item.TryGetProperty("type", out var itemType)
                        && itemType.ValueKind == JsonValueKind.String
                        && string.Equals(itemType.GetString(), "error", StringComparison.OrdinalIgnoreCase))
                    {
                        lastDetail = ReadCodexErrorMessage(item) ?? lastDetail;
                    }
                }
                catch (JsonException)
                {
                    // Only structured Codex events are safe to surface to the UI.
                }
            }
        }

        return lastDetail;
    }

    private static string? ReadCodexErrorMessage(JsonElement element)
    {
        string? message = null;
        if (element.TryGetProperty("message", out var messageElement)
            && messageElement.ValueKind == JsonValueKind.String)
        {
            message = messageElement.GetString();
        }
        else if (element.TryGetProperty("error", out var errorElement))
        {
            message = errorElement.ValueKind == JsonValueKind.String
                ? errorElement.GetString()
                : errorElement.ValueKind == JsonValueKind.Object
                  && errorElement.TryGetProperty("message", out var nestedMessage)
                  && nestedMessage.ValueKind == JsonValueKind.String
                    ? nestedMessage.GetString()
                    : null;
        }

        if (string.IsNullOrWhiteSpace(message))
        {
            return null;
        }

        return RedactDiagnostic(message);
    }

    private static string? ExtractSafePlainTextDiagnostic(string standardError)
    {
        string? lastErrorLine = null;
        using var reader = new StringReader(standardError);
        while (reader.ReadLine() is { } line)
        {
            var normalized = NormalizeDiagnosticLine(line);
            if (string.IsNullOrWhiteSpace(normalized) || !LooksLikeCodexError(normalized))
            {
                continue;
            }

            lastErrorLine = RedactDiagnostic(normalized);
        }

        return lastErrorLine;
    }

    private static bool LooksLikeCodexError(string line) =>
        line.StartsWith("error", StringComparison.OrdinalIgnoreCase) ||
        line.StartsWith("fatal", StringComparison.OrdinalIgnoreCase) ||
        line.StartsWith("failed", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("unauthorized", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("forbidden", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("timeout", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("connection", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("http ", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("unexpected argument", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("invalid value", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("not found", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeDiagnosticLine(string line) => string.Join(
        " ",
        TerminalAnsiSequence.Replace(line, string.Empty)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string RedactDiagnostic(string diagnostic)
    {
        var normalized = NormalizeDiagnosticLine(diagnostic);
        var redacted = BearerDiagnosticValue.Replace(normalized, "Bearer [REDACTED]");
        redacted = SensitiveDiagnosticValue.Replace(redacted, "$1[REDACTED]");
        return redacted.Length <= 280 ? redacted : redacted[..280] + "…";
    }

    private static async Task StopAndDrainProcessAsync(
        Process process,
        bool processStarted,
        Task<string>? standardOutputTask,
        Task<string>? standardErrorTask)
    {
        if (!processStarted)
        {
            return;
        }

        try
        {
            process.StandardInput.Close();
        }
        catch
        {
            // The process may already have closed its input pipe.
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Best effort; still wait briefly for pipe closure below.
        }

        try
        {
            using var exitTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(exitTimeout.Token);
        }
        catch
        {
            // Never block the provider fallback indefinitely during cleanup.
        }

        var drainTasks = new[] { standardOutputTask, standardErrorTask }
            .Where(task => task is not null)
            .Cast<Task<string>>()
            .ToArray();
        if (drainTasks.Length == 0)
        {
            return;
        }

        try
        {
            var drainAllTask = Task.WhenAll(drainTasks);
            var completedTask = await Task.WhenAny(
                drainAllTask,
                Task.Delay(TimeSpan.FromSeconds(5)));
            if (completedTask == drainAllTask)
            {
                await drainAllTask;
            }
        }
        catch
        {
            // Diagnostics are optional once the provider attempt has completed.
        }
    }

    private static void TryDeleteTemporaryDirectory(string? temporaryRoot)
    {
        if (string.IsNullOrWhiteSpace(temporaryRoot))
        {
            return;
        }

        try
        {
            var fullTemporaryRoot = Path.GetFullPath(temporaryRoot);
            var systemTemporaryRoot = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(Path.GetTempPath()));
            var expectedPrefix = systemTemporaryRoot + Path.DirectorySeparatorChar;
            if (!fullTemporaryRoot.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(fullTemporaryRoot).StartsWith(
                    "AutomationPlatform-AgentRouter-",
                    StringComparison.Ordinal))
            {
                return;
            }

            Directory.Delete(fullTemporaryRoot, recursive: true);
        }
        catch
        {
            // A locked diagnostic file can be left for the OS temp cleaner.
        }
    }

    private sealed record AiProvider(
        string Name,
        string ApiUrl,
        string Model,
        string ApiKey,
        string? ExecutablePath)
    {
        public override string ToString() => $"{Name} ({Model})";
    }

    private sealed record ProviderAttemptResult(
        bool Success,
        string Content,
        string UserMessage,
        bool DisableForSession)
    {
        public static ProviderAttemptResult Succeeded(string content) =>
            new(true, content, string.Empty, false);

        public static ProviderAttemptResult Failed(string userMessage, bool disableForSession) =>
            new(false, string.Empty, userMessage, disableForSession);
    }
}
