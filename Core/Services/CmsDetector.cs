using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;

namespace NetStuff.Services;

public class Detection
{
    public string Name { get; set; }
    public string Category { get; set; }
    public string Version { get; set; } = "";
    public int Confidence { get; set; }
    public List<string> Evidence { get; } = new();

    public string ConfidenceText => Confidence >= 100 ? "sicher" : Confidence >= 60 ? "wahrscheinlich" : "möglich";
    public string EvidenceText => string.Join("; ", Evidence.Distinct());
}

public class CmsResult
{
    public string FinalUrl { get; set; }
    public int StatusCode { get; set; }
    public List<Detection> Detections { get; } = new();
    public List<(string Slug, string Version)> WpPlugins { get; } = new();
    public List<(string Slug, string Version)> WpThemes { get; } = new();
    public List<string> Notes { get; } = new();
}

/// <summary>Erkennt CMS, Shopsysteme, Frameworks und Dienste anhand von HTML, Headern, Cookies und typischen Pfaden.</summary>
public static class CmsDetector
{
    private enum Src { Html, Header, Cookie, Meta, Probe }

    private record Rule(string Name, string Category, Src Source, string Pattern, int Weight, string Key = null, string Probe = null, string VersionPattern = null);

    private static readonly Rule[] Rules =
    {
        // CMS
        new("WordPress", "CMS", Src.Meta, @"WordPress\s*([\d.]+)?", 100, VersionPattern: @"WordPress\s*([\d.]+)"),
        new("WordPress", "CMS", Src.Html, @"/wp-(content|includes)/", 80),
        new("WordPress", "CMS", Src.Header, @"<[^>]+/wp-json/>", 100, Key: "Link"),
        new("WordPress", "CMS", Src.Probe, @"""namespaces""", 100, Probe: "/wp-json/"),
        new("WordPress", "CMS", Src.Html, @"ver=([\d.]+)['""][^>]*wp-includes|wp-includes/[^""']*\?ver=([\d.]+)", 20, VersionPattern: @"wp-includes/[^""']*\?ver=([\d.]+)"),
        new("Joomla", "CMS", Src.Meta, @"Joomla!?\s*-?\s*([\d.]+)?", 100, VersionPattern: @"Joomla!?\s*([\d.]+)"),
        new("Joomla", "CMS", Src.Html, @"/media/(jui|system)/js/|/components/com_", 70),
        new("Joomla", "CMS", Src.Probe, @"(?i)joomla", 70, Probe: "/administrator/"),
        new("Drupal", "CMS", Src.Meta, @"Drupal\s*([\d.]+)?", 100, VersionPattern: @"Drupal\s*([\d.]+)"),
        new("Drupal", "CMS", Src.Header, @"Drupal", 100, Key: "X-Generator"),
        new("Drupal", "CMS", Src.Header, @".", 80, Key: "X-Drupal-Cache"),
        new("Drupal", "CMS", Src.Html, @"drupal-settings-json|Drupal\.settings|/sites/default/files/", 70),
        new("TYPO3", "CMS", Src.Meta, @"TYPO3\s*(CMS)?\s*([\d.]+)?", 100, VersionPattern: @"TYPO3\s*(?:CMS\s*)?([\d.]+)"),
        new("TYPO3", "CMS", Src.Html, @"/typo3(conf|temp)/|/_assets/[0-9a-f]{32}/", 80),
        new("Contao", "CMS", Src.Meta, @"Contao", 100),
        new("Contao", "CMS", Src.Html, @"/assets/contao/|/system/modules/", 70),
        new("Neos", "CMS", Src.Header, @"Neos", 100, Key: "X-Flow-Powered"),
        new("Craft CMS", "CMS", Src.Header, @"Craft CMS", 100, Key: "X-Powered-By"),
        new("Ghost", "CMS", Src.Meta, @"Ghost\s*([\d.]+)?", 100, VersionPattern: @"Ghost\s*([\d.]+)"),
        new("Kirby", "CMS", Src.Html, @"/media/plugins/|kirby", 30),
        new("Umbraco", "CMS", Src.Html, @"/umbraco/", 60),
        new("Sitecore", "CMS", Src.Cookie, @"^SC_ANALYTICS_GLOBAL_COOKIE", 90),
        new("Adobe Experience Manager", "CMS", Src.Html, @"/etc\.clientlibs/|/content/dam/", 80),
        new("HubSpot CMS", "CMS", Src.Header, @".", 80, Key: "X-HS-Hub-Id"),
        new("Strapi", "CMS", Src.Header, @"Strapi", 100, Key: "X-Powered-By"),

        // Website-Baukästen
        new("Wix", "Baukasten", Src.Header, @".", 100, Key: "X-Wix-Request-Id"),
        new("Wix", "Baukasten", Src.Html, @"static\.wixstatic\.com|wix-bolt", 80),
        new("Squarespace", "Baukasten", Src.Html, @"static1\.squarespace\.com|Squarespace", 90),
        new("Jimdo", "Baukasten", Src.Html, @"jimdo(cdn|static)?\.com", 90),
        new("Webflow", "Baukasten", Src.Html, @"data-wf-(page|site)", 100),
        new("IONOS MyWebsite", "Baukasten", Src.Html, @"mywebsite-editor\.com|website-editor\.net", 90),
        new("Weebly", "Baukasten", Src.Html, @"editmysite\.com|weebly", 80),
        new("Framer", "Baukasten", Src.Html, @"framerusercontent\.com", 90),

        // Shops
        new("Shopware 6", "Shop", Src.Html, @"/bundles/storefront/|sw-plugin|data-csrf-mode", 90),
        new("Shopware 5", "Shop", Src.Html, @"/themes/Frontend/|shopware\.php", 80),
        new("Shopware", "Shop", Src.Probe, @"""version""", 60, Probe: "/api/_info/config"),
        new("WooCommerce", "Shop", Src.Html, @"/plugins/woocommerce/|woocommerce-(?:no-)?js|wc-block-|woocommerce_params", 90, VersionPattern: @"plugins/woocommerce/[^""']*\?ver=([\d.]+)"),
        new("Magento", "Shop", Src.Html, @"Mage\.Cookies|/static/version\d+/|mage/cookies", 90),
        new("Magento", "Shop", Src.Header, @".", 80, Key: "X-Magento-Cache-Debug"),
        new("PrestaShop", "Shop", Src.Meta, @"PrestaShop", 100),
        new("PrestaShop", "Shop", Src.Html, @"prestashop|/modules/ps_", 70),
        new("Shopify", "Shop", Src.Html, @"cdn\.shopify\.com|Shopify\.theme", 100),
        new("Shopify", "Shop", Src.Header, @".", 100, Key: "X-ShopId"),
        new("OXID eShop", "Shop", Src.Html, @"oxid|/out/flow/", 50),
        new("JTL-Shop", "Shop", Src.Meta, @"JTL", 90),
        new("Gambio", "Shop", Src.Html, @"gambio", 80),
        new("OpenCart", "Shop", Src.Html, @"catalog/view/theme|route=common/home", 80),
        new("Plentymarkets", "Shop", Src.Html, @"plenty", 50),

        // Frameworks
        new("Next.js", "Framework", Src.Html, @"__NEXT_DATA__|/_next/static/", 100),
        new("Nuxt", "Framework", Src.Html, @"__NUXT__|/_nuxt/", 100),
        new("Gatsby", "Framework", Src.Meta, @"Gatsby\s*([\d.]+)?", 100, VersionPattern: @"Gatsby\s*([\d.]+)"),
        new("Hugo", "Framework", Src.Meta, @"Hugo\s*([\d.]+)?", 100, VersionPattern: @"Hugo\s*([\d.]+)"),
        new("Jekyll", "Framework", Src.Meta, @"Jekyll", 100),
        new("Astro", "Framework", Src.Meta, @"Astro\s*v?([\d.]+)?", 100, VersionPattern: @"Astro\s*v?([\d.]+)"),
        new("Laravel", "Framework", Src.Cookie, @"^laravel_session", 100),
        new("Symfony", "Framework", Src.Header, @".", 60, Key: "X-Debug-Token"),
        new("ASP.NET", "Framework", Src.Header, @".", 90, Key: "X-AspNet-Version", VersionPattern: @"([\d.]+)"),
        new("ASP.NET", "Framework", Src.Cookie, @"^ASP\.NET_SessionId|^\.AspNetCore\.", 90),
        new("React", "JavaScript", Src.Html, @"data-reactroot|react(-dom)?(\.production)?(\.min)?\.js", 60),
        new("Vue.js", "JavaScript", Src.Html, @"data-v-[0-9a-f]{8}|vue(\.min)?\.js", 60),
        new("Angular", "JavaScript", Src.Html, @"ng-version=""([\d.]+)""", 100, VersionPattern: @"ng-version=""([\d.]+)"""),
        new("jQuery", "JavaScript", Src.Html, @"jquery[.-]?([\d.]+)?(\.min)?\.js", 90, VersionPattern: @"jquery[.-]([\d]+\.[\d.]+)(?:\.min)?\.js|jquery\.min\.js\?ver=([\d.]+)"),
        new("Bootstrap", "JavaScript", Src.Html, @"bootstrap(\.bundle)?(\.min)?\.(css|js)", 70),
        new("Elementor", "Page Builder", Src.Html, @"elementor", 90, VersionPattern: @"elementor/[^""']*\?ver=([\d.]+)"),
        new("Divi", "Page Builder", Src.Html, @"et_pb_|/themes/Divi/", 90),
        new("WPBakery", "Page Builder", Src.Html, @"js_composer|vc_row", 80),

        // Server / Sprache
        new("PHP", "Sprache", Src.Header, @"PHP/?([\d.]+)?", 100, Key: "X-Powered-By", VersionPattern: @"PHP/([\d.]+)"),
        new("PHP", "Sprache", Src.Cookie, @"^PHPSESSID", 80),
        new("nginx", "Webserver", Src.Header, @"nginx/?([\d.]+)?", 100, Key: "Server", VersionPattern: @"nginx/([\d.]+)"),
        new("Apache", "Webserver", Src.Header, @"Apache/?([\d.]+)?", 100, Key: "Server", VersionPattern: @"Apache/([\d.]+)"),
        new("LiteSpeed", "Webserver", Src.Header, @"LiteSpeed", 100, Key: "Server"),
        new("Microsoft IIS", "Webserver", Src.Header, @"IIS/?([\d.]+)?", 100, Key: "Server", VersionPattern: @"IIS/([\d.]+)"),
        new("Caddy", "Webserver", Src.Header, @"Caddy", 100, Key: "Server"),
        new("OpenResty", "Webserver", Src.Header, @"openresty", 100, Key: "Server"),
        new("Plesk", "Hosting", Src.Header, @"PleskLin|PleskWin", 100, Key: "X-Powered-By"),

        // CDN / Sicherheit
        new("Cloudflare", "CDN", Src.Header, @".", 100, Key: "CF-RAY"),
        new("Akamai", "CDN", Src.Header, @"AkamaiGHost", 100, Key: "Server"),
        new("Fastly", "CDN", Src.Header, @".", 90, Key: "X-Fastly-Request-ID"),
        new("Amazon CloudFront", "CDN", Src.Header, @"cloudfront", 100, Key: "Via"),
        new("Varnish", "Cache", Src.Header, @".", 80, Key: "X-Varnish"),
        new("Vercel", "Hosting", Src.Header, @".", 100, Key: "X-Vercel-Id"),
        new("Netlify", "Hosting", Src.Header, @".", 100, Key: "X-NF-Request-ID"),
        new("Sucuri", "Sicherheit", Src.Header, @".", 100, Key: "X-Sucuri-ID"),

        // Analyse / Marketing
        new("Google Tag Manager", "Analyse", Src.Html, @"googletagmanager\.com/gtm\.js|GTM-[A-Z0-9]+", 100),
        new("Google Analytics", "Analyse", Src.Html, @"google-analytics\.com|gtag\(|G-[A-Z0-9]{6,}", 80),
        new("Matomo", "Analyse", Src.Html, @"matomo\.(js|php)|piwik\.(js|php)|_paq\.push", 100),
        new("Facebook Pixel", "Analyse", Src.Html, @"connect\.facebook\.net/[^""']*/fbevents\.js", 100),
        new("Hotjar", "Analyse", Src.Html, @"static\.hotjar\.com", 100),
        new("Cookiebot", "Consent", Src.Html, @"consent\.cookiebot\.com", 100),
        new("Usercentrics", "Consent", Src.Html, @"usercentrics", 100),
        new("Borlabs Cookie", "Consent", Src.Html, @"borlabs-cookie", 100),
        new("Google reCAPTCHA", "Sicherheit", Src.Html, @"google\.com/recaptcha", 100),
        new("Google Fonts", "Schriften", Src.Html, @"fonts\.googleapis\.com", 100),
        new("Font Awesome", "Schriften", Src.Html, @"font-?awesome", 80),
    };

