// Serves /games/{id}.html — a thin shell that fetches /api/games/{id} and renders the cover, rating, badges,
// multi-language overviews, info table, external links, ROMs dialog and media gallery client-side. When the
// parental lock is engaged and the game's rating fails the rules, a generic "locked" page is served instead.
// Clean-room LiteBox rewrite of ExtendDB's GameDetailHandler (unlock-token / media-debug replay dropped —
// LiteBox has neither the LB CefSharp shell token nor the plugin's WebDebugMode).

using System;
using System.Text;

namespace LbApiHost.Host.Web;

internal static class GameDetailHandler
{
    public static HttpResponse Handle(RouteContext ctx)
    {
        var idStr = ctx.GetRoute("id");
        if (!int.TryParse(idStr, out var id)) return HttpResponse.NotFound();
        if (!DbRepository.AnyDbReady()) return HomeHandler.Handle(ctx);

        var repo = new DbRepository();
        var game = repo.GetGameById(id);
        if (game == null) return HttpResponse.NotFound("Game not found.");

        var parental = WebParentalState.From(ctx.Request);
        if (parental.IsLocked && !parental.IsRatingAllowed(game.ESRB))
            return BuildLockPage(parental);

        var platSlug = PlatformSlug.For(game.Platform);
        var sb = new StringBuilder();
        sb.Append(HtmlShared.Head($"{game.Name} — LiteBox"));
        var crumbs = new (string, string)[]
        {
            ("Accueil", "/"),
            (game.Platform, $"/platforms/{platSlug}.html"),
            (game.Name, null)
        };
        sb.Append(HtmlShared.BodyOpen(crumbs, parental));

        sb.Append("<div class=\"game-detail\" id=\"detail\">");
        sb.Append("<aside class=\"game-cover\" id=\"cover-block\">");
        sb.Append("<div class=\"game-cover-img\" id=\"cover-wrap\"><img id=\"cover-img\" src=\"\" alt=\"\"></div>");
        sb.Append("<div id=\"rating-block\"></div>");
        sb.Append("</aside>");

        sb.Append("<div class=\"game-info\">");
        sb.Append("<div id=\"year-line\" class=\"game-year\"></div>");
        sb.Append($"<h1>{HtmlShared.Esc(game.Name)}</h1>");
        sb.Append("<div class=\"tags\" id=\"tags\"></div>");
        sb.Append("<div class=\"overview-container\" id=\"overview-container\"></div>");
        sb.Append("<table class=\"info-table\" id=\"info-table\"><tbody></tbody></table>");
        sb.Append("<div class=\"external-links\" id=\"ext-links\"></div>");
        sb.Append("<div id=\"roms-section\"></div>");
        sb.Append("<div id=\"media-section\"></div>");
        sb.Append("</div></div>");

        var idJs = id;
        var platSlugJs = HtmlShared.EscJs(platSlug);
        sb.Append("<script>(function(){\n");
        sb.Append($"const ID = {idJs};\n");
        sb.Append($"const PLAT_SLUG = {platSlugJs};\n");
        sb.Append("function apiUrl(path){return path;}\n");
        sb.Append("""
            function esc(s){return String(s||'').replace(/[<>&"]/g, c => ({'<':'&lt;','>':'&gt;','&':'&amp;','"':'&quot;'})[c]);}

            var TYPE_ORDER = [
              'Box - Front','Box - Front - Reconstructed',
              'Box - Back','Box - Back - Reconstructed',
              'Box - 3D','Box - Spine',
              'Screenshot - Gameplay','Screenshot - Game Title','Screenshot - Game Select',
              'Screenshot - Game Over','Screenshot - High Scores',
              'Fanart - Background','Fanart - Box - Front','Fanart - Box - Back',
              'Fanart - Cart - Front','Fanart - Cart - Back','Fanart - Disc',
              'Clear Logo','Banner','Square','Icon','Poster',
              'Arcade - Cabinet','Arcade - Marquee','Arcade - Control Panel',
              'Arcade - Circuit Board','Arcade - Controls Information',
              'Cart - Front','Cart - Back','Cart - 3D',
              'Disc',
              'Advertisement Flyer - Front','Advertisement Flyer - Back',
              'Map','Manual','Press','Music'
            ];

            function regionFlag(region){
              var r=(region||'').toLowerCase();
              switch(r){
                case '': return '';
                case 'world': return '🌍';
                case 'usa': case 'united states': case 'north america': return '🇺🇸';
                case 'europe': return '🇪🇺';
                case 'japan': return '🇯🇵';
                case 'france': return '🇫🇷';
                case 'germany': return '🇩🇪';
                case 'spain': return '🇪🇸';
                case 'italy': return '🇮🇹';
                case 'australia': return '🇦🇺';
                case 'brazil': return '🇧🇷';
                case 'korea': return '🇰🇷';
                case 'china': return '🇨🇳';
                case 'uk': case 'united kingdom': return '🇬🇧';
                case 'canada': return '🇨🇦';
                case 'russia': return '🇷🇺';
                case 'portugal': return '🇵🇹';
                case 'netherlands': return '🇳🇱';
                case 'sweden': return '🇸🇪';
                case 'poland': return '🇵🇱';
                default: return '📦';
              }
            }

            function regionSortKey(k){
              if(!k) return '\x00';
              if(k==='World') return '\x01';
              if(k==='North America') return '\x02';
              return '\x03'+k;
            }

            var EXCLUDED_COVER_TYPES = ['Icon','Manual','Press','Map','Music','Video','VideoAdvert'];
            function getMediaKind(type, url){
              if(type==='Manual'||type==='Press'||type==='Map') return 'pdf';
              if(type==='Music') return 'audio';
              if(type==='Video'||type==='VideoAdvert') return 'video';
              var m=(url||'').match(/\.([a-z0-9]{1,6})$/i);
              var ext=m?m[1].toLowerCase():'';
              if(ext==='pdf') return 'pdf';
              if(ext==='mp3'||ext==='ogg'||ext==='wav'||ext==='flac'||ext==='m4a'||ext==='aac') return 'audio';
              if(ext==='mp4'||ext==='webm'||ext==='mkv'||ext==='mov'||ext==='avi') return 'video';
              if(!ext||ext==='jpg'||ext==='jpeg'||ext==='png'||ext==='gif'||ext==='webp'||ext==='bmp'||ext==='svg'||ext==='ico'||ext==='box2d'||ext==='box3d') return 'image';
              return 'generic';
            }
            function urlExt(url){
              var m=(url||'').match(/\.([a-z0-9]{1,6})$/i);
              return m?m[1].toLowerCase():'';
            }

            async function load(){
              var data;
              try { data = await fetch(apiUrl('/api/games/'+ID)).then(r=>r.json()); }
              catch(e){ document.getElementById('tags').innerHTML='<div style="color:#c66">Failed to load: '+esc(e.message)+'</div>'; return; }
              if(!data || !data.id) return;

              var coverWrap=document.getElementById('cover-wrap');
              var coverImg=document.getElementById('cover-img');
              var coverPick=null;
              if(data.images && data.images.length){
                var pickOrder=['Poster','Box - Front','Fanart - Box - Front','Box - 3D','Cart - Front'];
                for(var i=0;i<pickOrder.length && !coverPick;i++){
                  coverPick=data.images.find(im=>im.type===pickOrder[i]);
                }
                if(!coverPick) coverPick=data.images.find(im=>im.url && EXCLUDED_COVER_TYPES.indexOf(im.type)<0) || null;
              }
              if(coverPick){
                coverImg.src=coverPick.url+'?role=cover';
                coverImg.alt=data.name;
                if(coverPick.hasLocal) coverWrap.classList.add('has-local');
                if(coverPick.blur && data.adult){
                  coverWrap.classList.add('gdb-img-adult');
                  coverWrap.onclick=function(){
                    var m=typeof gdbGetAdultMode==='function'?gdbGetAdultMode():0;
                    if(m===2 || coverWrap.classList.contains('gdb-revealed')){
                      window.open(coverImg.src,'_blank');
                    } else {
                      coverWrap.classList.add('gdb-revealed');
                    }
                  };
                  if(typeof gdbApplyAdultMode==='function') gdbApplyAdultMode();
                }
              } else {
                coverWrap.outerHTML='<div class="game-cover-no-img"><span style="font-size:3rem;opacity:.2">🎮</span><span style="font-size:.8rem" data-i18n="no-cover">Pas de couverture</span></div>';
              }

              if(data.rating){
                document.getElementById('rating-block').innerHTML=
                  '<div class="rating-bar"><div class="rating-val">'+data.rating.toFixed(1)+'</div>'
                  +'<div class="rating-label" data-i18n="community-rating">Note communauté</div>'
                  +'<div style="font-size:.72rem;color:var(--muted);margin-top:.2rem">'+(data.ratingCount||0).toLocaleString()+' <span data-i18n="votes">votes</span></div>'
                  +'</div>';
              }

              if(data.year && data.year>1950 && data.year<2050)
                document.getElementById('year-line').textContent=data.year;
              else
                document.getElementById('year-line').remove();

              var tags=[];
              tags.push('<a href="/platforms/'+encodeURIComponent(PLAT_SLUG)+'.html" class="badge badge-platform">'+esc(data.platform)+'</a>');
              if(data.genres){
                data.genres.split(';').map(s=>s.trim()).filter(Boolean).forEach(function(g){
                  var cls='badge badge-genre', txt=g;
                  if(/^vndb-ero \//i.test(g)){ cls='badge badge-vndb-ero'; txt=g.slice(11); }
                  else if(/^vndb-cont \//i.test(g)){ cls='badge badge-vndb-cont'; txt=g.slice(12); }
                  else if(/^vndb-tech \//i.test(g)){ cls='badge badge-vndb-tech'; txt=g.slice(12); }
                  tags.push('<span class="'+cls+'">'+esc(txt)+'</span>');
                });
              }
              if(data.esrb) tags.push('<span class="badge badge-esrb">'+esc(data.esrb)+'</span>');
              document.getElementById('tags').innerHTML=tags.join(' ');

              var ovBox=document.getElementById('overview-container');
              var ovs=data.overviews||{};
              var langs=Object.keys(ovs);
              if(langs.length===0){
                if(data.fallbackOverview)
                  ovBox.innerHTML='<div class="overview-block">'+esc(data.fallbackOverview)+'</div>';
              } else {
                var html='';
                langs.forEach(function(lang){
                  html+='<div class="overview-block" data-lang="'+lang+'" style="display:none">'+esc(ovs[lang])+'</div>';
                });
                ovBox.innerHTML=html;
                function applyOv(lang){
                  var target=langs.indexOf(lang)>=0?lang:(langs.indexOf('en')>=0?'en':langs[0]);
                  ovBox.querySelectorAll('.overview-block').forEach(function(el){
                    el.style.display=(el.dataset.lang===target)?'':'none';
                  });
                }
                var _orig=window.gdbApplyLang;
                window.gdbApplyLang=function(lang,save){_orig(lang,save);applyOv(lang);};
                applyOv(typeof gdbDetectLang==='function'?gdbDetectLang():'fr');
              }

              var rows=[];
              function row(labelI18n,labelFallback,valHtml,valI18n){
                if(!valHtml) return;
                var li=labelI18n?(' data-i18n="'+labelI18n+'"'):'';
                var vi=valI18n?(' data-i18n="'+valI18n+'"'):'';
                rows.push('<tr><td'+li+'>'+esc(labelFallback)+'</td><td'+vi+'>'+valHtml+'</td></tr>');
              }
              row('platform','Plateforme','<a href="/platforms/'+encodeURIComponent(PLAT_SLUG)+'.html">'+esc(data.platform)+'</a>');
              if(data.releaseDate){
                var rd=data.releaseDate.length>=10?data.releaseDate.slice(0,10):data.releaseDate;
                row('release-date','Date de sortie',esc(rd));
              } else if(data.year){
                row('release-date','Date de sortie',data.year);
              }
              row('type','Type',esc(data.releaseType));
              row('developer','Développeur',esc(data.developer));
              row('publisher','Éditeur',esc(data.publisher));
              row('max-players','Max joueurs',data.maxPlayers?String(data.maxPlayers):null);
              row('cooperative','Coopératif',data.cooperative?'Oui':'Non',data.cooperative?'yes':'no');
              row(null,'ESRB',esc(data.esrb));
              if(data.wikipediaUrl)
                row('wikipedia','Wikipedia','<a href="'+esc(data.wikipediaUrl)+'" target="_blank" rel="noopener">Wikipedia ↗</a>');
              if(data.videoUrl)
                row('video-link','Vidéo','<a href="'+esc(data.videoUrl)+'" target="_blank" rel="noopener">Voir la vidéo ↗</a>');
              if(data.alts && data.alts.length){
                var altLines=data.alts
                  .filter(function(a){return a.name && (a.region||'').toLowerCase()!=='world';})
                  .map(function(a){
                    var f=regionFlag(a.region||'');
                    return '<span style="color:var(--muted)">'+(f?(f+' '):'')+esc(a.name)+' <span style="font-size:.75em;opacity:.6">('+esc(a.region||'')+')</span></span>';
                  }).join('<br>');
                if(altLines) row('alt-titles','Titres alt.',altLines);
              }
              document.getElementById('info-table').querySelector('tbody').innerHTML=rows.join('');

              var links=[];
              if((data.origin||'').toLowerCase()==='launchbox')
                links.push('<a class="ext-link ext-link-lb" href="https://gamesdb.launchbox-app.com/games/dbid/'+data.id+'" target="_blank" rel="noopener noreferrer" referrerpolicy="no-referrer">LaunchBox</a>');
              if(data.steamAppId)
                links.push('<a class="ext-link ext-link-steam" href="https://store.steampowered.com/app/'+data.steamAppId+'" target="_blank" rel="noopener noreferrer" referrerpolicy="no-referrer">Steam</a>');
              if(data.igdbSlug)
                links.push('<a class="ext-link ext-link-igdb" href="https://www.igdb.com/games/'+encodeURIComponent(data.igdbSlug)+'" target="_blank" rel="noopener noreferrer" referrerpolicy="no-referrer">IGDB</a>');
              if(data.vndbId)
                links.push('<a class="ext-link ext-link-vndb" href="https://vndb.org/v'+data.vndbId+'" target="_blank" rel="noopener noreferrer" referrerpolicy="no-referrer">VNDB</a>');
              if(data.screenscraperId)
                links.push('<a class="ext-link ext-link-ss" href="https://screenscraper.fr/gameinfos.php?gameid='+data.screenscraperId+'" target="_blank" rel="noopener noreferrer" referrerpolicy="no-referrer">ScreenScraper</a>');
              document.getElementById('ext-links').innerHTML=links.join('');

              if(data.roms && data.roms.length){
                var modalId='roms-modal-'+data.id;
                var rowHtml=data.roms.map(function(r){
                  return '<tr>'
                    +'<td class="rom-filename">'+esc(r.fileName)+'</td>'
                    +'<td class="rom-size">'+esc(r.size)+'</td>'
                    +'<td class="rom-crc">'+esc(r.crc32)+'</td>'
                    +'<td class="rom-origin">'+esc(r.origin)+'</td>'
                    +'</tr>';
                }).join('');
                document.getElementById('roms-section').innerHTML=
                  '<button class="roms-btn" onclick="document.getElementById(\''+modalId+'\').showModal()">'
                  +'<span>💾</span> '+data.roms.length+' ROM'+(data.roms.length>1?'s':'')+'</button>'
                  +'<dialog id="'+modalId+'" class="roms-dialog" onclick="if(event.target===this)this.close()">'
                  +'<div class="roms-dialog-inner">'
                  +'<div class="roms-dialog-header">'
                  +'<span class="roms-dialog-title">'+esc(data.name)+' — ROMs</span>'
                  +'<button class="roms-close" onclick="this.closest(\'dialog\').close()">✕</button>'
                  +'</div>'
                  +'<div class="roms-dialog-body">'
                  +'<table class="roms-table">'
                  +'<thead><tr><th>Fichier</th><th>Taille</th><th>CRC32</th><th>Origin</th></tr></thead>'
                  +'<tbody>'+rowHtml+'</tbody></table>'
                  +'</div></div></dialog>';
              }

              renderMedia(data);
            }

            function renderMedia(data){
              var imgs=(data.images||[]).filter(im=>im.url);
              if(!imgs.length) return;
              var videos=imgs.filter(im=>getMediaKind(im.type, im.url)==='video');
              var others=imgs.filter(im=>getMediaKind(im.type, im.url)!=='video');
              var html='<div class="media-section"><h2 data-i18n="media">Médias</h2>';

              if(others.length){
                var byType={};
                others.forEach(function(im){(byType[im.type]=byType[im.type]||[]).push(im);});
                var ordered=TYPE_ORDER.filter(t=>byType[t]).map(t=>[t,byType[t]]);
                Object.keys(byType).forEach(function(t){if(TYPE_ORDER.indexOf(t)<0) ordered.push([t,byType[t]]);});
                ordered.forEach(function(p){
                  var type=p[0], items=p[1];
                  html+='<div class="media-type-title">'+esc(type)+'</div>';
                  html+='<div class="media-grid">';
                  var byRegion={};
                  items.forEach(function(im){var k=im.region||'';(byRegion[k]=byRegion[k]||[]).push(im);});
                  var regKeys=Object.keys(byRegion).sort(function(a,b){return regionSortKey(a).localeCompare(regionSortKey(b));});
                  var showRegion=regKeys.length>1 || (regKeys.length===1 && regKeys[0]!=='');
                  regKeys.forEach(function(rk){
                    byRegion[rk].forEach(function(im){
                      var titleParts=[type];
                      if(rk) titleParts.push(rk);
                      if(im.origin) titleParts.push(im.origin);
                      var titleAttr=esc(titleParts.join(' – '));
                      var flag=showRegion?regionFlag(rk):'';
                      var flagHtml=flag?'<span class="media-flag" title="'+esc(rk)+'">'+flag+'</span>':'';
                      var localCls=im.hasLocal?' has-local':'';
                      var kind=getMediaKind(type, im.url);

                      if(kind==='pdf' || kind==='generic'){
                        var icon=kind==='pdf'?'📄':'📦';
                        var ext=urlExt(im.url);
                        var inner;
                        if(im.name){
                          var parts=[];
                          if(rk) parts.push(regionFlag(rk)+' '+esc(rk));
                          if(im.origin) parts.push(esc(im.origin));
                          var secondary=parts.join(' · ');
                          inner='<span class="media-doc-text">'
                              +'<span class="media-doc-name">'+esc(im.name)+'</span>'
                              +(secondary?'<span class="media-doc-region">'+secondary+'</span>':'')
                              +'</span>';
                        } else {
                          inner=esc(type)
                              +(showRegion&&rk?' '+regionFlag(rk)+' '+esc(rk):'')
                              +(kind==='generic'&&ext?' (.'+ext+')':'');
                        }
                        html+='<a class="media-doc'+localCls+'" href="'+esc(im.url)+'" target="_blank" rel="noopener" title="'+titleAttr+'">'
                          +'<span class="icon">'+icon+'</span> '+inner+'</a>';
                      } else if(kind==='audio'){
                        html+='<div class="audio-wrap'+localCls+'" title="'+titleAttr+'">'
                          +flagHtml
                          +'<audio controls preload="none"><source src="'+esc(im.url)+'"></audio>'
                          +'</div>';
                      } else if(im.blur && data.adult){
                        html+='<div class="gdb-img-adult'+localCls+'" onclick="var m=typeof gdbGetAdultMode===\'function\'?gdbGetAdultMode():0;if(m===2||this.classList.contains(\'gdb-revealed\')){var i=this.querySelector(\'img\');if(i)window.open(i.src,\'_blank\');}else{this.classList.add(\'gdb-revealed\');}">'
                          +flagHtml+'<img src="'+esc(im.url)+'" alt="'+titleAttr+'" title="'+titleAttr+'" loading="lazy"></div>';
                      } else {
                        html+='<a class="'+localCls.trim()+'" href="'+esc(im.url)+'" target="_blank" rel="noopener">'
                          +flagHtml+'<img src="'+esc(im.url)+'" alt="'+titleAttr+'" title="'+titleAttr+'" loading="lazy"></a>';
                      }
                    });
                  });
                  html+='</div>';
                });
              }

              if(videos.length){
                var byVidType={};
                videos.forEach(function(v){(byVidType[v.type]=byVidType[v.type]||[]).push(v);});
                Object.keys(byVidType).sort(function(a,b){return (a==='Video'?0:1)-(b==='Video'?0:1);}).forEach(function(type){
                  html+='<div class="media-type-title">'+esc(type)+'</div><div class="media-grid">';
                  var items=byVidType[type];
                  var byRegion={};
                  items.forEach(function(v){var k=v.region||'';(byRegion[k]=byRegion[k]||[]).push(v);});
                  var regKeys=Object.keys(byRegion).sort(function(a,b){return regionSortKey(a).localeCompare(regionSortKey(b));});
                  var showRegion=regKeys.length>1 || (regKeys.length===1 && regKeys[0]!=='');
                  regKeys.forEach(function(rk){
                    byRegion[rk].forEach(function(v){
                      var flag=showRegion?regionFlag(rk):'';
                      var flagHtml=flag?'<div class="video-flag" title="'+esc(rk)+'">'+flag+'</div>':'';
                      var vCls='video-wrap'+(v.hasLocal?' has-local':'');
                      html+='<div class="'+vCls+'">'+flagHtml
                        +'<video controls preload="none"><source src="'+esc(v.url)+'" type="video/mp4">'
                        +'<a href="'+esc(v.url)+'" target="_blank">Voir la vidéo</a></video></div>';
                    });
                  });
                  html+='</div>';
                });
              }

              html+='</div>';
              document.getElementById('media-section').innerHTML=html;
            }

            load();
            })();</script>
            """);

        sb.Append(HtmlShared.BodyClose);
        return HttpResponse.Html(sb.ToString());
    }

