using BrimDeck.Core;

// A page's link is the one thing it can ask the host to do outside itself, so the text it writes is checked here: the
// scheme, the length, and how often the page has asked. These are the branches that decide whether the desktop browser
// starts, which is the last place a plugin could reach without a window open.
internal static class ExternalLinkTests
{
    internal static void Run(Action<string, bool> check)
    {
        // The addresses a reader would expect to land in a browser.
        check("Links: an absolute web address opens, and the host answers it exactly as written",
            ExternalLinks.Accept("https://example.com/docs") == ("https://example.com/docs", null));
        check("Links: http, a port and a path with a query are all still a web page",
            ExternalLinks.Accept("http://localhost:8080/a?b=1") is { Url: { Length: > 0 } }
            && ExternalLinks.Accept("https://example.com:8443/") is { Url: { Length: > 0 } });
        check("Links: the spaces an author leaves around a link are not part of it",
            ExternalLinks.Accept("  https://example.com  ") == ("https://example.com/", null));
        check("Links: the scheme is not case-sensitive, and neither is the host's writing of it",
            ExternalLinks.Accept("HTTPS://Example.COM/x") is { Url: { Length: > 0 } });

        // Everything that is not a web page: each one is a different way to start something other than a browser tab.
        string[] schemes = ["javascript:alert(1)", "data:text/html,<script>alert(1)</script>", "file:///C:/Windows/win.ini",
            "mailto:someone@example.com", "about:blank", "brimdeck-plugin:do-thing", "javascript%3aalert(1)"];
        bool allRefused = true, reasonSaysWhy = true;
        foreach (var bad in schemes)
        {
            var (url, reason) = ExternalLinks.Accept(bad);
            if (url is not null) allRefused = false;
            else if (reason is not { Length: > 0 }) reasonSaysWhy = false;
        }
        check("Links: no scheme but http/https opens anything, and each refusal says why", allRefused && reasonSaysWhy);
        check("Links: a relative path is refused — the host has no idea what it is relative to",
            ExternalLinks.Accept("page.html") is (null, { Length: > 0 }) && ExternalLinks.Accept("/settings") is (null, { Length: > 0 }));
        check("Links: nothing, spaces, and text that is not a URL at all are refused, not crashed on",
            ExternalLinks.Accept(null) is (null, { Length: > 0 }) && ExternalLinks.Accept("") is (null, { Length: > 0 })
            && ExternalLinks.Accept("   ") is (null, { Length: > 0 }) && ExternalLinks.Accept("example.com not a url") is (null, { Length: > 0 }));
        check("Links: a scheme with no host behind it is refused",
            ExternalLinks.Accept("https://") is (null, { Length: > 0 }));
        check("Links: a space or a control character inside the address is refused, because that is where one argument becomes two",
            ExternalLinks.Accept("https://example.com/a b") is (null, { Length: > 0 })
            && ExternalLinks.Accept("https://example.com/a\nb") is (null, { Length: > 0 })
            && ExternalLinks.Accept("https://example.com/a\tb") is (null, { Length: > 0 }));
        check("Links: past the ceiling it is a payload, not a link",
            ExternalLinks.Accept("https://example.com/" + new string('x', Bridge.MaximumUrlChars)) is (null, { Length: > 0 })
            && ExternalLinks.Accept("https://example.com/" + new string('x', 100)) is { Url: { Length: > 0 } });

        // A plugin's own virtual host exists only inside its own page — the folder mapping lives with the control, not
        // with the desktop — so the address the browser would be handed resolves to nothing there.
        const string own = "demo-1234abcd.plugin.brimdeck";
        var selfRefusal = ExternalLinks.Accept("https://" + own + "/page.html", own);
        check("Links: the asking plugin's own address is not handed to the browser, with a reason of its own",
            selfRefusal.Url is null && selfRefusal.Reason is { Length: > 0 });
        check("Links: the host is matched the way a host is matched — not by case",
            ExternalLinks.Accept("https://" + own.ToUpperInvariant() + "/x", own) is (null, { Length: > 0 }));
        check("Links: another plugin's address, or any other address, is not this refusal",
            ExternalLinks.Accept("https://other-9999ffff.plugin.brimdeck/x", own) is { Url: { Length: > 0 } }
            && ExternalLinks.Accept("https://example.com/x", own) is { Url: { Length: > 0 } });
        check("Links: without a plugin to ask for, the same address is a plain web page",
            ExternalLinks.Accept("https://" + own + "/page.html") is { Url: { Length: > 0 } });

        // The rate a page with no click behind it is held to: the count is by wall clock the caller brings.
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var budget = new LinkBudget(3, TimeSpan.FromSeconds(10));
        bool first = budget.Allow(start) && budget.Allow(start + TimeSpan.FromSeconds(1)) && budget.Allow(start + TimeSpan.FromSeconds(2));
        check("Links: the budget lets a plugin ask the set number of times",
            first && !budget.Allow(start + TimeSpan.FromSeconds(3)) && !budget.Allow(start + TimeSpan.FromSeconds(4)));
        check("Links: the oldest ask leaves the window, so room comes back as time passes",
            budget.Allow(start + TimeSpan.FromSeconds(11)) && !budget.Allow(start + TimeSpan.FromSeconds(11)));
        check("Links: a long silence empties the budget entirely",
            budget.Allow(start + TimeSpan.FromMinutes(5)) && budget.Allow(start + TimeSpan.FromMinutes(5) + TimeSpan.FromMilliseconds(1))
            && budget.Allow(start + TimeSpan.FromMinutes(5) + TimeSpan.FromMilliseconds(2)));
    }
}
