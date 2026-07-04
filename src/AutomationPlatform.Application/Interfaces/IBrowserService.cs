namespace AutomationPlatform.Application.Interfaces;

/// <summary>
/// Trừu tượng hóa Playwright – cho phép mock và test
/// </summary>
public interface IBrowserService : IAsyncDisposable
{
    Task InitializeAsync(BrowserConfig config, CancellationToken ct = default);
    Task<IBrowserPage> NavigateToAsync(string url, CancellationToken ct = default);
    Task<string> GetPageContentAsync(CancellationToken ct = default);
    Task ClickSelectorAsync(string selector, CancellationToken ct = default);
    Task TypeTextAsync(string selector, string text, CancellationToken ct = default);
    Task<bool> IsElementVisibleAsync(string selector, int timeoutMs = 5000, CancellationToken ct = default);
}

public interface IBrowserPage : IAsyncDisposable
{
    string Url { get; }
    Task<string> TitleAsync();
    Task CloseAsync();
}

public sealed record BrowserConfig
{
    public bool Headless { get; init; } = true;
    public int LaunchTimeoutMs { get; init; } = 30000;
    public int ViewportWidth { get; init; } = 1366;
    public int ViewportHeight { get; init; } = 768;
    public string? CustomUserAgent { get; init; }
}
