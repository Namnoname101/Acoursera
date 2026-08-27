using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

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
    private const int MaxCodexStandardOutputChars = 2_000_000;
    private const int MaxCodexStandardErrorChars = 256_000;

    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(60)
    };

    private readonly HashSet<string> _disabledProviders = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _disabledProvidersLock = new();

    public async Task<AiCompletionResult> CompleteAsync(
        string systemPrompt,
        string userPrompt,
        double temperature,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var providers = GetConfiguredProviders();
        if (providers.Count == 0)
        {
            return AiCompletionResult.Failed(
                "Chưa cấu hình khóa AI. Hãy đặt AGENTROUTER_API_KEY, GROQ_API_KEY " +
                "hoặc GEMINI_API_KEY (DEEPSEEK_API_KEY là tùy chọn) rồi thử lại.");
        }

        var failures = new List<string>();
        for (var index = 0; index < providers.Count; index++)
        {
            var provider = providers[index];
            if (IsDisabled(provider.Name))
            {
                failures.Add($"{provider.Name}: đã tạm vô hiệu trong phiên này");
                continue;
            }

            progress?.Report($"🤖 Đang hỏi {provider.Name}...");
            var attempt = provider.Transport == AiProviderTransport.CodexCli
                ? await TryCompleteWithCodexAsync(
                    provider,
                    systemPrompt,
                    userPrompt,
                    cancellationToken)
                : await TryCompleteWithOpenAiHttpAsync(
                    provider,
                    systemPrompt,
                    userPrompt,
                    temperature,
                    cancellationToken);

            if (attempt.Success)
            {
                return AiCompletionResult.Succeeded(attempt.Content, provider.Name);
            }

            failures.Add($"{provider.Name}: {attempt.UserMessage}");
            if (attempt.DisableForSession)
            {
                Disable(provider.Name);
            }

            var nextProvider = providers
                .Skip(index + 1)
                .FirstOrDefault(candidate => !IsDisabled(candidate.Name));
            if (nextProvider is not null)
            {
                progress?.Report(
                    $"⚠️ {provider.Name} hết quota hoặc gặp lỗi; đang chuyển sang {nextProvider.Name}...");
            }
        }

        return AiCompletionResult.Failed(
            "Không provider AI nào trả lời được. " + string.Join("; ", failures));
    }

    private static List<AiProvider> GetConfiguredProviders()
    {
        var providers = new List<AiProvider>();

        AddProviderIfConfigured(
            providers,
            name: "AgentRouter",
            apiUrl: "https://agentrouter.org/v1",
            model: GetEnvironmentValue("AGENTROUTER_MODEL") ?? "gpt-5.6-sol",
            apiKey: GetEnvironmentValue("AGENTROUTER_API_KEY"),
            supportsTemperature: false,
            disableThinking: false,
            transport: AiProviderTransport.CodexCli,
            executablePath: ResolveCodexExecutablePath());

        AddProviderIfConfigured(
            providers,
            name: "DeepSeek",
            apiUrl: "https://api.deepseek.com/chat/completions",
            model: GetEnvironmentValue("DEEPSEEK_MODEL") ?? "deepseek-v4-flash",
            apiKey: GetEnvironmentValue("DEEPSEEK_API_KEY"),
            supportsTemperature: true,
            disableThinking: true);

        AddProviderIfConfigured(
            providers,
            name: "Groq Free",
            apiUrl: "https://api.groq.com/openai/v1/chat/completions",
            model: GetEnvironmentValue("GROQ_MODEL") ?? "openai/gpt-oss-120b",
            apiKey: GetEnvironmentValue("GROQ_API_KEY"),
            supportsTemperature: true,
            disableThinking: false);

        AddProviderIfConfigured(
            providers,
            name: "Gemini Free",
            apiUrl: "https://generativelanguage.googleapis.com/v1beta/openai/chat/completions",
            model: GetEnvironmentValue("GEMINI_MODEL") ?? "gemini-3.7-flash",
            apiKey: GetEnvironmentValue("GEMINI_API_KEY") ?? GetEnvironmentValue("GOOGLE_API_KEY"),
            supportsTemperature: false,
            disableThinking: false);

        return providers;
    }

    private static void AddProviderIfConfigured(
        ICollection<AiProvider> providers,
        string name,
        string apiUrl,
        string model,
        string? apiKey,
        bool supportsTemperature,
        bool disableThinking,
        AiProviderTransport transport = AiProviderTransport.OpenAiChatCompletions,
        string? executablePath = null)
    {
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            providers.Add(new AiProvider(
                name,
                apiUrl,
                model,
                apiKey,
                supportsTemperature,
                disableThinking,
                transport,
                executablePath));
        }
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
            return File.Exists(fullPath) ? fullPath : null;
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

            var standardOutput = await standardOutputTask;
            var standardError = await standardErrorTask;
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
            RedirectStandardError = true
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
        AddCodexConfig(startInfo, "model_providers.agentrouter.request_max_retries=0");
        AddCodexConfig(startInfo, "model_providers.agentrouter.stream_max_retries=0");
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

        return ProviderAttemptResult.Failed(
            $"Codex CLI kết thúc với mã lỗi {exitCode}",
            disableForSession: false);
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

    private static async Task<ProviderAttemptResult> TryCompleteWithOpenAiHttpAsync(
        AiProvider provider,
        string systemPrompt,
        string userPrompt,
        double temperature,
        CancellationToken cancellationToken)
    {
        var requestBody = new Dictionary<string, object?>
        {
            ["model"] = provider.Model,
            ["messages"] = new[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userPrompt }
            }
        };

        if (provider.SupportsTemperature)
        {
            requestBody["temperature"] = temperature;
        }

        if (provider.DisableThinking)
        {
            requestBody["thinking"] = new { type = "disabled" };
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, provider.ApiUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", provider.ApiKey);
            request.Content = new StringContent(
                JsonSerializer.Serialize(requestBody),
                Encoding.UTF8,
                "application/json");

            using var response = await HttpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return CreateHttpFailure(response.StatusCode);
            }

            var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
            using var jsonDocument = JsonDocument.Parse(responseJson);
            if (!jsonDocument.RootElement.TryGetProperty("choices", out var choices)
                || choices.ValueKind != JsonValueKind.Array
                || choices.GetArrayLength() == 0
                || !choices[0].TryGetProperty("message", out var message)
                || !message.TryGetProperty("content", out var contentElement))
            {
                return ProviderAttemptResult.Failed(
                    "phản hồi không đúng định dạng",
                    disableForSession: false);
            }

            var content = contentElement.GetString()?.Trim();
            if (string.IsNullOrWhiteSpace(content))
            {
                return ProviderAttemptResult.Failed(
                    "phản hồi rỗng",
                    disableForSession: false);
            }

            return ProviderAttemptResult.Succeeded(content);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ProviderAttemptResult.Failed("quá thời gian chờ", disableForSession: false);
        }
        catch (HttpRequestException)
        {
            return ProviderAttemptResult.Failed("lỗi kết nối", disableForSession: false);
        }
        catch (JsonException)
        {
            return ProviderAttemptResult.Failed("phản hồi JSON không hợp lệ", disableForSession: false);
        }
        catch (Exception)
        {
            return ProviderAttemptResult.Failed("lỗi không xác định", disableForSession: false);
        }
    }

    private static ProviderAttemptResult CreateHttpFailure(HttpStatusCode statusCode)
    {
        var code = (int)statusCode;
        return statusCode switch
        {
            HttpStatusCode.PaymentRequired => ProviderAttemptResult.Failed(
                "hết số dư (HTTP 402)", disableForSession: true),
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => ProviderAttemptResult.Failed(
                $"khóa API không hợp lệ hoặc không có quyền (HTTP {code})", disableForSession: true),
            HttpStatusCode.TooManyRequests => ProviderAttemptResult.Failed(
                "đã chạm giới hạn miễn phí (HTTP 429)", disableForSession: true),
            HttpStatusCode.RequestTimeout => ProviderAttemptResult.Failed(
                "provider quá thời gian chờ (HTTP 408)", disableForSession: false),
            _ when code >= 500 => ProviderAttemptResult.Failed(
                $"dịch vụ tạm lỗi (HTTP {code})", disableForSession: false),
            _ => ProviderAttemptResult.Failed(
                $"yêu cầu bị từ chối (HTTP {code})", disableForSession: false)
        };
    }

    private bool IsDisabled(string providerName)
    {
        lock (_disabledProvidersLock)
        {
            return _disabledProviders.Contains(providerName);
        }
    }

    private void Disable(string providerName)
    {
        lock (_disabledProvidersLock)
        {
            _disabledProviders.Add(providerName);
        }
    }

    private sealed record AiProvider(
        string Name,
        string ApiUrl,
        string Model,
        string ApiKey,
        bool SupportsTemperature,
        bool DisableThinking,
        AiProviderTransport Transport,
        string? ExecutablePath)
    {
        public override string ToString() => $"{Name} ({Model})";
    }

    private enum AiProviderTransport
    {
        OpenAiChatCompletions,
        CodexCli
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
