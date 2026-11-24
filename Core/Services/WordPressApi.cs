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
            string Str(JsonElement e, string prop) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out var v)
                ? (v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ValueKind == JsonValueKind.Number ? v.ToString() : "") : "";
            string Rendered(JsonElement e, string prop) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out var v)
                ? WebUtility.HtmlDecode(v.ValueKind == JsonValueKind.Object ? Str(v, "rendered") : v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "").Trim() : "";
            string Date(JsonElement e, string prop) => DateTime.TryParse(Str(e, prop), out var d) ? d.ToString("d") : "";
            var home = Str(root, "home") is { Length: > 0 } h ? h : origin;

            // ---------- Website ----------
            var info = new List<CheckItem>();
            void Add(string label, string prop) { var s = Str(root, prop).Trim(); if (s != "") info.Add(new CheckItem(label, s)); }
            Add("Name", "name");
            Add("Beschreibung", "description");
            Add("URL", "url");
            Add("Startseite", "home");
            Add("Zeitzone", "timezone_string");
            Add("GMT-Versatz", "gmt_offset");
            Add("Website-Icon", "site_icon_url");
            var namespaces = root.TryGetProperty("namespaces", out var ns) && ns.ValueKind == JsonValueKind.Array
                ? ns.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x != "").ToList()
                : new List<string>();

            // Routen je Namespace (Anzahl, davon mit Schreibmethoden)
            var routeInfo = new Dictionary<string, (int Total, int Write, List<string> Paths)>();
            int routeCount = 0, writeCount = 0;
            if (root.TryGetProperty("routes", out var rts) && rts.ValueKind == JsonValueKind.Object)
            {
                foreach (var rt in rts.EnumerateObject())
                {
                    var nsName = Str(rt.Value, "namespace");
                    bool write = rt.Value.TryGetProperty("methods", out var m) && m.ValueKind == JsonValueKind.Array
                                 && m.EnumerateArray().Any(x => x.GetString() is "POST" or "PUT" or "PATCH" or "DELETE");
                    routeCount++;
                    if (write) writeCount++;
                    if (nsName == "") continue;
                    (int Total, int Write, List<string> Paths) cur = routeInfo.TryGetValue(nsName, out var ri) ? ri : (0, 0, new List<string>());
                    cur.Paths.Add(rt.Name);
                    routeInfo[nsName] = (cur.Total + 1, cur.Write + (write ? 1 : 0), cur.Paths);
                }
            }
            info.Add(new CheckItem("API", L.F("{0} · {1} Namespaces · {2} Routen", apiUrl, namespaces.Count, routeCount)));
            sections.Add(new CheckSection("WordPress-REST-API (/wp-json/)", info));

            // ---------- Plugins aus Namespaces ----------
            string RouteText(IEnumerable<string> spaces)
            {
                int t = 0, w = 0;
                foreach (var s in spaces) if (routeInfo.TryGetValue(s, out var x)) { t += x.Total; w += x.Write; }
                return t == 0 ? "" : " · " + L.F("{0} Routen, {1} davon schreibend", t, w);
            }
            var groups = namespaces
                .GroupBy(n => n.Split('/')[0])
                .Select(g => (Prefix: g.Key, Name: Known.TryGetValue(g.Key, out var k) ? k : null, Spaces: g.ToList()))
                .ToList();
            var plugins = groups.Where(g => g.Name == null || !g.Name.StartsWith("WordPress-Kern"))
                .GroupBy(g => g.Name ?? g.Prefix)
                .OrderBy(g => g.Any(x => x.Name == null)).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .Select(g =>
                {
                    var spaces = g.SelectMany(x => x.Spaces).ToList();
                    var text = string.Join(", ", spaces) + RouteText(spaces);
                    if (g.Any(x => x.Name == null))
                    {
                        var sample = spaces.SelectMany(s => routeInfo.TryGetValue(s, out var x) ? x.Paths : new List<string>())
                            .Where(p => !spaces.Any(s => p.TrimEnd('/') == "/" + s)).Take(4).ToList();
                        text += " · " + L.T("unbekannter Namespace") + (sample.Count > 0 ? " · " + L.T("z. B. ") + string.Join(", ", sample) : "");
                    }
                    return new CheckItem(g.Key, text);
                })
                .ToList();
            int pluginCount = plugins.Count;
            var core = groups.Where(g => g.Name != null && g.Name.StartsWith("WordPress-Kern")).SelectMany(g => g.Spaces).ToList();
            if (core.Count > 0) plugins.Add(new CheckItem("WordPress-Kern", string.Join(", ", core) + RouteText(core)));
            if (plugins.Count > 0)
                sections.Add(new CheckSection(L.F("Plugins & Erweiterungen laut API ({0})", pluginCount), plugins,
                    "Erkannt an den REST-Namespaces – zeigt auch Plugins, die im Seitenquelltext keine Spuren hinterlassen. „Schreibend“ heißt: Die Route nimmt POST/PUT/DELETE an (meist nur mit Anmeldung)."));

            // ---------- Abfragen parallel starten ----------
            Task<(int Status, string Body, int? Total)> Get(string route) => GetAsync(http, ep.Url(route), ct);
            var tPostsNew = Get("wp/v2/posts?per_page=10&_fields=date,modified,title,link");
            var tPostsOld = Get("wp/v2/posts?per_page=1&order=asc&_fields=date,title");
            var tPostsMod = Get("wp/v2/posts?per_page=1&orderby=modified&_fields=modified,title");
            var tPages = Get("wp/v2/pages?per_page=50&orderby=title&order=asc&_fields=title,link,modified");
            var tMedia = Get("wp/v2/media?per_page=100&_fields=date,mime_type");
            var tComments = Get("wp/v2/comments?per_page=1&_fields=date");
            var tCats = Get("wp/v2/categories?per_page=10&orderby=count&order=desc&_fields=name,count");
            var tTags = Get("wp/v2/tags?per_page=10&orderby=count&order=desc&_fields=name,count");
            var tTypes = Get("wp/v2/types");
            var tTax = Get("wp/v2/taxonomies");
            var tUsers = Get("wp/v2/users?per_page=100&_fields=id,name,slug");
            var tProducts = namespaces.Contains("wc/store/v1") ? Get("wc/store/v1/products?per_page=1") : null;
            var tOembed = Get("oembed/1.0/embed?url=" + Uri.EscapeDataString(home));
            var sensitive = new[] { "wp/v2/settings", "wp/v2/plugins", "wp/v2/themes", "wp/v2/users/me", "wp/v2/block-types" };
            var tSensitive = sensitive.Select(Get).ToList();
            var tXmlRpc = GetAsync(http, origin + "/xmlrpc.php", ct);
            var tAuthor = RedirectTargetAsync(origin + "/?author=1", ct);
            var all = new List<Task> { tPostsNew, tPostsOld, tPostsMod, tPages, tMedia, tComments, tCats, tTags, tTypes, tTax, tUsers, tOembed, tXmlRpc, tAuthor };
            all.AddRange(tSensitive);
            if (tProducts != null) all.Add(tProducts);
            await Task.WhenAll(all);

            JsonElement Arr((int Status, string Body, int? Total) r) =>
                r.Status == 200 && TryParse(r.Body, out var d) && d.RootElement.ValueKind == JsonValueKind.Array ? d.RootElement.Clone() : default;
            JsonElement Obj((int Status, string Body, int? Total) r) =>
                r.Status == 200 && TryParse(r.Body, out var d) && d.RootElement.ValueKind == JsonValueKind.Object ? d.RootElement.Clone() : default;
            string Count((int Status, string Body, int? Total) r) =>
                r.Status == 200 && r.Total != null ? r.Total.Value.ToString("N0") : r.Status is 401 or 403 ? L.T("nicht öffentlich") : L.T("nicht verfügbar");
            bool IsArr(JsonElement e) => e.ValueKind == JsonValueKind.Array;

            // ---------- Inhalte ----------
            var content = new List<CheckItem>();
            var postsNew = Arr(tPostsNew.Result);
            {
                var text = Count(tPostsNew.Result);
                if (IsArr(postsNew) && postsNew.GetArrayLength() > 0)
                    text += " · " + L.F("neuester vom {0}: „{1}“", Date(postsNew[0], "date"), Rendered(postsNew[0], "title"));
                var old = Arr(tPostsOld.Result);
                if (IsArr(old) && old.GetArrayLength() > 0)
                    text += " · " + L.F("ältester vom {0}", Date(old[0], "date"));
                content.Add(new CheckItem("Beiträge", text));
                var mod = Arr(tPostsMod.Result);
                if (IsArr(mod) && mod.GetArrayLength() > 0)
                    content.Add(new CheckItem("Zuletzt geändert", L.F("{0}: „{1}“", Date(mod[0], "modified"), Rendered(mod[0], "title"))));
            }
            content.Add(new CheckItem("Seiten", Count(tPages.Result)));
            {
                var media = Arr(tMedia.Result);
                var text = Count(tMedia.Result);
                if (IsArr(media) && media.GetArrayLength() > 0)
                {
                    text += " · " + L.F("neuester Upload {0}", Date(media[0], "date"));
                    var mt = media.EnumerateArray().Select(x => Str(x, "mime_type")).Where(x => x != "")
                        .GroupBy(x => x).OrderByDescending(g => g.Count()).Select(g => $"{g.Key} {g.Count()}");
                    text += " · " + L.F("Dateitypen der letzten {0}: {1}", media.GetArrayLength(), string.Join(", ", mt));
                }
                content.Add(new CheckItem("Medien", text));
            }
            {
                var com = Arr(tComments.Result);
                var text = Count(tComments.Result);
                if (IsArr(com) && com.GetArrayLength() > 0) text += " · " + L.F("neuester vom {0}", Date(com[0], "date"));
                content.Add(new CheckItem("Kommentare", text));
            }
            string Top(JsonElement arr)
            {
                if (!IsArr(arr)) return "";
                var used = arr.EnumerateArray().Where(x => Str(x, "count") is not ("" or "0"))
                    .Select(x => $"{WebUtility.HtmlDecode(Str(x, "name"))} ({Str(x, "count")})").ToList();
                return used.Count == 0 ? "" : " · " + L.T("meistgenutzt: ") + string.Join(", ", used);
            }
            content.Add(new CheckItem("Kategorien", Count(tCats.Result) + Top(Arr(tCats.Result))));
            content.Add(new CheckItem("Schlagwörter", Count(tTags.Result) + Top(Arr(tTags.Result))));
            if (tProducts != null) content.Add(new CheckItem("Produkte (WooCommerce)", Count(tProducts.Result)));

            // Eigene Inhaltstypen und Taxonomien
            var coreTypes = new HashSet<string> { "post", "page", "attachment", "nav_menu_item", "wp_block", "wp_template", "wp_template_part", "wp_navigation", "wp_global_styles", "wp_font_family", "wp_font_face" };
            var types = Obj(tTypes.Result);
            if (types.ValueKind == JsonValueKind.Object)
            {
                var custom = types.EnumerateObject().Where(t => !coreTypes.Contains(t.Name)).ToList();
                var countTasks = custom.Select(t =>
                {
                    var b = Str(t.Value, "rest_base");
                    var n = Str(t.Value, "rest_namespace") is { Length: > 0 } x ? x : "wp/v2";
                    return b == "" ? Task.FromResult((0, "", (int?)null)) : Get($"{n}/{b}?per_page=1&_fields=id");
                }).ToList();
                await Task.WhenAll(countTasks);
                for (int i = 0; i < custom.Count; i++)
                    content.Add(new CheckItem(L.F("Inhaltstyp „{0}“", WebUtility.HtmlDecode(Str(custom[i].Value, "name"))), $"{custom[i].Name} · {Count(countTasks[i].Result)}"));
            }
            var tax = Obj(tTax.Result);
            if (tax.ValueKind == JsonValueKind.Object)
            {
                var customTax = tax.EnumerateObject()
                    .Where(t => t.Name is not ("category" or "post_tag" or "nav_menu" or "wp_pattern_category" or "post_format" or "wp_theme" or "wp_template_part_area" or "link_category"))
                    .Select(t => $"{WebUtility.HtmlDecode(Str(t.Value, "name"))} ({t.Name})").ToList();
                if (customTax.Count > 0) content.Add(new CheckItem("Eigene Taxonomien", string.Join(", ", customTax)));
            }
            sections.Add(new CheckSection("Inhalte", content));

            // Neueste Beiträge
            if (IsArr(postsNew) && postsNew.GetArrayLength() > 0)
                sections.Add(new CheckSection(L.F("Neueste Beiträge ({0})", postsNew.GetArrayLength()),
                    postsNew.EnumerateArray().Select(p => new CheckItem(Date(p, "date"), $"{Rendered(p, "title")} · {Str(p, "link")}"))));

            // Seiten
            var pages = Arr(tPages.Result);
            if (IsArr(pages) && pages.GetArrayLength() > 0)
                sections.Add(new CheckSection(L.F("Seiten ({0})", tPages.Result.Total ?? pages.GetArrayLength()),
                    pages.EnumerateArray().Select(p => new CheckItem(Rendered(p, "title") is { Length: > 0 } t ? t : L.T("(ohne Titel)"),
                        $"{Str(p, "link")} · {L.F("geändert {0}", Date(p, "modified"))}")),
                    pages.GetArrayLength() < (tPages.Result.Total ?? 0) ? L.F("Die ersten {0} alphabetisch.", pages.GetArrayLength()) : null));

            // ---------- Sicherheit ----------
            var sec = new List<CheckItem>
            {
                new("REST-API", "Ohne Anmeldung erreichbar – das ist bei WordPress Standard und für Block-Editor und viele Plugins nötig.", Level.Info),
                new("Routen", L.F("{0} Routen, davon {1} mit Schreibmethoden (POST/PUT/DELETE) – diese verlangen normalerweise eine Anmeldung.", routeCount, writeCount), Level.Info),
            };

            List<(string Name, string Slug)> Users((int Status, string Body, int? Total) r)
            {
                var list = new List<(string, string)>();
                var a = Arr(r);
                if (IsArr(a)) { foreach (var u in a.EnumerateArray()) list.Add((Str(u, "name"), Str(u, "slug"))); }
                else if (Obj(r) is { ValueKind: JsonValueKind.Object } o && Str(o, "slug") != "") list.Add((Str(o, "name"), Str(o, "slug")));
                return list;
            }
            var users = Users(tUsers.Result);
            if (users.Count > 0)
            {
                sec.Add(new CheckItem("Benutzerliste", L.F("Öffentlich abrufbar ({0} Benutzer) – Angreifer erfahren so mögliche Anmeldenamen. Empfehlung: /wp/v2/users für nicht angemeldete Besucher sperren (z. B. per Sicherheits-Plugin).", users.Count), Level.Warn));
                foreach (var (name, slug) in users)
                    sec.Add(new CheckItem("  " + (name != "" ? name : slug), L.F("Slug „{0}“", slug), Level.Warn));
            }
            else
            {
                // Sperre umgehbar? (?rest_route= statt /wp-json/, Einzelabruf /users/1)
                var bypass = new List<string>();
                var alt = ep.QueryStyle ? new Endpoint { Root = origin + "/wp-json/", QueryStyle = false } : new Endpoint { Root = origin + "/", QueryStyle = true };
                var u1 = Users(await GetAsync(http, alt.Url("wp/v2/users?per_page=100&_fields=id,name,slug"), ct));
                if (u1.Count > 0) bypass.Add(alt.QueryStyle ? "?rest_route=/wp/v2/users" : "/wp-json/wp/v2/users");
                var u2 = Users(await Get("wp/v2/users/1?_fields=id,name,slug"));
                if (u2.Count > 0) bypass.Add("/wp/v2/users/1");
                if (bypass.Count > 0)
                {
                    var names = u1.Concat(u2).Select(u => u.Slug).Distinct();
                    sec.Add(new CheckItem("Benutzerliste", L.F("Gesperrt, aber umgehbar über {0} – gefunden: {1}", string.Join(", ", bypass), string.Join(", ", names)), Level.Bad));
                }
                else
                    sec.Add(new CheckItem("Benutzerliste", tUsers.Result.Status is 401 or 403 ? "Gesperrt – Benutzernamen sind nicht öffentlich abrufbar." : "Keine Benutzer öffentlich abrufbar.", Level.Ok));
            }

            // Autorennamen über oEmbed und /?author=1
            var oembed = Obj(tOembed.Result);
            if (oembed.ValueKind == JsonValueKind.Object)
            {
                var an = Str(oembed, "author_name");
                var au = Str(oembed, "author_url");
                if (au.Contains("/author/"))
                    sec.Add(new CheckItem("oEmbed", L.F("Verrät den Autor der Startseite: {0} ({1})", an, au), Level.Warn));
                else
                    sec.Add(new CheckItem("oEmbed", L.T("Kein Benutzername in den Einbettungsdaten."), Level.Ok));
            }
            var author = tAuthor.Result;
            if (author != null && author.Contains("/author/"))
                sec.Add(new CheckItem("Autoren-Archiv", L.F("/?author=1 leitet weiter auf {0} – verrät den Anmeldenamen von Benutzer 1.", author), Level.Warn));
            else
                sec.Add(new CheckItem("Autoren-Archiv", L.T("/?author=1 verrät keinen Benutzernamen."), Level.Ok));

            // Geschützte Endpunkte
            var open = new List<string>();
            for (int i = 0; i < sensitive.Length; i++)
            {
                var r = tSensitive[i].Result;
                if (r.Status != 200 || !TryParse(r.Body, out var d)) continue;
                using (d)
                {
                    var e = d.RootElement;
                    bool hasData = e.ValueKind == JsonValueKind.Array ? e.GetArrayLength() > 0 : e.ValueKind == JsonValueKind.Object && e.EnumerateObject().Any();
                    if (hasData) open.Add("/" + sensitive[i]);
                }
            }
            sec.Add(open.Count > 0
                ? new CheckItem("Geschützte Endpunkte", L.F("Ohne Anmeldung abrufbar: {0}", string.Join(", ", open)), Level.Bad)
                : new CheckItem("Geschützte Endpunkte", L.F("{0} verlangen eine Anmeldung.", string.Join(", ", sensitive.Select(s => "/" + s))), Level.Ok));

            // XML-RPC
            var xr = tXmlRpc.Result;
            if (xr.Status == 405 || (xr.Status == 200 && xr.Body.Contains("XML-RPC", StringComparison.OrdinalIgnoreCase)))
                sec.Add(new CheckItem("XML-RPC", "Aktiv (/xmlrpc.php) – beliebtes Ziel für Passwort-Raten. Wenn nicht benötigt (z. B. für Jetpack oder die WordPress-App), sperren.", Level.Warn));
            else
                sec.Add(new CheckItem("XML-RPC", xr.Status == 0 ? L.T("Nicht erreichbar.") : L.F("Gesperrt oder nicht vorhanden (HTTP {0}).", xr.Status), Level.Ok));

            if (root.TryGetProperty("authentication", out var auth) && auth.ValueKind == JsonValueKind.Object)
            {
                var methods = auth.EnumerateObject().Select(p => p.Name).ToList();
                if (methods.Contains("application-passwords"))
                    sec.Add(new CheckItem("Anwendungspasswörter", "Aktiviert – externe Programme können sich per Anwendungspasswort anmelden.", Level.Info));
                foreach (var m in methods.Where(m => m != "application-passwords"))
                    sec.Add(new CheckItem("Anmeldeverfahren", m, Level.Info));
            }
            sections.Add(new CheckSection("Sicherheit", sec, "Nur lesende Abfragen ohne Anmeldung – so, wie sie jeder Besucher machen kann."));
        }
        return sections;
    }

    /// <summary>Ziel einer Weiterleitung (Location), ohne ihr zu folgen; null, wenn keine.</summary>
    private static async Task<string> RedirectTargetAsync(string url, CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, ServerCertificateCustomValidationCallback = (_, _, _, _) => true })
            { Timeout = TimeSpan.FromSeconds(12) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd(NetUtil.UserAgent);
            using var res = await http.GetAsync(url, ct);
            return (int)res.StatusCode is >= 300 and < 400 ? res.Headers.Location?.ToString() : null;
        }
        catch { return null; }
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
