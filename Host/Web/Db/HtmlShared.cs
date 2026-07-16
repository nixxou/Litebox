// Shared server-rendered HTML for the database site — the Head (inline CSS + inline JS), the topbar with the
// global-search / adult-mode / owned-mode / parental-lock / language controls, and the body frame. The site
// is 100% server-rendered: there are no static asset files. The only external references are /thumbs/,
// /api/media, /api/search, /api/parental/* and Google Fonts (CDN).
//
// Clean-room LiteBox rewrite of ExtendDB's Web/Backend/HtmlShared.cs. The global search calls /api/search
// directly; the parental lock talks to the S2 /api/parental/* endpoints and reads window.LB_PARENTAL. Media
// stays on /thumbs/ and /api/media (S2 handlers).
//
// Composition:  Head(title) + BodyOpen(crumbs?, parental?) + …page content… + BodyClose

using System;
using System.Collections.Generic;
using System.Text;

namespace LbApiHost.Host.Web;

internal static class HtmlShared
{
    // ── CSS ────────────────────────────────────────────────────────────────────
    public const string SharedCss = """
        :root{--bg:#0a0c10;--bg2:#111520;--bg3:#181d2a;--card:#131825;--border:#1e2538;--accent:#e8a020;--accent2:#c07010;--text:#d4daf0;--muted:#6b7799;--highlight:#ffffff;--radius:6px;--font-display:'Rajdhani','Barlow Condensed',sans-serif;--font-body:'Source Sans 3','Noto Sans',sans-serif}
        *,*::before,*::after{box-sizing:border-box;margin:0;padding:0}
        html{scroll-behavior:smooth}
        body{background:var(--bg);color:var(--text);font-family:var(--font-body);font-size:15px;line-height:1.65;min-height:100vh}
        a{color:var(--accent);text-decoration:none}
        a:hover{color:var(--highlight);text-decoration:underline}

        .topbar{position:sticky;top:0;z-index:100;background:rgba(10,12,16,.95);backdrop-filter:blur(10px);border-bottom:1px solid var(--border);padding:0 2rem;display:flex;align-items:center;gap:1.5rem;height:56px}
        .topbar .brand{font-family:var(--font-display);font-size:1.3rem;font-weight:700;letter-spacing:.08em;color:var(--accent);text-decoration:none;display:flex;align-items:center;gap:.5rem;flex-shrink:0}
        .topbar nav{display:flex;gap:1.5rem;align-items:center}
        .topbar nav a{color:var(--muted);font-size:.85rem;letter-spacing:.05em;text-transform:uppercase;font-weight:600;transition:color .2s}
        .topbar nav a:hover{color:var(--text);text-decoration:none}
        .topbar .spacer{flex:1}
        .breadcrumb{font-size:.8rem;color:var(--muted);display:flex;align-items:center;gap:.5rem;flex-wrap:wrap;min-width:0;overflow:hidden}
        .breadcrumb a{white-space:nowrap;overflow:hidden;text-overflow:ellipsis;max-width:180px;display:inline-block}
        .breadcrumb span.sep{color:var(--border);flex-shrink:0}

        .lang-switcher{position:relative;flex-shrink:0}
        .lang-btn{display:flex;align-items:center;gap:.35rem;padding:.3rem .7rem;border-radius:var(--radius);border:1px solid var(--border);background:var(--bg3);color:var(--text);font-size:.8rem;font-weight:600;cursor:pointer;letter-spacing:.04em;transition:border-color .2s;font-family:var(--font-body)}
        .lang-btn:hover{border-color:var(--accent);color:var(--accent)}
        .lang-menu{display:none;position:absolute;top:calc(100% + 6px);right:0;background:var(--bg2);border:1px solid var(--border);border-radius:var(--radius);min-width:140px;box-shadow:0 8px 32px rgba(0,0,0,.5);z-index:200;overflow:hidden}
        .lang-menu-open{display:block}
        .lang-opt{display:flex;align-items:center;padding:.55rem 1rem;font-size:.83rem;color:var(--text);cursor:pointer;transition:background .15s;user-select:none}
        .lang-opt:hover{background:var(--bg3)}
        .lang-opt-active{color:var(--accent);font-weight:700}
        .lang-opt-active::after{content:'\2713';margin-left:auto;font-size:.75rem}

        .page-wrap{max-width:1280px;margin:0 auto;padding:2rem 2rem 4rem}
        footer{border-top:1px solid var(--border);padding:2rem;text-align:center;color:var(--muted);font-size:.8rem;letter-spacing:.04em}

        .badge{display:inline-block;padding:2px 10px;border-radius:3px;font-size:.72rem;font-weight:700;letter-spacing:.06em;text-transform:uppercase}
        .badge-platform{background:#1a2240;color:#7090e0;border:1px solid #2a3560}
        .badge-genre{background:#1a2a1a;color:#60c060;border:1px solid #2a4a2a}
        .badge-year{background:#2a1a0a;color:var(--accent);border:1px solid #4a3010}
        .badge-esrb{background:#1e1220;color:#b060c0;border:1px solid #3e2040}
        .badge-vndb-ero{background:#2a0a1a;color:#f0708a;border:1px solid #5a1030}
        .badge-vndb-cont{background:#1a1a2a;color:#8090d0;border:1px solid #2a3060}
        .badge-vndb-tech{background:#1a2020;color:#60b0a0;border:1px solid #2a4040}
        .badge-adult{background:#2a0a1a;color:#f472b6;border:1px solid #7f1d4a;font-weight:900}
        .audio-wrap{padding:.5rem;background:var(--bg3);border-radius:var(--radius);border:1px solid var(--border);width:100%;max-width:420px}
        .audio-wrap audio{width:100%}
        .adv-search-btn{background:var(--bg3);border:1px solid var(--border);border-radius:var(--radius);color:var(--muted);font-size:1rem;cursor:pointer;padding:.3rem .6rem;transition:all .2s;line-height:1}
        .adv-search-btn:hover{border-color:var(--accent);color:var(--accent)}
        .adv-search-btn.adv-active{border-color:var(--accent);color:var(--accent);background:#1a2a3a}
        .adv-dialog{padding:0;border:1px solid var(--border);border-radius:8px;background:var(--bg2);color:var(--text);max-width:90vw;width:480px;max-height:85vh;box-shadow:0 20px 60px rgba(0,0,0,.8)}
        .adv-dialog::backdrop{background:rgba(0,0,0,.7)}
        .adv-dialog-inner{display:flex;flex-direction:column;max-height:85vh}
        .adv-dialog-header{display:flex;align-items:center;justify-content:space-between;padding:.9rem 1.2rem;border-bottom:1px solid var(--border);flex-shrink:0}
        .adv-dialog-title{font-family:var(--font-display);font-size:1rem;font-weight:700;color:var(--accent)}
        .adv-close{background:none;border:none;color:var(--muted);font-size:1.1rem;cursor:pointer;padding:.2rem .4rem;border-radius:4px;line-height:1;transition:color .2s}
        .adv-close:hover{color:var(--text)}
        .adv-dialog-body{padding:1rem 1.2rem;overflow-y:auto}
        .adv-row{display:flex;align-items:center;gap:.8rem;margin-bottom:.7rem}
        .adv-row label{flex:0 0 120px;font-size:.82rem;color:var(--muted);text-align:right}
        .adv-row input,.adv-row select{flex:1;background:var(--bg3);border:1px solid var(--border);border-radius:var(--radius);padding:.35rem .6rem;color:var(--text);font-size:.82rem;font-family:var(--font-body)}
        .adv-row input:focus,.adv-row select:focus{outline:none;border-color:var(--accent)}
        .adv-actions{display:flex;gap:.6rem;justify-content:flex-end;margin-top:1rem;padding-top:.8rem;border-top:1px solid var(--border)}
        .adv-btn{padding:.45rem 1.2rem;border-radius:var(--radius);font-size:.82rem;font-weight:600;cursor:pointer;border:1px solid var(--border);font-family:var(--font-body);transition:all .2s}
        .adv-btn-apply{background:var(--accent);color:#fff;border-color:var(--accent)}
        .adv-btn-apply:hover{filter:brightness(1.15)}
        .adv-btn-reset{background:var(--bg3);color:var(--muted)}
        .adv-btn-reset:hover{border-color:var(--accent);color:var(--text)}
        .roms-btn{display:inline-flex;align-items:center;gap:.4rem;margin:.75rem 0 .25rem;padding:.45rem 1rem;background:var(--bg3);border:1px solid var(--border);border-radius:var(--radius);color:var(--text);font-size:.85rem;font-weight:600;cursor:pointer;transition:all .2s;font-family:var(--font-body)}
        .roms-btn:hover{border-color:var(--accent);color:var(--accent)}
        .roms-dialog{padding:0;border:1px solid var(--border);border-radius:8px;background:var(--bg2);color:var(--text);max-width:90vw;width:800px;max-height:80vh;box-shadow:0 20px 60px rgba(0,0,0,.8)}
        .roms-dialog::backdrop{background:rgba(0,0,0,.7)}
        .roms-dialog-inner{display:flex;flex-direction:column;max-height:80vh}
        .roms-dialog-header{display:flex;align-items:center;justify-content:space-between;padding:.9rem 1.2rem;border-bottom:1px solid var(--border);flex-shrink:0}
        .roms-dialog-title{font-family:var(--font-display);font-size:1rem;font-weight:700;color:var(--accent)}
        .roms-close{background:none;border:none;color:var(--muted);font-size:1.1rem;cursor:pointer;padding:.2rem .4rem;border-radius:4px;line-height:1;transition:color .2s}
        .roms-close:hover{color:var(--text)}
        .roms-dialog-body{overflow-y:auto;padding:1rem 1.2rem}
        .roms-table{width:100%;border-collapse:collapse;font-size:.82rem}
        .roms-table th{text-align:left;padding:.4rem .7rem;color:var(--muted);font-weight:600;border-bottom:1px solid var(--border);white-space:nowrap}
        .roms-table td{padding:.4rem .7rem;border-bottom:1px solid rgba(255,255,255,.04);vertical-align:middle}
        .roms-table tr:last-child td{border-bottom:none}
        .roms-table tr:hover td{background:var(--bg3)}
        .rom-filename{word-break:break-all;color:var(--text)}
        .rom-size{white-space:nowrap;color:var(--muted);text-align:right}
        .rom-crc{font-family:monospace;color:#7dd3fc;white-space:nowrap}
        .rom-origin{color:var(--muted);white-space:nowrap;text-align:center}

        .games-grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(180px,1fr));gap:1.25rem}
        .game-card{background:var(--card);border:1px solid var(--border);border-radius:var(--radius);overflow:hidden;transition:transform .2s,border-color .2s,box-shadow .2s;display:flex;flex-direction:column}
        .game-card:hover{transform:translateY(-4px);border-color:var(--accent2);box-shadow:0 8px 32px rgba(0,0,0,.5)}
        .game-card>a{color:inherit;text-decoration:none;display:flex;flex-direction:column;height:100%;position:relative}
        .game-card-img{width:100%;aspect-ratio:3/4;background:var(--bg3);overflow:hidden;flex-shrink:0}
        .game-card-img img{width:100%;height:100%;object-fit:cover;transition:transform .3s}
        .game-card:hover .game-card-img img{transform:scale(1.05)}
        .game-card-no-img{width:100%;aspect-ratio:3/4;background:var(--bg3);display:flex;flex-direction:column;align-items:center;justify-content:center;gap:.5rem;color:var(--muted);font-family:var(--font-display);font-size:.75rem;text-transform:uppercase;text-align:center;padding:1rem}
        .game-card-no-img .icon{font-size:2.5rem;opacity:.3}
        .game-card-body{padding:.7rem .8rem;flex:1;display:flex;flex-direction:column;gap:.3rem}
        .game-card-title{font-family:var(--font-display);font-size:.95rem;font-weight:700;color:var(--highlight);line-height:1.3;display:-webkit-box;-webkit-line-clamp:2;-webkit-box-orient:vertical;overflow:hidden}
        .game-card-meta{font-size:.72rem;color:var(--muted);display:flex;align-items:center;gap:.5rem;flex-wrap:wrap}

        .filter-bar{display:flex;gap:1rem;align-items:center;margin-bottom:1.5rem;flex-wrap:wrap}
        .filter-bar input[type=search],.filter-bar select{background:var(--bg3);border:1px solid var(--border);border-radius:var(--radius);padding:.5rem .9rem;color:var(--text);font-family:var(--font-body);font-size:.85rem;outline:none;transition:border-color .2s}
        .filter-bar input[type=search]:focus,.filter-bar select:focus{border-color:var(--accent)}
        .filter-bar input[type=search]{flex:1;min-width:200px}
        #game-genre{min-width:150px;max-width:210px}
        #page-jump{min-width:110px}
        .results-count{color:var(--muted);font-size:.82rem}
        .load-status{font-size:.75rem;color:var(--muted);padding:.3rem .8rem;background:var(--bg3);border:1px solid var(--border);border-radius:var(--radius);margin-bottom:.75rem;display:inline-flex;align-items:center;gap:.5rem}
        .load-status::before{content:'';display:inline-block;width:8px;height:8px;border-radius:50%;background:var(--accent);animation:gdb-pulse 1s ease-in-out infinite}

        .pagination{display:flex;gap:.4rem;align-items:center;justify-content:center;margin-top:2rem;flex-wrap:wrap}
        .pagination a,.pagination span{display:inline-flex;align-items:center;justify-content:center;min-width:34px;height:34px;padding:0 6px;border-radius:var(--radius);font-size:.82rem;font-weight:600;border:1px solid var(--border);background:var(--card);color:var(--text);cursor:pointer;transition:background .15s,border-color .15s}
        .pagination a{text-decoration:none}
        .pagination a:hover{background:var(--bg3);border-color:var(--accent);color:var(--accent)}
        .pg-active{background:var(--accent)!important;border-color:var(--accent)!important;color:#000!important;cursor:default!important}
        .pg-ellipsis{border:none!important;background:none!important;color:var(--muted)!important;cursor:default!important}
        .pg-disabled{opacity:.3!important;cursor:default!important;pointer-events:none!important}

        .platforms-grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(260px,1fr));gap:1.25rem}
        .category-heading{font-family:var(--font-display);font-size:1.1rem;color:var(--muted);text-transform:uppercase;letter-spacing:.08em;margin:2.5rem 0 1rem;padding-bottom:.5rem;border-bottom:1px solid var(--border)}
        .platform-card{background:var(--card);border:1px solid var(--border);border-radius:var(--radius);padding:1.4rem 1.6rem;transition:border-color .2s,transform .2s,box-shadow .2s}
        .platform-card:hover{border-color:var(--accent2);transform:translateY(-3px);box-shadow:0 6px 24px rgba(0,0,0,.4)}
        .platform-card>a{text-decoration:none;color:inherit;display:block}
        .platform-card-name{font-family:var(--font-display);font-size:1.15rem;font-weight:700;color:var(--highlight);margin-bottom:.3rem}
        .platform-card-meta{color:var(--muted);font-size:.8rem}
        .platform-card-count{display:inline-block;margin-top:.6rem;font-family:var(--font-display);font-size:1.1rem;color:var(--accent);font-weight:700}
        .show-small-btn{display:inline-flex;align-items:center;margin:.5rem 0 1.5rem;padding:.35rem .9rem;background:transparent;border:1px dashed var(--border);border-radius:var(--radius);color:var(--muted);font-size:.78rem;font-weight:600;cursor:pointer;letter-spacing:.03em;transition:all .2s;font-family:var(--font-body)}
        .show-small-btn:hover{border-color:var(--accent);color:var(--accent)}

        .page-header{padding:2.5rem 0 2rem;border-bottom:1px solid var(--border);margin-bottom:2rem}
        .page-header h1{font-family:var(--font-display);font-size:clamp(1.8rem,4vw,2.8rem);font-weight:700;color:var(--highlight);letter-spacing:.02em;line-height:1.2}
        .page-header .subtitle{color:var(--muted);font-size:.9rem;margin-top:.4rem}

        .platform-hero{background:linear-gradient(135deg,var(--bg2),var(--bg3));border:1px solid var(--border);border-radius:var(--radius);padding:2rem 2.5rem;margin-bottom:2rem;position:relative;overflow:hidden}
        .platform-hero::before{content:'';position:absolute;inset:0;background:repeating-linear-gradient(-45deg,transparent 0,transparent 10px,rgba(255,255,255,.01) 10px,rgba(255,255,255,.01) 11px)}
        .platform-hero h1{font-family:var(--font-display);font-size:clamp(1.5rem,4vw,2.5rem);font-weight:700;color:var(--highlight);letter-spacing:.02em;position:relative}
        .platform-hero-meta{color:var(--muted);font-size:.85rem;margin-top:.5rem;position:relative}
        .platform-hero-stats{display:flex;gap:2rem;margin-top:1.2rem;position:relative;flex-wrap:wrap}
        .stat-val{font-family:var(--font-display);font-size:1.8rem;font-weight:700;color:var(--accent);line-height:1}
        .stat-lbl{font-size:.7rem;text-transform:uppercase;letter-spacing:.08em;color:var(--muted)}

        .game-detail{display:grid;grid-template-columns:280px 1fr;gap:2.5rem}
        .game-cover{position:sticky;top:72px;align-self:start}
        .game-cover-img{width:100%;border-radius:var(--radius);border:1px solid var(--border);overflow:hidden;background:var(--bg3);aspect-ratio:3/4}
        .game-cover-img img{width:100%;height:100%;object-fit:cover}
        .game-cover-no-img{width:100%;aspect-ratio:3/4;background:var(--bg3);border-radius:var(--radius);border:1px solid var(--border);display:flex;flex-direction:column;align-items:center;justify-content:center;color:var(--muted);font-size:.8rem;gap:.5rem}
        .rating-bar{margin-top:1rem;background:var(--bg3);border-radius:var(--radius);padding:.8rem 1rem;border:1px solid var(--border);text-align:center}
        .rating-val{font-family:var(--font-display);font-size:2.2rem;font-weight:700;color:var(--accent);line-height:1}
        .rating-label{font-size:.72rem;color:var(--muted);text-transform:uppercase;letter-spacing:.08em}
        .game-info h1{font-family:var(--font-display);font-size:clamp(1.8rem,4vw,3rem);font-weight:700;color:var(--highlight);letter-spacing:.01em;line-height:1.1;margin-bottom:.6rem}
        .game-year{font-family:var(--font-display);font-size:1rem;color:var(--accent);font-weight:600;letter-spacing:.05em}
        .tags{display:flex;flex-wrap:wrap;gap:.4rem;margin:.8rem 0}
        .overview-container{margin:1.5rem 0}
        .overview-block{color:var(--text);line-height:1.75;font-size:.95rem}
        .info-table{width:100%;border-collapse:collapse;margin:1.5rem 0}
        .info-table tr{border-bottom:1px solid var(--border)}
        .info-table tr:last-child{border-bottom:none}
        .info-table td{padding:.55rem 0;vertical-align:top}
        .info-table td:first-child{width:140px;color:var(--muted);font-size:.8rem;text-transform:uppercase;letter-spacing:.06em;padding-right:1rem;font-weight:600;padding-top:.65rem}

        .media-section{margin-top:2.5rem}
        .media-section h2{font-family:var(--font-display);font-size:1.3rem;font-weight:700;color:var(--highlight);letter-spacing:.04em;margin-bottom:1rem;padding-bottom:.5rem;border-bottom:1px solid var(--border)}
        .media-type-title{font-family:var(--font-display);font-size:.9rem;font-weight:700;color:var(--muted);letter-spacing:.06em;text-transform:uppercase;margin:1.2rem 0 .4rem}
        .media-grid{display:flex;flex-wrap:wrap;gap:.75rem;align-items:flex-start}
        .media-grid a{display:block;position:relative;border-radius:var(--radius);overflow:hidden;border:1px solid var(--border);transition:border-color .2s,transform .2s}
        .media-grid a:hover{border-color:var(--accent);transform:scale(1.02)}
        .media-grid a.has-local,.media-grid .gdb-img-adult.has-local,.video-wrap.has-local,.game-cover-img.has-local,.audio-wrap.has-local{outline:3px solid #2d5a3d;outline-offset:-3px}
        .game-card.game-card-owned{position:relative}
        .game-card.game-card-owned::after{content:'';position:absolute;inset:0;border:3px solid #2d5a3d;border-radius:inherit;pointer-events:none;z-index:2}
        .media-grid img{display:block;max-height:220px;max-width:320px;object-fit:contain;background:var(--bg3)}
        .media-flag{position:absolute;top:4px;left:4px;font-size:14px;line-height:1;pointer-events:none;filter:drop-shadow(0 1px 3px rgba(0,0,0,.9))}
        .media-doc{display:flex;align-items:center;gap:.5rem;padding:.6rem 1rem;background:var(--bg3);border:1px solid var(--border);border-radius:var(--radius);font-size:.85rem;color:var(--text)}
        .media-doc:hover{border-color:var(--accent);color:var(--highlight);text-decoration:none}
        .media-doc-text{display:flex;flex-direction:column;gap:.15rem;min-width:0}
        .media-doc-name{font-weight:600;word-break:break-all}
        .media-doc-region{font-size:.72rem;color:var(--muted);font-weight:400}
        .video-wrap{display:inline-block;vertical-align:top;position:relative;border-radius:var(--radius);overflow:hidden;border:1px solid var(--border);max-width:480px;margin-bottom:.5rem}
        .video-wrap video{display:block;width:100%;background:#000}
        .video-flag{position:absolute;top:4px;left:4px;font-size:14px;line-height:1;pointer-events:none;z-index:1;filter:drop-shadow(0 1px 3px rgba(0,0,0,.9))}

        @media(max-width:760px){.game-detail{grid-template-columns:1fr}.game-cover{position:static}}
        @media(max-width:600px){.topbar{padding:0 1rem;gap:.75rem}.topbar nav{display:none}.page-wrap{padding:1rem 1rem 3rem}.games-grid{grid-template-columns:repeat(auto-fill,minmax(140px,1fr));gap:.75rem}.platforms-grid{grid-template-columns:1fr 1fr}.breadcrumb{display:none}}
        @keyframes gdb-pulse{0%,100%{opacity:.4;transform:scale(.8)}50%{opacity:1;transform:scale(1.2)}}
        .game-card-blurred .game-card-img img,.game-card-blurred .game-card-no-img{filter:blur(18px);transition:filter .3s}
        .game-card-blurred:hover .game-card-img img,.game-card-blurred:hover .game-card-no-img{filter:blur(4px)}
        .gdb-img-blurred img{filter:blur(18px);transition:filter .3s;cursor:pointer}
        .gdb-img-adult{cursor:pointer}
        .gdb-img-blurred:hover img{filter:blur(4px)}
        .gdb-img-blurred img:active,.gdb-img-blurred.gdb-revealed img{filter:none}
        .adult-badge{position:absolute;top:4px;right:4px;background:rgba(180,0,0,.85);color:#fff;font-size:.6rem;font-weight:700;padding:1px 4px;border-radius:3px;letter-spacing:.05em;pointer-events:none}
        .star{position:absolute;top:6px;right:6px;width:20px;height:20px;display:flex;align-items:center;justify-content:center;font-size:12px;line-height:1;border-radius:50%;pointer-events:none;z-index:2}
        .star-bronze{background:rgba(90,70,55,0.85);color:#c0a080}
        .star-silver{background:rgba(140,155,180,0.9);color:#e8f0ff;text-shadow:0 0 3px rgba(200,220,255,0.4)}
        .star-gold{background:rgba(200,160,30,0.92);color:#fff8d0;text-shadow:0 0 6px rgba(255,230,80,0.7);box-shadow:0 0 8px rgba(255,200,40,0.35)}
        .adult-btn{padding:.3rem .7rem;border-radius:var(--radius);border:1px solid var(--border);font-size:.75rem;font-weight:700;cursor:pointer;letter-spacing:.04em;transition:all .2s;white-space:nowrap;font-family:var(--font-body)}
        .adult-btn-0{background:var(--bg3);color:var(--muted);border-color:var(--border)}
        .adult-btn-0:hover{border-color:var(--accent);color:var(--text)}
        .adult-btn-1{background:#2a0a0a;color:#f87171;border-color:#7f1d1d}
        .adult-btn-1:hover{background:#3a0a0a;border-color:#ef4444}
        .adult-btn-2{background:#1a0a2a;color:#c084fc;border-color:#6b21a8}
        .adult-btn-2:hover{background:#2a0a3a;border-color:#a855f7}
        .owned-btn{padding:.3rem .7rem;border-radius:var(--radius);border:1px solid var(--border);font-size:.75rem;font-weight:700;cursor:pointer;letter-spacing:.04em;transition:all .2s;white-space:nowrap;font-family:var(--font-body)}
        .owned-btn-0{background:var(--bg3);color:var(--muted);border-color:var(--border)}
        .owned-btn-0:hover{border-color:var(--accent);color:var(--text)}
        .owned-btn-1{background:#0a2a14;color:#4ade80;border-color:#166534}
        .owned-btn-1:hover{background:#0a3a18;border-color:#22c55e}
        .lock-btn{padding:.3rem .7rem;border-radius:var(--radius);border:1px solid var(--border);font-size:.75rem;font-weight:700;cursor:pointer;letter-spacing:.04em;transition:all .2s;white-space:nowrap;font-family:var(--font-body)}
        .lock-btn-locked{background:#2a0a0a;color:#f87171;border-color:#7f1d1d}
        .lock-btn-locked:hover{background:#3a0a0a;border-color:#ef4444}
        .lock-btn-unlocked{background:#0a2a14;color:#4ade80;border-color:#166534}
        .lock-btn-unlocked:hover{background:#0a3a18;border-color:#22c55e}
        .lock-modal-overlay{position:fixed;inset:0;background:rgba(0,0,0,.8);z-index:9999;display:flex;align-items:center;justify-content:center;padding:1rem}
        .lock-modal{background:var(--bg2);border:1px solid #7f1d1d;border-radius:8px;padding:2rem;max-width:460px;width:100%;box-shadow:0 20px 60px rgba(0,0,0,.9)}
        .lock-modal-title{font-family:var(--font-display);font-size:1.4rem;font-weight:900;color:#f87171;margin:0 0 1rem;letter-spacing:.04em}
        .lock-modal-body{color:var(--text);font-size:.95rem;line-height:1.5;margin:0 0 1.5rem}
        .lock-modal-input{width:100%;padding:.6rem;border-radius:var(--radius);border:1px solid var(--border);background:var(--bg3);color:var(--text);font-size:1rem;font-family:var(--font-body);box-sizing:border-box;margin:0 0 1rem}
        .lock-modal-input:focus{outline:none;border-color:var(--accent)}
        .lock-modal-error{color:#f87171;font-size:.85rem;margin:0 0 1rem;min-height:1.2em}
        .lock-modal-btns{display:flex;gap:.6rem;flex-wrap:wrap}
        .lock-modal-confirm{flex:1;padding:.6rem;border-radius:var(--radius);border:1px solid #7f1d1d;background:#2a0a0a;color:#f87171;font-weight:900;cursor:pointer;font-family:var(--font-body);letter-spacing:.04em}
        .lock-modal-confirm:hover{background:#3a0a0a;border-color:#ef4444}
        .lock-modal-cancel{flex:1;padding:.6rem;border-radius:var(--radius);border:1px solid var(--border);background:var(--bg3);color:var(--muted);font-weight:700;cursor:pointer;font-family:var(--font-body);letter-spacing:.04em}
        .lock-modal-cancel:hover{border-color:var(--accent);color:var(--text)}
        .adult-modal-overlay{position:fixed;inset:0;background:rgba(0,0,0,.8);z-index:9999;display:flex;align-items:center;justify-content:center;padding:1rem}
        .adult-modal{background:var(--bg2);border:1px solid #7f1d1d;border-radius:8px;padding:2rem;max-width:460px;width:100%;box-shadow:0 20px 60px rgba(0,0,0,.9)}
        .adult-modal-title{font-size:1.2rem;font-weight:700;color:#f87171;margin-bottom:.75rem}
        .adult-modal-body{color:var(--text);font-size:.9rem;line-height:1.6;margin-bottom:1.5rem}
        .adult-modal-btns{display:flex;flex-direction:column;gap:.6rem}
        .adult-modal-confirm{padding:.65rem 1rem;background:#7f1d1d;color:#fff;border:none;border-radius:var(--radius);font-weight:700;cursor:pointer;font-size:.88rem;transition:background .2s;text-align:left}
        .adult-modal-confirm:hover{background:#991b1b}
        .adult-modal-confirm-nsfw{background:#4a1d96}
        .adult-modal-confirm-nsfw:hover{background:#6d28d9}
        .adult-modal-cancel{padding:.6rem 1rem;background:var(--bg3);color:var(--muted);border:1px solid var(--border);border-radius:var(--radius);font-weight:600;cursor:pointer;font-size:.88rem;transition:all .2s;text-align:left}
        .adult-modal-cancel:hover{border-color:var(--accent);color:var(--text)}
        .external-links{display:flex;flex-wrap:wrap;gap:.5rem;margin:.75rem 0}
        .ext-link{display:inline-flex;align-items:center;gap:.35rem;padding:.3rem .7rem;border-radius:var(--radius);font-size:.78rem;font-weight:600;text-decoration:none;border:1px solid var(--border);transition:all .2s}
        .ext-link:hover{transform:translateY(-1px);text-decoration:none}
        .ext-link-lb{background:#1a2a1a;color:#86efac;border-color:#166534}.ext-link-lb:hover{border-color:#4ade80;color:#4ade80}
        .ext-link-steam{background:#1a2030;color:#93c5fd;border-color:#1d4ed8}.ext-link-steam:hover{border-color:#60a5fa;color:#60a5fa}
        .ext-link-vndb{background:#2a1a2a;color:#d8b4fe;border-color:#7e22ce}.ext-link-vndb:hover{border-color:#c084fc;color:#c084fc}
        .ext-link-ss{background:#2a1a10;color:#fdba74;border-color:#c2410c}.ext-link-ss:hover{border-color:#fb923c;color:#fb923c}
        .ext-link-igdb{background:#1a201a;color:#a3e635;border-color:#3f6212}.ext-link-igdb:hover{border-color:#84cc16;color:#84cc16}
        .global-search-wrap{position:relative;flex:0 1 320px;min-width:160px}
        .global-search-input{width:100%;background:var(--bg3);border:1px solid var(--border);border-radius:var(--radius);padding:.38rem .8rem;color:var(--text);font-family:var(--font-body);font-size:.82rem;outline:none;transition:border-color .2s}
        .global-search-input:focus{border-color:var(--accent)}
        .global-search-input::placeholder{color:#4a5570;font-size:.8rem}
        .global-search-results{position:absolute;top:calc(100% + 4px);left:0;min-width:360px;width:max-content;max-width:520px;background:var(--bg2);border:1px solid var(--border);border-radius:var(--radius);max-height:460px;overflow-y:auto;z-index:500;box-shadow:0 8px 32px rgba(0,0,0,.6)}
        .gsr-platform-filter{padding:.35rem .5rem;border-bottom:1px solid var(--border);background:var(--bg3);position:sticky;top:0;z-index:1}
        .gsr-platform-filter select{width:100%;background:var(--bg2);color:var(--text);border:1px solid var(--border);border-radius:4px;padding:.28rem .5rem;font-size:.78rem;font-family:var(--font-body);cursor:pointer;outline:none}
        .gsr-platform-filter select:focus{border-color:var(--accent)}
        .gsr-item{display:flex;align-items:center;gap:.6rem;padding:.45rem .7rem;text-decoration:none;border-bottom:1px solid rgba(255,255,255,.04);color:var(--text);transition:background .12s}
        .gsr-item:last-child{border-bottom:none}
        .gsr-item:hover{background:var(--bg3);color:var(--highlight);text-decoration:none}
        .gsr-thumb{flex-shrink:0;width:32px;height:44px;background:var(--bg3);border-radius:3px;overflow:hidden;display:flex;align-items:center;justify-content:center}
        .gsr-thumb img{width:100%;height:100%;object-fit:cover;display:block}
        .gsr-info{flex:1;min-width:0}
        .gsr-name{font-size:.85rem;font-weight:700;color:var(--highlight);line-height:1.3;display:flex;align-items:center;gap:.4rem}
        .gsr-adult{font-size:.6rem;font-weight:900;background:rgba(180,0,0,.85);color:#fff;padding:1px 4px;border-radius:3px;letter-spacing:.05em;flex-shrink:0}
        .gsr-alt{font-size:.78rem;color:#8090b8;margin-top:.1rem;line-height:1.3;font-style:italic;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
        .gsr-plat{font-size:.72rem;color:#5a6a8a;margin-top:.15rem;text-transform:uppercase;letter-spacing:.04em}
        .gsr-none{padding:.8rem;color:var(--muted);font-size:.82rem;text-align:center}
        @media(max-width:900px){.global-search-wrap{flex:0 1 200px}.global-search-results{min-width:260px;max-width:360px}}
        """;

