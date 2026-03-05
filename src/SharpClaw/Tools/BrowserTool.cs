using System.Text.Json;
using Microsoft.Playwright;
using SharpClaw.Browser;
using SharpClaw.Configuration;
using SharpClaw.LLM;
using SharpClaw.LLM.Models;

namespace SharpClaw.Tools;

/// <summary>
/// Agent tool that drives a persistent headless Chromium browser.
/// Supports visual interaction (screenshot + vision model analysis, click by coords),
/// DOM interaction (selectors, JS evaluation, text/html extraction), and tab management.
/// Browser state (cookies, localStorage, sessions) persists across restarts.
/// </summary>
public class BrowserTool : ITool, IDisposable
{
    private readonly BrowserService _browser;
    private readonly SharpClawConfig _config;
    private readonly LlamaCppClient _visionClient;

    public BrowserTool(BrowserService browser, SharpClawConfig config)
    {
        _browser = browser;
        _config = config;
        _visionClient = new LlamaCppClient(config.VisionEndpoint);
    }

    public string Name => "browser";

    public string Description =>
        "Control a persistent headless Chromium browser. Cookies and login sessions survive restarts.\n" +
        "Actions and their required params:\n" +
        "  navigate(url) - go to a URL\n" +
        "  screenshot(question?) - take screenshot, vision model describes it. Pass question for focused analysis\n" +
        "  click(selector | x+y) - click element by CSS selector OR x,y pixel coordinates\n" +
        "  type(text, selector?) - type keystrokes, optionally into a specific element\n" +
        "  fill(selector, text) - fill an input field (clears first)\n" +
        "  press_key(key) - press a key like Enter, Tab, Escape, Control+a\n" +
        "  select_option(selector, value) - choose from a <select> dropdown\n" +
        "  evaluate_js(expression) - run JavaScript and return the result\n" +
        "  get_text(selector?) - get visible text of page or element (truncated to 12K chars)\n" +
        "  get_html(selector?, outer?) - get innerHTML or outerHTML\n" +
        "  get_attribute(selector, attribute) - get an element attribute value\n" +
        "  query_all(selector, extract?) - extract data from all matching elements\n" +
        "  scroll(direction?, amount?) - scroll the page up or down\n" +
        "  scroll_to(selector) - scroll an element into view\n" +
        "  back / forward - browser history navigation\n" +
        "  wait(selector) - wait for element to appear\n" +
        "  wait_load(load_state?) - wait for page load state\n" +
        "  page_info - get current URL and title\n" +
        "  tabs / switch_tab(tab_index) / new_tab(url?) / close_tab - manage tabs\n" +
        "  get_cookies(url?) / set_cookie(name, value, url) - cookie management\n" +
        "Tips: Use simple CSS selectors for click (e.g. 'button.submit', '#login'). " +
        "For complex pages, use evaluate_js to extract structured data instead of get_text.";

