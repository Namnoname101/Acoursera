using Microsoft.Playwright;
using AutomationPlatform.Application.Interfaces;

namespace AutomationPlatform.Infrastructure.Browser;

public sealed class PlaywrightService : IBrowserService
{
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private IPage? _page;
    private bool _disposed;

    public async Task InitializeAsync(BrowserConfig config, CancellationToken ct = default)
    {
        // Khởi tạo Playwright với chế độ headless theo config
        _playwright = await Playwright.CreateAsync();
        _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = config.Headless,
            Timeout = config.LaunchTimeoutMs,
            Args = new[] { "--disable-blink-features=AutomationControlled" } // Tránh bị phát hiện bot
        });

        var context = await _browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = config.ViewportWidth, Height = config.ViewportHeight },
            UserAgent = config.CustomUserAgent ?? "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36"
        });
        _page = await context.NewPageAsync();
    }

    public async Task<IBrowserPage> NavigateToAsync(string url, CancellationToken ct = default)
    {
        if (_page is null) throw new InvalidOperationException("Browser chưa được khởi tạo.");
        await _page.GotoAsync(url, new PageGotoOptions { Timeout = 30000, WaitUntil = WaitUntilState.NetworkIdle });
        return new PlaywrightPageWrapper(_page);
    }

    public async Task ClickSelectorAsync(string selector, CancellationToken ct = default)
    {
        if (_page is null) throw new InvalidOperationException("Browser chưa được khởi tạo.");
        await _page.ClickAsync(selector, new PageClickOptions { Timeout = 10000 });
    }

    public async Task TypeTextAsync(string selector, string text, CancellationToken ct = default)
    {
        if (_page is null) throw new InvalidOperationException("Browser chưa được khởi tạo.");
        await _page.FillAsync(selector, text, new PageFillOptions { Timeout = 10000 });
    }

    public async Task<bool> IsElementVisibleAsync(string selector, int timeoutMs = 5000, CancellationToken ct = default)
    {
        if (_page is null) return false;
        try
        {
            await _page.WaitForSelectorAsync(selector, new PageWaitForSelectorOptions { Timeout = timeoutMs, State = WaitForSelectorState.Visible });
            return true;
        }
        catch { return false; }
    }

    public async Task<string> GetPageContentAsync(CancellationToken ct = default)
    {
        if (_page is null) return string.Empty;
        return await _page.ContentAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _page?.CloseAsync().GetAwaiter().GetResult();
        _browser?.CloseAsync().GetAwaiter().GetResult();
        _playwright?.Dispose();
        _disposed = true;
        await Task.CompletedTask;
    }

    private sealed class PlaywrightPageWrapper : IBrowserPage
    {
        private readonly IPage _page;
        public string Url => _page.Url;
        public PlaywrightPageWrapper(IPage page) => _page = page;
        public async Task<string> TitleAsync() => await _page.TitleAsync();
        public async Task CloseAsync() => await _page.CloseAsync();
        public async ValueTask DisposeAsync() => await _page.CloseAsync();
    }
}
