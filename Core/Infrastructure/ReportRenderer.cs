using System.Net;
using System.Text;

namespace NetStuff.Infrastructure;

/// <summary>Erzeugt den HTML-Bericht (druckfertig, Grundlage für PDF).</summary>
public static class ReportRenderer
{
    private static string E(string s) => WebUtility.HtmlEncode(s ?? "");

    public static string LogoDataUri()
    {
        try
        {
            var s = AppSettings.Current;
            if (!string.IsNullOrEmpty(s.LogoFile) && File.Exists(s.LogoFile))
            {
                var ext = Path.GetExtension(s.LogoFile).ToLowerInvariant().TrimStart('.');
                var mime = ext switch { "svg" => "image/svg+xml", "jpg" or "jpeg" => "image/jpeg", _ => "image/" + ext };
                return $"data:{mime};base64,{Convert.ToBase64String(File.ReadAllBytes(s.LogoFile))}";
            }
            using var res = typeof(ReportRenderer).Assembly.GetManifestResourceStream("NetStuff.Assets.netstuff.png");
            using var ms = new MemoryStream();
            res.CopyTo(ms);
            return "data:image/png;base64," + Convert.ToBase64String(ms.ToArray());
        }
        catch { return ""; }
    }

    public static string Html(Project p)
    {
        var s = AppSettings.Current;
        var sb = new StringBuilder();
        sb.Append($@"<!doctype html><html lang=""{L.Language}""><head><meta charset=""utf-8""><title>{E(L.T("Netzwerkbericht"))} – {E(p.Display)}</title>
<style>
@page {{ size: A4; margin: 16mm 14mm; }}
* {{ box-sizing: border-box; }}
body {{ font-family: 'Segoe UI', Arial, sans-serif; color: #1f2328; font-size: 10.5pt; line-height: 1.45; margin: 0; background: #fff; }}
.wrap {{ max-width: 900px; margin: 0 auto; padding: 24px; }}
header {{ display: flex; align-items: center; justify-content: space-between; border-bottom: 3px solid; border-image: linear-gradient(90deg,#e53935,#1565c0) 1; padding-bottom: 14px; }}
header img {{ max-height: 64px; max-width: 220px; }}
.company {{ text-align: right; font-size: 9.5pt; color: #57606a; }}
.company b {{ color: #1f2328; font-size: 11pt; }}
h1 {{ font-size: 22pt; margin: 26px 0 6px; }}
.sub {{ color: #57606a; margin-bottom: 18px; }}
.meta {{ display: grid; grid-template-columns: 160px 1fr; gap: 4px 12px; background: #f6f8fa; border-radius: 8px; padding: 14px 18px; }}
.meta div:nth-child(odd) {{ color: #57606a; }}
.notes {{ margin-top: 14px; white-space: pre-wrap; }}
.toc {{ margin: 22px 0; }} .toc li {{ margin: 2px 0; }}
.entry {{ margin-top: 30px; page-break-inside: auto; }}
.entry h2 {{ font-size: 14.5pt; margin: 0 0 2px; padding-top: 8px; border-top: 1px solid #d0d7de; }}
.entry .when {{ color: #57606a; font-size: 9pt; margin-bottom: 10px; }}
.comment {{ background: #fff8c5; border-left: 4px solid #d4a72c; padding: 8px 12px; margin: 8px 0 12px; white-space: pre-wrap; }}
h3 {{ font-size: 11.5pt; margin: 16px 0 6px; }}
table {{ width: 100%; border-collapse: collapse; margin: 6px 0 12px; font-size: 9pt; }}
th {{ background: #f0f3f6; text-align: left; font-weight: 600; }}
th, td {{ border: 1px solid #d0d7de; padding: 4px 7px; vertical-align: top; word-break: normal; overflow-wrap: break-word; }}
tr {{ page-break-inside: avoid; }}
.kv td:first-child {{ width: 32%; font-weight: 600; }}
.lvl {{ display: inline-block; width: 10px; height: 10px; border-radius: 50%; margin-right: 6px; vertical-align: middle; }}
.Ok {{ background: #2da44e; }} .Warn {{ background: #d4a72c; }} .Bad {{ background: #cf222e; }} .Info {{ background: #8c959f; }}
img.shot {{ width: 100%; border: 1px solid #d0d7de; border-radius: 6px; margin: 4px 0 12px; }}
pre {{ background: #f6f8fa; border: 1px solid #d0d7de; border-radius: 6px; padding: 10px; font-size: 8.5pt; white-space: pre-wrap; word-break: break-all; }}
.headline {{ font-size: 18pt; font-weight: 600; margin: 6px 0; }}
footer {{ margin-top: 40px; border-top: 1px solid #d0d7de; padding-top: 8px; color: #8c959f; font-size: 8.5pt; text-align: center; }}
</style></head><body><div class=""wrap"">");

        var logo = LogoDataUri();
        sb.Append("<header>");
        sb.Append(logo != "" ? $"<img src=\"{logo}\" alt=\"Logo\">" : "<span></span>");
        sb.Append("<div class=\"company\">");
        if (s.CompanyName != "") sb.Append($"<b>{E(s.CompanyName)}</b><br>");
        if (s.Technician != "") sb.Append($"{E(L.T("Techniker"))}: {E(s.Technician)}<br>");
        if (s.CompanyContact != "") sb.Append(E(s.CompanyContact).Replace("\n", "<br>"));
        sb.Append("</div></header>");

        sb.Append($"<h1>{E(L.T("Netzwerkbericht"))}</h1><div class=\"sub\">{E(p.Display)} · {DateTime.Now:dd.MM.yyyy}</div>");
        sb.Append("<div class=\"meta\">");
        void Meta(string k, string v) { if (!string.IsNullOrWhiteSpace(v)) sb.Append($"<div>{E(L.T(k))}</div><div>{E(v).Replace("\n", "<br>")}</div>"); }
        Meta("Kunde", p.Customer);
        Meta("Standort", p.Site);
        Meta("Adresse", p.Address);
        Meta("Ansprechpartner", p.ContactPerson);
        Meta("Erstellt", p.Created.ToString("dd.MM.yyyy"));
        Meta("Einträge", p.Entries.Count.ToString());
        sb.Append("</div>");
        if (!string.IsNullOrWhiteSpace(p.Notes)) sb.Append($"<div class=\"notes\"><b>{E(L.T("Notizen"))}:</b><br>{E(p.Notes)}</div>");

        if (p.Entries.Count > 1)
        {
            sb.Append($"<div class=\"toc\"><b>{E(L.T("Inhalt"))}</b><ol>");
            foreach (var e in p.Entries) sb.Append($"<li><a href=\"#e{e.Id}\">{E(e.Title)}{(string.IsNullOrEmpty(e.Target) ? "" : " – " + E(e.Target))}</a></li>");
            sb.Append("</ol></div>");
        }

        foreach (var e in p.Entries)
        {
            sb.Append($"<div class=\"entry\" id=\"e{e.Id}\"><h2>{E(e.Title)}{(string.IsNullOrEmpty(e.Target) ? "" : " – " + E(e.Target))}</h2>");
            sb.Append($"<div class=\"when\">{e.Time:dd.MM.yyyy HH:mm}</div>");
            if (!string.IsNullOrWhiteSpace(e.Comment)) sb.Append($"<div class=\"comment\">{E(e.Comment)}</div>");
            foreach (var b in e.Blocks)
            {
                if (!string.IsNullOrEmpty(b.Title) && b.Kind != "headline") sb.Append($"<h3>{E(b.Title)}</h3>");
                switch (b.Kind)
                {
                    case "headline":
                        sb.Append($"<div class=\"headline\">{E(b.Text)}</div>");
                        break;
                    case "section":
                        sb.Append("<table class=\"kv\">");
                        foreach (var i in b.Items)
                            sb.Append($"<tr><td><span class=\"lvl {i.Level}\"></span>{E(i.Name)}</td><td>{E(i.Value).Replace("\n", "<br>")}</td></tr>");
                        sb.Append("</table>");
                        break;
                    case "table":
                        sb.Append("<table><tr>");
                        foreach (var h in b.Headers) sb.Append($"<th>{E(h)}</th>");
                        sb.Append("</tr>");
                        foreach (var r in b.Rows)
                        {
                            sb.Append("<tr>");
                            foreach (var c in r) sb.Append($"<td>{E(c).Replace("\n", "<br>")}</td>");
                            sb.Append("</tr>");
                        }
                        sb.Append("</table>");
                        break;
                    case "image":
                        sb.Append($"<img class=\"shot\" src=\"data:image/png;base64,{b.ImageBase64}\">");
                        break;
                    case "text":
                        sb.Append($"<pre>{E(b.Text)}</pre>");
                        break;
                }
            }
            sb.Append("</div>");
        }
        sb.Append($"<footer>{E(L.T("Erstellt mit Net-Stuff"))} · {DateTime.Now:dd.MM.yyyy HH:mm}</footer></div></body></html>");
        return sb.ToString();
    }
}