    public JsonElement ParameterSchema => JsonSerializer.SerializeToElement(new
    {
        type = "object",
        required = new[] { "action" },
        properties = new
        {
            action = new
            {
                type = "string",
                description = "Action to perform",
                @enum = new[]
                {
                    "navigate", "screenshot", "click", "type", "fill", "press_key", "select_option",
                    "evaluate_js", "get_text", "get_html", "get_attribute", "query_all",
                    "scroll", "scroll_to", "back", "forward",
                    "wait", "wait_load", "page_info",
                    "tabs", "switch_tab", "new_tab", "close_tab",
                    "get_cookies", "set_cookie"
                }
            },
            url = new { type = "string", description = "URL (required for navigate, optional for new_tab/get_cookies)" },
            selector = new { type = "string", description = "CSS selector (required for fill, select_option, get_attribute, query_all, scroll_to, wait). Use simple selectors like '#id', '.class', 'tag'." },
            x = new { type = "number", description = "X pixel coordinate for click (use with y instead of selector)" },
            y = new { type = "number", description = "Y pixel coordinate for click (use with x instead of selector)" },
            text = new { type = "string", description = "Text to type or fill into an input" },
            key = new { type = "string", description = "Key name for press_key: Enter, Tab, Escape, Backspace, Control+a, etc." },
            value = new { type = "string", description = "Value for select_option or set_cookie" },
            expression = new { type = "string", description = "JavaScript expression for evaluate_js. Return value is serialized as JSON." },
            direction = new { type = "string", @enum = new[] { "up", "down" }, description = "Scroll direction (default: down)" },
            amount = new { type = "integer", description = "Scroll amount in pixels (default: 500)" },
            full_page = new { type = "boolean", description = "screenshot only: capture full page instead of viewport (default: false)" },
            question = new { type = "string", description = "screenshot only: ask a specific question about what is visible on the page" },
            tab_index = new { type = "integer", description = "Tab index for switch_tab (0-based, see tabs action)" },
            timeout_ms = new { type = "integer", description = "Timeout in ms for wait/wait_load (default: 10000)" },
            extract = new { type = "string", description = "query_all: what to extract from each element - textContent, innerText, innerHTML, href, src, value (default: textContent)" },
            attribute = new { type = "string", description = "Attribute name for get_attribute (e.g. 'href', 'src', 'class')" },
            outer = new { type = "boolean", description = "get_html: return outerHTML instead of innerHTML (default: false)" },
            name = new { type = "string", description = "Cookie name for set_cookie" },
            load_state = new { type = "string", @enum = new[] { "domcontentloaded", "load", "networkidle" }, description = "wait_load state (default: domcontentloaded)" },
            delay_ms = new { type = "integer", description = "Delay between keystrokes in ms for type action" }
        }
    });

    public async Task<string> ExecuteAsync(JsonElement args, CancellationToken ct = default)
    {
        var action = args.GetProperty("action").GetString()!;

        try
        {
            return action switch
            {
                "navigate" => await _browser.NavigateAsync(
                    Require(args, "url")),

                "screenshot" => await ScreenshotWithVisionAsync(args),

                "click" => await _browser.ClickAsync(
                    Str(args, "selector"), Float(args, "x"), Float(args, "y")),

                "type" => await _browser.TypeAsync(
                    Require(args, "text"), Str(args, "selector"), Int(args, "delay_ms")),

                "fill" => await _browser.FillAsync(
                    Require(args, "selector"), Require(args, "text")),

                "press_key" => await _browser.PressKeyAsync(
                    Require(args, "key")),

                "select_option" => await _browser.SelectOptionAsync(
                    Require(args, "selector"), Require(args, "value")),

                "evaluate_js" => await _browser.EvaluateAsync(
                    Require(args, "expression")),

                "get_text" => Str(args, "question") != null
                    ? "Error: 'question' only works with the 'screenshot' action (which uses a vision model). " +
                      "Use get_text for raw text, or screenshot with a question for intelligent analysis."
                    : await _browser.GetTextAsync(Str(args, "selector")),

                "get_html" => await _browser.GetHtmlAsync(
                    Str(args, "selector"), Bool(args, "outer") ?? false),

                "get_attribute" => await _browser.GetAttributeAsync(
                    Require(args, "selector"), Require(args, "attribute")),

                "query_all" => args.TryGetProperty("selector", out _)
                    ? await _browser.QuerySelectorAllAsync(
                        Require(args, "selector"), Str(args, "extract") ?? "textContent")
                    : "Error: query_all requires 'selector' (a CSS selector like 'a', '.post', 'tr td'). " +
                      "Example: {\"action\":\"query_all\",\"selector\":\".thread\",\"extract\":\"textContent\"}",

                "scroll" => await _browser.ScrollAsync(
                    Str(args, "direction") ?? "down", Int(args, "amount") ?? 500),

                "scroll_to" => await _browser.ScrollToElementAsync(
                    Require(args, "selector")),

                "back" => await _browser.GoBackAsync(),
                "forward" => await _browser.GoForwardAsync(),

                "wait" => await _browser.WaitForSelectorAsync(
                    Require(args, "selector"), Int(args, "timeout_ms") ?? 10_000),

                "wait_load" => await _browser.WaitForLoadStateAsync(
                    Str(args, "load_state") ?? "domcontentloaded"),

                "page_info" => await _browser.GetPageInfoAsync(),

                "tabs" => await FormatTabsAsync(),

                "switch_tab" => await _browser.SwitchTabAsync(
                    Int(args, "tab_index") ?? throw new ArgumentException("tab_index is required")),

                "new_tab" => await _browser.NewTabAsync(
                    Str(args, "url")),

                "close_tab" => await _browser.CloseTabAsync(),

                "get_cookies" => await _browser.GetCookiesAsync(
                    Str(args, "url")),

                "set_cookie" => await _browser.SetCookieAsync(
                    Require(args, "name"), Require(args, "value"), Require(args, "url")),

                _ => $"Unknown browser action: {action}. Use page_info to see current state."
            };
        }
        catch (PlaywrightException ex) when (ex.Message.Contains("Timeout"))
        {
            return $"Browser timeout ({action}): {ex.Message}\n" +
                   "Hint: Try a simpler CSS selector, use evaluate_js to find elements, or use click with x/y coordinates from a screenshot.";
        }
        catch (PlaywrightException ex)
        {
            return $"Browser error ({action}): {ex.Message}";
        }
        catch (TimeoutException ex)
        {
            return $"Browser timeout ({action}): {ex.Message}\n" +
                   "Hint: The page may still be loading. Try wait_load first, or use a different selector.";
        }
    }

