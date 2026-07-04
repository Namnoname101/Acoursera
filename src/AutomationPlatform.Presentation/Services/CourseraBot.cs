using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Wpf;

namespace AutomationPlatform.Presentation.Services;

public class CourseraBot
{
    private readonly WebView2 _webView;
    // Khi khởi tạo Bot, truyền cái trình duyệt (WebView2) cho nó điều khiển
    public CourseraBot(WebView2 webView)
    {
        _webView = webView;
    }
    private async Task<string> RunJsAsync(string jsCode)
    {
        if (_webView.CoreWebView2 == null) return "Chưa tải web";
        try { return await _webView.ExecuteScriptAsync(jsCode); }
        catch (System.Exception ex) { return $"Lỗi: {ex.Message}"; }
    }

    public async Task<string> GetCourseTitleAsync()
    {
        return await RunJsAsync("document.querySelector('h1')?.innerText || 'Không thấy H1'");
    }

    public async Task<string> ClickNextButtonAsync()
    {
        return await RunJsAsync("document.querySelector('.next-btn')?.click(); 'Đã bấm'");
    }

}
