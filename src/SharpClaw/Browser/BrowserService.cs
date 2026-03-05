using Microsoft.Playwright;
using SharpClaw.Configuration;

namespace SharpClaw.Browser;

public class BrowserService : IAsyncDisposable
{
    private readonly SharpClawConfig _config;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private IPlaywright? _playwright;
    private IBrowserContext? _context;
    private IPage? _activePage;

    public bool IsInitialized => _context != null;

    public BrowserService(SharpClawConfig config)
    {
        _config = config;
    }

    private async Task EnsureInitializedAsync()
    {
        if (_context != null) return;

        await _initLock.WaitAsync();
        try
        {
            if (_context != null) return;

            _playwright = await Playwright.CreateAsync();

            var opts = new BrowserTypeLaunchPersistentContextOptions
            {
                Headless = _config.Browser.Headless,
                ViewportSize = new ViewportSize
                {
                    Width = _config.Browser.ViewportWidth,
                    Height = _config.Browser.ViewportHeight
                },
                AcceptDownloads = true,
                IgnoreHTTPSErrors = true,
            };

            try
            {
                _context = await _playwright.Chromium.LaunchPersistentContextAsync(
                    _config.ResolvedBrowserDataDir, opts);
            }
            catch (PlaywrightException)
            {
                Console.Error.WriteLine("[browser] Chromium not found, installing...");
                var exitCode = Microsoft.Playwright.Program.Main(["install", "chromium"]);
                if (exitCode != 0)
                    throw new InvalidOperationException(
                        "Failed to install Chromium. Run manually: npx playwright install chromium");

                _context = await _playwright.Chromium.LaunchPersistentContextAsync(
                    _config.ResolvedBrowserDataDir, opts);
            }

            _activePage = _context.Pages.FirstOrDefault() ?? await _context.NewPageAsync();
            _context.Page += (_, page) => _activePage = page;

            Console.Error.WriteLine($"[browser] Chromium ready (persistent context: {_config.ResolvedBrowserDataDir})");
        }
        finally
        {
            _initLock.Release();
        }
    }

    public async Task<string> NavigateAsync(string url)
    {
        await EnsureInitializedAsync();
        var page = _activePage!;
        var response = await page.GotoAsync(url, new PageGotoOptions
        {
            WaitUntil = WaitUntilState.DOMContentLoaded,
            Timeout = 30_000
        });
        return $"Navigated to {page.Url} (status: {response?.Status})";
    }

    public async Task<byte[]> ScreenshotAsync(bool fullPage = false)
    {
        await EnsureInitializedAsync();
        return await _activePage!.ScreenshotAsync(new PageScreenshotOptions
        {
            FullPage = fullPage,
            Type = ScreenshotType.Png
        });
    }

    public async Task<string> ClickAsync(string? selector = null, float? x = null, float? y = null)
    {
        await EnsureInitializedAsync();
        var page = _activePage!;

        if (selector != null)
        {
            await page.ClickAsync(selector, new PageClickOptions { Timeout = 10_000 });
            return $"Clicked element: {selector}";
        }

        if (x != null && y != null)
        {
            await page.Mouse.ClickAsync(x.Value, y.Value);
            return $"Clicked at ({x}, {y})";
        }

        return "Error: provide either 'selector' or both 'x' and 'y' coordinates";
    }

    public async Task<string> FillAsync(string selector, string text)
    {
        await EnsureInitializedAsync();
        await _activePage!.FillAsync(selector, text, new PageFillOptions { Timeout = 10_000 });
        return $"Filled '{selector}' with {text.Length} chars";
    }

    public async Task<string> TypeAsync(string text, string? selector = null, int? delayMs = null)
    {
        await EnsureInitializedAsync();
        var page = _activePage!;

        if (selector != null)
        {
            await page.ClickAsync(selector, new PageClickOptions { Timeout = 10_000 });
        }

        await page.Keyboard.TypeAsync(text, new KeyboardTypeOptions
        {
            Delay = delayMs ?? 0
        });
        return $"Typed {text.Length} characters" + (selector != null ? $" into {selector}" : "");
    }

    public async Task<string> PressKeyAsync(string key)
    {
        await EnsureInitializedAsync();
        await _activePage!.Keyboard.PressAsync(key);
        return $"Pressed key: {key}";
    }

