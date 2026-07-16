// Serves / and /index.html — the platform grid grouped by category. Platforms with ≥400 games show directly;
// smaller ones sit behind a "+ N smaller platform(s)" toggle. When the Extended DB isn't installed, renders a
// friendly "not installed" page instead of erroring. Clean-room LiteBox rewrite of ExtendDB's HomeHandler.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using LbApiHost.Host.Media;

namespace LbApiHost.Host.Web;

internal static class HomeHandler
{
    private const int MinGamesForVisible = 400;

    public static HttpResponse Handle(RouteContext ctx)
    {
        var parental = WebParentalState.From(ctx.Request);

        // Extended DB absent → the database site can't show anything. Point the user at the Base module.
        if (!DbRepository.AnyDbReady())
        {
            var w = new StringBuilder();
            w.Append(HtmlShared.Head("Game Database"));
            w.Append(HtmlShared.BodyOpen(null, parental));
            w.Append("<div class=\"page-header\" style=\"padding-top:3rem\">");
            w.Append("<h1>Game Database</h1>");
            w.Append("<p class=\"subtitle\">The extended metadata database is not installed yet.</p>");
            w.Append("<p class=\"subtitle\">Enable the &laquo;&nbsp;Base&nbsp;/&nbsp;Database&nbsp;&raquo; module in LiteBox and download the Extended Database to browse games here.</p>");
            w.Append("</div>");
            w.Append(HtmlShared.BodyClose);
            return HttpResponse.Html(w.ToString());
        }

        var repo = new DbRepository();
        var platforms = FilterHidden(repo.GetAllPlatforms(), parental);
        int totalGames = platforms.Sum(p => p.GameCount);

        var sb = new StringBuilder();
        sb.Append(HtmlShared.Head("Accueil"));
        sb.Append(HtmlShared.BodyOpen(null, parental));

        sb.Append("<div class=\"page-header\" style=\"padding-top:3rem\">");
        sb.Append("<h1>Game Database</h1>");
        sb.Append($"<p class=\"subtitle\">{platforms.Count} plateformes · {totalGames:N0} jeux</p>");
        sb.Append("</div>");

        var byCategory = platforms
            .GroupBy(p => string.IsNullOrEmpty(p.Category) ? null : p.Category)
            .OrderBy(g => g.Key is null ? 1 : 0)
            .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase);

        int catIndex = 0;
        foreach (var group in byCategory)
        {
            var catLabel = HtmlShared.Esc(group.Key ?? "Autres");
            var ordered = group.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
            var visible = ordered.Where(p => p.GameCount >= MinGamesForVisible).ToList();
            var hidden = ordered.Where(p => p.GameCount < MinGamesForVisible).ToList();

            sb.Append($"<h2 class=\"category-heading\">{catLabel}</h2>");
            sb.Append("<div class=\"platforms-grid\">");
            foreach (var p in visible) AppendCard(sb, p);
            sb.Append("</div>");

            if (hidden.Count > 0)
            {
                var toggleId = $"small-plat-{catIndex}";
                sb.Append($"<button class=\"show-small-btn\" id=\"btn-{toggleId}\" data-i18n-count=\"more-platforms\" data-count=\"{hidden.Count}\" onclick=\"gdbToggleSmall('{toggleId}')\">+ {hidden.Count} smaller platform(s)</button>");
                sb.Append($"<div id=\"{toggleId}\" class=\"platforms-grid platforms-grid-hidden\" style=\"display:none\">");
                foreach (var p in hidden) AppendCard(sb, p);
                sb.Append("</div>");
            }

            catIndex++;
        }

        // Per-platform owned counts for the client-side owned-only filter (one LB-API sweep).
        var ownedDict = OwnedLookup.GetCountsForPlatforms(platforms.Select(p => p.Name));
        int ownedTotal = ownedDict.Values.Sum();
        sb.Append("<script>window.GDB_OWNED_COUNTS=");
        sb.Append(JsonSerializer.Serialize(ownedDict));
        sb.Append(";window.GDB_OWNED_TOTAL=").Append(ownedTotal).Append(";</script>");

