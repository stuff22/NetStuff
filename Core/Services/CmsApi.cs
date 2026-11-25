using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using NetStuff.Infrastructure;

namespace NetStuff.Services;

/// <summary>
/// Wertet die öffentlichen Schnittstellen eines erkannten CMS/Shops aus (WordPress: <see cref="WordPressApi"/>).
/// Nur lesende GET-Anfragen ohne Anmeldung – so, wie sie jeder Besucher machen kann. Inhalte geschützter
/// Antworten (z. B. Konfiguration bei bekannten Lücken) werden nie angezeigt, nur ob sie offen sind.
/// </summary>
public static class CmsApi
{
    private static readonly (string System, string Api, string Description)[] Supported =
    {
        ("WordPress", "/wp-json/", "Website-Angaben, Plugins laut API-Namespaces, Inhalte, Seiten und Sicherheitsprüfungen (Benutzerliste, XML-RPC …)"),
        ("Joomla", "/api/index.php/v1/", "Version, Web-Services-API, bekannte Lücke CVE-2023-23752 (öffentliche Konfiguration), Administrator-Anmeldung und Sitemap"),
        ("Drupal", "/jsonapi/", "JSON:API-Ressourcen (Inhaltstypen, Taxonomien, Medien), neueste Inhalte, öffentliche Benutzernamen und Sitemap"),
        ("Shopware 6", "/api/ · /store-api/", "Admin- und Store-API, Administration, Sitemap (Anzahl Produkt- und Kategorieseiten)"),
        ("Shopware", "/api/ · /store-api/", "Admin- und Store-API, Administration, Sitemap (Anzahl Produkt- und Kategorieseiten)"),
        ("Magento", "/rest/V1/", "Version, Shop-Konfiguration, Währungen, öffentlicher Produktkatalog und Sitemap"),
        ("PrestaShop", "/api/", "Webservice-API (aktiv/gesperrt), Administration und Sitemap"),
        ("Strapi", "/api/", "Projektangaben, öffentliche Benutzer- und Medienlisten, API-Dokumentation und Admin-Endpunkte"),
        ("Directus", "/server/info · /items/", "Projektangaben, öffentlich lesbare Sammlungen, Benutzerliste und Sitemap"),
        ("Ghost", "/ghost/api/", "Website-Angaben inkl. Version, Beiträge, Seiten, Schlagwörter, Autoren (Content-API) und Mitgliederbereich"),
        ("Shopify", "/meta.json · /products.json", "Shop-Angaben, Anzahl Produkte und Kollektionen, neueste Produkte mit Preisen"),
        ("TYPO3", "Backend & Extensions", "Backend-Anmeldung, Versionshinweise, Headless-Erweiterung (JSON) und Sitemap"),
        ("Contao", "Backend & Erweiterungen", "Backend-Anmeldung, Contao-Manager, Versionshinweise und Sitemap"),
    };

    /// <summary>System aus der Analyse, für das es eine API-Auswertung gibt (sonst null).</summary>
    public static string SystemFor(CmsResult r) =>
        r?.Detections.Where(d => d.Category is "CMS" or "Shop" && d.Confidence >= 60)
            .Select(d => d.Name).FirstOrDefault(n => Supported.Any(s => s.System == n));

    /// <summary>Text für das Angebot „API auswerten?“.</summary>
    public static string OfferText(CmsResult r, string system)
    {
        var s = Supported.First(x => x.System == system);
        var api = system == "WordPress" && r.WpApiRoot != null ? r.WpApiRoot : s.Api;
        return L.F("Fragt {0} ab: {1}.", api, L.T(s.Description));
    }

    public static async Task<List<CheckSection>> AnalyzeAsync(CmsResult r, CancellationToken ct = default)
    {
        var system = SystemFor(r) ?? throw new InvalidOperationException(L.T("Für dieses System gibt es keine API-Auswertung."));
        if (system == "WordPress") return await WordPressApi.AnalyzeAsync(r.FinalUrl, r.WpApiRoot, ct);

        var origin = new Uri(r.FinalUrl).GetLeftPart(UriPartial.Authority);
        using var c = new Client(origin);
        var sections = system switch
        {
            "Joomla" => await JoomlaAsync(c, ct),
            "Drupal" => await DrupalAsync(c, ct),
            "Shopware 6" or "Shopware" => await ShopwareAsync(c, ct),
            "Magento" => await MagentoAsync(c, ct),
            "PrestaShop" => await PrestaShopAsync(c, ct),
            "Strapi" => await StrapiAsync(c, ct),
            "Directus" => await DirectusAsync(c, ct),
            "Ghost" => await GhostAsync(c, r.Html, ct),
            "Shopify" => await ShopifyAsync(c, ct),
            "TYPO3" => await Typo3Async(c, ct),
            "Contao" => await ContaoAsync(c, ct),
            _ => new List<CheckSection>(),
        };
        return sections;
    }

    // ===================================================================== Joomla