    /// <summary>Generic "content locked" page shown when the game's rating fails the parental rules.</summary>
    private static HttpResponse BuildLockPage(WebParentalState parental)
    {
        var sb = new StringBuilder();
        sb.Append(HtmlShared.Head("🔒 Locked — LiteBox"));
        sb.Append(HtmlShared.BodyOpen(null, parental));
        sb.Append("""
            <div style="max-width:560px;margin:6rem auto;padding:2rem;background:var(--bg2);
                        border:1px solid #7f1d1d;border-radius:8px;text-align:center">
              <div style="font-size:3rem;margin-bottom:1rem">🔒</div>
              <h1 style="font-family:var(--font-display);color:#f87171;margin:0 0 1rem"
                  data-i18n="lock-pin-title">Locked</h1>
              <p style="color:var(--text);line-height:1.5;margin:0 0 1.5rem"
                 data-i18n="lock-pin-body">Use the lock button in the header to unlock.</p>
              <a href="/" style="display:inline-block;padding:.6rem 1.2rem;border-radius:var(--radius);
                                 background:var(--bg3);color:var(--text);text-decoration:none;
                                 border:1px solid var(--border);font-weight:700"
                 data-i18n="home">Home</a>
            </div>
            """);
        sb.Append(HtmlShared.BodyClose);
        return HttpResponse.Html(sb.ToString(), 403);
    }
}