    // ── Shared JS (i18n + adult mode + owned mode + parental lock + global search) ──
    public const string SharedJs = """
        <script>
        const GDB_LANGS      = ['fr','en','de','es','it','pt'];
        const GDB_LANG_NAMES = {fr:'Français',en:'English',de:'Deutsch',es:'Español',it:'Italiano',pt:'Português'};
        const GDB_COOKIE     = 'litebox_lang';

        const GDB_I18N = {
          'platform':     {fr:'Plateforme',   en:'Platform',    de:'Plattform',     es:'Plataforma',        it:'Piattaforma',     pt:'Plataforma'},
          'release-date': {fr:'Date de sortie',en:'Release date',de:'Erscheinungsdatum',es:'Fecha de lanzamiento',it:'Data di uscita',pt:'Data de lançamento'},
          'type':         {fr:'Type',          en:'Type',        de:'Typ',           es:'Tipo',              it:'Tipo',            pt:'Tipo'},
          'developer':    {fr:'Développeur',   en:'Developer',   de:'Entwickler',    es:'Desarrollador',     it:'Sviluppatore',    pt:'Desenvolvedor'},
          'publisher':    {fr:'Éditeur',       en:'Publisher',   de:'Herausgeber',   es:'Editor',            it:'Editore',         pt:'Editor'},
          'max-players':  {fr:'Max joueurs',   en:'Max players', de:'Max. Spieler',  es:'Máx. jugadores',   it:'Max. giocatori',  pt:'Máx. jogadores'},
          'cooperative':  {fr:'Coopératif',    en:'Cooperative', de:'Kooperativ',    es:'Cooperativo',       it:'Cooperativo',     pt:'Cooperativo'},
          'yes':          {fr:'Oui',           en:'Yes',         de:'Ja',            es:'Sí',                it:'Sì',              pt:'Sim'},
          'no':           {fr:'Non',           en:'No',          de:'Nein',          es:'No',                it:'No',              pt:'Não'},
          'wikipedia':    {fr:'Wikipedia',     en:'Wikipedia',   de:'Wikipedia',     es:'Wikipedia',         it:'Wikipedia',       pt:'Wikipedia'},
          'video-link':   {fr:'Vidéo',         en:'Video',       de:'Video',         es:'Vídeo',             it:'Video',           pt:'Vídeo'},
          'alt-titles':   {fr:'Titres alt.',   en:'Alt. titles', de:'Alt. Titel',    es:'Títulos alt.',      it:'Titoli alt.',     pt:'Títulos alt.'},
          'media':        {fr:'Médias',        en:'Media',       de:'Medien',        es:'Medios',            it:'Media',           pt:'Mídia'},
          'videos':       {fr:'Vidéos',        en:'Videos',      de:'Videos',        es:'Vídeos',            it:'Video',           pt:'Vídeos'},
          'no-cover':     {fr:'Pas de couverture',en:'No cover', de:'Kein Cover',    es:'Sin portada',       it:'Nessuna copertina',pt:'Sem capa'},
          'community-rating':{fr:'Note communauté',en:'Community rating',de:'Community-Bewertung',es:'Nota comunidad',it:'Voto community',pt:'Nota comunidade'},
          'votes':        {fr:'votes',         en:'votes',       de:'Stimmen',       es:'votos',             it:'voti',            pt:'votos'},
          'home':         {fr:'Accueil',       en:'Home',        de:'Startseite',    es:'Inicio',            it:'Home',            pt:'Início'},
          'platforms-nav':{fr:'Plateformes',   en:'Platforms',   de:'Plattformen',   es:'Plataformas',       it:'Piattaforme',     pt:'Plataformas'},
          'search-placeholder':{fr:'Rechercher un jeu…',en:'Search a game…',de:'Spiel suchen…',es:'Buscar un juego…',it:'Cerca un gioco…',pt:'Buscar jogo…'},
          'adult-mode-0': {fr:'Adulte : OFF',      en:'Adult: OFF',      de:'Erwachsene: AUS', es:'Adulto: APAGADO', it:'Adulti: OFF',      pt:'Adulto: OFF'},
          'adult-mode-1': {fr:'Mode +18 (flou)',   en:'+18 (blurred)',   de:'+18 (unscharf)', es:'+18 (borroso)',   it:'+18 (sfocato)',   pt:'+18 (desfocado)'},
          'adult-mode-2': {fr:'Mode NSFW',          en:'NSFW mode',       de:'NSFW-Modus',     es:'Modo NSFW',       it:'Modalità NSFW',   pt:'Modo NSFW'},
          'adult-disclaimer-title':{fr:'Contenu réservé aux adultes',en:'Adult content',de:'Nur für Erwachsene',es:'Contenido para adultos',it:'Contenuto per adulti',pt:'Conteúdo adulto'},
          'adult-disclaimer-body': {fr:'Ce site contient du contenu réservé aux adultes (+18). Confirmez-vous avoir l\'âge légal requis dans votre pays ?',en:'This site contains adult content (+18). Do you confirm you are of legal age in your country?',de:'Diese Website enthält Inhalte nur für Erwachsene (+18). Bestätigen Sie, dass Sie in Ihrem Land volljährig sind?',es:'Este sitio contiene contenido para adultos (+18). ¿Confirma que tiene la edad legal requerida en su país?',it:'Questo sito contiene contenuti per adulti (+18). Confermi di essere maggiorenne nel suo paese?',pt:'Este site contém conteúdo adulto (+18). Confirma que tem a idade legal no seu país?'},
          'adult-confirm':{fr:'Oui, je suis majeur — activer le mode +18 (flou)',en:'Yes, I am of age — enable +18 mode (blurred)',de:'Ja, volljährig — +18-Modus aktivieren (unscharf)',es:'Sí, soy mayor — activar modo +18 (borroso)',it:'Sì, maggiorenne — attiva +18 (sfocato)',pt:'Sim, maior de idade — ativar modo +18 (desfocado)'},
          'adult-confirm-nsfw':{fr:'Oui — activer le mode NSFW (sans flou)',en:'Yes — enable NSFW mode (no blur)',de:'Ja — NSFW-Modus aktivieren (ohne Unschärfe)',es:'Sí — activar modo NSFW (sin blur)',it:'Sì — attiva NSFW (nessuna sfocatura)',pt:'Sim — ativar modo NSFW (sem blur)'},
          'adult-cancel': {fr:'Non, retour',en:'No, go back',de:'Nein, zurück',es:'No, volver',it:'No, torna',pt:'Não, voltar'},
          'games-count-suffix':{fr:'jeux',     en:'games',       de:'Spiele',        es:'juegos',            it:'giochi',          pt:'jogos'},
          'all-genres':    {fr:'Tous les genres',en:'All genres',de:'Alle Genres',es:'Todos los géneros',it:'Tutti i generi',pt:'Todos os gêneros'},
          'sort-alpha':   {fr:'Alphabétique',  en:'Alphabetical',de:'Alphabetisch',  es:'Alfabético',        it:'Alfabetico',      pt:'Alfabético'},
          'sort-year-asc':{fr:'Année ↑',       en:'Year ↑',      de:'Jahr ↑',        es:'Año ↑',             it:'Anno ↑',          pt:'Ano ↑'},
          'sort-year-desc':{fr:'Année ↓',      en:'Year ↓',      de:'Jahr ↓',        es:'Año ↓',             it:'Anno ↓',          pt:'Ano ↓'},
          'sort-rating':  {fr:'Note ↓',         en:'Rating ↓',    de:'Bewertung ↓',  es:'Nota ↓',            it:'Voto ↓',          pt:'Nota ↓'},
          'sort-stars':   {fr:'★ Étoiles',     en:'★ Stars',     de:'★ Sterne',     es:'★ Estrellas',       it:'★ Stelle',        pt:'★ Estrelas'},
          'search-global':{fr:'Recherche globale…',en:'Global search…',de:'Globale Suche…',es:'Búsqueda global…',it:'Ricerca globale…',pt:'Pesquisa global…'},
          'all-platforms':{fr:'Toutes les plateformes',en:'All platforms',de:'Alle Plattformen',es:'Todas las plataformas',it:'Tutte le piattaforme',pt:'Todas as plataformas'},
          'owned-mode-0':  {fr:'Tous les jeux',         en:'All games',           de:'Alle Spiele',         es:'Todos los juegos',   it:'Tutti i giochi',      pt:'Todos os jogos'},
          'owned-mode-1':  {fr:'Possédés uniquement',   en:'Owned only',          de:'Nur eigene',          es:'Solo poseídos',      it:'Solo posseduti',      pt:'Apenas possuídos'},
          'owned-mode-tooltip':{fr:'Afficher uniquement les jeux possédés',en:'Show only owned games',de:'Nur eigene Spiele anzeigen',es:'Mostrar solo juegos poseídos',it:'Mostra solo i giochi posseduti',pt:'Mostrar apenas jogos possuídos'},
          'more-platforms':{fr:'+ {n} plateforme(s) plus petite(s)',en:'+ {n} smaller platform(s)',de:'+ {n} kleinere Plattform(en)',es:'+ {n} plataforma(s) más pequeña(s)',it:'+ {n} piattaforma/e più piccola/e',pt:'+ {n} plataforma(s) menor(es)'},
          'less-platforms':{fr:'Masquer',en:'Hide',de:'Ausblenden',es:'Ocultar',it:'Nascondi',pt:'Ocultar'},
          'lock-locked':       {fr:'🔒 Verrouillé',         en:'🔒 Locked',           de:'🔒 Gesperrt',          es:'🔒 Bloqueado',        it:'🔒 Bloccato',         pt:'🔒 Bloqueado'},
          'lock-unlocked':     {fr:'🔓 Déverrouillé',       en:'🔓 Unlocked',         de:'🔓 Entsperrt',         es:'🔓 Desbloqueado',     it:'🔓 Sbloccato',        pt:'🔓 Desbloqueado'},
          'lock-pin-title':    {fr:'Déverrouiller le site', en:'Unlock the site',     de:'Site entsperren',      es:'Desbloquear sitio',   it:'Sblocca il sito',     pt:'Desbloquear site'},
          'lock-pin-body':     {fr:'Entrez le code PIN parental pour déverrouiller ce site (cette session de navigation uniquement).',en:'Enter the parental PIN to unlock this site (this browsing session only).',de:'Geben Sie die elterliche PIN ein, um diese Website zu entsperren (nur diese Browsersitzung).',es:'Introduzca el PIN parental para desbloquear este sitio (solo esta sesión de navegación).',it:'Inserisci il PIN parentale per sbloccare il sito (solo per questa sessione di navigazione).',pt:'Insira o PIN parental para desbloquear o site (apenas esta sessão de navegação).'},
          'lock-pin-placeholder':{fr:'Code PIN',             en:'PIN',                 de:'PIN',                  es:'PIN',                 it:'PIN',                 pt:'PIN'},
          'lock-pin-confirm':  {fr:'Déverrouiller',          en:'Unlock',              de:'Entsperren',           es:'Desbloquear',         it:'Sblocca',             pt:'Desbloquear'},
          'lock-pin-cancel':   {fr:'Annuler',                en:'Cancel',              de:'Abbrechen',            es:'Cancelar',            it:'Annulla',             pt:'Cancelar'},
          'lock-pin-wrong':    {fr:'PIN incorrect.',en:'Wrong PIN.',de:'Falsche PIN.',es:'PIN incorrecto.',it:'PIN errato.',pt:'PIN incorreto.'},
          'lock-confirm-title':{fr:'Verrouiller le site',    en:'Lock the site',       de:'Site sperren',         es:'Bloquear sitio',      it:'Blocca il sito',      pt:'Bloquear site'},
          'lock-confirm-body': {fr:'Le filtrage parental va s\'appliquer immédiatement. Continuer ?',en:'Parental filtering will apply immediately. Continue?',de:'Die Kindersicherung wird sofort wirksam. Fortfahren?',es:'El filtrado parental se aplicará inmediatamente. ¿Continuar?',it:'Il filtro parentale verrà applicato immediatamente. Continuare?',pt:'O filtro parental será aplicado imediatamente. Continuar?'},
          'lock-confirm-yes':  {fr:'Verrouiller',            en:'Lock',                de:'Sperren',              es:'Bloquear',            it:'Blocca',              pt:'Bloquear'},
          'lock-bb-only-title':{fr:'Verrouillage permanent',  en:'Permanent lock',      de:'Dauerhafte Sperre',   es:'Bloqueo permanente',  it:'Blocco permanente',   pt:'Bloqueio permanente'},
          'lock-bb-only-body': {fr:'Le déverrouillage nécessite qu\'un code PIN parental soit configuré. Configurez-le pour pouvoir déverrouiller cette session.',en:'Unlocking requires a parental PIN to be configured. Set one to unlock this session.',de:'Zum Entsperren muss eine elterliche PIN konfiguriert sein. Richten Sie eine ein, um diese Sitzung zu entsperren.',es:'El desbloqueo requiere un PIN parental configurado. Configure uno para desbloquear esta sesión.',it:'Lo sblocco richiede un PIN parentale configurato. Impostane uno per sbloccare questa sessione.',pt:'O desbloqueio requer um PIN parental configurado. Configure um para desbloquear esta sessão.'},
          'lock-bb-only-ok':   {fr:'Compris',                en:'OK',                  de:'Verstanden',           es:'Entendido',           it:'Capito',              pt:'Entendi'},
        };

        function gdbT(key,lang){const row=GDB_I18N[key];if(!row)return key;return row[lang]||row['en']||key;}

        function gdbGetCookie(n){const c=document.cookie.split(';').map(s=>s.trim()).find(s=>s.startsWith(n+'='));return c?decodeURIComponent(c.split('=')[1]):null;}
        function gdbSetCookie(n,v){document.cookie=n+'='+encodeURIComponent(v)+';path=/;max-age='+(365*24*3600)+';SameSite=Lax';}
        function gdbDetectLang(){const s=gdbGetCookie(GDB_COOKIE);if(s&&GDB_LANGS.includes(s))return s;const bl=(navigator.language||'en').slice(0,2).toLowerCase();return GDB_LANGS.includes(bl)?bl:'en';}

        function gdbApplyLang(lang,save){
          if(save)gdbSetCookie(GDB_COOKIE,lang);
          document.querySelectorAll('.overview-block').forEach(el=>{if(!el.dataset.lang)return;el.style.display=(el.dataset.lang===lang)?'':'none';});
          document.querySelectorAll('.lang-opt').forEach(el=>{el.classList.toggle('lang-opt-active',el.dataset.lang===lang);});
          const lbl=document.getElementById('lang-btn-label'); if(lbl)lbl.textContent=lang.toUpperCase();
          document.querySelectorAll('[data-i18n]').forEach(el=>{el.textContent=gdbT(el.dataset.i18n,lang);});
          document.querySelectorAll('[data-i18n-placeholder]').forEach(el=>{el.placeholder=gdbT(el.dataset.i18nPlaceholder,lang);});
          const gs=document.getElementById('global-search'); if(gs)gs.placeholder=gdbT('search-global',lang);
          document.querySelectorAll('[data-i18n-count]').forEach(el=>{
            const t=gdbT(el.dataset.i18nCount,lang).replace('{n}',el.dataset.count||'');
            el.textContent=t;
          });
          document.querySelectorAll('[data-i18n-options]').forEach(sel=>{
            sel.querySelectorAll('option[data-i18n]').forEach(opt=>{opt.textContent=gdbT(opt.dataset.i18n,lang);});
          });
        }

        function gdbToggleLangMenu(e){e.stopPropagation();document.getElementById('lang-menu')?.classList.toggle('lang-menu-open');}
        document.addEventListener('click',()=>{document.getElementById('lang-menu')?.classList.remove('lang-menu-open');});

        const GDB_ADULT_COOKIE='litebox_adult';
        function gdbGetAdultMode(){var v=parseInt(gdbGetCookie(GDB_ADULT_COOKIE)||'0');return (v===1||v===2)?v:0;}
        function gdbIsAdultMode(){return gdbGetAdultMode()>=1;}
        function gdbIsNsfwMode(){return gdbGetAdultMode()===2;}
        function gdbSetAdultMode(m){gdbSetCookie(GDB_ADULT_COOKIE,String(m));gdbApplyAdultMode();}
        function gdbApplyAdultMode(){
          var m=gdbGetAdultMode();
          var lang=gdbDetectLang();
          var btn=document.getElementById('adult-mode-btn');
          if(btn){btn.textContent=gdbT('adult-mode-'+m,lang);btn.className='adult-btn adult-btn-'+m;}
          var box=document.getElementById('global-search-results');
          if(box)box.style.display='none';
          if(typeof window.gdbPlatformUpdate==='function'){window.gdbPlatformUpdate();}
          document.querySelectorAll('.gdb-img-adult').forEach(function(wrap){wrap.classList.toggle('gdb-img-blurred',m===1);});
        }
        function gdbCycleAdultMode(){var m=gdbGetAdultMode();if(m===0){gdbAdultDisclaimer(null);return;}gdbSetAdultMode(m===1?2:0);}

        const GDB_OWNED_COOKIE='litebox_owned';
        function gdbGetOwnedMode(){return gdbGetCookie(GDB_OWNED_COOKIE)==='1'?1:0;}
        function gdbIsOwnedMode(){return gdbGetOwnedMode()===1;}
        function gdbSetOwnedMode(m){gdbSetCookie(GDB_OWNED_COOKIE,String(m?1:0));location.reload();}
        function gdbApplyOwnedMode(){
          var m=gdbGetOwnedMode();
          var lang=gdbDetectLang();
          var btn=document.getElementById('owned-mode-btn');
          if(btn){
            btn.textContent=gdbT('owned-mode-'+m,lang);
            btn.className='owned-btn owned-btn-'+m;
            btn.title=gdbT('owned-mode-tooltip',lang);
          }
        }
        function gdbCycleOwnedMode(){gdbSetOwnedMode(gdbGetOwnedMode()===1?0:1);}

        function gdbGetParentalState(){
          return window.LB_PARENTAL || {active:false,locked:false,canUnlock:false};
        }
        function gdbApplyParental(){
          var s=gdbGetParentalState();
          var btn=document.getElementById('lock-mode-btn');
          var adultBtn=document.getElementById('adult-mode-btn');
          if(adultBtn){
            adultBtn.style.display = (s.active && s.locked) ? 'none' : '';
          }
          if(!btn)return;
          if(!s.active){btn.style.display='none';return;}
          btn.style.display='';
          var lang=gdbDetectLang();
          if(s.locked){
            btn.textContent=gdbT('lock-locked',lang);
            btn.className='lock-btn lock-btn-locked';
          } else {
            btn.textContent=gdbT('lock-unlocked',lang);
            btn.className='lock-btn lock-btn-unlocked';
          }
        }
        function gdbClickLock(){
          var s=gdbGetParentalState();
          if(!s.active) return;
          if(s.locked){
            if(s.canUnlock){ gdbShowPinModal(); }
            else            { gdbShowLockInfoModal(); }
          } else {
            gdbShowLockConfirmModal();
          }
        }
        function gdbShowPinModal(){
          var lang=gdbDetectLang();
          var modal=document.createElement('div');
          modal.id='lock-modal-overlay';modal.className='lock-modal-overlay';
          function rm(){var o=document.getElementById('lock-modal-overlay');if(o)o.remove();}
          modal.innerHTML='<div class="lock-modal">'
            +'<div class="lock-modal-title">'+gdbT('lock-pin-title',lang)+'</div>'
            +'<div class="lock-modal-body">'+gdbT('lock-pin-body',lang)+'</div>'
            +'<input type="password" id="lock-modal-pin" class="lock-modal-input" placeholder="'+gdbT('lock-pin-placeholder',lang)+'" autocomplete="off" />'
            +'<div class="lock-modal-error" id="lock-modal-err"></div>'
            +'<div class="lock-modal-btns">'
            +'<button class="lock-modal-confirm" id="lock-modal-ok">'+gdbT('lock-pin-confirm',lang)+'</button>'
            +'<button class="lock-modal-cancel" id="lock-modal-cancel">'+gdbT('lock-pin-cancel',lang)+'</button>'
            +'</div></div>';
          document.body.appendChild(modal);
          var input=document.getElementById('lock-modal-pin');
          setTimeout(function(){try{input.focus();}catch(e){}},10);
          function submit(){
            var pin=(input.value||'').trim();
            if(!pin)return;
            fetch('/api/parental/unlock',{
              method:'POST',
              headers:{'Content-Type':'application/json'},
              body:JSON.stringify({pin:pin}),
            }).then(function(r){return r.json();}).then(function(d){
              if(d&&d.success){ rm(); location.reload(); return; }
              var err=document.getElementById('lock-modal-err');
              if(!err) return;
              if(d&&d.reason==='not-allowed')err.textContent=gdbT('lock-bb-only-body',lang);
              else                            err.textContent=gdbT('lock-pin-wrong',lang);
              input.value='';input.focus();
            }).catch(function(){
              var err=document.getElementById('lock-modal-err');
              if(err) err.textContent='Network error';
            });
          }
          document.getElementById('lock-modal-ok').onclick=submit;
          document.getElementById('lock-modal-cancel').onclick=rm;
          input.addEventListener('keydown',function(e){ if(e.key==='Enter'){e.preventDefault();submit();} else if(e.key==='Escape'){e.preventDefault();rm();} });
        }
        function gdbShowLockConfirmModal(){
          var lang=gdbDetectLang();
          var modal=document.createElement('div');
          modal.id='lock-modal-overlay';modal.className='lock-modal-overlay';
          function rm(){var o=document.getElementById('lock-modal-overlay');if(o)o.remove();}
          modal.innerHTML='<div class="lock-modal">'
            +'<div class="lock-modal-title">'+gdbT('lock-confirm-title',lang)+'</div>'
            +'<div class="lock-modal-body">'+gdbT('lock-confirm-body',lang)+'</div>'
            +'<div class="lock-modal-btns">'
            +'<button class="lock-modal-confirm" id="lock-modal-ok">'+gdbT('lock-confirm-yes',lang)+'</button>'
            +'<button class="lock-modal-cancel" id="lock-modal-cancel">'+gdbT('lock-pin-cancel',lang)+'</button>'
            +'</div></div>';
          document.body.appendChild(modal);
          document.getElementById('lock-modal-ok').onclick=function(){
            fetch('/api/parental/lock',{method:'POST'})
              .then(function(){rm();location.reload();})
              .catch(function(){rm();});
          };
          document.getElementById('lock-modal-cancel').onclick=rm;
        }
        function gdbShowLockInfoModal(){
          var lang=gdbDetectLang();
          var modal=document.createElement('div');
          modal.id='lock-modal-overlay';modal.className='lock-modal-overlay';
          function rm(){var o=document.getElementById('lock-modal-overlay');if(o)o.remove();}
          modal.innerHTML='<div class="lock-modal">'
            +'<div class="lock-modal-title">'+gdbT('lock-bb-only-title',lang)+'</div>'
            +'<div class="lock-modal-body">'+gdbT('lock-bb-only-body',lang)+'</div>'
            +'<div class="lock-modal-btns">'
            +'<button class="lock-modal-cancel" id="lock-modal-cancel">'+gdbT('lock-bb-only-ok',lang)+'</button>'
            +'</div></div>';
          document.body.appendChild(modal);
          document.getElementById('lock-modal-cancel').onclick=rm;
        }

        function gdbAdultDisclaimer(href){
          var lang=gdbDetectLang();
          var modal=document.createElement('div');
          modal.id='adult-modal-overlay';modal.className='adult-modal-overlay';
          function rm(){var o=document.getElementById('adult-modal-overlay');if(o)o.remove();}
          modal.innerHTML='<div class="adult-modal">'
            +'<div class="adult-modal-title">'+gdbT('adult-disclaimer-title',lang)+'</div>'
            +'<div class="adult-modal-body">'+gdbT('adult-disclaimer-body',lang)+'</div>'
            +'<div class="adult-modal-btns">'
            +'<button class="adult-modal-confirm" id="adm-btn1">'+gdbT('adult-confirm',lang)+'</button>'
            +'<button class="adult-modal-confirm adult-modal-confirm-nsfw" id="adm-btn2">'+gdbT('adult-confirm-nsfw',lang)+'</button>'
            +'<button class="adult-modal-cancel" id="adm-btn3">'+gdbT('adult-cancel',lang)+'</button>'
            +'</div></div>';
          document.body.appendChild(modal);
          document.getElementById('adm-btn1').onclick=function(){gdbSetAdultMode(1);rm();if(href)location.href=href;};
          document.getElementById('adm-btn2').onclick=function(){gdbSetAdultMode(2);rm();if(href)location.href=href;};
          document.getElementById('adm-btn3').onclick=rm;
        }

        document.addEventListener('DOMContentLoaded',()=>{
          const menu=document.getElementById('lang-menu');
          if(menu){
            menu.innerHTML=GDB_LANGS.map(l=>`<div class="lang-opt" data-lang="${l}" onclick="gdbApplyLang('${l}',true);document.getElementById('lang-menu').classList.remove('lang-menu-open')">${GDB_LANG_NAMES[l]}</div>`).join('');
          }
          gdbApplyLang(gdbDetectLang(),false);
          gdbApplyAdultMode();
          gdbApplyOwnedMode();
          gdbApplyParental();
        });

        // ── Global search via /api/search ────────────────────────────
        var _gdbSearchTimer=null;
        var _gdbSearchAbort=null;
        var _gdbSearchSeq=0;
        var _gdbAllResults=[];
        var _gdbPlatformFilter='';
        function gdbSearchEsc(s){return String(s||'').replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;').replace(/"/g,'&quot;');}
        function gdbSearchInput(val){
          clearTimeout(_gdbSearchTimer);
          if(_gdbSearchAbort){try{_gdbSearchAbort.abort();}catch(e){}_gdbSearchAbort=null;}
          var box=document.getElementById('global-search-results');
          var q=(val||'').trim();
          if(q.length<2){if(box)box.style.display='none';_gdbSearchSeq++;return;}
          _gdbSearchTimer=setTimeout(function(){gdbSearchRender(q);},250);
        }
        function gdbSearchRender(q){
          var box=document.getElementById('global-search-results');
          if(!box)return;
          var adult=typeof gdbGetAdultMode==='function'?gdbGetAdultMode():0;
          var mySeq=++_gdbSearchSeq;
          if(_gdbSearchAbort){try{_gdbSearchAbort.abort();}catch(e){}}
          _gdbSearchAbort=(typeof AbortController!=='undefined')?new AbortController():null;
          var opts=_gdbSearchAbort?{signal:_gdbSearchAbort.signal}:{};
          fetch('/api/search?q='+encodeURIComponent(q)+'&limit=50&adult='+adult,opts)
            .then(function(r){return r.json();})
            .then(function(items){
              if(mySeq!==_gdbSearchSeq) return;
              _gdbAllResults=items||[];
              _gdbPlatformFilter='';
              gdbSearchPaint();
            })
            .catch(function(e){
              if(e&&e.name==='AbortError') return;
              if(mySeq!==_gdbSearchSeq) return;
              box.innerHTML='<div class="gsr-none">Error</div>';box.style.display='';
            });
        }
        function gdbSearchPaint(){
          var box=document.getElementById('global-search-results');
          if(!box)return;
          if(!_gdbAllResults.length){
            box.innerHTML='<div class="gsr-none">No results</div>';
            box.style.display='';
            return;
          }
          var lang=typeof gdbDetectLang==='function'?gdbDetectLang():'fr';
          var allLabel=typeof gdbT==='function'?gdbT('all-platforms',lang):'All platforms';
          var platCount={};
          _gdbAllResults.forEach(function(r){var p=r.platform||'';if(!platCount[p])platCount[p]=0;platCount[p]++;});
          var platforms=Object.keys(platCount).filter(function(p){return p!=='';}).sort();
          var filtered=_gdbPlatformFilter
            ? _gdbAllResults.filter(function(r){return (r.platform||'')===_gdbPlatformFilter;})
            : _gdbAllResults;
          var filterHtml='';
          if(platforms.length>=2){
            var optsHtml='<option value="">'+gdbSearchEsc(allLabel)+' ('+_gdbAllResults.length+')</option>';
            platforms.forEach(function(p){
              var sel=(p===_gdbPlatformFilter)?' selected':'';
              optsHtml+='<option value="'+gdbSearchEsc(p)+'"'+sel+'>'+gdbSearchEsc(p)+' ('+platCount[p]+')</option>';
            });
            filterHtml='<div class="gsr-platform-filter"><select onchange="gdbSetPlatformFilter(this.value)">'+optsHtml+'</select></div>';
          }
          var itemsHtml=filtered.map(function(r){
            var url='/games/'+r.id+'.html';
            var hasAlt=r.matchedAlt&&r.matchedAlt!==r.name;
            var adultBadge=r.adult?'<span class="gsr-adult">18+</span>':'';
            var thumb='<div class="gsr-thumb"><img src="/thumbs/'+r.id+'.jpg" alt="" loading="lazy" onerror="this.parentNode.style.display=\'none\'"></div>';
            return '<a class="gsr-item" href="'+url+'">'
              +thumb
              +'<div class="gsr-info">'
              +'<div class="gsr-name">'+adultBadge+gdbSearchEsc(r.name)+'</div>'
              +(hasAlt?'<div class="gsr-alt">'+gdbSearchEsc(r.matchedAlt)+'</div>':'')
              +'<div class="gsr-plat">'+gdbSearchEsc(r.platform||'')+(r.year?(' · '+r.year):'')+'</div>'
              +'</div></a>';
          }).join('');
          box.innerHTML=filterHtml+itemsHtml;
          box.style.display='';
        }
        function gdbSetPlatformFilter(p){_gdbPlatformFilter=p||'';gdbSearchPaint();}
        document.addEventListener('click',function(e){
          var box=document.getElementById('global-search-results');
          if(box&&e.target.id!=='global-search'&&!box.contains(e.target))box.style.display='none';
        });
        </script>
        """;

