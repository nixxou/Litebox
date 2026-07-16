// Serves /platforms/{slug}.html — a hero + filter bar + empty grid the client populates via
// /api/platforms/{slug}/games. Advanced-search modal, page-jump, and a persisted page-size selector. Star
// tiers come from the games API. Clean-room LiteBox rewrite of ExtendDB's PlatformDetailHandler.

using System;
using System.Text;

namespace LbApiHost.Host.Web;

internal static class PlatformDetailHandler
{
    public static HttpResponse Handle(RouteContext ctx)
    {
        var slug = ctx.GetRoute("slug");
        if (string.IsNullOrEmpty(slug)) return HttpResponse.NotFound();
        if (!DbRepository.AnyDbReady()) return HomeHandler.Handle(ctx);

        var parental = WebParentalState.From(ctx.Request);
        var repo = new DbRepository();
        var platform = ResolvePlatformBySlug(repo, slug);
        if (platform == null) return HttpResponse.NotFound("Platform not found.");
        if (parental.IsLocked && parental.IsHidden(platform.Name))
            return HttpResponse.NotFound("Platform not found.");

        var sb = new StringBuilder();
        sb.Append(HtmlShared.Head(platform.Name));
        var crumbs = new (string, string)[]
        {
            ("Accueil",     "/"),
            ("Plateformes", "/platforms.html"),
            (platform.Name, null)
        };
        sb.Append(HtmlShared.BodyOpen(crumbs, parental));

        var yr = platform.ReleaseDate?.Length >= 4 ? platform.ReleaseDate[..4] : "";
        sb.Append("<div class=\"platform-hero\">");
        sb.Append("<h1>").Append(HtmlShared.Esc(platform.Name)).Append("</h1>");
        sb.Append("<div class=\"platform-hero-meta\">");
        sb.Append(string.IsNullOrEmpty(platform.Notes) ? "&nbsp;" : HtmlShared.Esc(platform.Notes));
        sb.Append("</div>");
        sb.Append("<div class=\"platform-hero-stats\">");
        sb.Append($"<div class=\"stat\"><div class=\"stat-val\">{platform.GameCount:N0}</div><div class=\"stat-lbl\">Jeux</div></div>");
        if (!string.IsNullOrEmpty(yr))
            sb.Append($"<div class=\"stat\"><div class=\"stat-val\">{yr}</div><div class=\"stat-lbl\">Année</div></div>");
        if (!string.IsNullOrEmpty(platform.Manufacturer))
            sb.Append($"<div class=\"stat\"><div class=\"stat-val\" style=\"font-size:1rem;padding-top:.4rem\">{HtmlShared.Esc(platform.Manufacturer)}</div><div class=\"stat-lbl\">Fabricant</div></div>");
        if (!string.IsNullOrEmpty(platform.Cpu))
            sb.Append($"<div class=\"stat\"><div class=\"stat-val\" style=\"font-size:.9rem;padding-top:.55rem\">{HtmlShared.Esc(platform.Cpu)}</div><div class=\"stat-lbl\">CPU</div></div>");
        sb.Append("</div></div>");

        sb.Append("""
            <div class="filter-bar">
              <input type="search" id="game-search" data-i18n-placeholder="search-placeholder" placeholder="Rechercher un jeu…">
              <select id="game-genre"><option value="" data-i18n="all-genres">Tous les genres</option></select>
              <select id="game-sort" data-i18n-options>
                <option value="alpha"     data-i18n="sort-alpha">Alphabétique</option>
                <option value="year_asc"  data-i18n="sort-year-asc">Année ↑</option>
                <option value="year_desc" data-i18n="sort-year-desc">Année ↓</option>
                <option value="rating"    data-i18n="sort-rating">Note ↓</option>
                <option value="stars"     data-i18n="sort-stars">★ Étoiles</option>
              </select>
              <button id="adv-search-btn" class="adv-search-btn" onclick="gdbOpenAdvSearch()" title="Recherche avancée">⚙</button>
              <select id="page-jump" style="display:none"><option>Page 1</option></select>
              <span class="results-count" id="results-count">…</span>
            </div>
            <div id="games" class="games-grid"></div>
            <div id="pagination" class="pagination"></div>
            <div style="text-align:center;margin-top:1rem;font-size:.8rem;color:var(--muted)">
              <label>Jeux par page :
              <select id="page-size-selector" style="background:var(--bg3);border:1px solid var(--border);border-radius:var(--radius);padding:.3rem .5rem;color:var(--text);font-size:.8rem">
                <option value="50">50</option>
                <option value="100">100</option>
                <option value="200" selected>200</option>
                <option value="500">500</option>
              </select></label>
            </div>
            """);

        sb.Append("""
            <dialog id="adv-search-dialog" class="adv-dialog" onclick="if(event.target===this)this.close()">
              <div class="adv-dialog-inner">
                <div class="adv-dialog-header">
                  <span class="adv-dialog-title">Recherche avancée</span>
                  <button class="adv-close" onclick="this.closest('dialog').close()">✕</button>
                </div>
                <div class="adv-dialog-body">
                  <div class="adv-row"><label>Année min</label><input type="number" id="adv-year-min" min="1950" max="2050" placeholder="1950"></div>
                  <div class="adv-row"><label>Année max</label><input type="number" id="adv-year-max" min="1950" max="2050" placeholder="2050"></div>
                  <div class="adv-row"><label>Note min</label><input type="number" id="adv-rating-min" min="0" max="5" step="0.1" placeholder="0"></div>
                  <div class="adv-row"><label>Votes min</label><input type="number" id="adv-votes-min" min="0" placeholder="0"></div>
                  <div class="adv-row"><label>Joueurs min</label><input type="number" id="adv-players-min" min="1" placeholder="1"></div>
                  <div class="adv-row"><label>Coopératif</label>
                    <select id="adv-coop"><option value="">Tous</option><option value="1">Oui</option><option value="0">Non</option></select>
                  </div>
                  <div class="adv-row"><label>Développeur</label><input type="text" id="adv-dev" list="adv-dev-list" placeholder="Rechercher..."><datalist id="adv-dev-list"></datalist></div>
                  <div class="adv-row"><label>Éditeur</label><input type="text" id="adv-pub" list="adv-pub-list" placeholder="Rechercher..."><datalist id="adv-pub-list"></datalist></div>
                  <div class="adv-row"><label>Type</label><select id="adv-type"><option value="">Tous</option></select></div>
            """);
        // Origin is an extended-DB-only column: on base the facet is empty → hide the whole row. The JS
        // tolerates the absent #adv-origin element (v()/reset both null-check).
        if (repo.IsExtended)
            sb.Append("""
                  <div class="adv-row"><label>Origin</label><select id="adv-origin"><option value="">Toutes</option></select></div>
            """);
        sb.Append("""
                  <div class="adv-actions">
                    <button class="adv-btn adv-btn-apply" onclick="gdbApplyAdvSearch()">Appliquer</button>
                    <button class="adv-btn adv-btn-reset" onclick="gdbResetAdvSearch()">Réinitialiser</button>
                  </div>
                </div>
              </div>
            </dialog>
            """);

        var slugJs = HtmlShared.EscJs(slug);
        sb.Append("<script>(function(){\n");
        sb.Append($"const SLUG = {slugJs};\n");
        sb.Append("""
            var DEFAULT_PAGE_SIZE=200;
            var PAGE_SIZE=parseInt(localStorage.getItem('gdb_page_size'))||DEFAULT_PAGE_SIZE;
            var state = {
              page: 1, sort: 'alpha', genre: '', q: '',
              adv: {}
            };
            var _lastTotalPages = 0;
            var _filtersLoaded = false;
            var _loadAbort = null;
            var _loadSeq  = 0;

            function esc(s){return String(s||'').replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;').replace(/"/g,'&quot;');}

            async function loadDropdowns(){
              if(_filtersLoaded) return;
              try{
                const [genres, devs, pubs, types, origins] = await Promise.all([
                  fetch('/api/platforms/'+encodeURIComponent(SLUG)+'/genres').then(r=>r.json()),
                  fetch('/api/platforms/'+encodeURIComponent(SLUG)+'/developers?limit=500').then(r=>r.json()),
                  fetch('/api/platforms/'+encodeURIComponent(SLUG)+'/publishers?limit=500').then(r=>r.json()),
                  fetch('/api/platforms/'+encodeURIComponent(SLUG)+'/release-types').then(r=>r.json()),
                  fetch('/api/platforms/'+encodeURIComponent(SLUG)+'/origins').then(r=>r.json()),
                ]);
                var gSel=document.getElementById('game-genre');
                for (const g of genres) {
                  var opt=document.createElement('option'); opt.value=g; opt.textContent=g;
                  if (/vndb/i.test(g)) opt.dataset.vndb='1';
                  gSel.appendChild(opt);
                }
                refreshGenreVndb();
                var devList=document.getElementById('adv-dev-list');
                for (const d of devs) { var o=document.createElement('option'); o.value=d; devList.appendChild(o); }
                var pubList=document.getElementById('adv-pub-list');
                for (const p of pubs) { var o=document.createElement('option'); o.value=p; pubList.appendChild(o); }
                var tSel=document.getElementById('adv-type');
                for (const t of types) { var o=document.createElement('option'); o.value=t; o.textContent=t; tSel.appendChild(o); }
                var oSel=document.getElementById('adv-origin');
                for (const o2 of origins) { var op=document.createElement('option'); op.value=o2; op.textContent=o2; oSel.appendChild(op); }
                _filtersLoaded=true;
              } catch(e) { console.error('filter load', e); }
            }

            function refreshGenreVndb(){
              var sel=document.getElementById('game-genre'); if(!sel) return;
              var m=typeof gdbGetAdultMode==='function'?gdbGetAdultMode():0;
              Array.from(sel.options).forEach(function(o){
                if(o.dataset.vndb) o.style.display = m===0?'none':'';
              });
              if(m===0 && sel.selectedOptions[0] && sel.selectedOptions[0].dataset.vndb){
                sel.value=''; state.genre=''; state.page=1;
              }
            }

            function makeCard(g, thumbBase){
              var div=document.createElement('div');
              var isAdult=!!g.adult;
              var needsBlur=!!g.coverBlur;
              var m=typeof gdbGetAdultMode==='function'?gdbGetAdultMode():0;
              var applyBlur=m===1 && needsBlur;
              div.className='game-card'+(isAdult?' game-card-adult':'')+(applyBlur?' game-card-blurred':'')+(g.owned?' game-card-owned':'');
              var thumbSrc=g.id ? (thumbBase+'/'+g.id+'.jpg') : '';
              var imgWrap = thumbSrc
                ? '<div class="game-card-img"><img src="'+esc(thumbSrc)+'" alt="'+esc(g.name)+'" loading="lazy" onerror="this.style.display=\'none\'"></div>'
                : '<div class="game-card-no-img"><span class="icon">'+(g.name?g.name[0].toUpperCase():'?')+'</span><span>'+esc((g.name||'').slice(0,20))+'</span></div>';
              var adultBadge=isAdult?'<span class="adult-badge">18+</span>':'';
              var starHtml='';
              if(g.starTier===3) starHtml='<span class="star star-gold">★</span>';
              else if(g.starTier===2) starHtml='<span class="star star-silver">★</span>';
              else if(g.starTier===1) starHtml='<span class="star star-bronze">★</span>';
              var validYear=g.year && g.year>1950 && g.year<2050;
              var firstGenre=g.genres ? esc(g.genres.split(';')[0].trim()) : '';
              var badges=(validYear?'<span class="badge badge-year">'+g.year+'</span>':'')
                        +(isAdult?'<span class="badge badge-adult">[+18]</span>':'')
                        +(firstGenre?'<span class="badge badge-genre">'+firstGenre+'</span>':'');
              div.innerHTML='<a href="/games/'+g.id+'.html">'+adultBadge+starHtml+imgWrap
                +'<div class="game-card-body"><div class="game-card-title">'+esc(g.name)+'</div>'
                +'<div class="game-card-meta">'+badges+'</div></div></a>';
              return div;
            }

            async function loadPage(){
              var params=new URLSearchParams({
                page: state.page, pageSize: PAGE_SIZE, sort: state.sort,
                adult: typeof gdbGetAdultMode==='function' ? gdbGetAdultMode() : 0,
                owned: typeof gdbGetOwnedMode==='function' ? gdbGetOwnedMode() : 0,
              });
              if(state.genre) params.set('genre', state.genre);
              if(state.q) params.set('q', state.q);
              var a=state.adv;
              if(a.year_min) params.set('minYear', a.year_min);
              if(a.year_max) params.set('maxYear', a.year_max);
              if(a.rating_min) params.set('minRating', a.rating_min);
              if(a.votes_min) params.set('minVotes', a.votes_min);
              if(a.players_min) params.set('minPlayers', a.players_min);
              if(a.coop) params.set('coop', a.coop);
              if(a.dev) params.set('developer', a.dev);
              if(a.pub) params.set('publisher', a.pub);
              if(a.type) params.set('releaseType', a.type);
              if(a.origin) params.set('origin', a.origin);

              var url='/api/platforms/'+encodeURIComponent(SLUG)+'/games?'+params.toString();
              if(_loadAbort){try{_loadAbort.abort();}catch(e){}}
              _loadAbort=(typeof AbortController!=='undefined')?new AbortController():null;
              var mySeq=++_loadSeq;
              var opts=_loadAbort?{signal:_loadAbort.signal}:{};
              try{
                var data=await fetch(url,opts).then(r=>r.json());
                if(mySeq!==_loadSeq) return;
                renderGames(data);
                renderPagination(data);
                updateCount(data);
              } catch(e){
                if(e&&e.name==='AbortError') return;
                if(mySeq!==_loadSeq) return;
                document.getElementById('games').innerHTML='<div style="color:#c66">Failed to load: '+esc(e.message)+'</div>';
              }
            }

            function renderGames(data){
              var root=document.getElementById('games');
              if(!data.items || !data.items.length){
                root.innerHTML='<div style="color:var(--muted);padding:2rem;text-align:center">No games match.</div>';
                return;
              }
              var thumbBase=data.thumbBase || '/thumbs';
              var frag=document.createDocumentFragment();
              data.items.forEach(function(g){frag.appendChild(makeCard(g, thumbBase));});
              root.innerHTML='';
              root.appendChild(frag);
            }

            function renderPagination(data){
              var root=document.getElementById('pagination');
              var pj=document.getElementById('page-jump');
              var totalPages=Math.max(1, Math.ceil(data.total / data.pageSize));
              _lastTotalPages=totalPages;
              if(totalPages<=1){ root.innerHTML=''; if(pj) pj.style.display='none'; return; }
              if(pj){
                pj.style.display='';
                var opts='';
                for(var i=1;i<=totalPages;i++) opts+='<option value="'+i+'"'+(i===state.page?' selected':'')+'>Page '+i+'</option>';
                pj.innerHTML=opts;
                pj.onchange=function(){state.page=parseInt(this.value)||1;loadPage();window.scrollTo(0,0);};
              }
              var html='';
              html+='<a class="'+(state.page<=1?'pg-disabled':'')+'" data-p="'+(state.page-1)+'">‹</a>';
              var pages=new Set([1, totalPages, state.page]);
              for(var d=1;d<=2;d++){pages.add(state.page-d);pages.add(state.page+d);}
              var sorted=[...pages].filter(p=>p>=1&&p<=totalPages).sort((a,b)=>a-b);
              var last=0;
              for(const p of sorted){
                if(last && p>last+1) html+='<span class="pg-ellipsis">…</span>';
                if(p===state.page) html+='<a class="pg-active">'+p+'</a>';
                else html+='<a data-p="'+p+'">'+p+'</a>';
                last=p;
              }
              html+='<a class="'+(state.page>=totalPages?'pg-disabled':'')+'" data-p="'+(state.page+1)+'">›</a>';
              root.innerHTML=html;
              root.querySelectorAll('a[data-p]').forEach(function(a){
                if(a.classList.contains('pg-disabled')) return;
                a.addEventListener('click', function(){
                  var p=parseInt(a.getAttribute('data-p'));
                  if(p>=1 && p<=totalPages){ state.page=p; loadPage(); window.scrollTo(0,0); }
                });
              });
            }

            function updateCount(data){
              var el=document.getElementById('results-count');
              var lang=typeof gdbDetectLang==='function'?gdbDetectLang():'fr';
              var sfx=typeof gdbT==='function'?gdbT('games-count-suffix',lang):'jeux';
              var totalPages=Math.max(1, Math.ceil(data.total / data.pageSize));
              if(data.total===0){ el.textContent='0 '+sfx; return; }
              var from=(state.page-1)*data.pageSize+1;
              var to=Math.min(state.page*data.pageSize, data.total);
              if(totalPages>1) el.textContent=from+'-'+to+' / '+data.total+' '+sfx;
              else el.textContent=data.total+' '+sfx;
            }

            var _advBtn=document.getElementById('adv-search-btn');
            window.gdbOpenAdvSearch=function(){
              loadDropdowns().then(function(){
                document.getElementById('adv-search-dialog').showModal();
              });
            };
            window.gdbApplyAdvSearch=function(){
              var v=function(id){var el=document.getElementById(id);return el?el.value.trim():'';};
              var n=function(id){var x=parseFloat(v(id));return isNaN(x)?'':x;};
              state.adv={
                year_min:n('adv-year-min'), year_max:n('adv-year-max'),
                rating_min:n('adv-rating-min'), votes_min:n('adv-votes-min'),
                players_min:n('adv-players-min'), coop:v('adv-coop'),
                dev:v('adv-dev'), pub:v('adv-pub'),
                type:v('adv-type'), origin:v('adv-origin'),
              };
              document.getElementById('adv-search-dialog').close();
              state.page=1; loadPage(); _updateAdvBtn();
            };
            window.gdbResetAdvSearch=function(){
              state.adv={};
              ['adv-year-min','adv-year-max','adv-rating-min','adv-votes-min','adv-players-min','adv-dev','adv-pub'].forEach(function(id){var el=document.getElementById(id);if(el)el.value='';});
              ['adv-coop','adv-type','adv-origin'].forEach(function(id){var el=document.getElementById(id);if(el)el.value='';});
              state.page=1; loadPage(); _updateAdvBtn();
            };
            function _updateAdvBtn(){
              var a=state.adv;
              var has=a.year_min||a.year_max||a.rating_min||a.votes_min||a.players_min||a.coop||a.dev||a.pub||a.type||a.origin;
              if(_advBtn) _advBtn.className=has?'adv-search-btn adv-active':'adv-search-btn';
            }

            function bind(){
              var t;
              document.getElementById('game-search').addEventListener('input', function(e){
                clearTimeout(t); t=setTimeout(function(){state.q=e.target.value.trim();state.page=1;loadPage();}, 250);
              });
              document.getElementById('game-genre').addEventListener('change', function(e){state.genre=e.target.value;state.page=1;loadPage();});
              document.getElementById('game-sort').addEventListener('change', function(e){state.sort=e.target.value;state.page=1;loadPage();});
              var pss=document.getElementById('page-size-selector');
              if(pss){
                pss.value=PAGE_SIZE;
                pss.addEventListener('change', function(e){
                  PAGE_SIZE=parseInt(e.target.value);
                  localStorage.setItem('gdb_page_size', PAGE_SIZE);
                  state.page=1; loadPage();
                });
              }
            }

            window.gdbPlatformUpdate=function(){ refreshGenreVndb(); state.page=1; loadPage(); };

            loadDropdowns();
            bind();
            loadPage();
            })();</script>
            """);

        sb.Append(HtmlShared.BodyClose);
        return HttpResponse.Html(sb.ToString());
    }

    /// <summary>Resolves a slug to its platform by re-slugging every platform name (≤ a few hundred rows).</summary>
    internal static DbPlatform ResolvePlatformBySlug(DbRepository repo, string slug)
    {
        foreach (var p in repo.GetAllPlatforms())
            if (string.Equals(PlatformSlug.For(p.Name), slug, StringComparison.OrdinalIgnoreCase))
                return p;
        return null;
    }
}
