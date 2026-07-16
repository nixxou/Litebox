// Serves /platforms.html — every category section with all its platform cards (no smaller-platforms collapse).
// Clean-room LiteBox rewrite of ExtendDB's PlatformsListHandler.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace LbApiHost.Host.Web;

internal static class PlatformsListHandler
{
    public static HttpResponse Handle(RouteContext ctx)
    {
        var parental = WebParentalState.From(ctx.Request);
        if (!DbRepository.AnyDbReady()) return HomeHandler.Handle(ctx);

        var repo = new DbRepository();
        var platforms = HomeHandler.FilterHidden(repo.GetAllPlatforms(), parental);

        var sb = new StringBuilder();
        sb.Append(HtmlShared.Head("Plateformes"));
        var crumbs = new (string, string)[] { ("Accueil", "/"), ("Plateformes", null) };
        sb.Append(HtmlShared.BodyOpen(crumbs, parental));

        sb.Append("<div class=\"page-header\">");
        sb.Append("<h1>Toutes les plateformes</h1>");
        sb.Append($"<p class=\"subtitle\">{platforms.Count} plateformes dans la base</p>");
        sb.Append("</div>");

        var byCategory = platforms
            .GroupBy(p => string.IsNullOrEmpty(p.Category) ? "Autres" : p.Category)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase);

        foreach (var group in byCategory)
        {
            sb.Append($"<h2 class=\"category-heading\">{HtmlShared.Esc(group.Key)}</h2>");
            sb.Append("<div class=\"platforms-grid\">");
            foreach (var p in group.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
            {
                var slug = PlatformSlug.For(p.Name);
                var metaParts = new List<string>();
                if (!string.IsNullOrEmpty(p.Manufacturer)) metaParts.Add(HtmlShared.Esc(p.Manufacturer));
                var yr = p.ReleaseDate?.Length >= 4 ? p.ReleaseDate[..4] : "";
                if (!string.IsNullOrEmpty(yr)) metaParts.Add(yr);
                var meta = metaParts.Count > 0 ? string.Join(" · ", metaParts) : "&nbsp;";

                sb.Append("<div class=\"platform-card\">");
                sb.Append($"<a href=\"/platforms/{HtmlShared.Esc(slug)}.html\">");
                sb.Append($"<div class=\"platform-card-name\">{HtmlShared.Esc(p.Name)}</div>");
                sb.Append($"<div class=\"platform-card-meta\">{meta}</div>");
                sb.Append($"<div class=\"platform-card-count\">{p.GameCount:N0} jeux</div>");
                sb.Append("</a></div>");
            }
            sb.Append("</div>");
        }

        sb.Append(HtmlShared.BodyClose);
        return HttpResponse.Html(sb.ToString());
    }
}