    private const string GoogleFonts =
        """<link rel="preconnect" href="https://fonts.googleapis.com"><link rel="preconnect" href="https://fonts.gstatic.com" crossorigin><link href="https://fonts.googleapis.com/css2?family=Rajdhani:wght@500;600;700&family=Source+Sans+3:wght@300;400;600&display=swap" rel="stylesheet">""";

    // ── Head ───────────────────────────────────────────────────────────────────
    public static string Head(string title, string extraHead = "") => $$"""
        <!DOCTYPE html>
        <html lang="fr">
        <head>
        <meta charset="UTF-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <meta name="robots" content="noindex,nofollow">
        <title>{{Esc(title)}} – LiteBox</title>
        {{GoogleFonts}}
        <style>{{SharedCss}}</style>
        {{SharedJs}}
        {{extraHead}}
        </head>
        """;

    // ── Topbar (with optional breadcrumbs) ─────────────────────────────────────
    public static string Topbar(IEnumerable<(string Label, string Href)> breadcrumbs = null)
    {
        var crumbs = "";
        if (breadcrumbs != null)
        {
            var sb = new StringBuilder();
            sb.Append("<div class=\"breadcrumb\">");
            bool first = true;
            foreach (var (label, href) in breadcrumbs)
            {
                if (!first) sb.Append("<span class=\"sep\">›</span>");
                first = false;
                if (href != null)
                    sb.Append($"<a href=\"{Esc(href)}\" title=\"{Esc(label)}\">{Esc(label)}</a>");
                else
                    sb.Append($"<span style=\"color:var(--text)\">{Esc(label)}</span>");
            }
            sb.Append("</div>");
            crumbs = sb.ToString();
        }

        return $$"""
            <header class="topbar">
              <a class="brand" href="/">
                <svg width="22" height="22" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><polyline points="22 12 18 12 15 21 9 3 6 12 2 12"/></svg>
                LiteBox
              </a>
              <nav>
                <a href="/" data-i18n="home">Accueil</a>
                <a href="/platforms.html" data-i18n="platforms-nav">Plateformes</a>
              </nav>
              <div class="spacer"></div>
              {{crumbs}}
              <div class="global-search-wrap">
                <input type="search" id="global-search" class="global-search-input" autocomplete="off"
                       placeholder="Recherche globale…"
                       oninput="gdbSearchInput(this.value)">
                <div id="global-search-results" class="global-search-results" style="display:none"></div>
              </div>
              <button id="adult-mode-btn" class="adult-btn adult-btn-0" onclick="gdbCycleAdultMode()" aria-label="Mode adulte">+18 off</button>
              <button id="owned-mode-btn" class="owned-btn owned-btn-0" onclick="gdbCycleOwnedMode()" aria-label="Owned filter" title="Owned filter">Tous</button>
              <button id="lock-mode-btn" class="lock-btn" onclick="gdbClickLock()" aria-label="Parental lock" style="display:none">…</button>
              <div class="lang-switcher">
                <button class="lang-btn" onclick="gdbToggleLangMenu(event)" aria-label="Langue">
                  <span>🌐</span><span id="lang-btn-label">…</span>
                </button>
                <div class="lang-menu" id="lang-menu"></div>
              </div>
            </header>
            """;
    }