        sb.Append("""
            <script>
            function gdbToggleSmall(id){
              var el=document.getElementById(id);
              var btn=document.getElementById('btn-'+id);
              if(!el||!btn)return;
              var open=el.style.display==='none'||el.style.display==='';
              el.style.display=open?'grid':'none';
              if(open){
                btn.dataset.i18nCount='less-platforms';
                btn.dataset.count='';
                btn.textContent=typeof gdbT==='function'?gdbT('less-platforms',typeof gdbDetectLang==='function'?gdbDetectLang():'fr'):'Masquer';
              }else{
                btn.dataset.i18nCount='more-platforms';
                btn.dataset.count=btn.dataset.origCount||'';
                btn.textContent=typeof gdbT==='function'?gdbT('more-platforms',typeof gdbDetectLang==='function'?gdbDetectLang():'fr').replace('{n}',btn.dataset.origCount||''):('+ '+btn.dataset.origCount+' smaller platform(s)');
              }
            }
            document.addEventListener('DOMContentLoaded',function(){
              document.querySelectorAll('.show-small-btn').forEach(function(btn){btn.dataset.origCount=btn.dataset.count;});
              gdbApplyOwnedFilterHome();
            });
            function gdbApplyOwnedFilterHome(){
              if(typeof gdbIsOwnedMode!=='function'||!gdbIsOwnedMode())return;
              var counts=window.GDB_OWNED_COUNTS||{};
              var totalPlat=0,totalOwned=0;
              document.querySelectorAll('.platform-card[data-platform]').forEach(function(card){
                var name=card.dataset.platform;
                var owned=counts[name]||0;
                if(owned===0){card.style.display='none';return;}
                totalPlat++;totalOwned+=owned;
                var total=parseInt(card.dataset.gameCount)||0;
                var pct=total>0?Math.round(100*owned/total):0;
                var el=card.querySelector('.platform-card-count');
                if(el)el.textContent=total.toLocaleString('fr-FR')+' jeux · '+owned.toLocaleString('fr-FR')+' possédés ('+pct+'%)';
              });
              document.querySelectorAll('.platforms-grid-hidden').forEach(function(g){g.style.display='grid';});
              document.querySelectorAll('.show-small-btn').forEach(function(b){b.style.display='none';});
              var sub=document.querySelector('.subtitle');
              if(sub)sub.textContent=totalPlat+' plateformes · '+totalOwned.toLocaleString('fr-FR')+' jeux possédés';
              document.querySelectorAll('.category-heading').forEach(function(h){
                var next=h.nextElementSibling;
                var anyVisible=false;
                while(next&&next.tagName!=='H2'){
                  if(next.classList&&next.classList.contains('platforms-grid')){
                    var has=Array.from(next.querySelectorAll('.platform-card')).some(function(c){return c.style.display!=='none';});
                    if(has)anyVisible=true;
                  }
                  next=next.nextElementSibling;
                }
                if(!anyVisible)h.style.display='none';
              });
            }
            </script>
            """);

        sb.Append(HtmlShared.BodyClose);
        return HttpResponse.Html(sb.ToString());
    }

    /// <summary>Hides platforms whose name is on the parental hide-list (locked only).</summary>
    internal static List<DbPlatform> FilterHidden(List<DbPlatform> platforms, WebParentalState parental)
    {
        if (parental == null || !parental.IsLocked) return platforms;
        return platforms.Where(p => !parental.IsHidden(p.Name)).ToList();
    }

    private static void AppendCard(StringBuilder sb, DbPlatform p)
    {
        var slug = PlatformSlug.For(p.Name);
        var metaParts = new List<string>();
        if (!string.IsNullOrEmpty(p.Manufacturer)) metaParts.Add(HtmlShared.Esc(p.Manufacturer));
        var yr = p.ReleaseDate?.Length >= 4 ? p.ReleaseDate[..4] : "";
        if (!string.IsNullOrEmpty(yr)) metaParts.Add(yr);
        var meta = metaParts.Count > 0 ? string.Join(" · ", metaParts) : "&nbsp;";

        sb.Append($"<div class=\"platform-card\" data-platform=\"{HtmlShared.Esc(p.Name)}\" data-game-count=\"{p.GameCount}\">");
        sb.Append($"<a href=\"/platforms/{HtmlShared.Esc(slug)}.html\">");
        sb.Append($"<div class=\"platform-card-name\">{HtmlShared.Esc(p.Name)}</div>");
        sb.Append($"<div class=\"platform-card-meta\">{meta}</div>");
        sb.Append($"<div class=\"platform-card-count\">{p.GameCount:N0} jeux</div>");
        sb.Append("</a></div>");
    }
}
