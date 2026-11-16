using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using QuasarBridge.Options;
using QuasarBridge.Services;

namespace QuasarBridge.Controllers;

[ApiController]
[Route("/")]
public class RootController : ControllerBase
{
    [HttpGet]
    public IActionResult Get(IOptions<QuasarBridgeOptions> options, MessageRouter router)
    {
        var o = options.Value;
        return Ok(new
        {
            ok = true,
            mode = o.Mode,
            http = $"http://127.0.0.1:{o.HttpPort}",
            https = o.EnableTls ? $"https://{(o.TlsBindAddress ?? "0.0.0.0")}:{o.TlsPort}" : null,
            upstream = o.UpstreamUrl,
            path = "/uni.ws",
            routingRevision = router.Revision,
            routingRules = router.RuleCount,
            swagger = o.SwaggerEnabled ? "/swagger" : null,
            api = new
            {
                healthz = "/healthz",
                status = "/api/status",
                config = "/api/config",
                rules = "/api/rules",
                events = "/api/events"
            }
        });
    }
}