    private async Task<string> ScreenshotWithVisionAsync(JsonElement args)
    {
        var fullPage = Bool(args, "full_page") ?? false;
        var question = Str(args, "question");

        var imageBytes = await _browser.ScreenshotAsync(fullPage);
        var base64 = Convert.ToBase64String(imageBytes);
        var pageInfo = await _browser.GetPageInfoAsync();

        var systemMsg = question != null
            ? $"You are analyzing a browser screenshot. Page info: {pageInfo}\n" +
              "Answer the user's specific question. Read all visible text carefully. " +
              "Report exact text, labels, URLs, and UI elements you can see."
            : $"You are analyzing a browser screenshot. Page info: {pageInfo}\n" +
              "Describe what you see: page layout, visible text and headings, navigation elements, " +
              "forms, buttons, links, images, and any notable content. Read all text carefully.";

        var userMsg = question ?? "Describe everything visible on this browser page.";

        var request = new ChatRequest
        {
            Model = _config.VisionModel,
            Messages =
            [
                Message.System(systemMsg),
                Message.UserWithImage(userMsg, base64)
            ],
            Stream = false
        };

        var response = await _visionClient.CompleteAsync(request);
        var description = response.Choices is { Count: > 0 }
            ? response.Choices[0].Message?.GetTextContent() ?? "(no description)"
            : "(vision model returned no response)";

        return $"[{pageInfo}]\n\n{description}";
    }

    private async Task<string> FormatTabsAsync()
    {
        var tabs = await _browser.ListTabsAsync();
        if (tabs.Count == 0) return "No tabs open.";

        var lines = tabs.Select(t =>
        {
            var marker = t.Active ? " *" : "";
            return $"  [{t.Index}]{marker} {t.Title}\n       {t.Url}";
        });
        return $"Open tabs ({tabs.Count}):\n{string.Join("\n", lines)}";
    }

    private static string Require(JsonElement el, string prop)
    {
        if (el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String)
        {
            var s = v.GetString();
            if (!string.IsNullOrEmpty(s)) return s;
        }
        throw new ArgumentException($"'{prop}' is required");
    }

    private static string? Str(JsonElement el, string prop)
    {
        if (el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String)
            return v.GetString();
        return null;
    }

    private static float? Float(JsonElement el, string prop)
    {
        if (el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number)
            return (float)v.GetDouble();
        return null;
    }

    private static int? Int(JsonElement el, string prop)
    {
        if (el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number)
            return v.GetInt32();
        return null;
    }

    private static bool? Bool(JsonElement el, string prop)
    {
        if (el.TryGetProperty(prop, out var v))
        {
            if (v.ValueKind == JsonValueKind.True) return true;
            if (v.ValueKind == JsonValueKind.False) return false;
        }
        return null;
    }

    public void Dispose()
    {
        _visionClient.Dispose();
        GC.SuppressFinalize(this);
    }
}