    private static async Task<List<CheckSection>> JoomlaAsync(Client c, CancellationToken ct)
    {
        var tManifest = c.GetAsync("/administrator/manifests/files/joomla.xml", ct);
        var tApi = c.GetAsync("/api/index.php/v1/content/articles", ct);
        var tConfig = c.GetAsync("/api/index.php/v1/config/application?public=true", ct);
        var tUsers = c.GetAsync("/api/index.php/v1/users?public=true", ct);
        var tAdmin = c.GetAsync("/administrator/", ct);
        await Task.WhenAll(tManifest, tApi, tConfig, tUsers, tAdmin);

        var info = new List<CheckItem>();
        var ver = Regex.Match(tManifest.Result.Body, @"<version>([^<]+)</version>");
        info.Add(ver.Success && tManifest.Result.Status == 200
            ? new CheckItem("Version", ver.Groups[1].Value + " " + L.T("(laut /administrator/manifests/files/joomla.xml – öffentlich lesbar)"), Level.Warn)
            : new CheckItem("Version", L.T("Nicht öffentlich auslesbar."), Level.Ok));
        var api = tApi.Result;
        info.Add(api.Status switch
        {
            401 or 403 => new CheckItem("Web-Services-API", L.T("Aktiv (/api/index.php/v1/) – verlangt einen API-Token."), Level.Ok),
            200 => new CheckItem("Web-Services-API", L.T("Beiträge ohne Anmeldung abrufbar."), Level.Warn),
            404 => new CheckItem("Web-Services-API", L.T("Nicht vorhanden oder deaktiviert (Joomla 3 hat keine Web-Services-API)."), Level.Ok),
            _ => new CheckItem("Web-Services-API", Status(api), Level.Info),
        });

        var sec = new List<CheckItem>();
        bool configOpen = tConfig.Result.Status == 200 && tConfig.Result.Body.Contains("\"attributes\"");
        bool usersOpen = tUsers.Result.Status == 200 && tUsers.Result.Body.Contains("\"attributes\"");
        sec.Add(configOpen
            ? new CheckItem("CVE-2023-23752", L.T("Konfiguration ohne Anmeldung abrufbar (enthält u. a. Datenbank-Zugangsdaten)! Joomla sofort aktualisieren und alle Passwörter ändern."), Level.Bad)
            : new CheckItem("CVE-2023-23752", L.T("Konfiguration ist nicht öffentlich abrufbar."), Level.Ok));
        sec.Add(usersOpen
            ? new CheckItem("Benutzerliste", L.T("Über die API ohne Anmeldung abrufbar (CVE-2023-23752)."), Level.Bad)
            : new CheckItem("Benutzerliste", L.T("Nicht öffentlich abrufbar."), Level.Ok));
        sec.Add(AdminItem("/administrator/", tAdmin.Result, "joomla|mod-login|login-form"));
        return new List<CheckSection>
        {
            new("Joomla – Web Services", info),
            new("Sicherheit", sec, "Nur lesende Abfragen ohne Anmeldung – so, wie sie jeder Besucher machen kann. Geschützte Inhalte werden nicht angezeigt."),
        };
    }

    // ===================================================================== Drupal

    private static async Task<List<CheckSection>> DrupalAsync(Client c, CancellationToken ct)
    {
        var index = await c.GetAsync("/jsonapi", ct);
        var info = new List<CheckItem>();
        var sections = new List<CheckSection>();
        var root = Json(index);
        var types = new List<(string Type, string Href)>();
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("links", out var links) && links.ValueKind == JsonValueKind.Object)
        {
            foreach (var l in links.EnumerateObject())
                if (l.Name != "self" && Str(l.Value, "href") is { Length: > 0 } href) types.Add((l.Name, href));
            info.Add(new CheckItem("JSON:API", L.F("Aktiv (/jsonapi) – {0} Ressourcentypen öffentlich gelistet.", types.Count), Level.Info));
            var groups = types.GroupBy(t => t.Type.Split("--")[0]).OrderBy(g => g.Key)
                .Select(g => new CheckItem(Pretty(g.Key), string.Join(", ", g.Select(t => t.Type.Contains("--") ? t.Type.Split("--")[1] : t.Type))));
            info.AddRange(groups);
        }
        else
            info.Add(new CheckItem("JSON:API", index.Status == 404 ? L.T("Modul nicht aktiv (/jsonapi nicht vorhanden).") : Status(index), Level.Ok));
        sections.Add(new CheckSection("Drupal – JSON:API", info));