    public async Task<string> SelectOptionAsync(string selector, string value)
    {
        await EnsureInitializedAsync();
        var selected = await _activePage!.SelectOptionAsync(selector, value);
        return $"Selected option in '{selector}': {string.Join(", ", selected)}";
    }

    public async Task<string> EvaluateAsync(string expression)
    {
        await EnsureInitializedAsync();
        var result = await _activePage!.EvaluateAsync(expression);
        var str = result?.ToString() ?? "(null)";
        if (str.Length > 12_000)
            str = str[..12_000] + $"\n... (truncated, {str.Length} total chars)";
        return str;
    }

    public async Task<string> EvaluateHandleAsync(string expression)
    {
        await EnsureInitializedAsync();
        var handle = await _activePage!.EvaluateHandleAsync(expression);
        var json = await handle.JsonValueAsync<object>();
        var str = json?.ToString() ?? "(null)";
        await handle.DisposeAsync();
        if (str.Length > 12_000)
            str = str[..12_000] + $"\n... (truncated, {str.Length} total chars)";
        return str;
    }

    public async Task<string> GetTextAsync(string? selector = null)
    {
        await EnsureInitializedAsync();
        var page = _activePage!;

        string text;
        if (selector != null)
        {
            text = await page.InnerTextAsync(selector, new PageInnerTextOptions { Timeout = 10_000 });
        }
        else
        {
            text = await page.InnerTextAsync("body");
        }

        if (text.Length > 12_000)
            text = text[..12_000] + $"\n... (truncated, {text.Length} total chars)";
        return text;
    }

    public async Task<string> GetHtmlAsync(string? selector = null, bool outer = false)
    {
        await EnsureInitializedAsync();
        var page = _activePage!;

        string html;
        if (selector != null)
        {
            if (outer)
            {
                html = await page.EvaluateAsync<string>(
                    $"s => document.querySelector(s)?.outerHTML ?? '(not found)'",
                    selector);
            }
            else
            {
                html = await page.InnerHTMLAsync(selector, new PageInnerHTMLOptions { Timeout = 10_000 });
            }
        }
        else
        {
            html = await page.ContentAsync();
        }

        if (html.Length > 16_000)
            html = html[..16_000] + $"\n... (truncated, {html.Length} total chars)";
        return html;
    }

    public async Task<string> GetAttributeAsync(string selector, string attribute)
    {
        await EnsureInitializedAsync();
        var value = await _activePage!.GetAttributeAsync(selector, attribute,
            new PageGetAttributeOptions { Timeout = 10_000 });
        return value ?? "(null)";
    }

    public async Task<string> QuerySelectorAllAsync(string selector, string extract = "textContent")
    {
        await EnsureInitializedAsync();
        var elements = await _activePage!.QuerySelectorAllAsync(selector);

        var results = new List<string>();
        for (int i = 0; i < Math.Min(elements.Count, 100); i++)
        {
            var el = elements[i];
            var value = extract switch
            {
                "textContent" => await el.TextContentAsync() ?? "",
                "innerText" => await el.InnerTextAsync(),
                "innerHTML" => await el.InnerHTMLAsync(),
                "href" => await el.GetAttributeAsync("href") ?? "",
                "src" => await el.GetAttributeAsync("src") ?? "",
                "value" => await el.GetAttributeAsync("value") ?? "",
                _ => await el.GetAttributeAsync(extract) ?? ""
            };
            results.Add($"[{i}] {value.Trim()}");
        }

        var summary = $"Found {elements.Count} elements matching '{selector}'";
        if (elements.Count > 100)
            summary += " (showing first 100)";
        return $"{summary}:\n{string.Join("\n", results)}";
    }

    public async Task<string> ScrollAsync(string direction = "down", int amount = 500)
    {
        await EnsureInitializedAsync();
        var delta = direction == "up" ? -amount : amount;
        await _activePage!.Mouse.WheelAsync(0, delta);
        return $"Scrolled {direction} by {amount}px";
    }

    public async Task<string> ScrollToElementAsync(string selector)
    {
        await EnsureInitializedAsync();
        var el = await _activePage!.QuerySelectorAsync(selector);
        if (el == null) return $"Element not found: {selector}";
        await el.ScrollIntoViewIfNeededAsync();
        return $"Scrolled to '{selector}'";
    }

