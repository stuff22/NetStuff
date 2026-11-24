using System.Net;
using System.Net.Http;
using System.Text.Json;
using NetStuff.Infrastructure;

namespace NetStuff.Services;

/// <summary>
/// Wertet die öffentliche WordPress-REST-API (/wp-json/) aus: Website-Angaben, Plugins anhand der API-Namespaces,
/// Anzahl der Inhalte und ob Benutzernamen öffentlich abrufbar sind. Nur lesende GET-Anfragen ohne Anmeldung.
/// </summary>
public static class WordPressApi
{
    /// <summary>Bekannte Namespaces → Plugin/Funktion (Präfix vor dem ersten „/“).</summary>
    private static readonly Dictionary<string, string> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        ["wp"] = "WordPress-Kern",
        ["oembed"] = "WordPress-Kern (oEmbed)",
        ["wp-site-health"] = "WordPress-Kern (Website-Zustand)",
        ["wp-block-editor"] = "WordPress-Kern (Block-Editor)",
        ["wp-abilities"] = "WordPress-Kern (Abilities-API)",
        ["wc"] = "WooCommerce",
        ["wc-admin"] = "WooCommerce",
        ["wc-analytics"] = "WooCommerce",
        ["wc-telemetry"] = "WooCommerce",
        ["wc-store"] = "WooCommerce",
        ["wccom-site"] = "WooCommerce.com-Verbindung",
        ["yoast"] = "Yoast SEO",
        ["rankmath"] = "Rank Math SEO",
        ["aioseo"] = "All in One SEO",
        ["seopress"] = "SEOPress",
        ["contact-form-7"] = "Contact Form 7",
        ["wpforms"] = "WPForms",
        ["gf"] = "Gravity Forms",
        ["ninja-forms-submissions"] = "Ninja Forms",
        ["jetpack"] = "Jetpack",
        ["wpcom"] = "Jetpack / WordPress.com",
        ["my-jetpack"] = "Jetpack",
        ["jetpack-boost"] = "Jetpack Boost",
        ["akismet"] = "Akismet",
        ["wordfence"] = "Wordfence Security",
        ["ithemes-security"] = "Solid Security (iThemes)",
        ["solid-security"] = "Solid Security",
        ["sucuri"] = "Sucuri Security",
        ["elementor"] = "Elementor",
        ["elementor-pro"] = "Elementor Pro",
        ["divi"] = "Divi",
        ["et"] = "Divi / Elegant Themes",
        ["litespeed"] = "LiteSpeed Cache",
        ["wp-rocket"] = "WP Rocket",
        ["wpo"] = "WP-Optimize",
        ["w3tc"] = "W3 Total Cache",
        ["updraftplus"] = "UpdraftPlus",
        ["mailpoet"] = "MailPoet",
        ["complianz"] = "Complianz (Cookie-Banner)",
        ["borlabs-cookie"] = "Borlabs Cookie",
        ["real-cookie-banner"] = "Real Cookie Banner",
        ["redirection"] = "Redirection",
        ["wpml"] = "WPML",
        ["pll"] = "Polylang",
        ["translatepress"] = "TranslatePress",
        ["wp-statistics"] = "WP Statistics",
        ["google-site-kit"] = "Site Kit by Google",
        ["monsterinsights"] = "MonsterInsights",
        ["really-simple-ssl"] = "Really Simple Security",
        ["regenerate-thumbnails"] = "Regenerate Thumbnails",
        ["wp-mail-smtp"] = "WP Mail SMTP",
        ["fluentform"] = "Fluent Forms",
        ["fluent-crm"] = "FluentCRM",
        ["tribe"] = "The Events Calendar",
        ["bbp"] = "bbPress",
        ["buddypress"] = "BuddyPress",
        ["buddyboss"] = "BuddyBoss",
        ["learnpress"] = "LearnPress",
        ["ldlms"] = "LearnDash",
        ["wpml-st"] = "WPML String Translation",
        ["acf"] = "Advanced Custom Fields",
        ["kadence-blocks"] = "Kadence Blocks",
        ["generateblocks"] = "GenerateBlocks",
        ["spectra"] = "Spectra",
        ["uagb"] = "Spectra",
        ["astra"] = "Astra",
        ["royal-mcp"] = "Royal MCP",
        ["mcp"] = "MCP-Adapter",
        ["simply-static"] = "Simply Static",
        ["duplicator"] = "Duplicator",
        ["all-in-one-wp-migration"] = "All-in-One WP Migration",
        ["wp-smushit"] = "Smush",
        ["imagify"] = "Imagify",
        ["ewww"] = "EWWW Image Optimizer",
        ["shortpixel"] = "ShortPixel",
        ["sg-security"] = "SiteGround Security",
        ["siteground-optimizer"] = "SiteGround Optimizer",
        ["code-snippets"] = "Code Snippets",
        ["jwt-auth"] = "JWT Authentication",
        ["layerslider"] = "LayerSlider",
        ["revslider"] = "Slider Revolution",
        ["sliderrevolution"] = "Slider Revolution",
        ["complianz_tc"] = "Complianz (Nutzungsbedingungen)",
        ["wordfence-login-security"] = "Wordfence Security",
        ["the7"] = "The7 (Theme)",
        ["uavc"] = "Ultimate Addons for WPBakery",
        ["ultimate-vc"] = "Ultimate Addons for WPBakery",
        ["js_composer"] = "WPBakery Page Builder",
        ["woocommerce-email-editor"] = "E-Mail-Editor (WooCommerce/MailPoet)",
        ["nps-survey"] = "NPS-Umfrage (Brainstorm Force)",
        ["avada"] = "Avada (Theme)",
        ["flatsome"] = "Flatsome (Theme)",
        ["enfold"] = "Enfold (Theme)",
        ["oxygen"] = "Oxygen Builder",
        ["bricks"] = "Bricks Builder",
        ["beaver-builder"] = "Beaver Builder",
        ["fl-builder"] = "Beaver Builder",
        ["wp-smtp"] = "WP SMTP",
        ["post-smtp"] = "Post SMTP",
        ["burst"] = "Burst Statistics",
        ["koko-analytics"] = "Koko Analytics",
        ["matomo"] = "Matomo",
        ["wp-graphql"] = "WPGraphQL",
    };

    private sealed class Endpoint
    {
        public required string Root;
        public required bool QueryStyle; // ?rest_route=/… statt /wp-json/…
        public string Url(string route) => QueryStyle
            ? Root + (Root.Contains('?') ? "&" : "?") + "rest_route=/" + route.Replace("?", "&")
            : Root.TrimEnd('/') + "/" + route;
    }

    public static async Task<List<CheckSection>> AnalyzeAsync(string siteUrl, string apiRoot = null, CancellationToken ct = default)
    {
        if (!siteUrl.Contains("://")) siteUrl = "https://" + siteUrl.Trim();
        var site = new Uri(siteUrl);
        var origin = site.GetLeftPart(UriPartial.Authority);

        using var http = new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
        })
        { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(NetUtil.UserAgent);
        http.DefaultRequestHeaders.Accept.ParseAdd("application/json");

        // REST-Basis finden: Angabe der Seite, /wp-json/, ?rest_route=/
        var candidates = new List<Endpoint>();
        if (!string.IsNullOrEmpty(apiRoot))
            candidates.Add(new Endpoint { Root = apiRoot, QueryStyle = apiRoot.Contains("rest_route=") });
        candidates.Add(new Endpoint { Root = origin + "/wp-json/", QueryStyle = false });
        candidates.Add(new Endpoint { Root = origin + "/", QueryStyle = true });

        Endpoint ep = null;
        JsonDocument index = null;
        string lastError = "";
        foreach (var c in candidates)
        {
            if (c.QueryStyle && c.Root.Contains("rest_route=")) c.Root = c.Root[..c.Root.IndexOf("rest_route=")].TrimEnd('?', '&');
            var (status, body, _) = await GetAsync(http, c.QueryStyle ? c.Url("") : c.Root, ct);
            if (status == 200 && TryParse(body, out var doc) && doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("namespaces", out _))
            {
                ep = c; index = doc;
                break;
            }
            lastError = status == 0 ? body : $"HTTP {status}";
        }

        var sections = new List<CheckSection>();
        if (ep == null)
        {
            sections.Add(new CheckSection("WordPress-REST-API (/wp-json/)", new[]
            {
                new CheckItem("Erreichbarkeit", L.F("Nicht öffentlich erreichbar ({0}) – vermutlich durch ein Sicherheits-Plugin oder den Server gesperrt.", lastError), Level.Ok),
            }));
            return sections;
        }

        using (index)
        {
            var root = index.RootElement;
            var apiUrl = ep.QueryStyle ? ep.Url("") : ep.Root;

            // Website
            var info = new List<CheckItem>();
            void Add(string label, string prop)
            {
                if (root.TryGetProperty(prop, out var v) && v.ValueKind is JsonValueKind.String or JsonValueKind.Number)
                {
                    var s = v.ToString().Trim();
                    if (s != "") info.Add(new CheckItem(label, s));
                }
            }
            Add("Name", "name");
            Add("Beschreibung", "description");
            Add("URL", "url");
            Add("Startseite", "home");
            Add("Zeitzone", "timezone_string");
            Add("GMT-Versatz", "gmt_offset");
            Add("Website-Icon", "site_icon_url");
            int routes = root.TryGetProperty("routes", out var r) && r.ValueKind == JsonValueKind.Object ? r.EnumerateObject().Count() : 0;
            var namespaces = root.TryGetProperty("namespaces", out var ns) && ns.ValueKind == JsonValueKind.Array
                ? ns.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x != "").ToList()
                : new List<string>();
            info.Add(new CheckItem("API", L.F("{0} · {1} Namespaces · {2} Routen", apiUrl, namespaces.Count, routes)));
            sections.Add(new CheckSection("WordPress-REST-API (/wp-json/)", info));

            // Plugins aus Namespaces
            var groups = namespaces
                .GroupBy(n => n.Split('/')[0])
                .Select(g => (Prefix: g.Key, Name: Known.TryGetValue(g.Key, out var k) ? k : null, Spaces: g.ToList()))
                .ToList();
            var plugins = groups.Where(g => g.Name == null || !g.Name.StartsWith("WordPress-Kern"))
                .GroupBy(g => g.Name ?? g.Prefix)
                .OrderBy(g => g.Any(x => x.Name == null)).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .Select(g => new CheckItem(g.Key,
                    string.Join(", ", g.SelectMany(x => x.Spaces)) + (g.Any(x => x.Name == null) ? " · " + L.T("unbekannter Namespace") : "")))
                .ToList();
            if (plugins.Count > 0)
                sections.Add(new CheckSection(L.F("Plugins & Erweiterungen laut API ({0})", plugins.Count), plugins,
                    "Erkannt an den REST-Namespaces – zeigt auch Plugins, die im Seitenquelltext keine Spuren hinterlassen."));

            // Inhalte
            var content = new List<CheckItem>();
            var counts = new (string Label, string Route)[]
            {
                ("Beiträge", "wp/v2/posts?per_page=1&_fields=id,date,modified,title,link"),
                ("Seiten", "wp/v2/pages?per_page=1&_fields=id"),
                ("Medien", "wp/v2/media?per_page=1&_fields=id"),
                ("Kategorien", "wp/v2/categories?per_page=1&_fields=id"),
                ("Schlagwörter", "wp/v2/tags?per_page=1&_fields=id"),
                ("Kommentare", "wp/v2/comments?per_page=1&_fields=id"),
            };
            var countTasks = counts.Select(c => GetAsync(http, ep.Url(c.Route), ct)).ToList();
            var userTask = GetAsync(http, ep.Url("wp/v2/users?per_page=100&_fields=id,name,slug"), ct);
            await Task.WhenAll(countTasks.Cast<Task>().Append(userTask));

            for (int i = 0; i < counts.Length; i++)
            {
                var (status, body, total) = countTasks[i].Result;
                if (status == 200 && total != null)
                {
                    var text = total.ToString();
                    if (i == 0 && TryParse(body, out var posts) && posts.RootElement.ValueKind == JsonValueKind.Array && posts.RootElement.GetArrayLength() > 0)
                    {
                        using (posts)
                        {
                            var p = posts.RootElement[0];
                            var title = p.TryGetProperty("title", out var t) && t.TryGetProperty("rendered", out var tr) ? WebUtility.HtmlDecode(tr.GetString() ?? "") : "";
                            var date = p.TryGetProperty("date", out var d) && DateTime.TryParse(d.GetString(), out var dt) ? dt.ToString("d") : "";
                            if (date != "") text += " · " + L.F("neuester vom {0}: „{1}“", date, title);
                        }
                    }
                    content.Add(new CheckItem(counts[i].Label, text));
                }
                else content.Add(new CheckItem(counts[i].Label, status is 401 or 403 ? L.T("nicht öffentlich") : L.T("nicht verfügbar")));
            }
            sections.Add(new CheckSection("Inhalte", content));

            // Sicherheit
            var sec = new List<CheckItem>
            {
                new("REST-API", "Ohne Anmeldung erreichbar – das ist bei WordPress Standard und für Block-Editor und viele Plugins nötig.", Level.Info),
            };
            var (uStatus, uBody, _) = userTask.Result;
            var users = new List<(string Name, string Slug)>();
            if (uStatus == 200 && TryParse(uBody, out var ud) && ud.RootElement.ValueKind == JsonValueKind.Array)
            {
                using (ud)
                {
                    foreach (var u in ud.RootElement.EnumerateArray())
                        users.Add((u.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                                   u.TryGetProperty("slug", out var s) ? s.GetString() ?? "" : ""));
                }
            }
            if (users.Count > 0)
            {
                sec.Add(new CheckItem("Benutzerliste", L.F("Öffentlich abrufbar ({0} Benutzer) – Angreifer erfahren so mögliche Anmeldenamen. Empfehlung: /wp/v2/users für nicht angemeldete Besucher sperren (z. B. per Sicherheits-Plugin).", users.Count), Level.Warn));
                foreach (var (name, slug) in users)
                    sec.Add(new CheckItem("  " + (name != "" ? name : slug), L.F("Slug „{0}“", slug), Level.Warn));
            }
            else
                sec.Add(new CheckItem("Benutzerliste", uStatus is 401 or 403 ? "Gesperrt – Benutzernamen sind nicht öffentlich abrufbar." : "Keine Benutzer öffentlich abrufbar.", Level.Ok));

            if (root.TryGetProperty("authentication", out var auth) && auth.ValueKind == JsonValueKind.Object)
            {
                var methods = auth.EnumerateObject().Select(p => p.Name).ToList();
                if (methods.Contains("application-passwords"))
                    sec.Add(new CheckItem("Anwendungspasswörter", "Aktiviert – externe Programme können sich per Anwendungspasswort anmelden.", Level.Info));
                foreach (var m in methods.Where(m => m != "application-passwords"))
                    sec.Add(new CheckItem("Anmeldeverfahren", m, Level.Info));
            }
            sections.Add(new CheckSection("Sicherheit", sec));
        }
        return sections;
    }

    private static bool TryParse(string body, out JsonDocument doc)
    {
        doc = null;
        try { doc = JsonDocument.Parse(body); return true; }
        catch { return false; }
    }

    /// <summary>GET mit Status, Inhalt und X-WP-Total (Status 0 = Verbindungsfehler, Inhalt = Fehlermeldung).</summary>
    private static async Task<(int Status, string Body, int? Total)> GetAsync(HttpClient http, string url, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(12000);
            using var res = await http.GetAsync(url, cts.Token);
            var body = await res.Content.ReadAsStringAsync(cts.Token);
            if (body.Length > 5_000_000) body = body[..5_000_000];
            int? total = res.Headers.TryGetValues("X-WP-Total", out var v) && int.TryParse(v.FirstOrDefault(), out var t) ? t : null;
            return ((int)res.StatusCode, body, total);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            return (0, ex.InnerException?.Message ?? ex.Message, null);
        }
    }
}