    // ── Footer ─────────────────────────────────────────────────────────────────
    public static string Footer() =>
        $"<footer>© {DateTime.Now.Year} LiteBox — local view (live SQLite)</footer>";

    // ── Composition helpers ────────────────────────────────────────────────────
    /// <summary>Opens the body, publishes the parental state for the JS lock button, emits the topbar + opening
    /// main. When <paramref name="parental"/> is null the lock button stays hidden.</summary>
    public static string BodyOpen(
        IEnumerable<(string Label, string Href)> breadcrumbs = null,
        WebParentalState parental = null)
    {
        bool active = parental?.IsActive ?? false;
        bool locked = parental?.IsLocked ?? false;
        bool canUnlock = parental?.CanUnlock ?? false;
        string bootstrap =
            "<script>window.LB_PARENTAL=" +
            "{active:" + (active ? "true" : "false") +
            ",locked:" + (locked ? "true" : "false") +
            ",canUnlock:" + (canUnlock ? "true" : "false") +
            "};</script>\n";
        return "<body>\n" + bootstrap + Topbar(breadcrumbs) + "\n<main class=\"page-wrap\">\n";
    }

    public static string BodyClose => "</main>\n" + Footer() + "\n</body></html>";

    public static string Esc(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Replace("&", "&amp;")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;")
                .Replace("\"", "&quot;")
                .Replace("'", "&#39;");
    }

    public static string EscJs(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return System.Text.Json.JsonSerializer.Serialize(s);
    }
}