    public async Task<string> GoBackAsync()
    {
        await EnsureInitializedAsync();
        await _activePage!.GoBackAsync(new PageGoBackOptions
        {
            WaitUntil = WaitUntilState.DOMContentLoaded
        });
        return $"Navigated back to {_activePage!.Url}";
    }

    public async Task<string> GoForwardAsync()
    {
        await EnsureInitializedAsync();
        await _activePage!.GoForwardAsync(new PageGoForwardOptions
        {
            WaitUntil = WaitUntilState.DOMContentLoaded
        });
        return $"Navigated forward to {_activePage!.Url}";
    }

    public async Task<string> WaitForSelectorAsync(string selector, int timeoutMs = 10_000)
    {
        await EnsureInitializedAsync();
        await _activePage!.WaitForSelectorAsync(selector, new PageWaitForSelectorOptions
        {
            Timeout = timeoutMs
        });
        return $"Element '{selector}' appeared";
    }

    public async Task<string> WaitForLoadStateAsync(string state = "domcontentloaded")
    {
        await EnsureInitializedAsync();
        var loadState = state switch
        {
            "load" => LoadState.Load,
            "networkidle" => LoadState.NetworkIdle,
            _ => LoadState.DOMContentLoaded
        };
        await _activePage!.WaitForLoadStateAsync(loadState);
        return $"Page reached state: {state}";
    }

    public async Task<string> GetPageInfoAsync()
    {
        await EnsureInitializedAsync();
        var page = _activePage!;
        var title = await page.TitleAsync();
        return $"URL: {page.Url}\nTitle: {title}";
    }

    public async Task<List<TabInfo>> ListTabsAsync()
    {
        await EnsureInitializedAsync();
        var tabs = new List<TabInfo>();
        var pages = _context!.Pages;
        for (int i = 0; i < pages.Count; i++)
        {
            var title = await pages[i].TitleAsync();
            tabs.Add(new TabInfo(i, pages[i].Url, title, pages[i] == _activePage));
        }
        return tabs;
    }

    public async Task<string> SwitchTabAsync(int index)
    {
        await EnsureInitializedAsync();
        var pages = _context!.Pages;
        if (index < 0 || index >= pages.Count)
            return $"Error: tab index {index} out of range (0-{pages.Count - 1})";

        _activePage = pages[index];
        await _activePage.BringToFrontAsync();
        var title = await _activePage.TitleAsync();
        return $"Switched to tab {index}: {_activePage.Url} ({title})";
    }

    public async Task<string> NewTabAsync(string? url = null)
    {
        await EnsureInitializedAsync();
        _activePage = await _context!.NewPageAsync();
        if (url != null)
        {
            await _activePage.GotoAsync(url, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded
            });
        }
        return $"Opened new tab: {_activePage.Url}";
    }

    public async Task<string> CloseTabAsync()
    {
        await EnsureInitializedAsync();
        if (_context!.Pages.Count <= 1)
            return "Cannot close the last tab";

        await _activePage!.CloseAsync();
        _activePage = _context.Pages.LastOrDefault();
        return $"Closed tab. Active tab: {_activePage?.Url}";
    }

    public async Task<string> SetCookieAsync(string name, string value, string url)
    {
        await EnsureInitializedAsync();
        await _context!.AddCookiesAsync([new Cookie
        {
            Name = name,
            Value = value,
            Url = url
        }]);
        return $"Cookie set: {name}";
    }

    public async Task<string> GetCookiesAsync(string? url = null)
    {
        await EnsureInitializedAsync();
        var cookies = url != null
            ? await _context!.CookiesAsync([url])
            : await _context!.CookiesAsync();

        if (cookies.Count == 0) return "No cookies.";

        var lines = cookies.Select(c => $"  {c.Name}={c.Value} (domain={c.Domain}, path={c.Path}, secure={c.Secure})");
        return $"Cookies ({cookies.Count}):\n{string.Join("\n", lines)}";
    }

    public async ValueTask DisposeAsync()
    {
        if (_context != null)
        {
            try { await _context.CloseAsync(); } catch { }
            _context = null;
        }
        _playwright?.Dispose();
        _playwright = null;
        _initLock.Dispose();
        GC.SuppressFinalize(this);
    }
}

public record TabInfo(int Index, string Url, string Title, bool Active);
