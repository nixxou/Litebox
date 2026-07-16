// Serves /robots.txt. This server is a local browsing/diagnostic surface, so disallow every crawler.

namespace LbApiHost.Host.Web;

internal static class RobotsHandler
{
    public static HttpResponse Handle(RouteContext ctx)
        => HttpResponse.PlainText("User-agent: *\nDisallow: /\n");
}