        if (types.Count > 0)
        {
            // neueste Inhalte je Inhaltstyp
            var nodeTypes = types.Where(t => t.Type.StartsWith("node--")).Take(12).ToList();
            var tasks = nodeTypes.Select(t => c.GetAsync(Rel(t.Href, c.Origin) + $"?page[limit]=1&sort=-created&fields[{t.Type}]=title,created", ct)).ToList();
            var tUsers = c.GetAsync("/jsonapi/user/user?page[limit]=50&fields[user--user]=name,display_name", ct);
            await Task.WhenAll(tasks.Cast<Task>().Append(tUsers));
            var content = new List<CheckItem>();
            for (int i = 0; i < nodeTypes.Count; i++)
            {
                var d = Json(tasks[i].Result);
                var first = d.ValueKind == JsonValueKind.Object && d.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array && data.GetArrayLength() > 0 ? data[0] : default;
                var attr = first.ValueKind == JsonValueKind.Object && first.TryGetProperty("attributes", out var a) ? a : default;
                content.Add(new CheckItem(L.F("Inhaltstyp „{0}“", nodeTypes[i].Type[6..]),
                    attr.ValueKind == JsonValueKind.Object
                        ? L.F("neuester vom {0}: „{1}“", ShortDate(Str(attr, "created")), Str(attr, "title"))
                        : tasks[i].Result.Status is 401 or 403 ? L.T("nicht öffentlich") : L.T("keine Einträge")));
            }
            if (content.Count > 0) sections.Add(new CheckSection("Inhalte", content));

            var sec = new List<CheckItem>();
            var users = Json(tUsers.Result);
            var names = users.ValueKind == JsonValueKind.Object && users.TryGetProperty("data", out var ud) && ud.ValueKind == JsonValueKind.Array
                ? ud.EnumerateArray().Select(u => u.TryGetProperty("attributes", out var ua) ? Str(ua, "name") is { Length: > 0 } n ? n : Str(ua, "display_name") : "")
                    .Where(n => n != "" && !n.Equals("Anonymous", StringComparison.OrdinalIgnoreCase)).ToList()
                : new List<string>();
            sec.Add(names.Count > 0
                ? new CheckItem("Benutzerliste", L.F("Öffentlich abrufbar ({0}): {1} – Berechtigung „Benutzerprofile ansehen“ für anonyme Besucher entziehen.", names.Count, string.Join(", ", names.Take(20))), Level.Warn)
                : new CheckItem("Benutzerliste", L.T("Keine Benutzernamen öffentlich abrufbar."), Level.Ok));
            sections.Add(new CheckSection("Sicherheit", sec));
        }
        var chlog = await c.GetAsync("/core/CHANGELOG.txt", ct);
        var dv = Regex.Match(chlog.Body ?? "", @"^Drupal ([\d.]+)", RegexOptions.Multiline);
        sections[0].Items.Add(chlog.Status == 200 && dv.Success
            ? new CheckItem("Version", L.F("{0} (laut /core/CHANGELOG.txt – öffentlich lesbar)", dv.Groups[1].Value), Level.Warn)
            : new CheckItem("Version", L.T("Nicht öffentlich auslesbar."), Level.Ok));
        return sections;
    }

    // ===================================================================== Shopware

    private static async Task<List<CheckSection>> ShopwareAsync(Client c, CancellationToken ct)
    {
        var tVersion = c.GetAsync("/api/_info/version", ct);
        var tConfig = c.GetAsync("/api/_info/config", ct);
        var tStore = c.GetAsync("/store-api/context", ct);
        var tAdmin = c.GetAsync("/admin", ct);
        var tSw5 = c.GetAsync("/api/version", ct);
        await Task.WhenAll(tVersion, tConfig, tStore, tAdmin, tSw5);

        var items = new List<CheckItem>();
        var v = tVersion.Result;
        items.Add(v.Status switch
        {
            401 or 403 => new CheckItem("Admin-API", L.T("Aktiv (/api/) – verlangt eine Anmeldung."), Level.Ok),
            200 => new CheckItem("Admin-API", L.F("Version ohne Anmeldung abrufbar: {0}", Str(Json(v), "version")), Level.Warn),
            _ => new CheckItem("Admin-API", Status(v), Level.Info),
        });
        if (tConfig.Result.Status == 200)
            items.Add(new CheckItem("Admin-API-Konfiguration", L.T("Ohne Anmeldung abrufbar (/api/_info/config)."), Level.Warn));
        var s = tStore.Result;
        items.Add(new CheckItem("Store-API", s.Status switch
        {
            200 => L.T("Ohne Zugriffsschlüssel abrufbar."),
            400 or 401 or 412 => L.T("Aktiv (/store-api/) – verlangt den Zugriffsschlüssel des Verkaufskanals (sw-access-key, Standard bei Shopware 6)."),
            404 => L.T("Nicht vorhanden."),
            _ => Status(s),
        }, Level.Info));
        if (tSw5.Result.Status is 200 or 401 && tSw5.Result.Body.Contains("version", StringComparison.OrdinalIgnoreCase))
            items.Add(new CheckItem("Shopware-5-API", tSw5.Result.Status == 200 ? L.T("Version ohne Anmeldung abrufbar (/api/version).") : L.T("Aktiv (/api/version) – verlangt eine Anmeldung."),
                tSw5.Result.Status == 200 ? Level.Warn : Level.Ok));
        items.Add(AdminItem("/admin", tAdmin.Result, "sw-admin|shopware|administration"));
        return new List<CheckSection> { new("Shopware – APIs", items, "Nur lesende Abfragen ohne Anmeldung.") };
    }

    // ===================================================================== Magento

    private static async Task<List<CheckSection>> MagentoAsync(Client c, CancellationToken ct)
    {
        var tVer = c.GetAsync("/magento_version", ct);
        var tStores = c.GetAsync("/rest/V1/store/storeConfigs", ct);
        var tCur = c.GetAsync("/rest/V1/directory/currency", ct);
        var tProducts = c.GetAsync("/rest/V1/products?searchCriteria[pageSize]=1", ct);
        var tCustomers = c.GetAsync("/rest/V1/customers/search?searchCriteria[pageSize]=1", ct);
        await Task.WhenAll(tVer, tStores, tCur, tProducts, tCustomers);

        var info = new List<CheckItem>();
        info.Add(tVer.Result.Status == 200 && tVer.Result.Body.StartsWith("Magento", StringComparison.OrdinalIgnoreCase)
            ? new CheckItem("Version", tVer.Result.Body.Trim() + " " + L.T("(laut /magento_version – öffentlich)"), Level.Warn)
            : new CheckItem("Version", L.T("Nicht öffentlich auslesbar (/magento_version gesperrt)."), Level.Ok));
        var stores = Json(tStores.Result);
        if (stores.ValueKind == JsonValueKind.Array)
            foreach (var st in stores.EnumerateArray())
                info.Add(new CheckItem(L.F("Store „{0}“", Str(st, "code")),
                    $"{Str(st, "base_url")} · {Str(st, "locale")} · {Str(st, "base_currency_code")} · {Str(st, "timezone")}"));
        var cur = Json(tCur.Result);
        if (cur.ValueKind == JsonValueKind.Object)
        {
            var avail = cur.TryGetProperty("available_currency_codes", out var ac) && ac.ValueKind == JsonValueKind.Array
                ? string.Join(", ", ac.EnumerateArray().Select(x => x.GetString())) : "";
            info.Add(new CheckItem("Währungen", L.F("Basis {0}, Anzeige {1}", Str(cur, "base_currency_code"), Str(cur, "default_display_currency_code")) + (avail != "" ? " · " + avail : "")));
        }

        var sec = new List<CheckItem>();
        var p = tProducts.Result;
        var pj = Json(p);
        sec.Add(p.Status == 200
            ? new CheckItem("Produktkatalog-API", L.F("Ohne Anmeldung abrufbar ({0} Produkte) – „Allow Anonymous Guest Access“ ist aktiv.", Str(pj, "total_count")), Level.Warn)
            : new CheckItem("Produktkatalog-API", L.T("Verlangt eine Anmeldung."), Level.Ok));
        sec.Add(tCustomers.Result.Status == 200
            ? new CheckItem("Kunden-API", L.T("Kundendaten ohne Anmeldung abrufbar!"), Level.Bad)
            : new CheckItem("Kunden-API", L.T("Verlangt eine Anmeldung."), Level.Ok));
        return new List<CheckSection> { new("Magento – REST-API", info), new("Sicherheit", sec) };
    }

    // ===================================================================== PrestaShop

    private static async Task<List<CheckSection>> PrestaShopAsync(Client c, CancellationToken ct)
    {
        var api = await c.GetAsync("/api/", ct);
        var items = new List<CheckItem>();
        if (api.Status == 401 && (api.Header("WWW-Authenticate") ?? "").Contains("PrestaShop", StringComparison.OrdinalIgnoreCase))
            items.Add(new CheckItem("Webservice-API", L.T("Aktiv (/api/) – verlangt einen Webservice-Schlüssel."), Level.Ok));
        else if (api.Status == 200 && api.Body.Contains("<api", StringComparison.OrdinalIgnoreCase))
            items.Add(new CheckItem("Webservice-API", L.T("Ohne Schlüssel abrufbar!"), Level.Bad));
        else if (api.Status == 503 || api.Body.Contains("disabled", StringComparison.OrdinalIgnoreCase))
            items.Add(new CheckItem("Webservice-API", L.T("Deaktiviert."), Level.Ok));
        else
            items.Add(new CheckItem("Webservice-API", Status(api), Level.Info));
        var ps = api.Header("PSWS-Version");
        if (!string.IsNullOrEmpty(ps)) items.Add(new CheckItem("Version", L.F("{0} (laut Header PSWS-Version)", ps), Level.Warn));
        items.Add(new CheckItem("Administration", L.T("PrestaShop verwendet einen zufälligen Admin-Ordnernamen – er wird nicht geraten."), Level.Info));
        return new List<CheckSection> { new("PrestaShop – Webservice", items) };
    }

    // ===================================================================== Strapi

    private static async Task<List<CheckSection>> StrapiAsync(Client c, CancellationToken ct)
    {
        var tInit = c.GetAsync("/admin/init", ct);
        var tType = c.GetAsync("/admin/project-type", ct);
        var tUsers = c.GetAsync("/api/users", ct);
        var tFiles = c.GetAsync("/api/upload/files", ct);
        var tDocs = c.GetAsync("/documentation", ct);
        var tCtb = c.GetAsync("/content-type-builder/content-types", ct);
        var tHealth = c.GetAsync("/_health", ct);
        await Task.WhenAll(tInit, tType, tUsers, tFiles, tDocs, tCtb, tHealth);

        var info = new List<CheckItem>();
        var init = Json(tInit.Result);
        var data = init.ValueKind == JsonValueKind.Object && init.TryGetProperty("data", out var d) ? d : default;
        if (data.ValueKind == JsonValueKind.Object)
        {
            info.Add(new CheckItem("Administration", L.F("/admin · Administrator eingerichtet: {0}", Bool(data, "hasAdmin")), Level.Info));
            if (Str(data, "uuid") != "") info.Add(new CheckItem("Projekt-ID", Str(data, "uuid")));
        }
        var type = Json(tType.Result);
        if (type.ValueKind == JsonValueKind.Object && type.TryGetProperty("data", out var td))
            info.Add(new CheckItem("Edition", Bool(td, "isEE") == L.T("ja") ? "Enterprise" : "Community"));
        info.Add(new CheckItem("Zustand", tHealth.Result.Status is 200 or 204 ? L.T("/_health antwortet") : Status(tHealth.Result)));

        var sec = new List<CheckItem>();
        var users = Json(tUsers.Result);
        sec.Add(users.ValueKind == JsonValueKind.Array
            ? new CheckItem("Benutzerliste", L.F("Öffentlich abrufbar ({0} Benutzer) – in „Users & Permissions“ für die Rolle Public sperren.", users.GetArrayLength()), Level.Bad)
            : new CheckItem("Benutzerliste", L.T("Nicht öffentlich abrufbar."), Level.Ok));
        var files = Json(tFiles.Result);
        sec.Add(files.ValueKind == JsonValueKind.Array
            ? new CheckItem("Medienbibliothek", L.F("Liste öffentlich abrufbar ({0} Dateien).", files.GetArrayLength()), Level.Warn)
            : new CheckItem("Medienbibliothek", L.T("Liste nicht öffentlich."), Level.Ok));
        sec.Add(tDocs.Result.Status == 200
            ? new CheckItem("API-Dokumentation", L.T("Öffentlich (/documentation) – zeigt alle Endpunkte."), Level.Warn)
            : new CheckItem("API-Dokumentation", L.T("Nicht öffentlich."), Level.Ok));
        sec.Add(tCtb.Result.Status == 200
            ? new CheckItem("Content-Type-Builder", L.T("Ohne Anmeldung abrufbar!"), Level.Bad)
            : new CheckItem("Content-Type-Builder", L.T("Verlangt eine Anmeldung."), Level.Ok));
        return new List<CheckSection> { new("Strapi", info), new("Sicherheit", sec) };
    }

    // ===================================================================== Directus

    private static async Task<List<CheckSection>> DirectusAsync(Client c, CancellationToken ct)
    {
        var tInfo = c.GetAsync("/server/info", ct);
        var tHealth = c.GetAsync("/server/health", ct);
        var tCols = c.GetAsync("/collections", ct);
        var tUsers = c.GetAsync("/users?limit=50&fields=first_name,last_name,email", ct);
        var tSpecs = c.GetAsync("/server/specs/oas", ct);
        await Task.WhenAll(tInfo, tHealth, tCols, tUsers, tSpecs);

        var info = new List<CheckItem>();
        var si = Data(Json(tInfo.Result));
        var proj = si.ValueKind == JsonValueKind.Object && si.TryGetProperty("project", out var p) ? p : si;
        foreach (var (label, prop) in new[] { ("Projekt", "project_name"), ("Beschreibung", "project_descriptor"), ("Sprache", "default_language"), ("URL", "project_url") })
            if (Str(proj, prop) is { Length: > 0 } v) info.Add(new CheckItem(label, v));
        if (si.ValueKind == JsonValueKind.Object && si.TryGetProperty("version", out var ver)) info.Add(new CheckItem("Version", ver.ToString(), Level.Warn));
        var health = Json(tHealth.Result);
        if (Str(health, "status") is { Length: > 0 } hs) info.Add(new CheckItem("Zustand", hs));

        var sec = new List<CheckItem>();
        var cols = Data(Json(tCols.Result));
        if (cols.ValueKind == JsonValueKind.Array)
        {
            var names = cols.EnumerateArray().Select(x => Str(x, "collection")).Where(n => n != "" && !n.StartsWith("directus_")).ToList();
            sec.Add(new CheckItem("Öffentliche Sammlungen", names.Count > 0 ? string.Join(", ", names) : L.T("keine"), names.Count > 0 ? Level.Info : Level.Ok));
        }
        else sec.Add(new CheckItem("Sammlungen", L.T("Liste nicht öffentlich."), Level.Ok));
        var users = Data(Json(tUsers.Result));
        sec.Add(users.ValueKind == JsonValueKind.Array && users.GetArrayLength() > 0
            ? new CheckItem("Benutzerliste", L.F("Öffentlich abrufbar ({0} Benutzer) – Leserechte der Public-Rolle auf directus_users entfernen.", users.GetArrayLength()), Level.Bad)
            : new CheckItem("Benutzerliste", L.T("Nicht öffentlich abrufbar."), Level.Ok));
        sec.Add(tSpecs.Result.Status == 200
            ? new CheckItem("OpenAPI-Beschreibung", L.T("Öffentlich (/server/specs/oas)."), Level.Info)
            : new CheckItem("OpenAPI-Beschreibung", L.T("Nicht öffentlich."), Level.Ok));
        return new List<CheckSection> { new("Directus", info), new("Sicherheit", sec) };
    }

    // ===================================================================== Ghost

    private static async Task<List<CheckSection>> GhostAsync(Client c, string html, CancellationToken ct)
    {
        var sections = new List<CheckSection>();
        var site = await c.GetAsync("/ghost/api/admin/site/", ct);
        var info = new List<CheckItem>();
        var s = Json(site);
        var sd = s.ValueKind == JsonValueKind.Object && s.TryGetProperty("site", out var x) ? x : default;
        foreach (var (label, prop) in new[] { ("Titel", "title"), ("Beschreibung", "description"), ("URL", "url") })
            if (Str(sd, prop) is { Length: > 0 } v) info.Add(new CheckItem(label, v));
        if (Str(sd, "version") is { Length: > 0 } gv) info.Add(new CheckItem("Version", gv + " " + L.T("(laut /ghost/api/admin/site/ – bei Ghost immer öffentlich)")));

        // Content-API-Schlüssel steht öffentlich im Seitenquelltext (Portal/Suche)
        var key = Regex.Match(html ?? "", @"data-key=""([0-9a-f]{26})""").Groups[1].Value;
        if (key == "") key = Regex.Match(html ?? "", @"(?:key|apiKey)[""']?\s*[:=]\s*[""']([0-9a-f]{26})[""']").Groups[1].Value;
        info.Add(new CheckItem("Content-API", key != "" ? L.T("Schlüssel im Seitenquelltext gefunden (bei Ghost so vorgesehen – nur Lesezugriff auf veröffentlichte Inhalte).") : L.T("Kein Schlüssel im Seitenquelltext – Inhalte können nicht abgefragt werden.")));
        sections.Add(new CheckSection("Ghost", info));

        if (key != "")
        {
            string Q(string path) => $"/ghost/api/content/{path}{(path.Contains('?') ? "&" : "?")}key={key}";
            var tPosts = c.GetAsync(Q("posts/?limit=10&fields=title,published_at,url"), ct);
            var tPages = c.GetAsync(Q("pages/?limit=1&fields=id"), ct);
            var tTags = c.GetAsync(Q("tags/?limit=10&include=count.posts&order=count.posts%20desc"), ct);
            var tAuthors = c.GetAsync(Q("authors/?limit=20&fields=name,slug"), ct);
            var tSettings = c.GetAsync(Q("settings/"), ct);
            await Task.WhenAll(tPosts, tPages, tTags, tAuthors, tSettings);

            string Total(Resp r) => Json(r) is { ValueKind: JsonValueKind.Object } j && j.TryGetProperty("meta", out var m)
                && m.TryGetProperty("pagination", out var pg) && pg.TryGetProperty("total", out var t) ? t.ToString() : L.T("nicht verfügbar");
            var content = new List<CheckItem>
            {
                new("Beiträge", Total(tPosts.Result)),
                new("Seiten", Total(tPages.Result)),
            };
            var tags = Json(tTags.Result);
            if (tags.ValueKind == JsonValueKind.Object && tags.TryGetProperty("tags", out var ta))
                content.Add(new CheckItem("Schlagwörter", Total(tTags.Result) + " · " + L.T("meistgenutzt: ") + string.Join(", ",
                    ta.EnumerateArray().Select(t => $"{Str(t, "name")}" + (t.TryGetProperty("count", out var cn) ? $" ({Str(cn, "posts")})" : "")))));
            var authors = Json(tAuthors.Result);
            if (authors.ValueKind == JsonValueKind.Object && authors.TryGetProperty("authors", out var au))
                content.Add(new CheckItem("Autoren", string.Join(", ", au.EnumerateArray().Select(a => $"{Str(a, "name")} ({Str(a, "slug")})"))));
            var settings = Json(tSettings.Result);
            if (settings.ValueKind == JsonValueKind.Object && settings.TryGetProperty("settings", out var st))
            {
                if (Str(st, "lang") is { Length: > 0 } lang) content.Add(new CheckItem("Sprache", lang));
                if (Str(st, "timezone") is { Length: > 0 } tz) content.Add(new CheckItem("Zeitzone", tz));
                if (st.TryGetProperty("members_signup_access", out var msa)) content.Add(new CheckItem("Mitglieder-Registrierung", msa.ToString()));
            }
            sections.Add(new CheckSection("Inhalte", content));

            var posts = Json(tPosts.Result);
            if (posts.ValueKind == JsonValueKind.Object && posts.TryGetProperty("posts", out var pa) && pa.GetArrayLength() > 0)
                sections.Add(new CheckSection(L.F("Neueste Beiträge ({0})", pa.GetArrayLength()),
                    pa.EnumerateArray().Select(p => new CheckItem(ShortDate(Str(p, "published_at")), $"{Str(p, "title")} · {Str(p, "url")}"))));
        }
        return sections;
    }

    // ===================================================================== Shopify

    private static async Task<List<CheckSection>> ShopifyAsync(Client c, CancellationToken ct)
    {
        var tMeta = c.GetAsync("/meta.json", ct);
        var tProducts = c.GetAsync("/products.json?limit=10", ct);
        var tCollections = c.GetAsync("/collections.json?limit=250", ct);
        await Task.WhenAll(tMeta, tProducts, tCollections);

        var sections = new List<CheckSection>();
        var info = new List<CheckItem>();
        var m = Json(tMeta.Result);
        foreach (var (label, prop) in new[] { ("Name", "name"), ("Beschreibung", "description"), ("Domain", "domain"), ("myshopify-Domain", "myshopify_domain"),
                     ("Stadt", "city"), ("Region", "province"), ("Land", "country"), ("Währung", "currency"), ("Produkte", "published_products_count"), ("Kollektionen", "published_collections_count") })
            if (Str(m, prop) is { Length: > 0 } v) info.Add(new CheckItem(label, v));
        if (info.Count == 0) info.Add(new CheckItem("/meta.json", Status(tMeta.Result)));
        sections.Add(new CheckSection("Shopify – Shop-Angaben", info, "Aus /meta.json – bei Shopify für jeden Shop öffentlich."));

        var products = Json(tProducts.Result);
        if (products.ValueKind == JsonValueKind.Object && products.TryGetProperty("products", out var pa) && pa.GetArrayLength() > 0)
        {
            sections.Add(new CheckSection(L.F("Neueste Produkte ({0})", pa.GetArrayLength()), pa.EnumerateArray().Select(p =>
            {
                var prices = p.TryGetProperty("variants", out var va) ? va.EnumerateArray().Select(v => decimal.TryParse(Str(v, "price"), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : -1).Where(d => d >= 0).ToList() : new List<decimal>();
                var price = prices.Count == 0 ? "" : prices.Min() == prices.Max() ? prices.Min().ToString("0.00") : $"{prices.Min():0.00}–{prices.Max():0.00}";
                return new CheckItem(Str(p, "title"), string.Join(" · ", new[] { Str(p, "vendor"), Str(p, "product_type"), price, ShortDate(Str(p, "published_at")) }.Where(x => x != "")));
            })));
        }
        var cols = Json(tCollections.Result);
        if (cols.ValueKind == JsonValueKind.Object && cols.TryGetProperty("collections", out var ca) && ca.GetArrayLength() > 0)
            sections.Add(new CheckSection(L.F("Kollektionen ({0})", ca.GetArrayLength()), new[]
                { new CheckItem("", string.Join(", ", ca.EnumerateArray().Select(x => Str(x, "title")))) }));
        sections.Add(new CheckSection("Hinweis", new[] { new CheckItem("Storefront-API", L.T("Die GraphQL-Storefront-API verlangt einen Zugriffstoken und wird nicht abgefragt.")) }));
        return sections;
    }

    // ===================================================================== TYPO3 / Contao

    private static async Task<List<CheckSection>> Typo3Async(Client c, CancellationToken ct)
    {
        var tBackend = c.GetAsync("/typo3/", ct);
        var tComposer = c.GetAsync("/typo3/sysext/core/composer.json", ct);
        var tChangelog = c.GetAsync("/typo3/sysext/core/Documentation/Changelog-13.rst", ct);
        var tHeadless = c.GetAsync("/?type=834", ct);
        var tInstall = c.GetAsync("/typo3/install.php", ct);
        await Task.WhenAll(tBackend, tComposer, tChangelog, tHeadless, tInstall);

        var items = new List<CheckItem> { AdminItem("/typo3/", tBackend.Result, "typo3") };
        var cv = Regex.Match(tComposer.Result.Body ?? "", @"""version""\s*:\s*""([^""]+)""");
        items.Add(tComposer.Result.Status == 200 && tComposer.Result.Body.Contains("typo3/cms-core")
            ? new CheckItem("Systemdateien", L.F("/typo3/sysext/ ist öffentlich lesbar{0} – Hinweis auf eine Installation ohne Composer-Public-Ordner.", cv.Success ? " (Version " + cv.Groups[1].Value + ")" : ""), Level.Warn)
            : new CheckItem("Systemdateien", L.T("/typo3/sysext/ ist nicht öffentlich lesbar."), Level.Ok));
        items.Add(tInstall.Result.Status == 200 && tInstall.Result.Body.Contains("Install", StringComparison.OrdinalIgnoreCase)
            ? new CheckItem("Install-Tool", L.T("/typo3/install.php erreichbar (passwortgeschützt, sollte aber nach Möglichkeit gesperrt sein)."), Level.Info)
            : new CheckItem("Install-Tool", L.T("Nicht erreichbar."), Level.Ok));
        var h = tHeadless.Result;
        items.Add(h.Status == 200 && Json(h).ValueKind == JsonValueKind.Object
            ? new CheckItem("Headless (EXT:headless)", L.T("Aktiv – Seiten sind als JSON abrufbar (/?type=834)."), Level.Info)
            : new CheckItem("Headless (EXT:headless)", L.T("Nicht aktiv."), Level.Info));
        items.Add(new CheckItem("REST-API", L.T("TYPO3 hat keine Standard-REST-API – Schnittstellen hängen von installierten Extensions ab.")));
        return new List<CheckSection> { new("TYPO3", items) };
    }

    private static async Task<List<CheckSection>> ContaoAsync(Client c, CancellationToken ct)
    {
        var tBackend = c.GetAsync("/contao/login", ct);
        var tManager = c.GetAsync("/contao-manager.phar.php", ct);
        var tApi = c.GetAsync("/api/", ct);
        var tPreview = c.GetAsync("/preview.php", ct);
        await Task.WhenAll(tBackend, tManager, tApi, tPreview);

        var items = new List<CheckItem> { AdminItem("/contao", tBackend.Result, "contao") };
        items.Add(tManager.Result.Status == 200
            ? new CheckItem("Contao-Manager", L.T("/contao-manager.phar.php erreichbar (passwortgeschützt, sollte aber nach Möglichkeit gesperrt sein)."), Level.Info)
            : new CheckItem("Contao-Manager", L.T("Nicht erreichbar."), Level.Ok));
        items.Add(tPreview.Result.Status is 200 or 302
            ? new CheckItem("Vorschau", L.T("/preview.php erreichbar."), Level.Info)
            : new CheckItem("Vorschau", L.T("Nicht erreichbar."), Level.Ok));
        items.Add(tApi.Result.Status is 200 or 401 or 403 && Json(tApi.Result).ValueKind != JsonValueKind.Undefined
            ? new CheckItem("REST-API (/api/)", L.F("Vorhanden ({0}) – stammt von einer Erweiterung.", Status(tApi.Result)), Level.Info)
            : new CheckItem("REST-API", L.T("Contao hat keine Standard-REST-API – Schnittstellen hängen von installierten Erweiterungen ab.")));
        return new List<CheckSection> { new("Contao", items) };
    }

    // ===================================================================== Hilfen

    private static CheckItem AdminItem(string path, Resp r, string pattern) =>
        r.Status == 200 && Regex.IsMatch(r.Body ?? "", pattern, RegexOptions.IgnoreCase)
            ? new CheckItem("Administration", L.F("Anmeldeseite {0} öffentlich erreichbar – wenn möglich per IP oder zusätzlichem Schutz einschränken.", path), Level.Info)
            : new CheckItem("Administration", L.F("{0}: {1}", path, Status(r)), Level.Ok);

    private static string Status(Resp r) => r.Status == 0 ? L.F("nicht erreichbar ({0})", r.Body) : $"HTTP {r.Status}";

    private static string Pretty(string group) => group switch
    {
        "node" => L.T("Inhaltstypen"),
        "taxonomy_term" => L.T("Taxonomien"),
        "media" => L.T("Medientypen"),
        "user" => L.T("Benutzer"),
        "file" => L.T("Dateien"),
        "menu_link_content" => L.T("Menüs"),
        "paragraph" => "Paragraphs",
        "block_content" => L.T("Blöcke"),
        _ => group,
    };

    private static string Rel(string href, string origin) => href.StartsWith(origin, StringComparison.OrdinalIgnoreCase) ? href[origin.Length..] : new Uri(href).PathAndQuery;

    private static string ShortDate(string s) => DateTime.TryParse(s, out var d) ? d.ToString("d") : s;

    private static string Bool(JsonElement e, string prop) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.True ? L.T("ja") : L.T("nein");

    private static JsonElement Data(JsonElement e) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty("data", out var d) ? d : default;

    private static string Str(JsonElement e, string prop) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out var v)
        ? v.ValueKind switch { JsonValueKind.String => v.GetString() ?? "", JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => v.ToString(), _ => "" }
        : "";

    private static JsonElement Json(Resp r)
    {
        if (r.Status != 200 || string.IsNullOrWhiteSpace(r.Body)) return default;
        try { using var d = JsonDocument.Parse(r.Body); return d.RootElement.Clone(); }
        catch { return default; }
    }

    private sealed record Resp(int Status, string Body, Dictionary<string, string> Headers)
    {
        public string Header(string name) => Headers.TryGetValue(name, out var v) ? v : null;
    }

    private sealed class Client : IDisposable
    {
        private readonly HttpClient _http;
        public string Origin { get; }

        public Client(string origin)
        {
            Origin = origin;
            _http = new HttpClient(new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.All,
                ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
                MaxAutomaticRedirections = 5,
            })
            { Timeout = TimeSpan.FromSeconds(20) };
            _http.DefaultRequestHeaders.UserAgent.ParseAdd(NetUtil.UserAgent);
            _http.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/html;q=0.8, */*;q=0.5");
        }

        public async Task<Resp> GetAsync(string pathOrUrl, CancellationToken ct)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(12000);
                var url = pathOrUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? pathOrUrl : Origin + pathOrUrl;
                using var res = await _http.GetAsync(url, cts.Token);
                var body = await res.Content.ReadAsStringAsync(cts.Token);
                if (body.Length > 3_000_000) body = body[..3_000_000];
                var headers = res.Headers.Concat(res.Content.Headers)
                    .GroupBy(h => h.Key, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => string.Join(", ", g.SelectMany(h => h.Value)), StringComparer.OrdinalIgnoreCase);
                return new Resp((int)res.StatusCode, body, headers);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                return new Resp(0, NetUtil.ErrorText(ex), new Dictionary<string, string>());
            }
        }

        public void Dispose() => _http.Dispose();
    }
}