    public static async Task<CmsResult> AnalyzeAsync(string url, CancellationToken ct = default)
    {
        if (!url.Contains("://")) url = "https://" + url.Trim();
        var baseUri = new Uri(url);

        using var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 8,
            AutomaticDecompression = DecompressionMethods.All,
            UseCookies = true,
            CookieContainer = new CookieContainer(),
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
        };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(NetUtil.UserAgent);
        http.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,*/*;q=0.8");
        http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("de-DE,de;q=0.9,en;q=0.8");

        var result = new CmsResult();
        using var res = await http.GetAsync(baseUri, ct);
        result.FinalUrl = res.RequestMessage?.RequestUri?.ToString() ?? url;
        result.StatusCode = (int)res.StatusCode;
        var html = await res.Content.ReadAsStringAsync(ct);
        if (html.Length > 3_000_000) html = html[..3_000_000];

        var headers = res.Headers.Concat(res.Content.Headers)
            .ToDictionary(h => h.Key, h => string.Join(", ", h.Value), StringComparer.OrdinalIgnoreCase);
        var finalUri = new Uri(result.FinalUrl);
        var cookies = handler.CookieContainer.GetAllCookies().Select(c => c.Name).ToList();
        if (headers.TryGetValue("Set-Cookie", out var sc))
            cookies.AddRange(Regex.Matches(sc, @"(?:^|,\s*)([^=;,\s]+)=").Select(m => m.Groups[1].Value));
        var generators = Regex.Matches(html, @"<meta[^>]+name=[""']generator[""'][^>]*content=[""']([^""']+)[""']|<meta[^>]+content=[""']([^""']+)[""'][^>]*name=[""']generator[""']", RegexOptions.IgnoreCase)
            .Select(m => m.Groups[1].Success && m.Groups[1].Value != "" ? m.Groups[1].Value : m.Groups[2].Value).ToList();
        foreach (var g in generators) result.Notes.Add("Generator-Meta: " + g);

        // Pfad-Proben parallel
        var probePaths = Rules.Where(r => r.Source == Src.Probe).Select(r => r.Probe).Distinct().ToList();
        var probes = new Dictionary<string, string>();
        await Task.WhenAll(probePaths.Select(async p =>
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(8000);
                using var pr = await http.GetAsync(new Uri(finalUri, p), cts.Token);
                if ((int)pr.StatusCode < 400)
                {
                    var body = await pr.Content.ReadAsStringAsync(cts.Token);
                    lock (probes) probes[p] = body.Length > 200_000 ? body[..200_000] : body;
                }
            }
            catch { }
        }));

        var dict = new Dictionary<string, Detection>();
        foreach (var r in Rules)
        {
            string haystack = null;
            string evidence = null;
            switch (r.Source)
            {
                case Src.Html: haystack = html; evidence = "HTML"; break;
                case Src.Meta: haystack = string.Join("\n", generators); evidence = "Generator-Meta"; break;
                case Src.Header:
                    if (headers.TryGetValue(r.Key, out var hv)) { haystack = hv; evidence = $"Header {r.Key}: {Shorten(hv)}"; }
                    break;
                case Src.Cookie:
                    var hit = cookies.FirstOrDefault(c => Regex.IsMatch(c, r.Pattern, RegexOptions.IgnoreCase));
                    if (hit != null) { haystack = hit; evidence = "Cookie " + hit; }
                    break;
                case Src.Probe:
                    if (probes.TryGetValue(r.Probe, out var pb)) { haystack = pb; evidence = "Pfad " + r.Probe; }
                    break;
            }
            if (haystack == null) continue;
            var m = Regex.Match(haystack, r.Pattern, RegexOptions.IgnoreCase);
            if (!m.Success) continue;

            if (!dict.TryGetValue(r.Name, out var d)) dict[r.Name] = d = new Detection { Name = r.Name, Category = r.Category };
            d.Confidence = Math.Min(100, d.Confidence + r.Weight);
            d.Evidence.Add(evidence);
            if (d.Version == "" && r.VersionPattern != null)
            {
                var vm = Regex.Match(haystack, r.VersionPattern, RegexOptions.IgnoreCase);
                if (vm.Success)
                {
                    var v = vm.Groups.Cast<Group>().Skip(1).FirstOrDefault(g => g.Success && g.Value != "")?.Value;
                    if (v != null) d.Version = v;
                }
            }
        }

        // Shopware 6 Version über Probe
        if (probes.TryGetValue("/api/_info/config", out var swcfg))
        {
            var vm = Regex.Match(swcfg, @"""version""\s*:\s*""([\d.]+)""");
            if (vm.Success && dict.TryGetValue("Shopware", out var sw)) sw.Version = vm.Groups[1].Value;
        }
        // "Shopware" generisch zu Shopware 6 zusammenführen
        if (dict.TryGetValue("Shopware", out var gen) && dict.TryGetValue("Shopware 6", out var sw6))
        {
            sw6.Confidence = 100;
            if (sw6.Version == "") sw6.Version = gen.Version;
            sw6.Evidence.AddRange(gen.Evidence);
            dict.Remove("Shopware");
        }

        // WordPress-Plugins & Themes
        if (dict.ContainsKey("WordPress"))
        {
            foreach (Match m in Regex.Matches(html, @"/wp-content/plugins/([a-z0-9_-]+)/[^""'\s>]*?(?:\?ver=([\d.]+))?[""'\s>]", RegexOptions.IgnoreCase))
                AddSlug(result.WpPlugins, m.Groups[1].Value, m.Groups[2].Value);
            foreach (Match m in Regex.Matches(html, @"/wp-content/themes/([a-z0-9_-]+)/[^""'\s>]*?(?:\?ver=([\d.]+))?[""'\s>]", RegexOptions.IgnoreCase))
                AddSlug(result.WpThemes, m.Groups[1].Value, m.Groups[2].Value);
            if (probes.ContainsKey("/wp-json/")) result.Notes.Add("WordPress REST-API öffentlich erreichbar (/wp-json/).");
        }

        result.Detections.AddRange(dict.Values
            .Where(d => d.Confidence >= 30)
            .OrderBy(d => CategoryOrder(d.Category)).ThenByDescending(d => d.Confidence).ThenBy(d => d.Name));
        return result;
    }

    private static void AddSlug(List<(string, string)> list, string slug, string version)
    {
        slug = slug.ToLowerInvariant();
        if (!string.IsNullOrEmpty(version) && !version.Contains('.')) version = ""; // Zeitstempel statt Version
        var idx = list.FindIndex(x => x.Item1 == slug);
        if (idx < 0) list.Add((slug, version ?? ""));
        else if (string.IsNullOrEmpty(list[idx].Item2) && !string.IsNullOrEmpty(version)) list[idx] = (slug, version);
    }

    private static int CategoryOrder(string c) => c switch
    {
        "CMS" => 0, "Shop" => 1, "Baukasten" => 2, "Page Builder" => 3, "Framework" => 4, "JavaScript" => 5,
        "Sprache" => 6, "Webserver" => 7, "Hosting" => 8, "CDN" => 9, "Cache" => 10, "Sicherheit" => 11, _ => 20,
    };

    private static string Shorten(string s) => s.Length > 60 ? s[..60] + "…" : s;
}
